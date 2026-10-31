// rst-c's own: the RST tray's verified installs on Linux (spec #20 §5, §6; AC6, AC7, AC8's logic; F17 WU4): the
// built URLs and asset names, the four checks with every refusal, the install coordinator (Revit deferral, 1618
// retries, a failed upgrade and "Reinstall previous version", per-machine installs, the self-update hand-off) and
// the updater's own re-checks. RST.RuntimeTests repeats the install half with the real msi.dll, msiexec and the
// fixture companion MSI on Windows CI.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using RST.Core.Tray;
using RST.Core.Updates;
using Xunit;

namespace RST.Tests.Updates;

public class InstallNamingTests
{
    private static readonly ReleaseVersion V120 = new(1, 2, 0);

    [Fact]
    public void Each_product_has_its_hard_coded_asset_name()
    {
        var catalog = ProductCatalog.Default;
        InstallChecks.AssetName(catalog.Rst, new ReleaseVersion(3, 1, 1), out var r1).Should().Be("RST.msi");
        r1.Passed.Should().BeTrue();
        InstallChecks.AssetName(catalog.Find("gantt-it")!, V120, out _).Should().Be("GanttIt-1.2.0.msi");
        InstallChecks.AssetName(catalog.Find("splash-it")!, V120, out _).Should().Be("SplashIt-1.2.0.msi");
        catalog.Products.Should().OnlyContain(p => p.IsInstallable);
    }

    [Theory]
    [InlineData("GanttIt-1.1.0.msi", "1.1.0", "GanttIt-{0}.msi")]
    [InlineData("RST-CompanionFixture-2.0.0.msi", "2.0.0", "RST-CompanionFixture-{0}.msi")]
    [InlineData("GanttIt-1.1.0.msi", "1.2.0", null)]
    [InlineData("GanttIt_1.1.0.msi", "1.1.0", null)]
    [InlineData("../GanttIt-1.1.0.msi", "1.1.0", null)]
    [InlineData("GanttIt-1.1.0.exe", "1.1.0", null)]
    [InlineData(null, "1.1.0", null)]
    public void A_companion_asset_template_comes_from_its_pinned_name(string? asset, string version, string? template) =>
        ProductCatalog.AssetTemplateFrom(asset, version).Should().Be(template);

    [Fact]
    public void A_name_that_does_not_match_the_hard_coded_pattern_is_refused()
    {
        var gantt = ProductCatalog.Default.Find("gantt-it")! with { AssetTemplate = "Gantt-It-{0}.msi" };
        InstallChecks.AssetName(gantt, V120, out var refusal).Should().BeNull();
        refusal.Problem.Should().Be(InstallProblem.AssetNameBad);
        var none = gantt with { AssetPattern = null };
        InstallChecks.AssetName(none, V120, out var r2).Should().BeNull();
        r2.Problem.Should().Be(InstallProblem.NotInstallable);
    }

    [Fact]
    public void Urls_are_built_from_the_repo_tag_and_name()
    {
        InstallChecks.DownloadUri("jedbjorn/gantt-it", V120, "GanttIt-1.2.0.msi").AbsoluteUri
            .Should().Be("https://github.com/jedbjorn/gantt-it/releases/download/v1.2.0/GanttIt-1.2.0.msi");
        InstallChecks.ReleaseApiUri("jedbjorn/gantt-it", V120).AbsoluteUri
            .Should().Be("https://api.github.com/repos/jedbjorn/gantt-it/releases/tags/v1.2.0");
        FluentActions.Invoking(() => InstallChecks.DownloadUri("jedbjorn/gantt-it", V120, "../x.msi")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => InstallChecks.DownloadUri("not a repo", V120, "x.msi")).Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("", null, true)]
    [InlineData("2", "1", true)]
    [InlineData("1", null, false)]
    [InlineData("2", null, false)]
    [InlineData("2", "0", false)]
    public void Per_user_means_no_ALLUSERS_or_2_with_MSIINSTALLPERUSER(string? allUsers, string? perUser, bool ok)
    {
        var gantt = ProductCatalog.Default.Find("gantt-it")!;
        var check = InstallChecks.Identity(gantt, V120, new MsiIdentity(gantt.UpgradeCodes[0], "1.2.0", null, allUsers, perUser));
        check.Passed.Should().Be(ok);
        if (!ok) check.Problem.Should().Be(InstallProblem.WrongScope);
    }

