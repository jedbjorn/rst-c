// ModelStatsGate.cs — when a model_stats record is captured (spec #24
// Capture Additions): a document that opens, saves or syncs is
// REQUESTED; the collector captures requested documents at the next
// Revit Idling (so nothing lengthens the open, save or sync itself), and
// each model emits at most once per 5 minutes.
//
// What is measured (decision #71): after an OPEN, the file size only —
// Document.GetWarnings once froze Revit 2026 for 3.2 s right after an
// open (rc.1 VM, F1), so the open capture never calls it and records
// warnings_count null ("not measured"; the fold keeps the last known
// count). After a save, save-as or sync the user already waits, so the
// warning count is measured too. A request carries that choice, and an
// open followed by a save before Idling is one capture that measures.
//
// Two identities (rc.1 VM, F2): a request names the exact DOCUMENT (the
// collector's per-open-document id), and the 5-minute window is per
// MODEL (the tracking key, which copies sharing a creation GUID also
// share). Matching requests by model key sampled the wrong one of two
// open copies; so pending requests and the closing state are per
// document, and only the window and the record's identity keys use the
// model key. A request for a model still inside its window is dropped on
// arrival — no Idling work is queued for a capture that cannot emit. The
// window is anchored to the last EMITTED record.
//
// Closing (PR #175 review, WU2-01): a document that closes before the
// next Idling would lose its capture, and Close → Save is exactly the
// save whose stats matter most. So a document's close takes its pending
// request and captures it at once, while the Document is still valid;
// and a save or sync that arrives while the document is closing (Revit's
// close prompts can save or sync after DocumentClosing has fired)
// captures at once instead of deferring. Both orders are handled, so the
// gate does not depend on which one Revit uses. A cancelled close ends
// the closing state and later requests defer again. Closing one document
// never touches another's request, even for the same model.
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
    /// <summary>Nothing: inside the model's window, or already pending (merged).</summary>
    Skip,
    /// <summary>Newly pending: make sure the Idling capture is hooked.</summary>
    Defer,
    /// <summary>The document is closing: capture now, while the Document is valid.</summary>
    CaptureNow,
}

/// <summary>The event that asked for a capture; it decides what is measured.</summary>
public enum ModelStatsTrigger
{
    /// <summary>DocumentOpened: file size only, never GetWarnings (decision #71).</summary>
    Open,
    /// <summary>DocumentSaved or DocumentSavedAs: warnings and file size.</summary>
    Save,
    /// <summary>DocumentSynchronizedWithCentral: warnings and file size.</summary>
    Sync,
}

/// <summary>One capture to run: the exact document, its model key (for the window and the
/// record), and whether the warning count is measured.</summary>
public sealed record ModelStatsRequest(string DocumentId, string ModelKey, bool MeasureWarnings);

public static class ModelStatsTriggers
{
    /// <summary>Whether a capture for <paramref name="trigger"/> calls GetWarnings: save and sync
    /// only (decision #71).</summary>
    public static bool MeasuresWarnings(this ModelStatsTrigger trigger) => trigger != ModelStatsTrigger.Open;
}

public sealed class ModelStatsGate
{
    /// <summary>Spec #24: at most one model_stats per model per 5 minutes.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(5);

    private readonly TimeSpan _interval;
    private readonly Func<TimeSpan> _monotonicNow;
    private readonly Dictionary<string, TimeSpan> _lastEmitted = new(StringComparer.Ordinal);
    // In request order: Idling captures the earliest first.
    private readonly List<ModelStatsRequest> _pending = new();
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

    /// <summary>True while at least one document waits for the next Idling.</summary>
    public bool HasPending => _pending.Count > 0;

