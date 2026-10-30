// rst-c's own: the WU4 review fixes on Linux (F17 WU4, review of PR #147; decision #34). One install queue (B1),
// the updater's Revit check and 1618 retries (B1, B2), RST updated the way it was installed (B3), the re-checks right
// before an install runs (N1), a failing Revit check (N3), the updater's refusals shown (N4), the coordinator's
// edge cases (N6), and self-update accepting only RST's per-user UpgradeCode (N8).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using RST.Core.Updates;
using Xunit;

namespace RST.Tests.Updates;

public sealed class InstallQueueTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly ReleaseVersion V110 = new(1, 1, 0), V120 = new(1, 2, 0), V311 = new(3, 1, 1);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RST-Queue-" + Guid.NewGuid().ToString("N"));
    private readonly ProductInfo _gantt = ProductCatalog.Default.Find("gantt-it")!;
    private readonly ProductInfo _splash = ProductCatalog.Default.Find("splash-it")!;
    private readonly ProductInfo _rst = ProductCatalog.Default.Rst;
    private readonly FakeGitHub _github = new();
    private readonly FakeInstalledProducts _installed = new();
    private readonly FakeMsiexec _msiexec = new();
    private readonly FakeRevit _revit = new();
    private readonly FakeSelfUpdater _self = new();
    private readonly ManualTimeProvider _time = new(Start);
    private readonly List<string> _log = new();
    private readonly List<string> _events = new();

    public InstallQueueTests()
    {
        _github.Add(_gantt.Repo, "v1.2.0", "GanttIt-1.2.0.msi", FakeMsi.Bytes(_gantt.UpgradeCodes[0], "1.2.0"));
        _github.Add(_splash.Repo, "v1.1.0", "SplashIt-1.1.0.msi", FakeMsi.Bytes(_splash.UpgradeCodes[0], "1.1.0"));
        _github.Add(_rst.Repo, "v3.1.1", "RST.msi", FakeMsi.Bytes(ProductCatalog.RstUpgradeCode, "3.1.1"));
        _installed.With(ProductCatalog.RstUpgradeCode, "3.1.0").With(_gantt.UpgradeCodes[0], "1.1.0");
        _msiexec.OnRun = (msi, code) => { lock (_events) _events.Add("msiexec " + Path.GetFileName(msi) + " " + code); };
    }

    private InstallPaths Paths => new(Path.Combine(_root, "cache"), Path.Combine(_root, "logs"), Path.Combine(_root, "selfupdate.json"));

    private InstallCoordinator Make() => new(
        ProductCatalog.Default.Products, new HttpClient(_github), _installed, FakeMsiReader.FromText(), new FakeBundleReader(), _msiexec,
        _revit, _self, Paths, _time, action => action(), _log.Add, () => { lock (_events) _events.Add("quit"); }, () => { });

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        condition().Should().BeTrue("the coordinator should reach that state");
    }

    private void ServeBundle(string fileVersion)
    {
        var release = _github.Release("v3.1.1");
        release.Asset = "RST-Setup.exe";
        release.Bytes = FakeMsi.Bundle(fileVersion);
        release.Sums = $"{FakeReleaseData.Sha256(release.Bytes)}  RST-Setup.exe\n";
        release.Digest = "sha256:" + FakeReleaseData.Sha256(release.Bytes);
    }

    [Fact]
    public async Task B1_RST_and_a_companion_both_waiting_for_Revit_the_companion_installs_and_is_recorded_before_the_hand_off()
    {
        _revit.Open();
        using var c = Make();
        var rst = c.InstallAsync(_rst, V311);
        var gantt = c.InstallAsync(_gantt, V120);
        await WaitFor(() => c.For("rst").Phase is InstallPhase.WaitingForRevit or InstallPhase.Queued
                            && c.For("gantt-it").Phase == InstallPhase.WaitingForRevit);
        _revit.Close();
        await Task.WhenAll(rst, gantt);

        _events.Should().Equal("msiexec GanttIt-1.2.0.msi 0", "quit");
        c.For("gantt-it").Phase.Should().Be(InstallPhase.Installed, "the companion's result is recorded before the tray quits");
        InstallCache.ReadState(Paths.CacheRoot, "gantt-it").Installed!.Version.Should().Be("1.2.0");
        _self.Started.Should().ContainSingle();
        c.For("rst").Phase.Should().Be(InstallPhase.HandingOff);
    }

    [Fact]
    public async Task B1_RST_shows_Queued_while_another_install_is_under_way_and_goes_last()
    {
        _revit.Open();
        using var c = Make();
        var splash = c.InstallAsync(_splash, V110);
        await WaitFor(() => c.For("splash-it").Phase == InstallPhase.WaitingForRevit);
        var rst = c.InstallAsync(_rst, V311);
        await WaitFor(() => c.For("rst").Phase is InstallPhase.WaitingForRevit or InstallPhase.Queued);
        _revit.Close();
        await Task.WhenAll(splash, rst);
        _events.Should().Equal("msiexec SplashIt-1.1.0.msi 0", "quit");
        _log.Should().Contain(l => l.Contains("RST 3.1.1 waits until the other installs have finished"));
    }

    [Fact]
    public async Task B1_a_click_on_a_companion_after_the_hand_off_has_begun_is_not_started()
    {
        using var c = Make();
        await c.InstallAsync(_rst, V311);
        await c.InstallAsync(_gantt, V120);
        _msiexec.Calls.Should().BeEmpty();
        _log.Should().Contain(l => l.Contains("not started: RST is updating itself"));
    }

    [Fact]
    public async Task B1_installs_take_turns_and_the_waiting_one_shows_Queued()
    {
        using var c = Make();
        Task? splash = null;
        var seen = InstallPhase.Idle;
        _msiexec.BeforeRun = msi =>
        {
            if (!Path.GetFileName(msi).StartsWith("GanttIt", StringComparison.Ordinal)) return;
            // While Gantt-It's msiexec "runs", Splash-It is asked for and must wait its turn.
            splash = Task.Run(() => c.InstallAsync(_splash, V110));
            for (var i = 0; i < 300 && c.For("splash-it").Phase != InstallPhase.Queued; i++) Thread.Sleep(10);
            seen = c.For("splash-it").Phase;
        };
        await c.InstallAsync(_gantt, V120);
        await splash!;
        seen.Should().Be(InstallPhase.Queued);
        _events.Should().Equal("msiexec GanttIt-1.2.0.msi 0", "msiexec SplashIt-1.1.0.msi 0");
    }

    [Fact]
    public async Task B3_RST_installed_by_the_bundle_is_updated_with_RST_Setup_exe()
    {
        _self.InstalledByBundle = true;
        ServeBundle("3.1.1.0");
        using var c = Make();
        await c.InstallAsync(_rst, V311);
        _self.Started.Should().ContainSingle().Which.Should().EndWith(Path.Combine("rst", "3.1.1", "RST-Setup.exe"));
        _github.Requests.Should().Contain(u => u.AbsolutePath.EndsWith("/releases/download/v3.1.1/RST-Setup.exe", StringComparison.Ordinal));
        _github.Requests.Should().NotContain(u => u.AbsolutePath.EndsWith("/RST.msi", StringComparison.Ordinal));
        _log.Should().Contain(l => l.Contains("installed by RST-Setup.exe"));
    }

    [Fact]
    public async Task B3_RST_installed_by_RST_msi_keeps_updating_with_RST_msi()
    {
        using var c = Make();
        await c.InstallAsync(_rst, V311);
        _self.Started.Should().ContainSingle().Which.Should().EndWith(Path.Combine("rst", "3.1.1", "RST.msi"));
        _log.Should().Contain(l => l.Contains("installed by RST.msi"));
    }

    [Fact]
    public async Task B3_a_bundle_whose_version_resource_is_not_the_release_is_refused()
    {
        _self.InstalledByBundle = true;
        ServeBundle("3.1.0.0");
        using var c = Make();
        await c.InstallAsync(_rst, V311);
        c.For("rst").Should().Match<InstallProgress>(p => p.Phase == InstallPhase.Refused && p.Problem == InstallProblem.WrongVersion);
        _self.Started.Should().BeEmpty();
    }

    [Theory]
    [InlineData("3.1.1.0", true)]
    [InlineData("3.1.1", true)]
    [InlineData("3.1.1.4", false)]
    [InlineData("3.1.2.0", false)]
    [InlineData(null, false)]
    public void B3_the_bundle_version_resource_must_be_the_release(string? fileVersion, bool ok) =>
        InstallChecks.BundleIdentity(V311, new BundleIdentity(fileVersion, "RST")).Passed.Should().Be(ok);

    [Fact]
    public void B3_the_bundle_asset_name_is_exactly_RST_Setup_exe()
    {
        InstallChecks.AssetName(_rst, V311, PackageKind.Bundle, out _).Should().Be("RST-Setup.exe");
        InstallChecks.AssetName(_gantt, V120, PackageKind.Bundle, out var refusal).Should().BeNull();
        refusal.Problem.Should().Be(InstallProblem.NotInstallable);
    }

    [Fact]
    public async Task N1_a_file_changed_after_verification_never_reaches_msiexec()
    {
        _revit.Open();
        using var c = Make();
        var install = c.InstallAsync(_gantt, V120);
        await WaitFor(() => c.For("gantt-it").Phase == InstallPhase.WaitingForRevit);
        File.AppendAllText(Path.Combine(Paths.CacheRoot, "gantt-it", "1.2.0", "GanttIt-1.2.0.msi"), "swapped while it waited");
        _revit.Close();
        await install;
        c.For("gantt-it").Should().Match<InstallProgress>(p => p.Phase == InstallPhase.Refused && p.Problem == InstallProblem.HashMismatch);
        _msiexec.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task N1_check_4_runs_again_right_before_msiexec()
    {
        _revit.Open();
        using var c = Make();
        var install = c.InstallAsync(_gantt, V120);
        await WaitFor(() => c.For("gantt-it").Phase == InstallPhase.WaitingForRevit);
        _installed.ByUpgradeCode.Remove(_gantt.UpgradeCodes[0]);
        _installed.With(_gantt.UpgradeCodes[0], "1.3.0"); // installed some other way meanwhile
        _revit.Close();
        await install;
        c.For("gantt-it").Problem.Should().Be(InstallProblem.NotNewer);
        _msiexec.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task N1_the_MSI_is_held_against_writes_while_msiexec_runs()
    {
        using var c = Make();
        Exception? write = null;
        _msiexec.BeforeRun = msi =>
        {
            try { File.AppendAllText(msi, "x"); }
            catch (IOException ex) { write = ex; }
        };
        await c.InstallAsync(_gantt, V120);
        // FileShare is enforced by Windows only (RuntimeTests HeldPackageTests proves it there).
        if (OperatingSystem.IsWindows()) write.Should().NotBeNull("nothing may write the MSI while it is being installed");
        c.For("gantt-it").Phase.Should().Be(InstallPhase.Installed);
    }

    [Fact]
    public async Task N3_a_failing_Revit_check_defers_instead_of_installing()
    {
        _revit.Throws = true;
        using var c = Make();
        c.RevitRunning.Should().BeTrue("a check that fails means Revit may be running");
        var install = c.InstallAsync(_gantt, V120);
        await WaitFor(() => c.For("gantt-it").Phase == InstallPhase.WaitingForRevit && _time.ArmedTimers > 0);
        _msiexec.Calls.Should().BeEmpty();
        _revit.Throws = false;
        _time.Advance(InstallCoordinator.RevitCheckRetry);
        await install;
        _msiexec.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task N6_when_detection_fails_after_a_failed_upgrade_the_reinstall_stays_on_offer()
    {
        _msiexec.Then(1603);
        _msiexec.OnRun = (_, code) => { if (code == 1603) _installed.Unreadable = true; };
        using var c = Make();
        await c.InstallAsync(_gantt, V120);
        c.For("gantt-it").Should().Match<InstallProgress>(p => p.Phase == InstallPhase.Failed && p.Previous == V110);
    }

    [Fact]
    public void N8_self_update_accepts_only_the_per_user_UpgradeCode()
    {
        const string machineCode = "11111111-2222-3333-4444-555555555555";
        var rstBoth = _rst with { UpgradeCodes = new[] { ProductCatalog.RstUpgradeCode, machineCode } };
        InstallChecks.IdentityOf(rstBoth, V311, WriteFake(FakeMsi.Bytes(machineCode, "3.1.1")), FakeMsiReader.FromText(), null)
            .Problem.Should().Be(InstallProblem.WrongUpgradeCode, "RST-Machine.msi's code is never accepted for self-update");
        InstallChecks.IdentityOf(rstBoth, V311, WriteFake(FakeMsi.Bytes(ProductCatalog.RstUpgradeCode, "3.1.1")), FakeMsiReader.FromText(), null)
            .Passed.Should().BeTrue();
    }

    [Fact]
    public async Task B2_a_self_update_handed_back_for_Revit_waits_here_and_hands_over_again_from_the_cache()
    {
        using (var first = Make()) await first.InstallAsync(_rst, V311); // verified into the cache, handed over
        _self.Started.Clear();
        var requests = _github.Count;
        Directory.CreateDirectory(_root);
        File.WriteAllText(Paths.SelfUpdateResultFile, new SelfUpdateResult("3.1.1", null, InstallProblem.RevitRunning, null, _time.GetUtcNow()).ToJson());
        _revit.Open();
        using var c = Make();
        var resume = c.ResumeDeferredSelfUpdateAsync();
        await WaitFor(() => c.For("rst").Phase == InstallPhase.WaitingForRevit);
        _self.Started.Should().BeEmpty();
        _revit.Close();
        await resume;
        _self.Started.Should().ContainSingle();
        _github.Count.Should().Be(requests, "the cached, re-checked package is used");
    }

    [Fact]
    public void N4_a_refusal_by_the_updater_is_shown_by_the_restarted_tray()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Paths.SelfUpdateResultFile, new SelfUpdateResult("3.1.1", null, InstallProblem.HashMismatch, null, _time.GetUtcNow()).ToJson());
        using var c = Make();
        c.For("rst").Should().Match<InstallProgress>(p => p.Phase == InstallPhase.Refused && p.Problem == InstallProblem.HashMismatch
                                                          && p.Version == V311);
    }

    [Fact]
    public async Task N2_a_download_redirected_back_to_github_com_is_refused()
    {
        var release = _github.Release("v1.2.0");
        release.RedirectHost = "github.com";
        var result = await new VerifiedDownload(new HttpClient(_github), FakeMsiReader.FromText(), Path.Combine(_root, "cache"), _log.Add)
            .RunAsync(_gantt, V120, V110, CancellationToken.None);
        result.Problem.Should().Be(InstallProblem.RedirectNotAllowed);
    }

    private string WriteFake(byte[] bytes)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".msi");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}

