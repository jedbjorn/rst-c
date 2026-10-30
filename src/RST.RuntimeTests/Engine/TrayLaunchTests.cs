// rst-c's own (not vendored): spec #20 §2 (F17 WU2), the Revit add-in starting the RST tray, on a real Windows
// runtime in every Revit leg (net8 for 2025/2026, net10 for 2027). The decisions run over a scripted environment:
// the mutex present or absent, Task Manager's Startup toggle, an elevated Revit, the fixed paths missing, a start
// that fails. The Windows reads run for real: a named mutex in this session, a StartupApproved value under HKCU (a
// scratch value name, never RST's own), the fixed paths, and a shell start that must not wait for the process.

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using RST.Core.Tray;
using RST.Engine.Tray;
using Xunit;

namespace RST.RuntimeTests.Engine;

/// <summary>A scripted Windows: what the launcher reads, and what it started.</summary>
internal sealed class FakeLaunchEnvironment : ITrayLaunchEnvironment
{
    public const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    public string UserSid { get; set; } = Sid;

    public bool IsElevated { get; set; }

    public HashSet<string> Mutexes { get; } = new(StringComparer.Ordinal);

    public byte[]? UserApproved { get; set; }

    public byte[]? MachineApproved { get; set; }

    public IReadOnlyList<TrayCandidate> Candidates { get; set; } =
    [
        new(@"C:\Users\modeller\AppData\Local\Programs\RST\Tray\RST.Tray.exe", PerMachine: false),
        new(@"C:\Program Files\RST\Tray\RST.Tray.exe", PerMachine: true),
    ];

    public IReadOnlyList<string> CandidatePaths => Candidates.Select(c => c.Path).ToList();

    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Exception? StartThrows { get; set; }

    public List<(string Path, string Arguments)> Started { get; } = [];

    public bool MutexExists(string name) => Mutexes.Contains(name);

    public byte[]? StartupApprovedValue(bool perMachine) => perMachine ? MachineApproved : UserApproved;

    public bool FileExists(string path) => Files.Contains(path);

    public string Start(string path, string arguments)
    {
        if (StartThrows is not null) throw StartThrows;
        Started.Add((path, arguments));
        return "scripted";
    }
}

public class TrayLaunchDecisionTests
{
    private static readonly byte[] Enabled = [0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] Disabled = [0x03, 0, 0, 0, 0x5c, 0x1e, 0x3b, 0x8f, 0x6d, 0x4a, 0xdb, 0x01];

    private static FakeLaunchEnvironment Installed(bool perUser = true, bool perMachine = false)
    {
        var env = new FakeLaunchEnvironment();
        if (perUser) env.Files.Add(env.CandidatePaths[0]);
        if (perMachine) env.Files.Add(env.CandidatePaths[1]);
        return env;
    }

    [Fact]
    public void Not_running_it_is_started_hidden_from_the_per_user_path_and_not_waited_for()
    {
        var env = Installed();

        var result = TrayLauncher.EnsureRunning(env);

        Assert.Equal(TrayLaunchOutcome.Started, result.Outcome);
        Assert.Equal(new[] { (env.CandidatePaths[0], "--startup") }, env.Started);
        Assert.Equal(env.CandidatePaths[0], result.Path);
        Assert.Contains("started the RST tray", result.Message);
    }

    [Fact]
    public void The_mutex_of_this_user_means_it_runs_and_nothing_is_started()
    {
        var env = Installed();
        env.Mutexes.Add(TrayPaths.MutexName(FakeLaunchEnvironment.Sid));

        Assert.Equal(TrayLaunchOutcome.AlreadyRunning, TrayLauncher.EnsureRunning(env).Outcome);
        Assert.Empty(env.Started);

        // Another user's tray in this session is not this user's.
        env.Mutexes.Clear();
        env.Mutexes.Add(TrayPaths.MutexName("S-1-5-21-9-9-9-1002"));
        Assert.Equal(TrayLaunchOutcome.Started, TrayLauncher.EnsureRunning(env).Outcome);
    }