    [Fact]
    public void The_identity_must_be_the_products_code_and_the_tags_version()
    {
        var gantt = ProductCatalog.Default.Find("gantt-it")!;
        InstallChecks.Identity(gantt, V120, new MsiIdentity("{" + gantt.UpgradeCodes[0].ToLowerInvariant() + "}", "1.2.0", null, null, null))
            .Passed.Should().BeTrue("braces and case are Windows Installer's, not identity");
        InstallChecks.Identity(gantt, V120, new MsiIdentity(ProductCatalog.RstUpgradeCode, "1.2.0", null, null, null))
            .Problem.Should().Be(InstallProblem.WrongUpgradeCode);
        InstallChecks.Identity(gantt, V120, new MsiIdentity(gantt.UpgradeCodes[0], "1.2.1", null, null, null))
            .Problem.Should().Be(InstallProblem.WrongVersion);
        InstallChecks.Identity(gantt, V120, new MsiIdentity(gantt.UpgradeCodes[0], "1.2.0.0", null, null, null))
            .Problem.Should().Be(InstallProblem.WrongVersion);
        InstallChecks.Identity(gantt, V120, new MsiIdentity(null, "1.2.0", null, null, null))
            .Problem.Should().Be(InstallProblem.WrongUpgradeCode);
    }

    [Fact]
    public void Newer_means_strictly_newer()
    {
        InstallChecks.Newer(V120, null).Passed.Should().BeTrue();
        InstallChecks.Newer(V120, new ReleaseVersion(1, 1, 9)).Passed.Should().BeTrue();
        InstallChecks.Newer(V120, V120).Problem.Should().Be(InstallProblem.NotNewer);
        InstallChecks.Newer(V120, new ReleaseVersion(2, 0, 0)).Problem.Should().Be(InstallProblem.NotNewer);
    }

    [Fact]
    public void Network_failures_are_classified_for_plain_wording()
    {
        NetFailures.FromStatus(407).Should().Be(NetFailure.ProxyRefused);
        NetFailures.FromStatus(429).Should().Be(NetFailure.RateLimited);
        NetFailures.FromStatus(503).Should().Be(NetFailure.HttpStatus);
        NetFailures.FromException(new HttpRequestException("No such host is known.")).Should().Be(NetFailure.Offline);
        NetFailures.FromException(new HttpRequestException("x", null, HttpStatusCode.ProxyAuthenticationRequired)).Should().Be(NetFailure.ProxyRefused);
        NetFailures.FromException(new InvalidOperationException()).Should().Be(NetFailure.Unexpected);
        ReleaseFeed.ParseLatestRedirect("jedbjorn/rst-c", HttpStatusCode.ProxyAuthenticationRequired, null,
                                        new Uri("https://github.com/jedbjorn/rst-c/releases/latest"))
            .Failure.Should().Be(NetFailure.ProxyRefused);
    }

    [Fact]
    public void The_updater_arguments_parse()
    {
        var args = TrayArguments.Parse(new[] { "--apply", @"C:\x\RST.msi", "--wait-pid", "4242" });
        args.IsApply.Should().BeTrue();
        args.ApplyMsi.Should().Be(@"C:\x\RST.msi");
        args.WaitPid.Should().Be(4242);
        args.Unknown.Should().BeEmpty();
        TrayArguments.Parse(new[] { "--apply" }).IsApply.Should().BeFalse();
        TrayArguments.Parse(new[] { "--wait-pid", "-3" }).WaitPid.Should().BeNull();
        TrayArguments.Parse(new[] { "--startup" }).IsApply.Should().BeFalse();
    }
}

