// rst-c's own: the RST tray's update rules on Linux (spec #20 §4, §5, §6; AC4, AC5; F17 WU3): release versions
// and tags, the releases/latest redirect, SHA256SUMS.txt, the product catalog embedded from companions.json, the
// host allowlist, detection and page states, the check loop over a simulated 48 hours with a counting handler,
// and the once-per-version notification rule.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using RST.Core.Tray;
using RST.Core.Updates;
using RST.Tests.Activation;
using Xunit;

namespace RST.Tests.Updates;

public class ReleaseVersionTests
{
    [Theory]
    [InlineData("v3.1.0", 3, 1, 0)]
    [InlineData("v0.0.1", 0, 0, 1)]
    [InlineData("v10.20.300", 10, 20, 300)]
    [InlineData("v03.1.0", 3, 1, 0)]
    public void A_full_release_tag_is_accepted(string tag, int major, int minor, int patch)
    {
        ReleaseVersion.TryParseTag(tag).Should().Be(new ReleaseVersion(major, minor, patch));
        new ReleaseVersion(major, minor, patch).Tag.Should().Be($"v{major}.{minor}.{patch}");
    }

    [Theory]
    [InlineData("v3.1.0-rc.1")] // a pre-release is never offered
    [InlineData("v3.1.0-beta")]
    [InlineData("v3.1.0+build")]
    [InlineData("3.1.0")]
    [InlineData("V3.1.0")]
    [InlineData("v3.1")]
    [InlineData("v3.1.0.0")]
    [InlineData("v3.1.0\n")] // .NET's $ would allow this
    [InlineData(" v3.1.0")]
    [InlineData("v\u0663.1.0")] // an Arabic-Indic digit: .NET's \d would allow it
    [InlineData("v1234567890.0.0")] // overflow
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_refused(string? tag) => ReleaseVersion.TryParseTag(tag).Should().BeNull();

    [Theory]
    [InlineData("3.1.0", 3, 1, 0)]
    [InlineData("3.1.0.7", 3, 1, 0)] // Windows Installer ignores the fourth field
    [InlineData(" 1.1.0 ", 1, 1, 0)]
    public void An_installed_version_is_read(string text, int major, int minor, int patch) =>
        ReleaseVersion.TryParseInstalled(text).Should().Be(new ReleaseVersion(major, minor, patch));

    [Theory]
    [InlineData("3.1")]
    [InlineData("v3.1.0")]
    [InlineData("3.1.0-dev")]
    [InlineData("")]
    public void An_unreadable_installed_version_is_null(string text) => ReleaseVersion.TryParseInstalled(text).Should().BeNull();

    [Fact]
    public void Versions_compare_numerically_field_by_field()
    {
        var v = (string s) => ReleaseVersion.TryParseInstalled(s)!.Value;
        (v("3.10.0") > v("3.9.9")).Should().BeTrue();
        (v("3.1.1") > v("3.1.0")).Should().BeTrue();
        (v("4.0.0") > v("3.99.99")).Should().BeTrue();
        (v("3.1.0") <= v("3.1.0")).Should().BeTrue();
        (v("3.1.0") < v("3.1.0")).Should().BeFalse();
        new[] { v("1.2.0"), v("1.10.0"), v("1.9.3") }.Max().Should().Be(v("1.10.0"));
        v("3.1.0").ToString().Should().Be("3.1.0");
    }
}

public class ReleaseFeedTests
{
    private const string Repo = "jedbjorn/rst-c";
    private static readonly Uri Request = ReleaseFeed.LatestUri(Repo);

    private static FeedResult Parse(string? location, HttpStatusCode status = HttpStatusCode.Found) =>
        ReleaseFeed.ParseLatestRedirect(Repo, status, location is null ? null : new Uri(location, UriKind.RelativeOrAbsolute), Request);

    [Fact]
    public void The_request_is_built_from_the_repo()
    {
        Request.AbsoluteUri.Should().Be("https://github.com/jedbjorn/rst-c/releases/latest");
        var bad = () => ReleaseFeed.LatestUri("jedbjorn/rst-c/../evil");
        bad.Should().Throw<ArgumentException>();
        ReleaseFeed.IsRepo("jedbjorn/gantt-it").Should().BeTrue();
        ReleaseFeed.IsRepo("https://github.com/jedbjorn/gantt-it").Should().BeFalse();
        ReleaseFeed.IsRepo("jedbjorn/..").Should().BeFalse();
        ReleaseFeed.IsRepo("jedbjorn").Should().BeFalse();
    }

    [Theory]
    [InlineData("https://github.com/jedbjorn/rst-c/releases/tag/v3.1.1")]
    [InlineData("https://github.com/JEDBJORN/RST-C/releases/tag/v3.1.1")] // GitHub echoes the request's case
    [InlineData("/jedbjorn/rst-c/releases/tag/v3.1.1")] // relative to the request
    [InlineData("https://github.com/jedbjorn/rst-c/releases/tag/v3.1.1/")]
    public void The_latest_full_release_is_read_from_the_redirect(string location) =>
        Parse(location).Should().Be(FeedResult.Latest(new ReleaseVersion(3, 1, 1)));

