// OutboxFiles.cs — the outbox file contract: naming, liveness probing,
// and tolerant reading. This is the stable seam a future shipper keys on.
//
//   %LOCALAPPDATA%\RST\telemetry\outbox\{install_id}_{session_guid}.jsonl
//
// One file per Revit session; the writer holds it with LiveFileShare,
// so a concurrent instance's live file refuses an exclusive probe.
// Closed-file contract: a file is closed ⟺ it contains a session_end
// event — the recovery scanner guarantees every non-live file reaches
// that state.

using System.IO;

namespace RST.Core.Telemetry;

public static class OutboxFiles
{
    public const string Extension = ".jsonl";

    public static string SessionFileName(string installId, string sessionGuid) =>
        installId + "_" + sessionGuid + Extension;

    /// <summary>
    /// Recover the session_guid from a {install_id}_{session_guid}.jsonl
    /// name — both parts are GUIDs, so the last underscore splits them.
    /// Null when the name doesn't fit the contract.
    /// </summary>
    public static string? TryParseSessionGuid(string fileName)
    {
        if (!fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) return null;
        var stem = fileName.Substring(0, fileName.Length - Extension.Length);
        var sep = stem.LastIndexOf('_');
        if (sep < 0 || sep == stem.Length - 1) return null;
        var candidate = stem.Substring(sep + 1);
        return Guid.TryParse(candidate, out var g) ? g.ToString() : null;
    }

    /// <summary>
    /// The share mode a live session writer holds its file with: readers
    /// (dashboard, future shipper) stay welcome, write access is denied.
    /// On Windows the OS enforces this directly; on Unix .NET backs a
    /// write-access-with-share-Read open with an advisory lock that the
    /// exclusive probe in <see cref="IsLocked"/> conflicts with, while
    /// read-only opens take no lock — verified behavior on net8, so
    /// lock-skip semantics hold on the Linux CI runner too.
    /// </summary>
    public const FileShare LiveFileShare = FileShare.Read;

    /// <summary>
    /// True when another process (a live Revit session, or a reader
    /// holding an incompatible lock) prevents exclusive write access.
    /// Probed by attempting an exclusive open — denied by a live
    /// writer's share mode on Windows and by its lock on Unix. Missing
    /// file counts as locked: there is nothing safe to do with it either
    /// way.
    /// </summary>
    public static bool IsLocked(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            return false;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Read every parseable event in file order, skipping blank and
    /// unparseable lines (a crash costs at most one partial trailing
    /// line). Opens with FileShare.ReadWrite so live files stay readable.
    /// Throws on open failure — callers decide how to degrade.
    /// </summary>
    public static List<TelemetryEvent> ReadEvents(string path)
    {
        var events = new List<TelemetryEvent>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var e = TelemetryJson.TryParseLine(line);
            if (e is not null) events.Add(e);
        }
        return events;
    }

    /// <summary>False when the file is non-empty and its last byte is not
    /// '\n' — i.e. a partial trailing line needs a defensive newline
    /// before anything is appended.</summary>
    public static bool EndsCleanly(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length == 0) return true;
        fs.Seek(-1, SeekOrigin.End);
        return fs.ReadByte() == '\n';
    }
}
