// TelemetrySession.cs — mints enveloped events for one Revit session.
//
// Owns the session_guid and the per-session monotonic seq counter. The
// collector calls Create from the UI thread and the heartbeat timer
// thread concurrently, so seq is interlocked. The clock is injectable
// for tests; production uses UtcNow.

using System.Threading;

namespace RST.Core.Telemetry;

public sealed class TelemetrySession
{
    private readonly Func<DateTimeOffset> _clock;
    private long _seq;

    public TelemetrySession(string? sessionGuid = null, Func<DateTimeOffset>? clock = null)
    {
        SessionGuid = sessionGuid ?? Guid.NewGuid().ToString();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public string SessionGuid { get; }

    /// <summary>Last seq handed out; 0 before the first event.</summary>
    public long CurrentSeq => Interlocked.Read(ref _seq);

    public TelemetryEvent Create(string eventType) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        SessionGuid = SessionGuid,
        Seq = Interlocked.Increment(ref _seq),
        Ts = _clock(),
        EventType = eventType,
        SchemaVersion = TelemetrySchema.Version,
        Source = TelemetrySources.Addin,
    };
}