    [Fact]
    public void A_repo_with_no_full_release_redirects_to_releases() =>
        Parse("https://github.com/jedbjorn/rst-c/releases").Outcome.Should().Be(FeedOutcome.NoRelease);

    [Theory]
    [InlineData("https://github.com/jedbjorn/rst-c/releases/tag/v3.2.0-rc.1", "not a full-release tag")] // pre-release ignored
    [InlineData("https://github.com/jedbjorn/rst-c/releases/tag/latest", "not a full-release tag")]
    [InlineData("https://github.com/jedbjorn/rst-c/releases/tag/v3.1.1%0A", "not a full-release tag")]
    [InlineData("https://evil.example/jedbjorn/rst-c/releases/tag/v9.9.9", "off the allowlist")]
    [InlineData("https://github.com.evil.example/jedbjorn/rst-c/releases/tag/v9.9.9", "off the allowlist")]
    [InlineData("http://github.com/jedbjorn/rst-c/releases/tag/v9.9.9", "off the allowlist")]
    [InlineData("https://github.com:8443/jedbjorn/rst-c/releases/tag/v9.9.9", "off the allowlist")]
    [InlineData("https://user@github.com/jedbjorn/rst-c/releases/tag/v9.9.9", "off the allowlist")]
    [InlineData("https://github.com/someone/else/releases/tag/v9.9.9", "unexpected Location")]
    [InlineData("https://github.com/jedbjorn/rst-c/releases/tag/v9.9.9?x=1", "unexpected Location")]
    [InlineData("https://github.com/login?return_to=x", "unexpected Location")]
    public void A_bad_redirect_fails_closed(string location, string reason)
    {
        var result = Parse(location);
        result.Outcome.Should().Be(FeedOutcome.Failed);
        result.Version.Should().BeNull();
        result.Reason.Should().Contain(reason);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "HTTP 200")]
    [InlineData(HttpStatusCode.NotFound, "HTTP 404")]
    [InlineData(HttpStatusCode.TooManyRequests, "HTTP 429")]
    [InlineData(HttpStatusCode.ProxyAuthenticationRequired, "HTTP 407")]
    public void Anything_but_a_redirect_fails(HttpStatusCode status, string reason) =>
        Parse("https://github.com/jedbjorn/rst-c/releases/tag/v3.1.1", status).Reason.Should().StartWith(reason);

    [Fact]
    public void A_redirect_without_a_location_fails() => Parse(null).Outcome.Should().Be(FeedOutcome.Failed);

    [Fact]
    public async Task One_HEAD_is_sent_and_its_redirect_read()
    {
        var handler = new CountingHandler(CountingHandler.Redirect(new Dictionary<string, string> { [Repo] = "v3.1.1" }));
        using var client = new HttpClient(handler);
        var result = await ReleaseFeed.GetLatestAsync(client, Repo, CancellationToken.None);
        result.Should().Be(FeedResult.Latest(new ReleaseVersion(3, 1, 1)));
        handler.Requests.Should().ContainSingle().Which.Should().Be((HttpMethod.Head, Request));
    }

    [Fact]
    public async Task A_network_failure_is_a_failed_result_not_an_exception()
    {
        var handler = new CountingHandler(_ => throw new HttpRequestException("No such host is known."));
        using var client = new HttpClient(handler);
        var result = await ReleaseFeed.GetLatestAsync(client, Repo, CancellationToken.None);
        result.Outcome.Should().Be(FeedOutcome.Failed);
        result.Reason.Should().Contain("No such host");
    }
}

public class Sha256SumsTests
{
    private const string GanttHash = "06f403468ae484526dc7e8ddc9251bf2b34715ddcb14d668049b63c74d1eb18b";
    private static readonly string Other = new('a', 64);

    [Fact]
    public void The_sha256sum_format_is_read()
    {
        // The sister repos' file as published (gantt-it v1.1.0), plus a CRLF line and a trailing blank line.
        var text = $"53f9704fba2da63573e34e6e58e95d4ec8dc5edb7f312374f217b692bb52f64c  GanttIt-1.1.0-Revit2025.zip\n"
                 + $"{GanttHash}  GanttIt-1.1.0.msi\r\n"
                 + $"{Other}  RST-Setup.exe\n\n";
        var sums = Sha256Sums.TryParse(text, out var error);
        error.Should().BeNull();
        sums!.Count.Should().Be(3);
        sums.HashOf("GanttIt-1.1.0.msi").Should().Be(GanttHash);
        sums.HashOf("RST-Setup.exe").Should().Be(Other);
        sums.HashOf("ganttit-1.1.0.msi").Should().BeNull("names match exactly");
        sums.HashOf("RST.msi").Should().BeNull();
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("\n\n", "no entries")]
    [InlineData("06f4  RST.msi", "line 1")] // short hash
    [InlineData("zz03468ae484526dc7e8ddc9251bf2b34715ddcb14d668049b63c74d1eb18b  RST.msi", "line 1")]
    [InlineData("06f403468ae484526dc7e8ddc9251bf2b34715ddcb14d668049b63c74d1eb18b RST.msi", "line 1")] // one space
    [InlineData("06f403468ae484526dc7e8ddc9251bf2b34715ddcb14d668049b63c74d1eb18b  dir/RST.msi", "line 1")]
    [InlineData("06f403468ae484526dc7e8ddc9251bf2b34715ddcb14d668049b63c74d1eb18b  ..\\RST.msi", "line 1")]
    [InlineData("06f403468ae484526dc7e8ddc9251bf2b34715ddcb14d668049b63c74d1eb18b   RST.msi", "line 1")] // name starts with a space
    [InlineData("06f403468ae484526dc7e8ddc9251bf2b34715ddcb14d668049b63c74d1eb18b  RST.msi ", "ends in a space")]
    [InlineData("# comment", "line 1")]
    // Only the form build/sha256sums.sh writes: no upper-case hex, no binary marker, no control characters.
    [InlineData("06F403468AE484526DC7E8DDC9251BF2B34715DDCB14D668049B63C74D1EB18B  RST.msi", "line 1")]
    [InlineData("06f403468ae484526dc7e8ddc9251bf2b34715ddcb14d668049b63c74d1eb18b *RST.msi", "line 1")]
    [InlineData("06f403468ae484526dc7e8ddc9251bf2b34715ddcb14d668049b63c74d1eb18b  RST\t.msi", "line 1")]
    [InlineData("06f403468ae484526dc7e8ddc9251bf2b34715ddcb14d668049b63c74d1eb18b  RST\u0007.msi", "line 1")]
    public void A_malformed_file_is_refused_whole(string text, string reason)
    {
        Sha256Sums.TryParse(text, out var error).Should().BeNull();
        error.Should().Contain(reason);
    }

