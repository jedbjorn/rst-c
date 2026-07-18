// OutboxTestData.cs — shared builders for synthetic outbox files used by
// the recovery-scanner and retention-pruner tests.

using System;
using System.IO;
using System.Linq;
using RST.Core.Telemetry;

namespace RST.Tests.Telemetry;

internal static class OutboxTestData
{
    public const string InstallId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

    public static TelemetryEvent Event(
        string sessionGuid, long seq, string eventType, DateTimeOffset ts) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        SessionGuid = sessionGuid,
        Seq = seq,
        Ts = ts,
        EventType = eventType,
    };

    /// <summary>Write a session file from events; returns its path.</summary>
    public static string WriteSessionFile(string dir, string sessionGuid, params TelemetryEvent[] events)
    {
        var path = Path.Combine(dir, OutboxFiles.SessionFileName(InstallId, sessionGuid));
        File.WriteAllText(path,
            string.Concat(events.Select(e => TelemetryJson.SerializeLine(e) + "\n")));
        return path;
    }
}
