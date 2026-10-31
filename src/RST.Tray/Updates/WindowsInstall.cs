// rst-c's own (not vendored): the Windows half of the tray's installs (spec #20 §5; F17 WU4):
//
//   Msiexec     System32's msiexec.exe only, /i <msi> /qn /norestart /l*v <log>. It is waited on by ITS OWN process
//               handle: no redirected output (whose end-of-stream would wait for every child) and no job object.
//               Windows Installer's Restart Manager starts the RST tray again as msiexec's child at the end of an
//               install (WU1's hang notes), and that child must never hold the wait.
//   RevitWatch  Revit.exe processes in THIS user's session; waiting means awaiting their exit (each process's own
//               exit event), then looking again, until none runs. No polling, except for a Revit this user's tray
//               cannot open (an elevated Revit), which is looked for again every 15 seconds while an install waits.

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using RST.Core.Updates;

namespace RST.Tray.Updates;

internal sealed class Msiexec : IMsiexec
{
    /// <summary>%SystemRoot%\System32\msiexec.exe (the tray is a 64-bit process, so this is never SysWOW64).</summary>
    public static string ExePath => Path.Combine(Environment.SystemDirectory, "msiexec.exe");

    public async Task<int> InstallAsync(string msiPath, string logPath, CancellationToken cancel)
    {
        var start = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.SystemDirectory,
        };
        foreach (var arg in new[] { "/i", msiPath, "/qn", "/norestart", "/l*v", logPath })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("msiexec did not start");
        await process.WaitForExitAsync(cancel).ConfigureAwait(false);
        return process.ExitCode;
    }

    /// <summary>
    /// RST-Setup.exe /quiet /norestart /log &lt;log&gt; (decision #34): per user, no companion switches, so the bundle
    /// installs RST only (companions it installed before are Permanent and stay). Burn's own process is waited on by
    /// its handle; it returns when its engine has finished.
    /// </summary>
    public async Task<int> RunBundleAsync(string exePath, string logPath, CancellationToken cancel)
    {
        var start = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
        };
        foreach (var arg in new[] { "/quiet", "/norestart", "/log", logPath })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("RST-Setup.exe did not start");
        await process.WaitForExitAsync(cancel).ConfigureAwait(false);
        return process.ExitCode;
    }
}

/// <summary>RST-Setup.exe's version resource (Burn writes the bundle's Version and Name into it).</summary>
internal sealed class BundleReader : IBundleReader
{
    public BundleIdentity Read(string exePath)
    {
        var info = FileVersionInfo.GetVersionInfo(exePath);
        var version = info.FileMajorPart == 0 && info.FileMinorPart == 0 && info.FileBuildPart == 0 && info.FilePrivatePart == 0
            ? info.FileVersion
            : $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}.{info.FilePrivatePart}";
        return new BundleIdentity(version, info.ProductName);
    }
}

/// <summary>
/// Why <see cref="RevitWatch"/> waits for Revit, for its log lines: an install waiting to run, or the open window
/// watching to refresh its "… when Revit closes" buttons, which is not an install (SC-043 follow-up).
/// </summary>
internal sealed record RevitWaitPurpose(string Area, Func<int, string> Waiting)
{
    public static readonly RevitWaitPurpose Install = new("install", n => $"waiting for {n} Revit process(es) to exit");

    public static readonly RevitWaitPurpose Window = new("window", n => $"watching {n} Revit process(es) to refresh the Install buttons");
}

internal sealed class RevitWatch : IRevitWatch
{
    private static readonly TimeSpan UnopenableRecheck = TimeSpan.FromSeconds(15);
    private readonly string _processName;
    private readonly Action<string> _log;

    /// <param name="processName">Revit.exe's process name, "Revit" (tests use a stand-in).</param>
    public RevitWatch(string processName = "Revit", Action<string>? log = null)
    {
        _processName = processName;
        _log = log ?? (_ => { });
    }

    public bool AnyRunning()
    {
        var found = Find();
        foreach (var p in found) p.Dispose();
        return found.Count > 0;
    }

    /// <summary>The install's wait (IRevitWatch): its log lines start "install: waiting for …".</summary>
    public Task WaitUntilNoneAsync(CancellationToken cancel) => WaitUntilNoneAsync(RevitWaitPurpose.Install, cancel);

    /// <summary>The same wait, logged for <paramref name="purpose"/> (the window's watch is not an install).</summary>
    public async Task WaitUntilNoneAsync(RevitWaitPurpose purpose, CancellationToken cancel)
    {
        while (true)
        {
            var running = Find();
            if (running.Count == 0) return;
            try
            {
                _log($"{purpose.Area}: {purpose.Waiting(running.Count)}: {string.Join(", ", running.Select(p => p.Id))}");
                await Task.WhenAll(running.Select(p => WaitAsync(p, purpose, cancel))).ConfigureAwait(false);
            }
            finally
            {
                foreach (var p in running) p.Dispose();
            }
        }
    }

    private async Task WaitAsync(Process process, RevitWaitPurpose purpose, CancellationToken cancel)
    {
        try
        {
            await process.WaitForExitAsync(cancel).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // Not ours to open (an elevated Revit): look again until it is gone.
            _log($"{purpose.Area}: cannot watch Revit pid {process.Id} ({ex.Message}); checking every {UnopenableRecheck.TotalSeconds:0} s");
            var id = process.Id;
            while (Exists(id))
                await Task.Delay(UnopenableRecheck, cancel).ConfigureAwait(false);
        }
    }

    private static bool Exists(int id)
    {
        try
        {
            using var p = Process.GetProcessById(id);
            return !p.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return ex is Win32Exception; // can't ask: assume it still runs; ArgumentException: it is gone
        }
    }

    private List<Process> Find()
    {
        var session = Process.GetCurrentProcess().SessionId;
        var found = new List<Process>();
        foreach (var p in Process.GetProcessesByName(_processName))
        {
            int pSession;
            try
            {
                pSession = p.SessionId;
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                p.Dispose();
                continue;
            }
            if (pSession == session) found.Add(p);
            else p.Dispose();
        }
        return found;
    }
}