    [Fact]
    public void Task_Managers_Startup_toggle_off_for_the_found_install_means_no_start()
    {
        var env = Installed();
        env.UserApproved = Disabled; // RST.msi's HKCU Run value
        var result = TrayLauncher.EnsureRunning(env);
        Assert.Equal(TrayLaunchOutcome.StartupDisabled, result.Outcome);
        Assert.Contains("Task Manager", result.Message);
        Assert.Empty(env.Started);

        env.UserApproved = Enabled;
        Assert.Equal(TrayLaunchOutcome.Started, TrayLauncher.EnsureRunning(env).Outcome);

        var machine = Installed(perUser: false, perMachine: true);
        machine.MachineApproved = Disabled; // RST-Machine.msi's HKLM Run value
        Assert.Equal(TrayLaunchOutcome.StartupDisabled, TrayLauncher.EnsureRunning(machine).Outcome);
        Assert.Empty(machine.Started);
    }

    [Fact]
    public void Only_the_found_installs_hive_counts_so_a_leftover_toggle_does_not_block_the_other_install()
    {
        // Review N3: a per-user install the user disabled leaves HKCU's value behind after its uninstall; the shared
        // tray RST-Machine.msi installed later has its own HKLM entry, enabled.
        var machine = Installed(perUser: false, perMachine: true);
        machine.UserApproved = Disabled;
        Assert.Equal(TrayLaunchOutcome.Started, TrayLauncher.EnsureRunning(machine).Outcome);
        Assert.Equal(machine.CandidatePaths[1], machine.Started.Single().Path);

        var user = Installed();
        user.MachineApproved = Disabled;
        Assert.Equal(TrayLaunchOutcome.Started, TrayLauncher.EnsureRunning(user).Outcome);
    }

    [Fact]
    public void An_elevated_Revit_never_starts_the_tray()
    {
        var env = Installed();
        env.IsElevated = true;

        var result = TrayLauncher.EnsureRunning(env);

        Assert.Equal(TrayLaunchOutcome.Elevated, result.Outcome);
        Assert.Contains("never started elevated", result.Message);
        Assert.Empty(env.Started);
    }

    [Fact]
    public void Only_the_fixed_paths_are_tried_per_user_first()
    {
        var both = Installed(perUser: true, perMachine: true);
        TrayLauncher.EnsureRunning(both);
        Assert.Equal(both.CandidatePaths[0], both.Started.Single().Path);

        var machine = Installed(perUser: false, perMachine: true);
        TrayLauncher.EnsureRunning(machine);
        Assert.Equal(machine.CandidatePaths[1], machine.Started.Single().Path);
    }

    [Fact]
    public void A_path_that_does_not_exist_is_not_installed_and_nothing_starts()
    {
        var env = Installed(perUser: false);

        var result = TrayLauncher.EnsureRunning(env);

        Assert.Equal(TrayLaunchOutcome.NotInstalled, result.Outcome);
        Assert.Contains(env.CandidatePaths[0], result.Message);
        Assert.Contains(env.CandidatePaths[1], result.Message);
        Assert.Empty(env.Started);
    }

    [Fact]
    public void A_start_that_fails_is_one_message_and_never_an_exception()
    {
        var env = Installed();
        env.StartThrows = new Win32Exception(1260, "This program is blocked by group policy.");

        var result = TrayLauncher.EnsureRunning(env);

        Assert.Equal(TrayLaunchOutcome.Failed, result.Outcome);
        Assert.Contains(env.CandidatePaths[0], result.Message);
        Assert.Contains("blocked by group policy", result.Message);

        var noSid = Installed();
        noSid.UserSid = @"..\Global";
        Assert.Equal(TrayLaunchOutcome.Failed, TrayLauncher.EnsureRunning(noSid).Outcome);
        Assert.Empty(noSid.Started);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(new byte[0], false)]
    [InlineData(new byte[] { 0x02, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 0x06, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 0x03, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x07, 0, 0, 0 }, true)]
    public void StartupApproved_is_off_when_the_first_byte_is_odd(byte[]? value, bool disabled) =>
        Assert.Equal(disabled, StartupApproved.IsDisabled(value));

    [Fact]
    public void The_start_says_how_it_was_started()
    {
        var env = Installed();
        Assert.EndsWith("--startup (scripted)", TrayLauncher.EnsureRunning(env).Message);
    }
}

