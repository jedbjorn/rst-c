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

namespace RST.Core.Telemetry;

public static class InstallIdStore
{
    public const string FileName = "install_id";

    /// <summary>
    /// Read the persisted install id, minting + persisting one on first
    /// run. On IO failure returns a fresh GUID for this session and logs
    /// once — telemetry keeps working, attribution just loses stability.
    /// </summary>
    public static string GetOrCreate(string telemetryRoot, Action<string>? log = null)
    {
        var path = Path.Combine(telemetryRoot, FileName);
        try
        {
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path).Trim();
                if (Guid.TryParse(text, out var existing)) return existing.ToString();
            }
        }
        catch (Exception ex)
        {
            SafeLog(log, "install_id read failed: " + ex.Message);
        }

        var minted = Guid.NewGuid().ToString();
        try
        {
            Directory.CreateDirectory(telemetryRoot);
            File.WriteAllText(path, minted);
        }
        catch (Exception ex)
        {
            SafeLog(log, "install_id write failed — using session-scoped id: " + ex.Message);
        }
        return minted;
    }

    private static void SafeLog(Action<string>? log, string message)
    {
        try { log?.Invoke(message); }
        catch { /* never throw for logging */ }
    }
}
