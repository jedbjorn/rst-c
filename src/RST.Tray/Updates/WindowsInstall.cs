// rst-c's own (not vendored): the Windows half of the tray's installs (spec #20 §5; F17 WU4):
//
//   Msiexec     System32's msiexec.exe only, /i <msi> /qn /norestart /l*v <log>, with Restart Manager disabled
//               (decision #73: advise closing Revit, never close it for the user). Waited on by ITS OWN process
//               handle: no redirected output (whose end-of-stream would wait for every child) and no job object.
//               Windows Installer's Restart Manager starts the RST tray again as msiexec's child at the end of an
//               install (WU1's hang notes), and that child must never hold the wait.

using System.Diagnostics;
using System.IO;
using RST.Core.Updates;

namespace RST.Tray.Updates;

internal sealed class Msiexec : IMsiexec
{
    /// <summary>%SystemRoot%\System32\msiexec.exe (the tray is a 64-bit process, so this is never SysWOW64).</summary>
    public static string ExePath => Path.Combine(Environment.SystemDirectory, "msiexec.exe");

    internal static ProcessStartInfo InstallStartInfo(string msiPath, string logPath)
    {
        var start = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.SystemDirectory,
        };
        foreach (var arg in new[] { "/i", msiPath, "/qn", "/norestart", "MSIRESTARTMANAGERCONTROL=Disable", "/l*v", logPath })
            start.ArgumentList.Add(arg);
        return start;
    }

    public async Task<int> InstallAsync(string msiPath, string logPath, CancellationToken cancel)
    {
        using var process = Process.Start(InstallStartInfo(msiPath, logPath)) ?? throw new InvalidOperationException("msiexec did not start");
        await process.WaitForExitAsync(cancel).ConfigureAwait(false);
        return process.ExitCode;
    }

    /// <summary>
    /// RST-Setup.exe /quiet /norestart RstRestartManagerControl=Disable /log &lt;log&gt;: per user, no companion switches, so the bundle
    /// installs RST only (companions it installed before are Permanent and stay). Burn's own process is waited on by
    /// its handle; it returns when its engine has finished.
    /// </summary>
    internal static ProcessStartInfo BundleStartInfo(string exePath, string logPath)
    {
        var start = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
        };
        foreach (var arg in new[] { "/quiet", "/norestart", "RstRestartManagerControl=Disable", "/log", logPath })
            start.ArgumentList.Add(arg);
        return start;
    }

    public async Task<int> RunBundleAsync(string exePath, string logPath, CancellationToken cancel)
    {
        using var process = Process.Start(BundleStartInfo(exePath, logPath)) ?? throw new InvalidOperationException("RST-Setup.exe did not start");
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