public class TrayLaunchWindowsTests
{
    [Fact]
    public void A_named_mutex_in_this_session_is_seen_and_gone_once_released()
    {
        var env = new MachineTrayLaunchEnvironment();
        var name = TrayPaths.MutexName(env.UserSid).Replace("RST.Tray.", "RST.Tray.Test." + Guid.NewGuid().ToString("N") + ".", StringComparison.Ordinal);

        Assert.False(env.MutexExists(name));
        using (var held = new Mutex(true, name))
            Assert.True(env.MutexExists(name));
        Assert.False(env.MutexExists(name));
    }

    [Fact]
    public void Task_Managers_value_is_read_from_HKCU()
    {
        var scratch = "RST.Test." + Guid.NewGuid().ToString("N");
        var env = new MachineTrayLaunchEnvironment(runValueName: scratch);
        Assert.Null(env.StartupApprovedValue(perMachine: false));
        Assert.Null(env.StartupApprovedValue(perMachine: true));
        using var key = Registry.CurrentUser.CreateSubKey(StartupApproved.RunKey);
        try
        {
            key.SetValue(scratch, new byte[] { 0x03, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 }, RegistryValueKind.Binary);
            Assert.True(StartupApproved.IsDisabled(env.StartupApprovedValue(perMachine: false)));
            Assert.Null(env.StartupApprovedValue(perMachine: true)); // HKCU's value is not HKLM's
            key.SetValue(scratch, new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
            Assert.False(StartupApproved.IsDisabled(env.StartupApprovedValue(perMachine: false)));
        }
        finally
        {
            key.DeleteValue(scratch, throwOnMissingValue: false);
        }
    }

    [Fact]
    public void The_fixed_paths_are_the_per_user_then_the_per_machine_install()
    {
        var paths = MachineTrayLaunchEnvironment.FixedPaths();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.Equal(new[]
        {
            new TrayCandidate(Path.Combine(local, "Programs", "RST", "Tray", "RST.Tray.exe"), PerMachine: false),
            new TrayCandidate(Path.Combine(programFiles, "RST", "Tray", "RST.Tray.exe"), PerMachine: true),
        }, paths);
        Assert.Equal(paths, new MachineTrayLaunchEnvironment().Candidates);
        Assert.Equal(Path.Combine(programFiles, "RST", "Tray"), TrayLauncher.PerMachineInstallDir);
    }

    [Fact]
    public void With_no_tray_at_the_paths_nothing_starts_and_nothing_throws()
    {
        var nowhere = Path.Combine(Path.GetTempPath(), "rst-no-tray-" + Guid.NewGuid().ToString("N"), "RST.Tray.exe");
        var env = new MachineTrayLaunchEnvironment([new TrayCandidate(nowhere, PerMachine: false)], runValueName: "RST.Test." + Guid.NewGuid().ToString("N"));

        var running = env.MutexExists(TrayPaths.MutexName(env.UserSid));

        var result = TrayLauncher.EnsureRunning(env);

        // The path is looked for before anything else is read (a seat with a real tray running stops at its mutex).
        Assert.Equal(running ? TrayLaunchOutcome.AlreadyRunning : TrayLaunchOutcome.NotInstalled, result.Outcome);
    }

    [Fact]
    public void Elevated_means_a_full_token_under_UAC()
    {
        // TOKEN_ELEVATION_TYPE: 1 Default (UAC off, or a standard user), 2 Full (run as administrator), 3 Limited.
        var type = MachineTrayLaunchEnvironment.ElevationType();
        Assert.InRange(type, 1, 3);
        Assert.Equal(type == 2, new MachineTrayLaunchEnvironment().IsElevated);
        if (type == 2) Assert.True(Environment.IsPrivilegedProcess);
    }

    [Fact]
    public void The_shell_start_returns_without_waiting_for_the_process()
    {
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var env = new MachineTrayLaunchEnvironment();
        var sw = Stopwatch.StartNew();

        var route = env.Start(cmd, "/c ping -n 6 127.0.0.1 >nul");

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"Start waited {sw.Elapsed.TotalSeconds:0.0} s");
        Assert.False(string.IsNullOrEmpty(route));
    }
}
