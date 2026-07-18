// InstallIdStore.cs — one GUID per install, minted on first run.
//
//   %LOCALAPPDATA%\RST\telemetry\install_id
//
// Machine-scoped on purpose: a roaming-profile user gets a different
// install_id per machine, which is exactly what lets a server
// disambiguate their outboxes. Rides every session file name and every
// session_start so server-side attribution needs no other local state.
// Unreadable/unwritable states degrade to a fresh in-memory GUID — an
// identity gap is data, never an exception.

using System.IO;
using System.Text;
using System.Threading;

namespace RST.Core.Telemetry;

public static class InstallIdStore
{
    public const string FileName = "install_id";

    /// <summary>
    /// Read the persisted install id, minting + persisting one on first
    /// run. First-run minting is atomic (FileMode.CreateNew — O_EXCL /
    /// CREATE_NEW): two Revit instances starting simultaneously race to
    /// create the file, the loser re-reads the winner's id, and both
    /// sessions report the SAME install id. On IO failure returns a
    /// fresh GUID for this session and logs once — telemetry keeps
    /// working, attribution just loses stability.
    /// </summary>
    public static string GetOrCreate(string telemetryRoot, Action<string>? log = null)
    {
        var path = Path.Combine(telemetryRoot, FileName);
        if (File.Exists(path))
        {
            var existing = TryReadExisting(path);
            if (existing is not null) return existing;
            // Fall through: file exists but never parsed — genuinely
            // corrupt, repaired below by overwrite.
        }

        var minted = Guid.NewGuid().ToString();
        try
        {
            Directory.CreateDirectory(telemetryRoot);
            if (File.Exists(path))
            {
                // Corruption repair, not a first-run race: any writer
                // lands a valid GUID, last one wins.
                File.WriteAllText(path, minted);
                return minted;
            }
            using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            var bytes = Encoding.UTF8.GetBytes(minted);
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
            return minted;
        }
        catch (IOException) when (File.Exists(path))
        {
            // Lost the first-run race — the winner's id IS the install id.
            var winner = TryReadExisting(path);
            if (winner is not null) return winner;
            SafeLog(log, "install_id unreadable after losing first-run race — using session-scoped id");
            return minted;
        }
        catch (Exception ex)
        {
            SafeLog(log, "install_id write failed — using session-scoped id: " + ex.Message);
            return minted;
        }
    }

    /// <summary>
    /// Read + parse the id file, retrying briefly: a race loser can
    /// observe the winner's file created but not yet flushed.
    /// </summary>
    private static string? TryReadExisting(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var text = File.ReadAllText(path).Trim();
                if (Guid.TryParse(text, out var g)) return g.ToString();
            }
            catch
            {
                // unreadable this attempt — retry below
            }
            if (attempt >= 4) return null;
            Thread.Sleep(10);
        }
    }

    private static void SafeLog(Action<string>? log, string message)
    {
        try { log?.Invoke(message); }
        catch { /* never throw for logging */ }
    }
}