/// <summary>The updater copy's re-checks and its result (AC8's logic; the real self-update runs on Windows CI).</summary>
public sealed class SelfUpdateApplyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RST-SelfUpdate-" + Guid.NewGuid().ToString("N"));
    private readonly FakeMsiexec _msiexec = new();
    private readonly FakeRevit _revit = new();
    private readonly List<string> _log = new();
    private readonly FakeHost _host = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero));

    private sealed class FakeHost : ISelfUpdateHost
    {
        public List<int> Waited { get; } = new();

        public int TraysStarted { get; private set; }

        /// <summary>What happened, in order: "claim", "release", "start" (Planner, from #149's review).</summary>
        public List<string> Events { get; } = new();

        /// <summary>The runner's calls see whether the mutex is held while they run.</summary>
        public bool Held { get; private set; }

        /// <summary>False: the mutex is not free in time (a tray still holds it), so the claim comes back pending (SC-022).</summary>
        public bool ClaimFree { get; set; } = true;

        /// <summary>The pending claim takes the mutex now: msiexec's Restart Manager has closed the tray (SC-022).</summary>
        public void TrayLetGo()
        {
            if (Held) return;
            Held = true;
            Events.Add("taken late");
        }

        public bool WaitForTrayExit(int pid, TimeSpan timeout)
        {
            Waited.Add(pid);
            return true;
        }

        public bool StartTray()
        {
            TraysStarted++;
            Events.Add("start");
            return true;
        }

        public ITrayInstanceClaim? ClaimTrayInstance(TimeSpan timeout)
        {
            Events.Add("claim");
            Held = ClaimFree;
            return new Claim(this);
        }

        private sealed class Claim(FakeHost host) : ITrayInstanceClaim
        {
            private bool? _released;

            public bool Held => _released is null && host.Held;

            public bool Release()
            {
                if (_released is { } was) return was;
                _released = host.Held;
                host.Events.Add(host.Held ? "release" : "stop waiting");
                host.Held = false;
                return _released.Value;
            }

            public void Dispose() => Release();
        }
    }

    [Fact]
    public async Task The_updater_holds_the_tray_mutex_while_it_installs_and_hands_it_over_before_starting_the_tray()
    {
        var heldDuringRun = false;
        _msiexec.BeforeRun = _ => heldDuringRun = _host.Held;
        (await Apply(Verified())).Should().Be(SelfUpdateApply.Installed);
        heldDuringRun.Should().BeTrue("a tray Revit starts during the update must find one running and exit");
        _host.Events.Should().Equal("claim", "release", "start");
    }

    [Fact]
    public async Task A_tray_that_does_not_let_go_in_time_is_waited_for_and_the_mutex_taken_once_msiexec_closes_it()
    {
        // SC-022: the tray still runs (it was never asked to quit); msiexec's Restart Manager closes it, and the claim
        // takes the mutex at that moment, so the hand-over is the same as when the tray quit in time.
        _host.ClaimFree = false;
        _msiexec.BeforeRun = _ => _host.TrayLetGo();
        (await Apply(Verified())).Should().Be(SelfUpdateApply.Installed);
        _host.Events.Should().Equal("claim", "taken late", "release", "start");
        _log.Should().Contain(l => l.Contains("was not free within 60 s; going on, and taking it the moment the tray lets it go"));
        _log.Should().Contain("updater: released the tray's single-instance mutex");
        _log.Should().NotContain(l => l.Contains("going on without it"));
    }

    [Fact]
    public async Task A_claim_that_never_gets_the_mutex_stops_waiting_and_the_started_tray_defers_to_the_running_one()
    {
        // SC-022: a tray holds the mutex throughout. The updater still starts the installed tray: that launch's first act
        // is the single-instance check, so it brings the running tray's window forward and exits; never two trays.
        _host.ClaimFree = false;
        (await Apply(Verified())).Should().Be(SelfUpdateApply.Installed);
        _host.Events.Should().Equal("claim", "stop waiting", "start");
        _host.TraysStarted.Should().Be(1);
        _log.Should().Contain(l => l.Contains("stopped waiting for the tray's single-instance mutex"));
        _log.Should().NotContain("updater: released the tray's single-instance mutex");
    }

    [Fact]
    public async Task The_mutex_is_handed_over_on_the_deferral_path_too()
    {
        _revit.Open();
        (await Apply(Verified())).Should().Be(SelfUpdateApply.DeferredForRevit);
        _host.Events.Should().Equal("claim", "release", "start");
    }

    private InstallPaths Paths => new(Path.Combine(_root, "cache"), Path.Combine(_root, "logs"), Path.Combine(_root, "selfupdate.json"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>RST's package (RST.msi or RST-Setup.exe) verified in the cache, as the tray leaves it.</summary>
    private string Verified(string version = "3.1.1", string? upgradeCode = null, string asset = "RST.msi", byte[]? bytes = null)
    {
        var dir = Path.Combine(Paths.CacheRoot, "rst", version);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, asset);
        File.WriteAllBytes(path, bytes ?? FakeMsi.Bytes(upgradeCode ?? ProductCatalog.RstUpgradeCode, version));
        InstallCache.WriteVerified(path, new VerifiedRecord("rst", version, asset, InstallCache.Sha256Of(path), DateTimeOffset.UtcNow));
        return path;
    }

    private Task<int> Apply(string path, int? pid = 4242, TimeProvider? time = null) =>
        SelfUpdateApply.RunAsync(path, pid, Paths, FakeMsiReader.FromText(), new FakeBundleReader(), _msiexec, _revit, _host,
                                 time ?? TimeProvider.System, _log.Add);

    private SelfUpdateResult Result() => SelfUpdateResult.TryParse(File.ReadAllText(Paths.SelfUpdateResultFile))!;

    private InstallCoordinator Tray() => new(ProductCatalog.Default.Products, new HttpClient(new FakeGitHub()), new FakeInstalledProducts(),
        FakeMsiReader.FromText(), new FakeBundleReader(), _msiexec, new FakeRevit(), new FakeSelfUpdater(), Paths, TimeProvider.System,
        a => a(), _log.Add, () => { }, () => { });

    private async Task AdvanceThroughRetries(int runs)
    {
        for (var i = 1; i < runs; i++)
        {
            for (var w = 0; w < 500 && !(_msiexec.Calls.Count == i && _time.ArmedTimers > 0); w++) await Task.Delay(10);
            _time.Advance(InstallCoordinator.BusyRetryDelay);
        }
    }

    [Fact]
    public async Task It_waits_for_the_tray_installs_records_and_starts_the_new_tray()
    {
        var msi = Verified();
        (await Apply(msi)).Should().Be(SelfUpdateApply.Installed);
        _host.Waited.Should().Equal(4242);
        _msiexec.Calls.Should().ContainSingle().Which.Msi.Should().Be(msi);
        _host.TraysStarted.Should().Be(1);
        InstallCache.ReadState(Paths.CacheRoot, "rst").Installed!.Version.Should().Be("3.1.1");

        using var tray = Tray(); // the new tray shows the result once
        tray.For("rst").Should().Match<InstallProgress>(p => p.Phase == InstallPhase.Installed && p.Version == new ReleaseVersion(3, 1, 1));
        File.Exists(Paths.SelfUpdateResultFile).Should().BeFalse("shown once");
    }

    [Fact]
    public async Task B3_a_bundle_package_is_run_as_the_bundle()
    {
        var exe = Verified(asset: "RST-Setup.exe", bytes: FakeMsi.Bundle("3.1.1.0"));
        (await Apply(exe)).Should().Be(SelfUpdateApply.Installed);
        _msiexec.Calls.Should().ContainSingle().Which.Msi.Should().Be(exe);
        _log.Should().Contain(l => l.Contains("/quiet /norestart /log"));
    }

    [Fact]
    public async Task B3_a_bundle_with_another_version_resource_is_refused()
    {
        var exe = Verified(asset: "RST-Setup.exe", bytes: FakeMsi.Bundle("3.0.0.0"));
        (await Apply(exe)).Should().Be(SelfUpdateApply.Refused);
        _msiexec.Calls.Should().BeEmpty();
        Result().Problem.Should().Be(InstallProblem.WrongVersion);
    }

    [Fact]
    public async Task B2_Revit_open_means_nothing_is_installed_and_the_old_tray_takes_the_wait_back()
    {
        _revit.Open();
        (await Apply(Verified())).Should().Be(SelfUpdateApply.DeferredForRevit);
        _msiexec.Calls.Should().BeEmpty();
        _host.TraysStarted.Should().Be(1, "the old tray is started again");
        Result().Problem.Should().Be(InstallProblem.RevitRunning);
        _log.Should().Contain(l => l.Contains("Revit is running"));
    }

    [Fact]
    public async Task B2_a_failing_Revit_check_also_installs_nothing()
    {
        _revit.Throws = true;
        (await Apply(Verified())).Should().Be(SelfUpdateApply.DeferredForRevit);
        _msiexec.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task B1_1618_is_tried_again_after_three_minutes()
    {
        _msiexec.Then(1618, 1618, 0);
        var run = Apply(Verified(), time: _time);
        await AdvanceThroughRetries(3);
        (await run).Should().Be(SelfUpdateApply.Installed);
        _msiexec.Calls.Should().HaveCount(3);
    }

    [Fact]
    public async Task B1_1618_three_times_is_reported_as_another_installation_running()
    {
        _msiexec.Then(1618, 1618, 1618);
        var run = Apply(Verified(), time: _time);
        await AdvanceThroughRetries(3);
        (await run).Should().Be(SelfUpdateApply.InstallFailed);
        _msiexec.Calls.Should().HaveCount(3);
        using var tray = Tray();
        tray.For("rst").Problem.Should().Be(InstallProblem.InstallerBusy);
    }

    [Fact]
    public async Task A_failed_install_is_recorded_and_the_tray_still_starts()
    {
        _msiexec.Then(1603);
        (await Apply(Verified())).Should().Be(SelfUpdateApply.InstallFailed);
        _host.TraysStarted.Should().Be(1);
        Result().ExitCode.Should().Be(1603);
    }

    [Fact]
    public async Task N1_the_package_is_held_against_writes_while_it_runs()
    {
        Exception? write = null;
        _msiexec.BeforeRun = path =>
        {
            try { File.AppendAllText(path, "x"); }
            catch (IOException ex) { write = ex; }
        };
        (await Apply(Verified())).Should().Be(SelfUpdateApply.Installed);
        if (OperatingSystem.IsWindows()) write.Should().NotBeNull();
    }

    [Fact]
    public async Task It_installs_nothing_it_was_not_given_by_the_tray_and_says_so()
    {
        var outside = Path.Combine(_root, "elsewhere", "3.1.1", "RST.msi");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.Copy(Verified(), outside);
        (await Apply(outside)).Should().Be(SelfUpdateApply.Refused);

        var companion = Path.Combine(Paths.CacheRoot, "rst", "3.1.1", "GanttIt-1.2.0.msi");
        File.WriteAllText(companion, "x");
        (await Apply(companion)).Should().Be(SelfUpdateApply.Refused);

        var tampered = Verified("3.1.2");
        File.AppendAllText(tampered, "changed after it was verified");
        (await Apply(tampered)).Should().Be(SelfUpdateApply.Refused);
        Result().Problem.Should().Be(InstallProblem.HashMismatch, "review N4: the refusal is recorded for the tray to show");

        var other = Verified("3.1.3", upgradeCode: "644FCAD5-E631-4441-9A11-75F175565136");
        (await Apply(other)).Should().Be(SelfUpdateApply.Refused);
        Result().Problem.Should().Be(InstallProblem.WrongUpgradeCode);
        using (var tray = Tray())
            tray.For("rst").Should().Match<InstallProgress>(p => p.Phase == InstallPhase.Refused && p.Problem == InstallProblem.WrongUpgradeCode);

        _msiexec.Calls.Should().BeEmpty();
        _host.TraysStarted.Should().Be(4, "the old tray is started again whatever happens");
        _log.Count(l => l.StartsWith("updater: refused", StringComparison.Ordinal)).Should().Be(4);
    }
}