    [Fact]
    public void A_name_listed_twice_refuses_the_file()
    {
        Sha256Sums.TryParse($"{GanttHash}  RST.msi\n{Other}  RST.msi\n", out var error).Should().BeNull();
        error.Should().Contain("listed twice");
        Sha256Sums.TryParse($"{GanttHash}  RST.msi\n{Other}  rst.MSI\n", out error).Should().BeNull("one Windows file");
    }

    [Fact]
    public void An_oversized_file_is_refused_unread() =>
        Sha256Sums.TryParse(new string('\n', Sha256Sums.MaxLength + 1), out _).Should().BeNull();
}

public class UpdateHostsTests
{
    [Theory]
    [InlineData("https://github.com/jedbjorn/rst-c/releases/latest", UpdateHostUse.Feed, true)]
    [InlineData("https://GITHUB.com/x/y", UpdateHostUse.Feed, true)]
    [InlineData("https://api.github.com/repos/x/y/releases/tags/v1.0.0", UpdateHostUse.Feed, false)]
    [InlineData("https://api.github.com/repos/x/y/releases/tags/v1.0.0", UpdateHostUse.Api, true)]
    [InlineData("https://github.com/x/y/releases/download/v1.0.0/RST.msi", UpdateHostUse.Download, true)]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/1", UpdateHostUse.DownloadRedirect, true)]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset-2e65be/1", UpdateHostUse.DownloadRedirect, true)]
    [InlineData("https://release-assets.githubusercontent.com/x", UpdateHostUse.Feed, false)]
    // WU4 review N2: a download starts at github.com and is redirected ONLY to the release-asset hosts.
    [InlineData("https://release-assets.githubusercontent.com/x", UpdateHostUse.Download, false)]
    [InlineData("https://github.com/other/repo/releases/download/v1.0.0/RST.msi", UpdateHostUse.DownloadRedirect, false)]
    [InlineData("https://api.github.com/x", UpdateHostUse.DownloadRedirect, false)]
    [InlineData("https://evil.githubusercontent.com/x", UpdateHostUse.Download, false)]
    [InlineData("https://raw.githubusercontent.com/x", UpdateHostUse.Download, false)]
    [InlineData("https://github.com.evil.example/x", UpdateHostUse.Download, false)]
    [InlineData("http://github.com/x", UpdateHostUse.Download, false)]
    [InlineData("https://github.com:444/x", UpdateHostUse.Download, false)]
    [InlineData("https://a:b@github.com/x", UpdateHostUse.Download, false)]
    public void Only_the_listed_hosts_over_https(string url, UpdateHostUse use, bool allowed) =>
        UpdateHosts.IsAllowed(new Uri(url), use).Should().Be(allowed);

    [Fact]
    public void Relative_and_null_are_refused_and_the_list_is_complete()
    {
        UpdateHosts.IsAllowed(null, UpdateHostUse.Feed).Should().BeFalse();
        UpdateHosts.IsAllowed(new Uri("/x/y", UriKind.Relative), UpdateHostUse.Feed).Should().BeFalse();
        UpdateHosts.All.Should().BeEquivalentTo(
            "github.com", "api.github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com");
    }
}

