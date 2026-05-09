// BootLog.cs — zero-dependency text log for the pre-Serilog phase.
//
// The engine sets up Serilog in RstApplication.ConfigureLogging, but
// the bootstrap runs *before* the engine loads. Pulling Serilog into
// the bootstrap would force its DLLs into the addins-dir payload — the
// whole point of RST-033 is to keep that dir lean. So the bootstrap
// uses File.AppendAllText to a sibling log next to the engine's
// session log: rst_<timestamp>_boot.log paired with rst_<timestamp>.log
// by shared timestamp. Engine's PruneOldSessionLogs ('rst_*.log' glob)
// cleans both up together.
//
// Logger is best-effort and must never throw — every Write is wrapped.

using System;
using System.IO;

namespace RST.Bootstrap;

internal static class BootLog
{
    private static readonly string LogPath = ResolveLogPath();

    private static string ResolveLogPath()
    {
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        var logsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RST", "logs");
        try { Directory.CreateDirectory(logsDir); }
        catch { /* best-effort — caller still gets a path, writes will fail silently */ }
        return Path.Combine(logsDir, $"rst_{stamp}_boot.log");
    }

    public static void Info(string message) => Write("INF", message);

    public static void Error(string message) => Write("ERR", message);

    public static void Error(string message, Exception ex) =>
        Write("ERR", $"{message} :: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} {level} {message}{Environment.NewLine}";
            File.AppendAllText(LogPath, line);
        }
        catch
        {
            // Bootstrap continues even if logging fails entirely.
        }
    }
}
