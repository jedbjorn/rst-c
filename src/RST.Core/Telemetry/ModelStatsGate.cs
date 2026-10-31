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
// The window is anchored to the last EMITTED record (PulseThrottle).
//
// UI-thread-only, like PulseThrottle and ViewActivationGate: every
// Revit event and Idling run on Revit's UI thread, so this type is
// deliberately not thread-safe. The clock is injectable for tests.

namespace RST.Core.Telemetry;

public sealed class ModelStatsGate
{
    /// <summary>Spec #24: at most one model_stats per model per 5 minutes.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(5);

    private readonly PulseThrottle _throttle;
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);

    public ModelStatsGate(TimeSpan? interval = null, Func<DateTimeOffset>? clock = null)
    {
        _throttle = new PulseThrottle(interval ?? DefaultInterval, clock);
    }

    /// <summary>True while at least one model waits for the next Idling.</summary>
    public bool HasPending => _pending.Count > 0;

    /// <summary>
    /// Ask for a capture of the model keyed <paramref name="key"/> at the
    /// next Idling. False when the model is still inside its window (the
    /// request is dropped) or is already pending.
    /// </summary>
    public bool Request(string key)
    {
        if (!_throttle.CanAcquire(key)) return false;
        return _pending.Add(key);
    }

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
    public bool CanEmit(string key) => _throttle.CanAcquire(key);

    /// <summary>Commit the emission (the lifecycle's admit callback):
    /// true opens a new 5-minute window for <paramref name="key"/>.</summary>
    public bool TryAcquire(string key) => _throttle.TryAcquire(key);
}