public class ProductCatalogTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RST-C.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    [Fact]
    public void The_embedded_catalog_is_RST_then_the_shippable_companions_from_companions_json()
    {
        var logged = new List<string>();
        var catalog = ProductCatalog.FromEmbedded(logged.Add);
        logged.Should().BeEmpty();
        catalog.Products.Select(p => p.Id).Should().Equal("rst", "gantt-it", "splash-it");

        catalog.Rst.Name.Should().Be("RST");
        catalog.Rst.Repo.Should().Be("jedbjorn/rst-c");
        // RST.msi (per user) and RST-Machine.msi (per machine, F17 WU5): one product, two packages.
        catalog.Rst.UpgradeCodes.Should().Equal("AF0331FC-5280-492A-94BD-95EDF108BE74", "79C25959-C28A-4ED8-8984-85ED525476C3");
        catalog.Rst.AddinClientId.Should().BeNull();

        var gantt = catalog.Find("gantt-it")!;
        gantt.Name.Should().Be("Gantt-It");
        gantt.Repo.Should().Be("jedbjorn/gantt-it");
        gantt.UpgradeCodes.Should().Equal("644FCAD5-E631-4441-9A11-75F175565136");
        gantt.AddinClientId.Should().Be("9920fb42-542b-4ce1-9659-a078489af6b0");
        var splash = catalog.Find("splash-it")!;
        splash.UpgradeCodes.Should().Equal("080648F4-EEC7-4DB6-A793-683A1C008BFC");
        splash.AddinClientId.Should().Be("0b1cc712-e936-4da1-9680-2d9a95f88271");
        ProductCatalog.Default.Products.Select(p => p.Id).Should().Equal(catalog.Products.Select(p => p.Id));
    }

    [Fact]
    public void The_embedded_file_is_the_checked_in_companions_json()
    {
        using var stream = typeof(ProductCatalog).Assembly.GetManifestResourceStream(ProductCatalog.CompanionsResource)!;
        using var reader = new StreamReader(stream);
        reader.ReadToEnd().Should().Be(File.ReadAllText(Path.Combine(RepoRoot(), "companions", "companions.json")));
    }

    [Fact]
    public void Only_identity_fields_are_used_and_unidentifiable_entries_are_left_out()
    {
        static string Entry(string id, string status = "shippable", string? upgradeCode = "11111111-2222-3333-4444-555555555555",
                            string? clientId = "{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}", string repo = "jedbjorn/x") =>
            JsonSerializer.Serialize(new
            {
                id, name = id.ToUpperInvariant(), description = "d", repo, status, addinClientId = clientId,
                release = upgradeCode is null ? null : new { tag = "v9.9.9", upgradeCode, version = "9.9.9" },
            });

        var json = "{\"companions\":[" + string.Join(",",
            Entry("good"),
            Entry("dev", status: "in_development"),
            Entry("retired", status: "retired", upgradeCode: "21111111-2222-3333-4444-555555555555", clientId: "b1111111-2222-3333-4444-555555555555"),
            Entry("no-code", upgradeCode: null, clientId: "c1111111-2222-3333-4444-555555555555"),
            Entry("no-client", upgradeCode: "31111111-2222-3333-4444-555555555555", clientId: null),
            Entry("bad-repo", upgradeCode: "41111111-2222-3333-4444-555555555555", clientId: "d1111111-2222-3333-4444-555555555555", repo: "https://x"),
            Entry("claims-rst", upgradeCode: "af0331fc-5280-492a-94bd-95edf108be74", clientId: "e1111111-2222-3333-4444-555555555555"),
            Entry("dup-code", clientId: "f1111111-2222-3333-4444-555555555555"),
            Entry("Bad_Id", upgradeCode: "51111111-2222-3333-4444-555555555555", clientId: "a2111111-2222-3333-4444-555555555555")) + "]}";

        var logged = new List<string>();
        var catalog = ProductCatalog.Parse(json, logged.Add);
        catalog.Products.Select(p => p.Id).Should().Equal("rst", "good");
        var good = catalog.Find("good")!;
        good.UpgradeCodes.Should().Equal("11111111-2222-3333-4444-555555555555");
        good.AddinClientId.Should().Be("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "braces stripped, lower-cased");
        // F17 WU4: "good" has no pinned asset and no hard-coded pattern for its id, so it is detected and checked but
        // never installed (logged once more).
        good.IsInstallable.Should().BeFalse();
        logged.Should().HaveCount(7, "each unidentifiable shippable entry is logged; in_development and retired are silent");
        logged.Should().ContainSingle(l => l.Contains("good has no known asset name"));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void An_unreadable_registry_leaves_RST_alone(string json) =>
        ProductCatalog.Parse(json).Products.Should().ContainSingle().Which.IsRst.Should().BeTrue();
}

public sealed class ProductDetectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RST-Detect-" + Guid.NewGuid().ToString("N"));
    private static readonly ProductInfo Gantt = ProductCatalog.Default.Find("gantt-it")!;
    private static readonly Dictionary<string, string> NoManifests = new();

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }

    private static ReleaseVersion V(string s) => ReleaseVersion.TryParseInstalled(s)!.Value;

    [Fact]
    public void Windows_Installer_is_the_signal_and_the_highest_version_is_shown()
    {
        var source = new FakeInstalledProducts()
            .With(Gantt.UpgradeCodes[0], "1.0.0", InstallContext.Machine)
            .With(Gantt.UpgradeCodes[0], "1.1.0");
        var found = ProductDetection.Query(source, Gantt);
        var install = ProductDetection.Decide(Gantt, found, new Dictionary<string, string> { [Gantt.AddinClientId!] = @"C:\x.addin" });
        install.Kind.Should().Be(InstallKind.Installer, "an MSI install also has its .addin; the MSI wins");
        install.Version.Should().Be(V("1.1.0"));
        install.Installed!.Context.Should().Be(InstallContext.User);
    }

    [Fact]
    public void A_ClientId_without_an_installer_product_is_a_manual_install()
    {
        var install = ProductDetection.Decide(Gantt, Array.Empty<InstalledProduct>(),
            new Dictionary<string, string> { [Gantt.AddinClientId!] = @"C:\Users\m\AppData\Roaming\Autodesk\Revit\Addins\2026\GanttIt.addin" });
        install.Kind.Should().Be(InstallKind.Manual);
        install.ManifestPath.Should().EndWith("GanttIt.addin");
        ProductDetection.Decide(Gantt, Array.Empty<InstalledProduct>(), NoManifests).Should().Be(ProductInstallation.None);
        ProductDetection.Decide(ProductCatalog.RstProduct, Array.Empty<InstalledProduct>(),
            new Dictionary<string, string> { [Gantt.AddinClientId!] = "x" }).Kind.Should().Be(InstallKind.NotInstalled);
    }

    [Fact]
    public void An_unreadable_Windows_Installer_is_Unknown_never_not_installed_or_manual()
    {
        var logged = new List<string>();
        var found = ProductDetection.Query(new ThrowingProducts(), Gantt, logged.Add);
        found.Should().BeNull("couldn't tell is not the same as none");
        logged.Should().ContainSingle().Which.Should().Contain("Gantt-It");

        // Even with Gantt-It's .addin present, an unreadable Windows Installer is Unknown, not "installed manually".
        var install = ProductDetection.Decide(Gantt, found, new Dictionary<string, string> { [Gantt.AddinClientId!] = @"C:\x.addin" });
        install.Kind.Should().Be(InstallKind.Unknown);
        ProductStatus.Compute(install, V("9.9.9")).Should().Be(ProductState.Unknown);
        new UpdateState().ToNotify(new[] { new ProductStatus(Gantt, install, V("9.9.9"), false) }).Should().BeEmpty();
    }

    private sealed class ThrowingProducts : IInstalledProducts
    {
        public IReadOnlyList<InstalledProduct> Related(string upgradeCode) => throw new InvalidOperationException("msi.dll");
    }

    [Theory]
    [InlineData(InstallKind.NotInstalled, null, null, ProductState.NotInstalled)]
    [InlineData(InstallKind.NotInstalled, null, "3.1.1", ProductState.NotInstalled)]
    [InlineData(InstallKind.Manual, null, "3.1.1", ProductState.InstalledManually)]
    [InlineData(InstallKind.Installer, "3.1.0", null, ProductState.Installed)]
    [InlineData(InstallKind.Installer, "3.1.0", "3.1.1", ProductState.UpdateAvailable)]
    [InlineData(InstallKind.Installer, "3.1.1", "3.1.1", ProductState.UpToDate)]
    [InlineData(InstallKind.Installer, "3.2.0", "3.1.1", ProductState.UpToDate)] // a newer local build is never "downgrade available"
    [InlineData(InstallKind.Installer, "0.0.0-dev", "3.1.1", ProductState.Installed)]
    [InlineData(InstallKind.Unknown, null, "3.1.1", ProductState.Unknown)]
    public void The_page_state(InstallKind kind, string? installed, string? latest, ProductState expected)
    {
        var install = kind == InstallKind.Installer
            ? new ProductInstallation(kind, new InstalledProduct("{X}", installed!, InstallContext.User))
            : new ProductInstallation(kind);
        ProductStatus.Compute(install, latest is null ? null : V(latest)).Should().Be(expected);
    }

    [Theory]
    [InlineData("3.1.1", "3.1.1", ProductState.Installed)]       // "up to date" would be stale after a failed check
    [InlineData("3.2.0", "3.1.1", ProductState.Installed)]
    [InlineData("3.1.0", "3.1.1", ProductState.UpdateAvailable)] // a known newer version stays true
    public void After_a_failed_check_up_to_date_is_never_claimed(string installed, string lastKnown, ProductState expected)
    {
        var install = new ProductInstallation(InstallKind.Installer, new InstalledProduct("{X}", installed, InstallContext.User));
        ProductStatus.Compute(install, V(lastKnown), latestFailed: true).Should().Be(expected);
        new ProductStatus(ProductCatalog.Default.Rst, install, V(lastKnown), LatestFailed: true).State.Should().Be(expected);
    }

    // F16 review delta N1 (F17 WU2): the "installed manually" scan reads add-ins where each installed Revit year reads
    // them, through feature #16's inventory, not a hand-kept folder list.
    private const string GanttId = "9920fb42-542b-4ce1-9659-a078489af6b0";
    private const string SplashId = "0b1cc712-e936-4da1-9680-2d9a95f88271";

    [Fact]
    public void Manifests_are_scanned_with_the_inventory_in_every_installed_2025_plus_year()
    {
        using var tree = new FakeAddinTree();
        var a = tree.Manifest(tree.UserAddins(2026), "GanttIt.addin", "Gantt-It", "{9920FB42-542B-4CE1-9659-A078489AF6B0}");
        var b = tree.Manifest(tree.ProgramDataAddins(2026), "SplashIt.addin", "Splash-It", SplashId, renamed: true);
        tree.Manifest(tree.UserAddins(2024), "Old.addin", "Old", "11111111-1111-1111-1111-111111111111");
        tree.Bundle(tree.UserPlugins, "P", new FakeAddinTree.Component("R2026", 2026, 2026, "P.addin", "P", "22222222-2222-2222-2222-222222222222"));
        File.WriteAllText(Path.Combine(tree.UserAddins(2026), "broken.addin"), "<not xml");

        var ids = ProductDetection.InstalledClientIds(tree.Roots, [FakeAddinTree.Year(2024), FakeAddinTree.Year(2026)]);

        ids.Should().ContainKey(GanttId).WhoseValue.Should().Be(a);
        ids.Should().ContainKey(SplashId).WhoseValue.Should().Be(b + ".RSTdisabled", "a switched-off add-in is still installed");
        ids.Should().ContainKey("22222222-2222-2222-2222-222222222222");
        ids.Should().NotContainKey("11111111-1111-1111-1111-111111111111", "Revit 2024 is not supported (decision #21)");
        ProductDetection.InstalledClientIds(tree.Roots, []).Should().BeEmpty("no Revit year is installed");
    }

    [Fact]
    public void Revit_2027_is_read_from_Program_Files_and_never_from_ProgramData()
    {
        using var tree = new FakeAddinTree();
        var gantt = tree.Manifest(tree.MachineAddins(2027), "GanttIt.addin", "Gantt-It", GanttId);
        var splash = tree.Bundle(tree.ProgramFilesPlugins, "SplashIt",
            new FakeAddinTree.Component("R2027", 2027, null, "SplashIt.addin", "Splash-It", SplashId));
        tree.Manifest(tree.ProgramDataAddins(2027), "Ignored.addin", "Ignored", "33333333-3333-3333-3333-333333333333");
        tree.Bundle(tree.ProgramDataPlugins, "Ignored2027",
            new FakeAddinTree.Component("R2027", 2027, 2027, "I.addin", "I", "44444444-4444-4444-4444-444444444444"));
        tree.Manifest(Path.Combine(tree.RevitInstallAddins(2027), "Own"), "Own.addin", "Own", "55555555-5555-5555-5555-555555555555");

        var ids = ProductDetection.InstalledClientIds(tree.Roots, [FakeAddinTree.Year(2026), FakeAddinTree.Year(2027)]);

        ids.Should().ContainKey(GanttId).WhoseValue.Should().Be(gantt, "2027 reads machine-wide add-ins from Program Files\\Autodesk\\Revit\\Addins\\2027");
        ids.Should().ContainKey(SplashId).WhoseValue.Should().StartWith(splash, "and machine-wide bundles from Program Files\\Autodesk\\ApplicationPlugins");
        ids.Should().NotContainKey("33333333-3333-3333-3333-333333333333", "Revit 2027 ignores %ProgramData%'s Addins\\2027");
        ids.Should().NotContainKey("44444444-4444-4444-4444-444444444444",
            "2027 ignores %ProgramData%'s ApplicationPlugins, and 2026 reads that bundle only for the years it names");
        ids.Should().NotContainKey("55555555-5555-5555-5555-555555555555", "Revit's own add-ins are never a companion");
        ProductDetection.Decide(Gantt, [], ids).Kind.Should().Be(InstallKind.Manual);
    }

    [Fact]
    public void A_2026_only_bundle_does_not_count_for_2027()
    {
        using var tree = new FakeAddinTree();
        tree.Bundle(tree.UserPlugins, "GanttIt", new FakeAddinTree.Component("R2026", 2026, 2026, "GanttIt.addin", "Gantt-It", GanttId));

        ProductDetection.InstalledClientIds(tree.Roots, [FakeAddinTree.Year(2027)]).Should().NotContainKey(GanttId);
        ProductDetection.InstalledClientIds(tree.Roots, [FakeAddinTree.Year(2026)]).Should().ContainKey(GanttId);
    }
}

