// RetentionPruner.cs — deletes CLOSED outbox files whose newest event is
// older than the retention window (default 180 days, prefs-configurable).
//
// Retention is independent of any future server ack — files prune by
// age, never by ack (spec Scope Decision 5). Prune never touches the
// live file (lock probe) or a file the recovery scanner hasn't closed
// yet — an orphan without session_end is recovery's to fix first, and
// pruning it would erase the crash evidence. A file locked by a reader
// is skipped until next startup. Runs on a background task after
// OnStartup; never throws.

using System.IO;

namespace RST.Core.Telemetry;

public static class RetentionPruner
{
    public const int DefaultRetentionDays = 180;

    /// <summary>Upper bound (10 years) on a sane retention window — the
    /// guard that keeps a corrupt-but-parseable prefs value (int.MaxValue
    /// overflows AddDays) inside Prune's never-throws contract.</summary>
    public const int MaxRetentionDays = 3650;

    /// <summary>
    /// Delete closed files whose newest event predates
    /// <paramref name="utcNow"/> − <paramref name="retentionDays"/>.
    /// Returns the paths deleted. An out-of-range window — non-positive
    /// or beyond <see cref="MaxRetentionDays"/> — prunes nothing: corrupt
    /// prefs must never mass-delete history, and a huge value means
    /// "keep everything", not an AddDays overflow.
    /// </summary>
    public static List<string> Prune(
        string outboxDir,
        int retentionDays,
        DateTimeOffset utcNow,
        Action<string>? log = null)
    {
        var deleted = new List<string>();
        if (retentionDays <= 0 || retentionDays > MaxRetentionDays) return deleted;

        string[] files;
        try
        {
            if (!Directory.Exists(outboxDir)) return deleted;
            files = Directory.GetFiles(outboxDir, "*" + OutboxFiles.Extension);
        }
        catch (Exception ex)
        {
            SafeLog(log, "telemetry prune scan failed: " + ex.Message);
            return deleted;
        }

        var cutoff = utcNow.AddDays(-retentionDays);
        foreach (var path in files)
        {
            try
            {
                if (OutboxFiles.IsLocked(path)) continue;

                var events = OutboxFiles.ReadEvents(path);
                if (!events.Any(e => e.EventType == TelemetryEventTypes.SessionEnd))
                    continue; // not closed — recovery's job, never prune's

                var newest = events.Max(e => e.Ts);
                if (newest >= cutoff) continue;

                File.Delete(path);
                deleted.Add(path);
            }
            catch (Exception ex)
            {
                SafeLog(log, "telemetry prune skipped " + Path.GetFileName(path) + ": " + ex.Message);
            }
        }
        return deleted;
    }

    private static void SafeLog(Action<string>? log, string message)
    {
        try { log?.Invoke(message); }
        catch { /* logging must never take pruning down */ }
    }
}
