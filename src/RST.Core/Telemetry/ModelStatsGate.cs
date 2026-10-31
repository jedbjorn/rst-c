// ModelStatsGate.cs — when a model_stats record is captured (spec #24
// Capture Additions): a model that opens, saves or syncs is REQUESTED;
// the collector captures requested models at the next Revit Idling (so
// Document.GetWarnings never lengthens the open, save or sync itself),
// and each model emits at most once per 5 minutes.
//
// Requests are a set keyed by the collector's tracking key, so an open
// followed by a save before Revit goes idle is one capture, not two. A
// request for a model still inside its 5-minute window is dropped on
// arrival — no Idling work is queued for a capture that cannot emit.
// The window is anchored to the last EMITTED record.
//
// Closing (PR #175 review, WU2-01): a model that closes before the next
// Idling would lose its capture, and Close → Save is exactly the save
// whose stats matter most. So a model's close takes any pending request
// and captures it at once, while the Document is still valid; and a
// save or sync that arrives while the model is closing (Revit's close
// prompts can save or sync after DocumentClosing has fired) captures at
// once instead of deferring. Both orders are handled, so the gate does
// not depend on which one Revit uses. A cancelled close ends the
// closing state and later requests defer again.
//
// The window runs on a MONOTONIC clock (review WU2-02): a wall-clock
// correction backwards must not stretch it past 5 minutes, nor one
// forwards shorten it. Event timestamps stay wall-clock; only this
// throttle is monotonic. The source is injectable for tests.
//
// UI-thread-only, like PulseThrottle and ViewActivationGate: every
// Revit event and Idling run on Revit's UI thread, so this type is
// deliberately not thread-safe.

using System.Diagnostics;

namespace RST.Core.Telemetry;

/// <summary>What the collector does with one request.</summary>
public enum ModelStatsDecision
{
    /// <summary>Nothing: inside the model's window, or already pending.</summary>
    Skip,
    /// <summary>Newly pending: make sure the Idling capture is hooked.</summary>
    Defer,
    /// <summary>The model is closing: capture now, while the Document is valid.</summary>
    CaptureNow,
}

public sealed class ModelStatsGate
{
    /// <summary>Spec #24: at most one model_stats per model per 5 minutes.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(5);

    private readonly TimeSpan _interval;
    private readonly Func<TimeSpan> _monotonicNow;
    private readonly Dictionary<string, TimeSpan> _lastEmitted = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _closing = new(StringComparer.Ordinal);

    /// <param name="interval">The per-model window; 5 minutes by default.</param>
    /// <param name="monotonicNow">Elapsed time on a clock that never
    /// steps (default: a Stopwatch started with the gate). Never the
    /// wall clock.</param>
    public ModelStatsGate(TimeSpan? interval = null, Func<TimeSpan>? monotonicNow = null)
    {
        _interval = interval ?? DefaultInterval;
        if (monotonicNow is null)
        {
            var watch = Stopwatch.StartNew();
            monotonicNow = () => watch.Elapsed;
        }
        _monotonicNow = monotonicNow;
    }

    /// <summary>True while at least one model waits for the next Idling.</summary>
    public bool HasPending => _pending.Count > 0;

    /// <summary>
    /// After an open, save or sync of the model keyed <paramref name="key"/>.
    /// <see cref="ModelStatsDecision.CaptureNow"/> while the model is
    /// closing; otherwise <see cref="ModelStatsDecision.Defer"/> for a new
    /// pending request, or <see cref="ModelStatsDecision.Skip"/> when the
    /// model is inside its window (dropped) or already pending.
    /// </summary>
    public ModelStatsDecision Request(string key)
    {
        if (!CanEmit(key)) return ModelStatsDecision.Skip;
        if (_closing.Contains(key))
        {
            _pending.Remove(key);
            return ModelStatsDecision.CaptureNow;
        }
        return _pending.Add(key) ? ModelStatsDecision.Defer : ModelStatsDecision.Skip;
    }

    /// <summary>
    /// DocumentClosing for the model keyed <paramref name="key"/>: marks
    /// it closing, and takes its pending request. True = capture now
    /// (a request was pending and the window allows it).
    /// </summary>
    public bool Closing(string key)
    {
        _closing.Add(key);
        return _pending.Remove(key) && CanEmit(key);
    }

    /// <summary>DocumentClosed (succeeded or cancelled): the model is no
    /// longer closing. A cancelled close's model defers again.</summary>
    public void ClosingEnded(string key) => _closing.Remove(key);

    /// <summary>
    /// The pending keys, emptied. A key whose model is no longer open by
    /// the time Idling runs simply finds no document and is dropped.
    /// </summary>
    public IReadOnlyList<string> TakePending()
    {
        if (_pending.Count == 0) return Array.Empty<string>();
        var keys = _pending.ToArray();
        _pending.Clear();
        return keys;
    }

    /// <summary>Pure peek: whether <see cref="TryAcquire"/> would admit
    /// <paramref name="key"/> now — lets the collector skip the
    /// GetWarnings read for a capture that could not emit.</summary>
    public bool CanEmit(string key) =>
        !(_lastEmitted.TryGetValue(key, out var last) && _monotonicNow() - last < _interval);

    /// <summary>Commit the emission (the lifecycle's admit callback):
    /// true opens a new 5-minute window for <paramref name="key"/>.</summary>
    public bool TryAcquire(string key)
    {
        if (!CanEmit(key)) return false;
        _lastEmitted[key] = _monotonicNow();
        return true;
    }
}