public sealed class UpdateCheckLoopTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 30, 8, 0, 0, TimeSpan.Zero);

    private static (ManualTimeProvider Time, UpdateCheckLoop Loop, List<(DateTimeOffset At, UpdateCheckTrigger Trigger)> Runs) Make(
        Func<bool> on, TimeSpan? firstDelay = null)
    {
        var time = new ManualTimeProvider(Start);
        var runs = new List<(DateTimeOffset, UpdateCheckTrigger)>();
        var loop = new UpdateCheckLoop(time, on, (t, _) =>
        {
            lock (runs) runs.Add((time.GetUtcNow(), t));
            return Task.CompletedTask;
        }, () => firstDelay ?? TimeSpan.FromMinutes(7));
        return (time, loop, runs);
    }

    private static void Advance(ManualTimeProvider time, UpdateCheckLoop loop, TimeSpan by) =>
        time.Advance(by, () => loop.Running?.GetAwaiter().GetResult());

    [Fact]
    public void With_checks_on_it_runs_after_the_start_delay_then_every_24_hours()
    {
        var (time, loop, runs) = Make(() => true);
        loop.Start();
        loop.NextDueUtc.Should().Be(Start.AddMinutes(7));
        Advance(time, loop, TimeSpan.FromHours(48));
        runs.Select(r => r.At).Should().Equal(Start.AddMinutes(7), Start.AddMinutes(7).AddHours(24));
        runs.Should().OnlyContain(r => r.Trigger == UpdateCheckTrigger.Automatic);
        loop.NextDueUtc.Should().Be(Start.AddMinutes(7).AddHours(48));
        time.ArmedTimers.Should().Be(1, "one timer, re-armed: no polling");
    }

    [Fact]
    public async Task With_checks_off_nothing_runs_in_48_hours_but_Check_now_still_does()
    {
        var asked = 0;
        var (time, loop, runs) = Make(() => { asked++; return false; });
        loop.Start();
        Advance(time, loop, TimeSpan.FromHours(48));
        runs.Should().BeEmpty();
        asked.Should().Be(2, "the setting is read fresh at each slot");

        await loop.CheckNowAsync();
        runs.Should().ContainSingle().Which.Trigger.Should().Be(UpdateCheckTrigger.User);
    }

    [Fact]
    public void A_setting_that_cannot_be_read_counts_as_off()
    {
        var (time, loop, runs) = Make(() => throw new IOException("locked"));
        loop.Start();
        Advance(time, loop, TimeSpan.FromHours(48));
        runs.Should().BeEmpty();
    }

    [Fact]
    public void The_start_delay_is_random_between_2_and_10_minutes_and_clamped()
    {
        for (var i = 0; i < 200; i++)
        {
            var d = UpdateCheckLoop.RandomFirstDelay();
            d.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMinutes(2)).And.BeLessThanOrEqualTo(TimeSpan.FromMinutes(10));
        }
        var (_, early, _) = Make(() => true, TimeSpan.FromSeconds(1));
        early.Start();
        early.NextDueUtc.Should().Be(Start.AddMinutes(2));
        var (_, late, _) = Make(() => true, TimeSpan.FromHours(3));
        late.Start();
        late.NextDueUtc.Should().Be(Start.AddMinutes(10));
    }

    [Fact]
    public void Turning_checks_back_on_moves_the_next_slot_to_within_10_minutes()
    {
        var on = false;
        var (time, loop, runs) = Make(() => on);
        loop.Start();
        Advance(time, loop, TimeSpan.FromHours(1)); // the start slot passed while off
        loop.NextDueUtc.Should().Be(Start.AddMinutes(7).AddHours(24));
        on = true;
        loop.ChecksTurnedOn();
        loop.NextDueUtc.Should().Be(Start.AddHours(1).AddMinutes(7));
        Advance(time, loop, TimeSpan.FromMinutes(7));
        runs.Should().ContainSingle();
    }

    [Fact]
    public void Resync_after_sleep_runs_a_missed_slot_soon_and_otherwise_keeps_the_slot()
    {
        var (time, loop, runs) = Make(() => true);
        loop.Start();
        loop.Resync();
        loop.NextDueUtc.Should().Be(Start.AddMinutes(7), "not due yet: unchanged");
        Advance(time, loop, TimeSpan.FromMinutes(7));
        runs.Should().HaveCount(1);

        // The PC sleeps 30 hours: the wall clock moves on, the paused timer would still wait its full 24 hours.
        time.Sleep(TimeSpan.FromHours(30));
        loop.Resync();
        loop.NextDueUtc.Should().Be(time.GetUtcNow().AddMinutes(7), "the missed slot runs once the network is back");
        Advance(time, loop, TimeSpan.FromMinutes(7));
        runs.Should().HaveCount(2);
        loop.NextDueUtc.Should().Be(time.GetUtcNow().AddHours(24));

        // A short sleep that does not cross the slot keeps the slot's wall-clock time.
        var due = loop.NextDueUtc;
        time.Sleep(TimeSpan.FromHours(2));
        loop.Resync();
        loop.NextDueUtc.Should().Be(due);
        Advance(time, loop, TimeSpan.FromHours(22));
        runs.Should().HaveCount(3);
    }

    [Fact]
    public async Task Check_now_during_a_running_check_joins_it()
    {
        var time = new ManualTimeProvider(Start);
        var gate = new TaskCompletionSource();
        var runs = 0;
        using var loop = new UpdateCheckLoop(time, () => true, async (_, _) =>
        {
            Interlocked.Increment(ref runs);
            await gate.Task;
        });
        var first = loop.CheckNowAsync();
        var second = loop.CheckNowAsync();
        second.Should().BeSameAs(first);
        gate.SetResult();
        await first;
        runs.Should().Be(1);
        await loop.CheckNowAsync();
        runs.Should().Be(1, "within 30 s of the last check, Check now shows its result and asks nothing");
        time.Advance(TimeSpan.FromSeconds(29));
        await loop.CheckNowAsync();
        runs.Should().Be(1);
        time.Advance(TimeSpan.FromSeconds(2));
        await loop.CheckNowAsync();
        runs.Should().Be(2);
        loop.LastRunEndedUtc.Should().Be(time.GetUtcNow());
    }

    [Fact]
    public void After_dispose_nothing_runs()
    {
        var (time, loop, runs) = Make(() => true);
        loop.Start();
        loop.Dispose();
        Advance(time, loop, TimeSpan.FromHours(48));
        loop.CheckNowAsync().IsCompleted.Should().BeTrue();
        runs.Should().BeEmpty();
        loop.NextDueUtc.Should().BeNull();
    }

    /// <summary>
    /// AC4 in Core: the loop driving the real feed client over a counting handler, three repos, 48 simulated
    /// hours. Off: zero requests until Check now (then exactly three HEADs). On: two checks of three HEADs.
    /// </summary>
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 6)]
    public async Task Over_48_hours_the_feed_is_asked_only_when_checks_are_on(bool on, int expected)
    {
        var handler = new CountingHandler(CountingHandler.Redirect(new Dictionary<string, string> { ["jedbjorn/rst-c"] = "v3.1.1" }));
        using var client = new HttpClient(handler);
        var time = new ManualTimeProvider(Start);
        var repos = ProductCatalog.Default.Products.Select(p => p.Repo).ToList();
        using var loop = new UpdateCheckLoop(time, () => on, async (_, cancel) =>
        {
            foreach (var repo in repos) await ReleaseFeed.GetLatestAsync(client, repo, cancel);
        });
        loop.Start();
        Advance(time, loop, TimeSpan.FromHours(48));
        handler.Count.Should().Be(expected);

        await loop.CheckNowAsync();
        handler.Count.Should().Be(expected + 3);
        handler.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Head && r.Uri.Host == "github.com");
    }
}

