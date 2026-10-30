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
using RST.Tray.Shell;
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

    /// <summary>
    /// The tray runs from Program Files: a per-machine RST (RST-Machine.msi puts it in
    /// <see cref="TrayPaths.PerMachineInstallDir"/>), which this per-user tray never updates. Anywhere under Program Files
    /// counts, the broader and safer rule.
    /// </summary>
    public bool PerMachineTray => IsUnder(_sourceDir, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
                                  || IsUnder(_sourceDir, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));

    /// <summary>RST-Setup.exe installed RST: its per-user registration is present (decision #34).</summary>
    public bool InstalledByBundle => BundleRegistration.RstBundleInstalled(_log);

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

/// <summary>
/// Whether RST-Setup.exe installed RST for this user (decision #34): Burn registers a per-user bundle under
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\{bundle id}, with a BundleUpgradeCode value (REG_MULTI_SZ)
/// holding the bundle's UpgradeCode. A failed read counts as "not found" (logged): RST.msi then updates it.
/// </summary>
internal static class BundleRegistration
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    public static bool RstBundleInstalled(Action<string>? log = null)
    {
        var code = "{" + ProductCatalog.RstBundleUpgradeCode + "}";
        try
        {
            using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(UninstallKey);
            if (root is null) return false;
            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);
                var value = key?.GetValue("BundleUpgradeCode");
                var codes = value switch
                {
                    string[] many => many,
                    string one => new[] { one },
                    _ => Array.Empty<string>(),
                };
                if (codes.Any(c => string.Equals(c.Trim(), code, StringComparison.OrdinalIgnoreCase)))
                {
                    log?.Invoke($"self-update: RST-Setup.exe's registration found (HKCU\\{UninstallKey}\\{name})");
                    return true;
                }
            }
            return false;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            log?.Invoke("self-update: the bundle registration could not be read: " + ex.Message);
            return false;
        }
    }
}

/// <summary>
/// The updater holding the tray's single-instance mutex (<c>Local\RST.Tray.&lt;UserSid&gt;</c>, the same
/// TrayPaths.MutexName the tray claims) while it replaces RST's files. A Mutex belongs to the thread that took it,
/// and the updater's work is async, so one dedicated thread takes it, holds it, and releases it when disposed.
/// </summary>
internal sealed class TrayInstanceClaim : IDisposable
{
    private readonly ManualResetEventSlim _release = new();
    private Thread? _owner;
    private int _disposed;

    private TrayInstanceClaim()
    {
    }

    /// <summary>The claim, or null when the mutex was not free within <paramref name="timeout"/>.</summary>
    public static TrayInstanceClaim? TryClaim(string userSid, TimeSpan timeout)
    {
        var claim = new TrayInstanceClaim();
        var decided = new ManualResetEventSlim();
        var taken = false;
        var name = TrayPaths.MutexName(userSid);
        claim._owner = new Thread(() =>
        {
            using var mutex = new Mutex(false, name);
            try
            {
                taken = mutex.WaitOne(timeout);
            }
            catch (AbandonedMutexException)
            {
                taken = true; // the old tray ended without releasing it: ours now
            }
            decided.Set();
            if (!taken) return;
            claim._release.Wait();
            mutex.ReleaseMutex();
        })
        { IsBackground = true, Name = "RST tray instance claim" };
        claim._owner.Start();
        decided.Wait();
        if (taken) return claim;
        claim._owner.Join();
        return null;
    }

    /// <summary>Releases the mutex (and waits until it is released, so a tray started next can take it).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _release.Set();
        _owner?.Join(TimeSpan.FromSeconds(10));
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

    public IDisposable? ClaimTrayInstance(TimeSpan timeout) => TrayInstanceClaim.TryClaim(SingleInstance.CurrentUserSid(), timeout);

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
        var code = SelfUpdateApply.RunAsync(args.ApplyMsi!, args.WaitPid, paths, new MsiDatabase(), new BundleReader(), new Msiexec(),
                                            new RevitWatch(log: m => Log.Information("{Message}", m)), new UpdaterHost(),
                                            TimeProvider.System, m => Log.Information("{Message}", m)).GetAwaiter().GetResult();
        Log.Information("RST updater exits {Code}", code);
        return code;
    }
}
