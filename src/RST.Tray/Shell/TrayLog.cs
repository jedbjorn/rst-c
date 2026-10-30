// rst-c's own (not vendored): the RST tray's log (spec #20 §1; F17 WU1): %AppData%\RST\logs\tray_<yyyyMMdd>.log,
// rolled daily (and at 5 MB), ten files kept. The sink's retention only deletes files of its own tray_ pattern,
// and the engine's boot-time prune only deletes rst_*.log (AppDataPaths.EngineSessionLogGlob), so neither
// touches the other's files. Shared mode: two trays of one user (two sessions) can write the same day's file.
// The tray is not in Revit, but logs through the same in-house logger as the engine (decision #33).

using System.Collections.Generic;
using System.IO;
using RST.Core.Logging;
using RST.Core.Tray;

namespace RST.Tray.Shell;

internal static class TrayLog
{
    public static void Configure()
    {
        try
        {
            Directory.CreateDirectory(TrayPaths.LogsDir);
            Log.Logger = new Logger(
                LogLevel.Information,
                new ILogSink[]
                {
                    new FileSink(new FileSinkOptions
                    {
                        Path = TrayPaths.LogFileBase,
                        OutputTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3} [{ProcessId}] {Message:lj}{NewLine}{Exception}",
                        RollingInterval = RollingInterval.Day,
                        RetainedFileCountLimit = TrayPaths.LogFilesKept,
                        FileSizeLimitBytes = 5 * 1024 * 1024,
                        RollOnFileSizeLimit = true,
                        Shared = true,
                    }),
                },
                new Dictionary<string, object?>
                {
                    ["Component"] = "RST.Tray",
                    ["ProcessId"] = Environment.ProcessId,
                });
        }
        catch (Exception)
        {
            // No log folder (a locked-down profile): run without a log rather than not at all.
            Log.Logger = Logger.None;
        }
    }
}