public sealed class UpdateStateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RST-UpdateState-" + Guid.NewGuid().ToString("N"));
    private string PathIn => Path.Combine(_root, "tray_updates.json");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }

    private static ReleaseVersion V(string s) => ReleaseVersion.TryParseInstalled(s)!.Value;

    private static ProductStatus Status(string id, string? installed, string? latest, InstallKind kind = InstallKind.Installer)
    {
        var product = ProductCatalog.Default.Find(id)!;
        var install = kind == InstallKind.Installer && installed is not null
            ? new ProductInstallation(kind, new InstalledProduct("{X}", installed, InstallContext.User))
            : new ProductInstallation(kind);
        return new ProductStatus(product, install, latest is null ? null : V(latest), false);
    }

    [Fact]
    public void Each_new_version_is_announced_once_and_a_newer_one_again()
    {
        var state = new UpdateState();
        var day1 = new[] { Status("rst", "3.1.0", "3.1.1"), Status("gantt-it", "1.1.0", "1.1.0"), Status("splash-it", null, "1.2.0", InstallKind.NotInstalled) };
        state.ToNotify(day1).Select(s => s.Product.Id).Should().Equal("rst");
        state.SetNotified("rst", V("3.1.1"));

        state.ToNotify(day1).Should().BeEmpty("the same version is not announced every 24 hours");
        var day3 = new[] { Status("rst", "3.1.0", "3.1.2"), Status("gantt-it", "1.1.0", "1.2.0"), Status("splash-it", null, "1.2.0", InstallKind.Manual) };
        state.ToNotify(day3).Select(s => s.Product.Id).Should().Equal("rst", "gantt-it");
    }

    [Fact]
    public void No_release_is_remembered_apart_from_never_checked()
    {
        var state = new UpdateState();
        state.SetNoRelease("splash-it", true);
        state.Write(PathIn).Should().BeTrue();
        var read = UpdateState.Read(PathIn);
        read.NoRelease("splash-it").Should().BeTrue();
        read.NoRelease("rst").Should().BeFalse();
        read.Latest("splash-it").Should().BeNull();
        File.ReadAllText(PathIn).Should().Contain("\"noRelease\": true").And.NotContain("\"noRelease\": null");
    }

    [Fact]
    public void The_state_round_trips_and_a_damaged_file_starts_empty()
    {
        var state = new UpdateState { LastCheckedUtc = new DateTimeOffset(2026, 10, 30, 9, 14, 2, TimeSpan.Zero) };
        state.SetLatest("rst", V("3.1.1"));
        state.SetNotified("rst", V("3.1.1"));
        state.SetLatest("gantt-it", null);
        state.Write(PathIn).Should().BeTrue();

        var read = UpdateState.Read(PathIn);
        read.LastCheckedUtc.Should().Be(state.LastCheckedUtc);
        read.Latest("rst").Should().Be(V("3.1.1"));
        read.Notified("rst").Should().Be(V("3.1.1"));
        read.Latest("gantt-it").Should().BeNull();
        read.Latest("splash-it").Should().BeNull();
        File.ReadAllText(PathIn).Should().Contain("\"latest\": \"v3.1.1\"");

        UpdateState.Read(Path.Combine(_root, "missing.json")).LastCheckedUtc.Should().BeNull();
        File.WriteAllText(PathIn, "{ broken");
        var logged = new List<string>();
        UpdateState.Read(PathIn, logged.Add).Latest("rst").Should().BeNull();
        logged.Should().ContainSingle();
        File.WriteAllText(PathIn, "{\"products\":{\"rst\":{\"latest\":\"v3.2.0-rc.1\",\"notified\":\"nonsense\"}}}");
        UpdateState.Read(PathIn).Latest("rst").Should().BeNull("a pre-release is never remembered as latest");
    }

    [Fact]
    public void The_file_lives_beside_the_tray_settings() =>
        Path.GetDirectoryName(TrayPaths.UpdatesFile).Should().Be(Path.GetDirectoryName(TrayPaths.SettingsFile));
}
