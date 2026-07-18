// ViewActivationGate.cs — decides when a ViewActivated event becomes a
// view_activated record: only when the ACTIVE DOCUMENT changes (never on
// view switches within one document), and at most once per document per
// interval so rapid A↔B flipping can't flood the outbox.
//
// The two conditions compose: a document-change that loses the per-doc
// throttle still updates "current document", so switching straight back
// counts as a change again — wall-clock attribution across open docs
// stays honest without exceeding the cap. UI-thread-only, like
// PulseThrottle.

namespace RST.Core.Telemetry;

public sealed class ViewActivationGate
{
    private readonly PulseThrottle _throttle;
    private string? _activeKey;

    public ViewActivationGate(TimeSpan? interval = null, Func<DateTimeOffset>? clock = null)
    {
        _throttle = new PulseThrottle(interval, clock);
    }

    /// <summary>
    /// Record that the document keyed <paramref name="key"/> is now
    /// active; true when a view_activated event should be emitted.
    /// The first activation of the session emits (it IS a change).
    /// </summary>
    public bool ShouldEmit(string key)
    {
        if (key == _activeKey) return false;
        _activeKey = key;
        return _throttle.TryAcquire(key);
    }
}