/// <summary>The four checks against a fake GitHub, every refusal included (AC6's logic; the real MSI half runs on Windows CI).</summary>
public sealed class VerifiedDownloadTests : IDisposable
{
    private const string Repo = "jedbjorn/gantt-it";
    private static readonly ReleaseVersion V110 = new(1, 1, 0), V120 = new(1, 2, 0);
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "RST-Verify-" + Guid.NewGuid().ToString("N"));
    private readonly ProductInfo _gantt = ProductCatalog.Default.Find("gantt-it")!;
    private readonly FakeGitHub _github = new();
    private readonly List<string> _log = new();

    public void Dispose()
    {
        if (Directory.Exists(_cache)) Directory.Delete(_cache, recursive: true);
    }

    private FakeReleaseData Release(string version = "1.2.0", string? upgradeCode = null, string? allUsers = null) =>
        _github.Add(Repo, "v" + version, $"GanttIt-{version}.msi", FakeMsi.Bytes(upgradeCode ?? _gantt.UpgradeCodes[0], version, allUsers));

    private Task<VerifyResult> Run(ReleaseVersion version, ReleaseVersion? installed = null, ProductInfo? product = null) =>
        new VerifiedDownload(new HttpClient(_github), FakeMsiReader.FromText(), _cache, _log.Add)
            .RunAsync(product ?? _gantt, version, installed, CancellationToken.None);

    private string MsiPath(ReleaseVersion v) => Path.Combine(_cache, "gantt-it", v.ToString(), $"GanttIt-{v}.msi");

    private void AssertRefused(VerifyResult result, InstallProblem problem)
    {
        result.Ok.Should().BeFalse();
        result.Problem.Should().Be(problem);
        result.Detail.Should().NotBeNullOrEmpty();
        _log.Should().Contain(l => l.StartsWith($"refused Gantt-It ", StringComparison.Ordinal) && l.Contains(problem.ToString()),
            "each refusal is logged with its reason");
        File.Exists(MsiPath(V120)).Should().BeFalse("nothing unverified stays in the cache");
    }

    [Fact]
    public async Task A_good_release_is_verified_through_the_allowlist_only()
    {
        Release();
        var result = await Run(V120, installed: V110);
        result.Ok.Should().BeTrue(result.Detail);
        result.Msi!.Path.Should().Be(MsiPath(V120));
        File.Exists(MsiPath(V120)).Should().BeTrue();
        File.Exists(MsiPath(V120) + ".download").Should().BeFalse();
        InstallCache.ReadVerified(MsiPath(V120))!.Sha256.Should().Be(result.Msi.Sha256);
        _github.Requests.Select(u => u.Host).Should().Equal(
            "github.com", "release-assets.githubusercontent.com",   // SHA256SUMS.txt and its redirect
            "api.github.com",                                       // the one API call
            "github.com", "release-assets.githubusercontent.com");  // the MSI and its redirect
        _github.Requests.Should().OnlyContain(u => u.Scheme == "https" && UpdateHosts.All.Contains(u.Host));
        _github.Requests[2].AbsoluteUri.Should().Be("https://api.github.com/repos/jedbjorn/gantt-it/releases/tags/v1.2.0");
    }

    [Fact]
    public async Task The_download_logs_the_release_tag_and_the_asset_url_but_never_a_signed_redirect()
    {
        // SC-015: support can tell from the tray log what an install or self-update fetched.
        Release();
        (await Run(V120, installed: V110)).Ok.Should().BeTrue();
        _log.Should().Contain(
            "download: Gantt-It 1.2.0 (release v1.2.0): https://github.com/jedbjorn/gantt-it/releases/download/v1.2.0/GanttIt-1.2.0.msi, "
            + "checksums https://github.com/jedbjorn/gantt-it/releases/download/v1.2.0/SHA256SUMS.txt");
        _log.Should().NotContain(l => l.Contains("sig=") || l.Contains("githubusercontent"),
            "the redirect's signed URL is the CDN's, and stays out of the log");
    }

    [Fact]
    public void The_download_line_cuts_each_url_to_its_path()
    {
        var line = VerifiedDownload.DownloadLine(_gantt, V120,
            new Uri("https://github.com/jedbjorn/gantt-it/releases/download/v1.2.0/GanttIt-1.2.0.msi?token=secret#x"),
            new Uri("https://github.com/jedbjorn/gantt-it/releases/download/v1.2.0/SHA256SUMS.txt?token=secret"));
        line.Should().NotContain("secret").And.NotContain("?").And.NotContain("#");
        line.Should().StartWith("download: Gantt-It 1.2.0 (release v1.2.0): https://github.com/");
    }

    [Fact]
    public async Task A_wrong_hash_is_refused()
    {
        var r = Release();
        r.Bytes = FakeMsi.Bytes(_gantt.UpgradeCodes[0], "1.2.0", salt: "tampered"); // sums and digest agree; the file differs
        AssertRefused(await Run(V120), InstallProblem.HashMismatch);
    }

    [Fact]
    public async Task A_hash_missing_from_SHA256SUMS_is_refused()
    {
        var r = Release();
        r.Sums = new string('a', 64) + "  RST.msi\n";
        AssertRefused(await Run(V120), InstallProblem.NotInSums);
        _github.Requests.Should().NotContain(u => u.AbsolutePath.EndsWith(".msi", StringComparison.Ordinal), "no download");
    }

    [Fact]
    public async Task A_digest_mismatch_is_refused_before_the_download()
    {
        var r = Release();
        r.Digest = "sha256:" + new string('b', 64);
        AssertRefused(await Run(V120), InstallProblem.DigestMismatch);
        _github.Requests.Should().NotContain(u => u.AbsolutePath.EndsWith(".msi", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_missing_digest_is_refused()
    {
        var r = Release();
        r.Digest = null;
        AssertRefused(await Run(V120), InstallProblem.DigestMissing);
    }

    [Fact]
    public async Task A_wrong_UpgradeCode_is_refused()
    {
        Release(upgradeCode: ProductCatalog.RstUpgradeCode);
        AssertRefused(await Run(V120), InstallProblem.WrongUpgradeCode);
    }

    [Fact]
    public async Task A_per_machine_MSI_is_refused()
    {
        Release(allUsers: "1");
        AssertRefused(await Run(V120), InstallProblem.WrongScope);
    }

    [Fact]
    public async Task A_downgrade_is_refused_without_a_request()
    {
        Release("1.1.0");
        var result = await Run(V110, installed: V120);
        result.Problem.Should().Be(InstallProblem.NotNewer);
        (await Run(V120, installed: V120)).Problem.Should().Be(InstallProblem.NotNewer, "the same version is not reinstalled");
        _github.Count.Should().Be(0);
    }

    [Fact]
    public async Task A_redirect_off_the_allowlist_is_refused()
    {
        var r = Release();
        r.RedirectHost = "evil.example.com";
        AssertRefused(await Run(V120), InstallProblem.RedirectNotAllowed);
        _github.Requests.Should().NotContain(u => u.Host == "evil.example.com", "the redirect is checked before it is followed");
    }

    [Fact]
    public async Task A_redirect_to_another_github_host_is_refused_too()
    {
        var r = Release();
        r.RedirectHost = "raw.githubusercontent.com";
        AssertRefused(await Run(V120), InstallProblem.RedirectNotAllowed);
    }

    [Fact]
    public async Task A_bad_asset_name_is_refused_without_a_request()
    {
        Release();
        var renamed = _gantt with { AssetTemplate = "Gantt It {0}.msi" };
        var result = await Run(V120, product: renamed);
        result.Problem.Should().Be(InstallProblem.AssetNameBad);
        _github.Count.Should().Be(0);
    }

    [Fact]
    public async Task A_release_without_SHA256SUMS_is_refused()
    {
        var r = Release();
        r.Sums = null;
        AssertRefused(await Run(V120), InstallProblem.SumsMissing);
    }

    [Fact]
    public async Task A_damaged_SHA256SUMS_is_refused()
    {
        var r = Release();
        r.Sums = r.Sums!.ToUpperInvariant();
        AssertRefused(await Run(V120), InstallProblem.SumsUnreadable);
    }

    [Fact]
    public async Task A_draft_or_another_tag_from_the_API_is_refused()
    {
        var r = Release();
        r.Prerelease = true;
        AssertRefused(await Run(V120), InstallProblem.ReleaseNotFound);
        r.Prerelease = false;
        r.ApiTag = "v1.2.1";
        AssertRefused(await Run(V120), InstallProblem.ReleaseNotFound);
    }

    [Fact]
    public async Task A_wrong_ProductVersion_is_refused()
    {
        var r = Release();
        r.Bytes = FakeMsi.Bytes(_gantt.UpgradeCodes[0], "1.1.0");
        r.Sums = $"{FakeReleaseData.Sha256(r.Bytes)}  {r.Asset}\n";
        r.Digest = "sha256:" + FakeReleaseData.Sha256(r.Bytes);
        AssertRefused(await Run(V120), InstallProblem.WrongVersion);
    }

    [Fact]
    public async Task Something_that_is_not_an_MSI_is_refused()
    {
        var r = Release();
        r.Bytes = "<html>not an msi</html>"u8.ToArray();
        r.Sums = $"{FakeReleaseData.Sha256(r.Bytes)}  {r.Asset}\n";
        r.Digest = "sha256:" + FakeReleaseData.Sha256(r.Bytes);
        AssertRefused(await Run(V120), InstallProblem.MsiUnreadable);
    }

    [Fact]
    public async Task Offline_and_a_refusing_proxy_are_network_failures_with_their_kind()
    {
        Release();
        _github.Fail = new HttpRequestException("No such host is known. (github.com:443)");
        var offline = await Run(V120);
        offline.Problem.Should().Be(InstallProblem.Network);
        offline.Net.Should().Be(NetFailure.Offline);
        _github.Fail = null;
        _github.Status = HttpStatusCode.ProxyAuthenticationRequired;
        var proxy = await Run(V120);
        proxy.Net.Should().Be(NetFailure.ProxyRefused);
        proxy.Status.Should().Be(407);
    }

    [Fact]
    public async Task A_verified_MSI_already_in_the_cache_is_not_downloaded_again()
    {
        Release();
        (await Run(V120, V110)).Ok.Should().BeTrue();
        var before = _github.Count;
        (await Run(V120, V110)).Ok.Should().BeTrue();
        _github.Requests.Skip(before).Should().NotContain(u => u.AbsolutePath.EndsWith(".msi", StringComparison.Ordinal));
    }
}

/// <summary>The install coordinator: what happens after the checks (AC6, AC7 and AC8's logic).</summary>
public sealed class InstallCoordinatorTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RST-Install-" + Guid.NewGuid().ToString("N"));
    private readonly ProductInfo _gantt = ProductCatalog.Default.Find("gantt-it")!;
    private readonly ProductInfo _rst = ProductCatalog.Default.Rst;
    private readonly FakeGitHub _github = new();
    private readonly FakeInstalledProducts _installed = new();
    private readonly FakeMsiexec _msiexec = new();
    private readonly FakeRevit _revit = new();
    private readonly FakeSelfUpdater _self = new();
    private readonly ManualTimeProvider _time = new(Start);
    private readonly List<string> _log = new();
    private int _quits, _changes;

    public InstallCoordinatorTests()
    {
        foreach (var v in new[] { "1.1.0", "1.2.0" })
            _github.Add(_gantt.Repo, "v" + v, $"GanttIt-{v}.msi", FakeMsi.Bytes(_gantt.UpgradeCodes[0], v));
        _github.Add(_rst.Repo, "v3.1.1", "RST.msi", FakeMsi.Bytes(ProductCatalog.RstUpgradeCode, "3.1.1"));
        // msiexec "installs": exit 0 puts that MSI's version in the fake Windows Installer.
        _msiexec.OnRun = (msi, code) =>
        {
            if (code is not (0 or 3010)) return;
            var version = Path.GetFileName(Path.GetDirectoryName(msi))!;
            _installed.ByUpgradeCode.Remove(_gantt.UpgradeCodes[0]);
            _installed.With(_gantt.UpgradeCodes[0], version);
        };
    }

    private InstallPaths Paths => new(Path.Combine(_root, "cache"), Path.Combine(_root, "logs"), Path.Combine(_root, "selfupdate.json"));

    private InstallCoordinator Make() => new(
        ProductCatalog.Default.Products, new HttpClient(_github), _installed, FakeMsiReader.FromText(), new FakeBundleReader(), _msiexec, _revit, _self, Paths,
        _time, action => action(), _log.Add, () => _quits++, () => _changes++);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task A_verified_companion_is_installed_and_kept_as_the_previous_version()
    {
        _installed.With(_gantt.UpgradeCodes[0], "1.1.0");
        using var c = Make();
        await c.InstallAsync(_gantt, new ReleaseVersion(1, 2, 0));
        var p = c.For("gantt-it");
        p.Phase.Should().Be(InstallPhase.Installed);
        p.RestartNeeded.Should().BeFalse();
        var call = _msiexec.Calls.Should().ContainSingle().Subject;
        call.Msi.Should().EndWith(Path.Combine("gantt-it", "1.2.0", "GanttIt-1.2.0.msi"));
        Path.GetFileName(call.Log).Should().MatchRegex(@"^install-gantt-it-\d{8}-\d{6}\.log$");
        Path.GetDirectoryName(call.Log).Should().Be(Paths.LogsDir);
        InstallCache.ReadState(Paths.CacheRoot, "gantt-it").Installed!.Version.Should().Be("1.2.0");
        InstallCache.InstalledMsi(Paths.CacheRoot, "gantt-it", new ReleaseVersion(1, 2, 0)).Should().NotBeNull();
        _changes.Should().Be(1, "detection runs again after an install");
    }

    [Fact]
    public void A_second_install_in_the_same_second_keeps_the_first_log()
    {
        var at = new DateTimeOffset(2026, 10, 30, 9, 15, 0, TimeSpan.Zero);
        Directory.CreateDirectory(Paths.LogsDir);
        var first = Paths.LogFor("gantt-it", at);
        File.WriteAllText(first, "the failed install");
        var second = Paths.LogFor("gantt-it", at);
        second.Should().NotBe(first);
        Path.GetFileName(second).Should().MatchRegex(@"^install-gantt-it-\d{8}-\d{6}-2\.log$");
    }

    [Fact]
    public async Task Exit_3010_is_installed_with_a_restart_needed_and_never_a_restart()
    {
        _msiexec.Then(3010);
        using var c = Make();
        await c.InstallAsync(_gantt, new ReleaseVersion(1, 2, 0));
        c.For("gantt-it").Should().Match<InstallProgress>(p => p.Phase == InstallPhase.Installed && p.RestartNeeded && p.ExitCode == 3010);
    }

    [Fact]
    public async Task While_Revit_runs_it_waits_and_installs_when_Revit_closes()
    {
        _revit.Open();
        using var c = Make();
        c.RevitRunning.Should().BeTrue();
        var install = c.InstallAsync(_gantt, new ReleaseVersion(1, 2, 0));
        await WaitFor(() => c.For("gantt-it").Phase == InstallPhase.WaitingForRevit);
        _msiexec.Calls.Should().BeEmpty("never while Revit runs");
        _log.Should().Contain(l => l.Contains("installs when it closes"));
        _revit.Close();
        await install;
        _msiexec.Calls.Should().ContainSingle();
        c.For("gantt-it").Phase.Should().Be(InstallPhase.Installed);
    }

    [Fact]
    public async Task Cancel_while_waiting_for_Revit_installs_nothing()
    {
        _revit.Open();
        using var c = Make();
        var install = c.InstallAsync(_gantt, new ReleaseVersion(1, 2, 0));
        await WaitFor(() => c.For("gantt-it").Phase == InstallPhase.WaitingForRevit);
        c.Cancel("gantt-it");
        await install;
        c.For("gantt-it").Phase.Should().Be(InstallPhase.Idle);
        _revit.Close();
        _msiexec.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Exit_1618_is_tried_again_after_three_minutes()
    {
        _msiexec.Then(1618, 0);
        using var c = Make();
        var install = c.InstallAsync(_gantt, new ReleaseVersion(1, 2, 0));
        await WaitFor(() => c.For("gantt-it").Phase == InstallPhase.RetryLater && _time.ArmedTimers > 0);
        _msiexec.Calls.Should().HaveCount(1);
        _time.Advance(TimeSpan.FromMinutes(2));
        await Task.Delay(20);
        _msiexec.Calls.Should().HaveCount(1);
        _time.Advance(TimeSpan.FromMinutes(1));
        await install;
        _msiexec.Calls.Should().HaveCount(2);
        c.For("gantt-it").Phase.Should().Be(InstallPhase.Installed);
    }

    [Fact]
    public async Task Exit_1618_three_times_gives_up_with_a_plain_reason()
    {
        _msiexec.Then(1618, 1618, 1618);
        using var c = Make();
        var install = c.InstallAsync(_gantt, new ReleaseVersion(1, 2, 0));
        for (var i = 0; i < 2; i++)
        {
            await WaitFor(() => c.For("gantt-it").Phase == InstallPhase.RetryLater && _msiexec.Calls.Count == i + 1 && _time.ArmedTimers > 0);
            _time.Advance(InstallCoordinator.BusyRetryDelay);
        }
        await install;
        c.For("gantt-it").Should().Match<InstallProgress>(p => p.Phase == InstallPhase.Failed && p.Problem == InstallProblem.InstallerBusy);
    }

    [Fact]
    public async Task A_failed_upgrade_that_removed_the_old_version_offers_it_again_and_reinstalls_it_from_the_cache()
    {
        using (var first = Make())
            await first.InstallAsync(_gantt, new ReleaseVersion(1, 1, 0)); // installed by the tray: 1.1.0 cached
        _msiexec.Then(1603);
        _msiexec.OnRun = (msi, code) =>
        {
            if (code == 1603) _installed.ByUpgradeCode.Clear(); // afterInstallValidate: the old one went first
            else if (code == 0) _installed.With(_gantt.UpgradeCodes[0], Path.GetFileName(Path.GetDirectoryName(msi))!);
        };

        using (var c = Make())
        {
            await c.InstallAsync(_gantt, new ReleaseVersion(1, 2, 0));
            var failed = c.For("gantt-it");
            failed.Phase.Should().Be(InstallPhase.Failed);
            failed.Problem.Should().Be(InstallProblem.InstallFailed);
            failed.ExitCode.Should().Be(1603);
            failed.LogPath.Should().NotBeNull();
            failed.Previous.Should().Be(new ReleaseVersion(1, 1, 0));
        }

        // The offer survives a tray restart (cache\gantt-it\state.json).
        using var again = Make();
        again.For("gantt-it").Previous.Should().Be(new ReleaseVersion(1, 1, 0));
        var requests = _github.Count;
        await again.ReinstallPreviousAsync(_gantt);
        again.For("gantt-it").Should().Match<InstallProgress>(p => p.Phase == InstallPhase.Installed && p.Version == new ReleaseVersion(1, 1, 0));
        _github.Count.Should().Be(requests, "the previous MSI came from the cache");
        _msiexec.Calls.Last().Msi.Should().EndWith(Path.Combine("1.1.0", "GanttIt-1.1.0.msi"));
        InstallCache.ReadState(Paths.CacheRoot, "gantt-it").FailedUpgrade.Should().BeNull();
    }

    [Fact]
    public async Task Without_a_cached_MSI_the_previous_version_is_downloaded_and_verified()
    {
        _installed.With(_gantt.UpgradeCodes[0], "1.1.0"); // installed by the bundle, not the tray: nothing cached
        _msiexec.Then(1603);
        _msiexec.OnRun = (msi, code) =>
        {
            if (code == 1603) _installed.ByUpgradeCode.Clear();
            else _installed.With(_gantt.UpgradeCodes[0], Path.GetFileName(Path.GetDirectoryName(msi))!);
        };
        using var c = Make();
        await c.InstallAsync(_gantt, new ReleaseVersion(1, 2, 0));
        c.For("gantt-it").Previous.Should().Be(new ReleaseVersion(1, 1, 0));
        await c.ReinstallPreviousAsync(_gantt);
        c.For("gantt-it").Phase.Should().Be(InstallPhase.Installed);
        _github.Requests.Should().Contain(u => u.AbsoluteUri.EndsWith("/releases/download/v1.1.0/GanttIt-1.1.0.msi", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failure_that_left_the_old_version_installed_offers_no_reinstall()
    {
        _installed.With(_gantt.UpgradeCodes[0], "1.1.0");
        _msiexec.Then(1603);
        _msiexec.OnRun = null; // the old version stays
        using var c = Make();
        await c.InstallAsync(_gantt, new ReleaseVersion(1, 2, 0));
        c.For("gantt-it").Should().Match<InstallProgress>(p => p.Phase == InstallPhase.Failed && p.Previous == null);
    }

    [Fact]
    public async Task A_companion_installed_for_everyone_is_never_installed_over()
    {
        _installed.With(_gantt.UpgradeCodes[0], "1.1.0", InstallContext.Machine);
        using var c = Make();
        await c.InstallAsync(_gantt, new ReleaseVersion(1, 2, 0));
        c.For("gantt-it").Should().Match<InstallProgress>(p => p.Phase == InstallPhase.Refused && p.Problem == InstallProblem.PerMachine);
        _github.Count.Should().Be(0);
        _msiexec.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Unreadable_Windows_Installer_installs_nothing()
    {
        _installed.Unreadable = true;
        using var c = Make();
        await c.InstallAsync(_gantt, new ReleaseVersion(1, 2, 0));
        c.For("gantt-it").Problem.Should().Be(InstallProblem.DetectionFailed);
        _github.Count.Should().Be(0);
    }

    [Fact]
    public async Task A_refused_download_never_reaches_msiexec()
    {
        _github.Release("v1.2.0").Digest = "sha256:" + new string('c', 64);
        using var c = Make();
        await c.InstallAsync(_gantt, new ReleaseVersion(1, 2, 0));
        c.For("gantt-it").Should().Match<InstallProgress>(p => p.Phase == InstallPhase.Refused && p.Problem == InstallProblem.DigestMismatch);
        _msiexec.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task RST_hands_over_to_the_updater_after_Revit_closes_then_the_tray_quits()
    {
        _installed.With(ProductCatalog.RstUpgradeCode, "3.1.0");
        _revit.Open();
        using var c = Make();
        var install = c.InstallAsync(_rst, new ReleaseVersion(3, 1, 1));
        await WaitFor(() => c.For("rst").Phase == InstallPhase.WaitingForRevit);
        _self.Started.Should().BeEmpty();
        _revit.Close();
        await install;
        _self.Started.Should().ContainSingle().Which.Should().EndWith(Path.Combine("rst", "3.1.1", "RST.msi"));
        c.For("rst").Phase.Should().Be(InstallPhase.HandingOff);
        _quits.Should().Be(1);
        _msiexec.Calls.Should().BeEmpty("the updater copy runs msiexec, not the tray");
    }

    [Fact]
    public async Task A_per_machine_RST_is_never_updated_from_the_tray()
    {
        _installed.With(ProductCatalog.RstUpgradeCode, "3.1.0", InstallContext.Machine);
        using (var c = Make())
        {
            await c.InstallAsync(_rst, new ReleaseVersion(3, 1, 1));
            c.For("rst").Problem.Should().Be(InstallProblem.PerMachine);
        }
        _installed.ByUpgradeCode.Clear();
        _installed.With(ProductCatalog.RstUpgradeCode, "3.1.0");
        _self.PerMachineTray = true;
        using (var c = Make())
        {
            await c.InstallAsync(_rst, new ReleaseVersion(3, 1, 1));
            c.For("rst").Problem.Should().Be(InstallProblem.PerMachine);
        }
        _self.Started.Should().BeEmpty();
        _quits.Should().Be(0);
    }

    // F17 WU5 (WU4 review DN5): RST-Machine.msi under its own UpgradeCode is found through RstProduct, and the tray
    // shows the administrator message; self-update still accepts only the per-user UpgradeCode.
    [Fact]
    public async Task RST_Machine_found_by_its_own_UpgradeCode_is_never_updated_from_the_tray()
    {
        _rst.UpgradeCodes.Should().Contain(ProductCatalog.RstMachineUpgradeCode, "RST-Machine.msi is detected as RST");
        ProductCatalog.RstPerUserUpdate.UpgradeCodes.Should().Equal(ProductCatalog.RstUpgradeCode);

        _installed.With(ProductCatalog.RstMachineUpgradeCode, "3.1.0", InstallContext.Machine);
        using (var c = Make())
        {
            await c.InstallAsync(_rst, new ReleaseVersion(3, 1, 1));
            c.For("rst").Problem.Should().Be(InstallProblem.PerMachine);
        }
        // Both installed (the user's own RST.msi shadows RST-Machine): still the administrator's to update.
        _installed.With(ProductCatalog.RstUpgradeCode, "3.0.0");
        using (var c = Make())
        {
            await c.InstallAsync(_rst, new ReleaseVersion(3, 1, 1));
            c.For("rst").Problem.Should().Be(InstallProblem.PerMachine);
        }
        _github.Count.Should().Be(0);
        _self.Started.Should().BeEmpty();
        _msiexec.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task An_updater_that_cannot_start_leaves_the_tray_running()
    {
        _installed.With(ProductCatalog.RstUpgradeCode, "3.1.0");
        _self.Failure = "IOException: the updater folder is in use";
        using var c = Make();
        await c.InstallAsync(_rst, new ReleaseVersion(3, 1, 1));
        c.For("rst").Should().Match<InstallProgress>(p => p.Phase == InstallPhase.Failed && p.Problem == InstallProblem.UpdaterFailed);
        _quits.Should().Be(0);
    }

    [Fact]
    public async Task A_second_click_while_busy_does_nothing()
    {
        _revit.Open();
        using var c = Make();
        var first = c.InstallAsync(_gantt, new ReleaseVersion(1, 2, 0));
        await WaitFor(() => c.For("gantt-it").Phase == InstallPhase.WaitingForRevit);
        await c.InstallAsync(_gantt, new ReleaseVersion(1, 2, 0)); // returns at once
        _revit.Close();
        await first;
        _msiexec.Calls.Should().ContainSingle();
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        condition().Should().BeTrue("the coordinator should reach that state");
    }
}

