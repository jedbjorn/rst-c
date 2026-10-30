// rst-c's own (not vendored): RST updating itself, tray included (spec #20 §5; research Q5; F17 WU4).
//
//   SelfUpdater   in the running tray: copies the tray's folder to %LocalAppData%\RST\Tray\updater\, with RST.Tray.exe
//                 renamed RST.TrayUpdater.exe (the apphost still loads RST.Tray.dll beside it), and starts that copy
//                 with --apply <verified RST.msi> --wait-pid <this tray>. The tray then quits.
//   Updater       the copy's Main: only as RST.TrayUpdater.exe in the updater folder; runs RST.Core's
//                 SelfUpdateApply (re-verify, wait for the tray, msiexec, record, start the new tray).
//
// The copy's name matters: RST.msi's util:CloseApplication closes every RST.Tray.exe by name (WU1 review N2), and
// Restart Manager only touches processes holding the files being replaced; the updater holds neither.

using System.Diagnostics;
using System.IO;
using RST.Core.Tray;
using RST.Core.Updates;
using Serilog;

namespace RST.Tray.Updates;

internal sealed class SelfUpdater : ISelfUpdater
{
    private readonly string _sourceDir;
    private readonly string _updaterDir;
    private readonly int _waitPid;
    private readonly Action<string> _log;

    /// <param name="sourceDir">The running tray's folder (AppContext.BaseDirectory).</param>
    /// <param name="updaterDir">%LocalAppData%\RST\Tray\updater.</param>
    /// <param name="waitPid">The tray process the updater waits for (this one).</param>
    public SelfUpdater(string sourceDir, string updaterDir, int waitPid, Action<string>? log = null)
    {
        _sourceDir = Path.GetFullPath(sourceDir);
        _updaterDir = Path.GetFullPath(updaterDir);
        _waitPid = waitPid;
        _log = log ?? (_ => { });
    }

    /// <summary>The tray runs from Program Files: a per-machine RST, which this per-user tray never updates.</summary>
    public bool PerMachineTray => IsUnder(_sourceDir, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
                                  || IsUnder(_sourceDir, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));

    public string? Start(string msiPath)
    {
        try
        {
            if (Directory.Exists(_updaterDir)) Directory.Delete(_updaterDir, recursive: true);
            Directory.CreateDirectory(_updaterDir);
            var copied = 0;
            foreach (var file in Directory.EnumerateFiles(_sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(_sourceDir, file);
                if (string.Equals(relative, TrayPaths.ExeName, StringComparison.OrdinalIgnoreCase)) relative = TrayPaths.UpdaterExeName;
                var target = Path.Combine(_updaterDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
                copied++;
            }
            var exe = Path.Combine(_updaterDir, TrayPaths.UpdaterExeName);
            if (!File.Exists(exe)) return $"{TrayPaths.ExeName} is not in {_sourceDir}";
            if (File.Exists(Path.Combine(_updaterDir, TrayPaths.ExeName))) return "the updater folder still holds RST.Tray.exe";

            // ShellExecute: the copy inherits no handle from this process (a console or test host's output pipes would
            // otherwise stay open in it, and in the tray it starts).
            var start = new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = _updaterDir };
            start.ArgumentList.Add(TrayPaths.ApplyArgument);
            start.ArgumentList.Add(msiPath);
            start.ArgumentList.Add(TrayPaths.WaitPidArgument);
            start.ArgumentList.Add(_waitPid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var process = Process.Start(start);
            if (process is null) return "the updater did not start";
            _log($"self-update: copied {copied} files to {_updaterDir}; started {TrayPaths.UpdaterExeName} (pid {process.Id}) --apply \"{msiPath}\" --wait-pid {_waitPid}");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return ex.GetType().Name + ": " + ex.Message;
        }
    }

    internal static bool IsUnder(string path, string? root)
    {
        if (string.IsNullOrEmpty(root)) return false;
        var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(full, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>The updater's Windows: wait for the old tray, start the new one.</summary>
internal sealed class UpdaterHost : ISelfUpdateHost
{
    private readonly string _trayExe;

    public UpdaterHost(string? trayExe = null) => _trayExe = trayExe ?? Path.Combine(TrayPaths.PerUserInstallDir, TrayPaths.ExeName);

    public bool WaitForTrayExit(int pid, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!string.Equals(process.ProcessName, Path.GetFileNameWithoutExtension(TrayPaths.ExeName), StringComparison.OrdinalIgnoreCase))
                return true; // not an RST tray: nothing to wait for
            return process.WaitForExit(timeout);
        }
        catch (ArgumentException)
        {
            return true; // already gone
        }
    }

    public bool StartTray()
    {
        if (!File.Exists(_trayExe)) return false;
        using var process = Process.Start(new ProcessStartInfo(_trayExe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(_trayExe)! });
        return process is not null;
    }
}

internal static class Updater
{
    /// <summary>
    /// The updater copy's entry point (--apply). Refused unless this process is RST.TrayUpdater.exe in the updater
    /// folder: the installed RST.Tray.exe never installs over its own files. Returns the process exit code.
    /// </summary>
    public static int Run(TrayArguments args)
    {
        var self = Environment.ProcessPath ?? "";
        var expected = Path.Combine(TrayPaths.UpdaterDir, TrayPaths.UpdaterExeName);
        if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning("{Argument} ignored: only {Updater} applies an update (this is {Self})", TrayPaths.ApplyArgument, expected, self);
            return SelfUpdateApply.Refused;
        }
        Log.Information("RST updater (pid {Pid}) applying {Msi}", Environment.ProcessId, args.ApplyMsi);
        var paths = new InstallPaths(TrayPaths.CacheDir, TrayPaths.InstallLogsDir, TrayPaths.SelfUpdateResultFile);
        var code = SelfUpdateApply.RunAsync(args.ApplyMsi!, args.WaitPid, paths, new MsiDatabase(), new Msiexec(), new UpdaterHost(),
                                            TimeProvider.System, m => Log.Information("{Message}", m)).GetAwaiter().GetResult();
        Log.Information("RST updater exits {Code}", code);
        return code;
    }
}