    /// <summary>
    /// After an open, save or sync of document <paramref name="documentId"/>, a copy of the
    /// model keyed <paramref name="modelKey"/>.
    /// <see cref="ModelStatsDecision.CaptureNow"/> while the document is closing (measure
    /// per <see cref="ModelStatsTriggers.MeasuresWarnings"/>); otherwise
    /// <see cref="ModelStatsDecision.Defer"/> for a new pending request, or
    /// <see cref="ModelStatsDecision.Skip"/> when the model is inside its window (dropped) or
    /// the document is already pending (merged: a save after an open makes it measure).
    /// </summary>
    public ModelStatsDecision Request(string documentId, string modelKey, ModelStatsTrigger trigger)
    {
        if (!CanEmit(modelKey)) return ModelStatsDecision.Skip;
        var at = IndexOf(documentId);
        if (_closing.Contains(documentId))
        {
            if (at >= 0) _pending.RemoveAt(at);
            return ModelStatsDecision.CaptureNow;
        }
        var request = new ModelStatsRequest(documentId, modelKey, trigger.MeasuresWarnings());
        if (at < 0)
        {
            _pending.Add(request);
            return ModelStatsDecision.Defer;
        }
        var merged = _pending[at];
        // The latest key (a Save As can change it) and measure if either asked to.
        _pending[at] = request with { MeasureWarnings = merged.MeasureWarnings || request.MeasureWarnings };
        return ModelStatsDecision.Skip;
    }

    /// <summary>
    /// DocumentClosing for document <paramref name="documentId"/>: marks it closing, and takes
    /// its own pending request (never another document's). Non-null = capture it now (a request
    /// was pending and the window allows it).
    /// </summary>
    public ModelStatsRequest? Closing(string documentId)
    {
        _closing.Add(documentId);
        var at = IndexOf(documentId);
        if (at < 0) return null;
        var request = _pending[at];
        _pending.RemoveAt(at);
        return CanEmit(request.ModelKey) ? request : null;
    }

    /// <summary>DocumentClosed (succeeded or cancelled): the document is no
    /// longer closing. A cancelled close's document defers again.</summary>
    public void ClosingEnded(string documentId) => _closing.Remove(documentId);

    /// <summary>
    /// The pending requests in request order, emptied. A request whose document is no longer
    /// open by the time Idling runs simply finds no document and is dropped.
    /// </summary>
    public IReadOnlyList<ModelStatsRequest> TakePending()
    {
        if (_pending.Count == 0) return Array.Empty<ModelStatsRequest>();
        var requests = _pending.ToArray();
        _pending.Clear();
        return requests;
    }

    /// <summary>Pure peek: whether <see cref="TryAcquire"/> would admit
    /// <paramref name="modelKey"/> now — lets the collector skip the
    /// capture's Revit reads when it could not emit.</summary>
    public bool CanEmit(string modelKey) =>
        !(_lastEmitted.TryGetValue(modelKey, out var last) && _monotonicNow() - last < _interval);

    /// <summary>Commit the emission (the lifecycle's admit callback):
    /// true opens a new 5-minute window for <paramref name="modelKey"/>.</summary>
    public bool TryAcquire(string modelKey)
    {
        if (!CanEmit(modelKey)) return false;
        _lastEmitted[modelKey] = _monotonicNow();
        return true;
    }

    /// <summary>
    /// Pair each request with ITS open document, in request order: the document whose
    /// <paramref name="documentId"/> equals the request's, never merely one of the same model.
    /// A request with no open document (it closed before Idling) is left out; a document whose id
    /// cannot be read (null) matches nothing.
    /// </summary>
    public static IReadOnlyList<(T Document, ModelStatsRequest Request)> Resolve<T>(
        IReadOnlyList<ModelStatsRequest> requests, IEnumerable<T> openDocuments, Func<T, string?> documentId)
    {
        if (requests.Count == 0) return Array.Empty<(T, ModelStatsRequest)>();
        var byId = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var doc in openDocuments)
            if (documentId(doc) is { } id) byId.TryAdd(id, doc);
        var resolved = new List<(T, ModelStatsRequest)>(requests.Count);
        foreach (var r in requests)
            if (byId.TryGetValue(r.DocumentId, out var doc)) resolved.Add((doc, r));
        return resolved;
    }

    private int IndexOf(string documentId) =>
        _pending.FindIndex(r => string.Equals(r.DocumentId, documentId, StringComparison.Ordinal));
}
