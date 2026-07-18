// PulseThrottle.cs — per-key rate limiter behind doc_changed_pulse
// (and, composed into ViewActivationGate, view_activated).
//
// Spec Threading & Safety: throttle state (per-doc last-pulse
// timestamps) lives in the collector and is touched only on the Revit
// UI thread — so this type is deliberately NOT thread-safe; keeping it
// lock-free keeps the hot DocumentChanged path cheap. The clock is
// injectable for tests; production uses UtcNow.

namespace RST.Core.Telemetry;

public sealed class PulseThrottle
{
    /// <summary>Spec cap for pulse-class events: ≤1 per document per minute.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(1);

    private readonly Dictionary<string, DateTimeOffset> _lastByKey = new();
    private readonly TimeSpan _interval;
    private readonly Func<DateTimeOffset> _clock;

    public PulseThrottle(TimeSpan? interval = null, Func<DateTimeOffset>? clock = null)
    {
        _interval = interval ?? DefaultInterval;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// True when <paramref name="key"/> may emit now — records the
    /// acquisition so the next attempt inside the interval is refused.
    /// A refused attempt records nothing: the window is anchored to the
    /// last EMITTED pulse, so a steady event stream still emits once per
    /// interval instead of being pushed forever.
    /// </summary>
    public bool TryAcquire(string key)
    {
        var now = _clock();
        if (_lastByKey.TryGetValue(key, out var last) && now - last < _interval)
            return false;
        _lastByKey[key] = now;
        return true;
    }
}
