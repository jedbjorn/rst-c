// rst-c's own (not vendored): the RST tray's log (spec #20 §1; F17 WU1): %AppData%\RST\logs\tray_<yyyyMMdd>.log,
// rolled daily (and at 5 MB), ten files kept. Serilog's retention only deletes files of its own tray_ pattern,
// and the engine's boot-time prune only deletes rst_*.log (AppDataPaths.EngineSessionLogGlob), so neither
// touches the other's files. Shared mode: two trays of one user (two sessions) can write the same day's file.

using System.IO;
using RST.Core.Tray;
using Serilog;

namespace RST.Tray.Shell;

internal static class TrayLog
{
    public static void Configure()
    {
        try
        {
            Directory.CreateDirectory(TrayPaths.LogsDir);
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .Enrich.WithProperty("Component", "RST.Tray")
                .WriteTo.File(TrayPaths.LogFileBase,
                              outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3} [{ProcessId}] {Message:lj}{NewLine}{Exception}",
                              rollingInterval: RollingInterval.Day,
                              retainedFileCountLimit: TrayPaths.LogFilesKept,
                              fileSizeLimitBytes: 5 * 1024 * 1024,
                              rollOnFileSizeLimit: true,
                              shared: true)
                .Enrich.WithProperty("ProcessId", Environment.ProcessId)
                .CreateLogger();
        }
        catch (Exception)
        {
            // No log folder (a locked-down profile): run without a log rather than not at all.
            Log.Logger = Serilog.Core.Logger.None;
        }
    }
}
