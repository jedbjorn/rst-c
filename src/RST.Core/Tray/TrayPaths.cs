// rst-c's own (not vendored): the names and per-user locations the RST tray (src/RST.Tray, spec #20 §1)
// shares with its installer and, from WU2, with the Revit add-in that starts it. Pure path and name math, no
// Windows APIs, so RST.Tests checks it on Linux.
//
//   %LocalAppData%\Programs\RST\Tray\RST.Tray.exe   the tray (RST.msi, per user)
//   C:\Program Files\RST\Tray\RST.Tray.exe          the tray (RST-Machine.msi, per machine; HKLM Run)
//   %LocalAppData%\RST\Tray\cache\                  downloads (WU4); RST.msi removes it on uninstall
//   %AppData%\RST\tray_settings.json                the tray's settings (user data, survives uninstall)
//   %AppData%\RST\tray_updates.json                 the last update check and the versions announced (WU3)
//   %AppData%\RST\logs\tray_<yyyyMMdd>.log          the tray's log, 10 files kept (outside rst_*.log)
//   Local\RST.Tray.<UserSid>                        the single-instance mutex (per user and session)
//   Local\RST.Tray.Show.<UserSid>                   the event a second launch sets to show the window

using System;
using System.IO;
using RST.Core.Configuration;

namespace RST.Core.Tray;

public static class TrayPaths
{
    /// <summary>The tray's file name, as RST.msi installs it and <c>util:CloseApplication</c> targets it.</summary>
    public const string ExeName = "RST.Tray.exe";

    /// <summary>The name of the <c>HKMU\Software\Microsoft\Windows\CurrentVersion\Run</c> value RST.msi writes.</summary>
    public const string RunValueName = "RST";

    /// <summary>The argument the Run value passes: start hidden in the notification area.</summary>
    public const string StartupArgument = "--startup";

    /// <summary>The registry key (under HKLM and HKCU) that holds IT presets such as <see cref="UpdateChecksValueName"/>.</summary>
    public const string PresetKey = @"Software\Designs-OS\RST\Tray";

    /// <summary><c>UpdateChecks</c> = 0 under <see cref="PresetKey"/> turns update checks off and locks the setting.</summary>
    public const string UpdateChecksValueName = "UpdateChecks";

    /// <summary>
    /// The log's base path file name: the file sink's daily roll (RST.Core.Logging.FileSink) inserts the date before the extension, so the
    /// files are <c>tray_20261030.log</c> (and <c>tray_20261030_001.log</c> past the size limit).
    /// </summary>
    public const string LogFileBaseName = "tray_.log";

    /// <summary>How many tray log files are kept (the file sink's RetainedFileCountLimit).</summary>
    public const int LogFilesKept = 10;

    /// <summary>
    /// Matches every tray log file. It never matches <see cref="AppDataPaths.EngineSessionLogGlob"/>'s files, and that
    /// glob never matches these, so the engine's boot-time prune and the tray's roll each keep their own ten.
    /// </summary>
    public const string LogFileGlob = "tray_*.log";

    /// <summary>%AppData%\RST\tray_settings.json.</summary>
    public static string SettingsFile => Path.Combine(AppDataPaths.Root, "tray_settings.json");

    /// <summary>%AppData%\RST\tray_updates.json: the last check's result and the versions already announced (WU3).</summary>
    public static string UpdatesFile => Path.Combine(AppDataPaths.Root, "tray_updates.json");

    /// <summary>%AppData%\RST\logs\ (shared with the engine's session logs).</summary>
    public static string LogsDir => Path.Combine(AppDataPaths.Root, "logs");

    /// <summary>The log's base path, <c>%AppData%\RST\logs\tray_.log</c>.</summary>
    public static string LogFileBase => Path.Combine(LogsDir, LogFileBaseName);

    /// <summary>%LocalAppData%\RST\Tray\ (non-roaming tray state: the download cache from WU4).</summary>
    public static string LocalRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RST", "Tray");

    /// <summary>%LocalAppData%\RST\Tray\cache\ (removed by RST.msi on uninstall, util:RemoveFolderEx).</summary>
    public static string CacheDir => Path.Combine(LocalRoot, "cache");

    /// <summary>%LocalAppData%\RST\Tray\logs\: msiexec's verbose logs, <c>install-&lt;product&gt;-&lt;ts&gt;.log</c> (WU4).</summary>
    public static string InstallLogsDir => Path.Combine(LocalRoot, "logs");

    /// <summary>%LocalAppData%\RST\Tray\updater\: the tray's copy that installs RST over the tray itself (WU4).</summary>
    public static string UpdaterDir => Path.Combine(LocalRoot, "updater");

    /// <summary>
    /// The updater copy's exe name. Not <see cref="ExeName"/>: RST.msi's util:CloseApplication targets RST.Tray.exe
    /// by name, and must never reach the process that is running the install (WU1 review N2).
    /// </summary>
    public const string UpdaterExeName = "RST.TrayUpdater.exe";

    /// <summary>%LocalAppData%\RST\Tray\selfupdate.json: what the updater did, shown once by the new tray.</summary>
    public static string SelfUpdateResultFile => Path.Combine(LocalRoot, "selfupdate.json");

    /// <summary>The argument that makes the updater copy install an MSI.</summary>
    public const string ApplyArgument = "--apply";

    /// <summary>The argument naming the tray process the updater waits for.</summary>
    public const string WaitPidArgument = "--wait-pid";

    /// <summary>%LocalAppData%\Programs\RST\Tray\ (the per-user install folder).</summary>
    public static string PerUserInstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "RST", "Tray");

    /// <summary>C:\Program Files\RST\Tray\ (RST-Machine.msi's install folder, spec #20 §7; F17 WU5).</summary>
    public static string PerMachineInstallDir =>
        InstallLayout.MachineTrayDir(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));

    /// <summary>The single-instance mutex: <c>Local\RST.Tray.&lt;UserSid&gt;</c>.</summary>
    public static string MutexName(string userSid) => @"Local\RST.Tray." + RequireSid(userSid);

    /// <summary>The event a second launch sets so the running tray shows its window.</summary>
    public static string ShowEventName(string userSid) => @"Local\RST.Tray.Show." + RequireSid(userSid);

    private static string RequireSid(string userSid)
    {
        if (string.IsNullOrWhiteSpace(userSid)) throw new ArgumentException("A user SID is required.", nameof(userSid));
        // A SID is S-1-5-21-…: letters, digits and dashes. Anything else (a backslash would open another
        // namespace) is refused rather than escaped.
        foreach (var c in userSid)
            if (!(char.IsAsciiLetterOrDigit(c) || c == '-'))
                throw new ArgumentException($"'{userSid}' is not a SID.", nameof(userSid));
        return userSid;
    }
}
