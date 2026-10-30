// rst-c's own (not vendored): F17 WU2 review N1. Revit can run inside a job object that ends every process in it
// when the job closes (JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE: a deployment launcher, an app-virtualisation wrapper). The
// tray Revit starts must outlive that. This models it on a real Windows runtime: a PowerShell process is put in such
// a job (breakaway allowed), loads RST.Engine and runs the engine's own TrayProcessStart with a stand-in for the tray
// (PING.EXE, which runs for about a minute). Then the job is closed, which ends PowerShell and everything still in
// the job; the stand-in must still run. Only where this process's own job forbids a breakaway and there is no desktop
// shell (some CI runners) can the case not be modelled; the test then says so and checks nothing more.

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Xunit;
using Xunit.Abstractions;

namespace RST.RuntimeTests.Engine;

public class TrayJobTests
{
    private readonly ITestOutputHelper _output;

    public TrayJobTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// The observation, to the test output and to tray-job-route.txt beside the screenshots (CI uploads it with
    /// RST-screenshots), so whether the seat could model the job case is on record either way.
    /// </summary>
    private void Note(string line)
    {
        _output.WriteLine(line);
        try
        {
            Directory.CreateDirectory(RST.RuntimeTests.Support.Screenshots.Folder);
            File.AppendAllText(Path.Combine(RST.RuntimeTests.Support.Screenshots.Folder, "tray-job-route.txt"),
                $"net{Environment.Version.Major}: {line}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // The note is evidence, not the test.
        }
    }

    private static string? Pwsh()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "pwsh.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry.
            }
        }
        return null;
    }

    [Fact]
    public void A_tray_started_from_a_job_that_kills_its_processes_on_close_survives_the_close()
    {
        // PowerShell 7 runs on .NET 8 or later: it can load the net8 engine (the R25 leg), not the net10 one.
        if (Environment.Version.Major != 8)
        {
            Note("not modelled: net8 leg only (PowerShell 7 cannot load a net10 RST.Engine)");
            return;
        }
        var pwsh = Pwsh();
        if (pwsh is null)
        {
            Note("not modelled: no pwsh.exe on PATH");
            return;
        }

        var work = Path.Combine(Path.GetTempPath(), "rst-job-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var go = Path.Combine(work, "go");
        var result = Path.Combine(work, "result.txt");
        var engine = typeof(RST.Engine.Tray.TrayProcessStart).Assembly.Location;
        var ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");
        var marker = 47 + Random.Shared.Next(0, 10); // a ping count no other test uses
        var script = Path.Combine(work, "start.ps1");
        File.WriteAllText(script, $$"""
            $ErrorActionPreference = 'Stop'
            while (-not (Test-Path '{{go}}')) { Start-Sleep -Milliseconds 50 }
            $t = [Reflection.Assembly]::LoadFrom('{{engine}}').GetType('RST.Engine.Tray.TrayProcessStart', $true)
            $m = $t.GetMethod('Start', [Reflection.BindingFlags]'Static,Public,NonPublic')
            $r = $m.Invoke($null, @('{{ping}}', '-n {{marker}} 127.0.0.1'))
            $inJob = $t.GetMethod('InJob', [Reflection.BindingFlags]'Static,Public,NonPublic').Invoke($null, @())
            $route = $r.GetType().GetProperty('Route').GetValue($r)
            $id = $r.GetType().GetProperty('ProcessId').GetValue($r)
            "$route|$id|$inJob" | Set-Content -Path '{{result}}' -Encoding UTF8
            Start-Sleep -Seconds 60
            """);

        var job = CreateJobObject(IntPtr.Zero, null);
        Assert.NotEqual(IntPtr.Zero, job);
        var started = DateTime.Now;
        Process? shell = null;
        var jobOpen = true;
        try
        {
            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = KillOnJobClose | BreakawayOk },
            };
            Assert.True(SetInformationJobObject(job, ExtendedLimitInformation, ref limits, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()),
                "SetInformationJobObject: " + Marshal.GetLastWin32Error());

            shell = Process.Start(new ProcessStartInfo(pwsh, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            Assert.True(AssignProcessToJobObject(job, shell.Handle), "AssignProcessToJobObject: " + Marshal.GetLastWin32Error());
            File.WriteAllText(go, "");

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (!File.Exists(result) && DateTime.UtcNow < deadline && !shell.HasExited) Thread.Sleep(100);
            Assert.True(File.Exists(result), "PowerShell did not run the start (exited " + (shell.HasExited ? shell.ExitCode.ToString() : "no") + ")");
            Thread.Sleep(200);
            var parts = File.ReadAllText(result).Trim().Split('|');
            var route = parts[0];
            Note($"route: {route}; pid: {parts[1]}; PowerShell in a job: {parts[2]}");
            Assert.Equal("True", parts[2]);

            // Close the job: KILL_ON_JOB_CLOSE ends PowerShell and anything still in the job.
            CloseHandle(job);
            jobOpen = false;
            Assert.True(shell.WaitForExit(10_000), "the job's close did not end PowerShell");
            Thread.Sleep(1500);

            if (route.StartsWith("the shell, inside Revit's job", StringComparison.Ordinal))
            {
                Note("not modelled: this seat allows no breakaway and has no desktop shell");
                return;
            }

            var survivors = Process.GetProcessesByName("PING").Where(p =>
            {
                try { return p.StartTime >= started.AddSeconds(-1) && !p.HasExited; }
                catch (Exception) { return false; }
            }).ToList();
            if (int.TryParse(parts[1], out var pid))
                Assert.Contains(survivors, p => p.Id == pid);
            else
                Assert.NotEmpty(survivors); // the desktop shell does not say which process it started
            Note($"modelled: the started process survived the job's close ({survivors.Count} running)");
            foreach (var p in survivors)
            {
                try { p.Kill(); }
                catch (Exception) { /* already gone */ }
            }
        }
        finally
        {
            if (jobOpen) CloseHandle(job);
            try { shell?.Kill(entireProcessTree: true); }
            catch (Exception) { /* already gone */ }
            try { Directory.Delete(work, recursive: true); }
            catch (IOException) { }
        }
    }

    private const uint KillOnJobClose = 0x2000;
    private const uint BreakawayOk = 0x0800;
    private const int ExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
