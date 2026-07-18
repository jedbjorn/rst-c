// RecoveryScanner.cs — closes orphaned session files from crashed
// sessions at next startup (no OS watcher in v1).
//
// For each outbox file that is not locked (a live concurrent Revit
// instance holds its file) and lacks a session_end:
//   1. parse it tolerantly; note the last event's ts, the last seq, and
//      which documents were opened but never closed;
//   2. if the file ends in a partial line, append a defensive newline;
//   3. append a synthetic doc_closed per still-open doc, then a
//      synthetic session_end — ts = last observed ts, source "recovery",
//      synthetic true, seq continuing.
//
// Session length from a crashed session is therefore truncated to the
// last heartbeat — a bounded undercount of ≤2 minutes, never an
// overcount. Runs on a background task after OnStartup returns; every
// failure is per-file, logged, and never thrown.

using System.IO;
using System.Text;

namespace RST.Core.Telemetry;

public static class RecoveryScanner
{
    /// <summary>
    /// Scan <paramref name="outboxDir"/> and close every orphaned session
    /// file. Returns the paths recovered. Never throws; a missing dir is
    /// an empty scan.
    /// </summary>
    public static List<string> Scan(string outboxDir, Action<string>? log = null)
    {
        var recovered = new List<string>();
        string[] files;
        try
        {
            if (!Directory.Exists(outboxDir)) return recovered;
            files = Directory.GetFiles(outboxDir, "*" + OutboxFiles.Extension);
        }
        catch (Exception ex)
        {
            SafeLog(log, "telemetry recovery scan failed: " + ex.Message);
            return recovered;
        }

        Array.Sort(files, StringComparer.Ordinal);
        foreach (var path in files)
        {
            try
            {
                if (OutboxFiles.IsLocked(path)) continue; // live session or held reader
                if (RecoverFile(path)) recovered.Add(path);
            }
            catch (Exception ex)
            {
                SafeLog(log, "telemetry recovery skipped " + Path.GetFileName(path) + ": " + ex.Message);
            }
        }
        return recovered;
    }

    /// <summary>True when the file was an orphan and synthetic closes were appended.</summary>
    private static bool RecoverFile(string path)
    {
        var events = OutboxFiles.ReadEvents(path);
        if (events.Any(e => e.EventType == TelemetryEventTypes.SessionEnd))
            return false; // closed — the contract is already satisfied

        var openDocs = TrackOpenDocs(events);

        // Everything a synthetic record needs, with filename/mtime
        // fallbacks so even a zero-parseable-line file reaches the
        // closed state the contract promises.
        var last = events.Count > 0 ? events[^1] : null;
        var ts = last?.Ts ?? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        var seq = events.Count > 0 ? events.Max(e => e.Seq) : 0;
        var sessionGuid = last?.SessionGuid
            ?? OutboxFiles.TryParseSessionGuid(Path.GetFileName(path))
            ?? "";

        var sb = new StringBuilder();
        foreach (var identity in openDocs.Values)
        {
            var close = Synthetic(TelemetryEventTypes.DocClosed, sessionGuid, ++seq, ts);
            identity.WriteKeysTo(close);
            sb.Append(TelemetryJson.SerializeLine(close)).Append('\n');
        }
        var end = Synthetic(TelemetryEventTypes.SessionEnd, sessionGuid, ++seq, ts);
        sb.Append(TelemetryJson.SerializeLine(end)).Append('\n');

        var needsNewline = !OutboxFiles.EndsCleanly(path);
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        if (needsNewline) fs.WriteByte((byte)'\n');
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        fs.Write(bytes, 0, bytes.Length);
        fs.Flush(flushToDisk: true);
        return true;
    }

    /// <summary>
    /// Replay doc_opened / doc_closing / doc_closed to find the docs
    /// still open at the end of the file. doc_closed carries only
    /// closing_id + status (DocumentClosed doesn't expose the Document),
    /// so identity joins through the preceding doc_closing. A cancelled
    /// or failed close leaves the doc open.
    /// </summary>
    private static Dictionary<string, DocumentIdentity> TrackOpenDocs(List<TelemetryEvent> events)
    {
        var open = new Dictionary<string, DocumentIdentity>();
        var closingIdToKey = new Dictionary<string, string>();

        foreach (var e in events)
        {
            switch (e.EventType)
            {
                case TelemetryEventTypes.DocOpened:
                {
                    var identity = DocumentIdentity.ReadFrom(e);
                    open[identity.JoinKey ?? e.EventId] = identity;
                    break;
                }
                case TelemetryEventTypes.DocClosing:
                {
                    var closingId = e.GetString(TelemetryFields.ClosingId);
                    var key = DocumentIdentity.ReadFrom(e).JoinKey;
                    if (closingId is not null && key is not null) closingIdToKey[closingId] = key;
                    break;
                }
                case TelemetryEventTypes.DocClosed:
                {
                    var status = e.GetString(TelemetryFields.Status);
                    if (status is not null &&
                        (status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) ||
                         status.Equals("Failed", StringComparison.OrdinalIgnoreCase)))
                        break; // close didn't happen — doc is still open

                    var closingId = e.GetString(TelemetryFields.ClosingId);
                    var key = closingId is not null && closingIdToKey.TryGetValue(closingId, out var k)
                        ? k
                        : DocumentIdentity.ReadFrom(e).JoinKey; // synthetic closes carry keys directly
                    if (key is not null) open.Remove(key);
                    break;
                }
            }
        }
        return open;
    }

    private static TelemetryEvent Synthetic(string eventType, string sessionGuid, long seq, DateTimeOffset ts) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        SessionGuid = sessionGuid,
        Seq = seq,
        Ts = ts,
        EventType = eventType,
        SchemaVersion = TelemetrySchema.Version,
        Source = TelemetrySources.Recovery,
        Synthetic = true,
    };

    private static void SafeLog(Action<string>? log, string message)
    {
        try { log?.Invoke(message); }
        catch { /* logging must never take recovery down */ }
    }
}
