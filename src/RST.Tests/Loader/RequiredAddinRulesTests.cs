// rst-c's own (not vendored): RequiredAddinRules (spec #16, WU3, AC5), the C# port of
// profile_loader.html's normalizeRequiredAddins, BUILTIN_TABS / isAddinLoaded /
// getLoadedAddinName, the four-state status, the add-in row and getNotFoundAddins.

using System.Text.Json;
using FluentAssertions;
using RST.Core.AddIns;
using RST.Core.Loader;
using RST.Core.Profiles;
using RST.Core.Scanning;
using Xunit;

namespace RST.Tests.Loader;

public class RequiredAddinRulesTests
{
    private static readonly Dictionary<string, AddinLookupEntry> Lookup = new()
    {
        ["Lumion"] = new("Lumion LiveSync", "Lumion.addin", "https://lumion.com"),
        ["Kinship"] = new("Kinship", "Kinship.addin", ""),
        ["Enscape"] = new("Enscape", "Enscape.addin", "https://enscape3d.com"),
    };

    private static LoadedAddin Loaded(string fileName, string? displayName = null, bool disabled = false) =>
        new(fileName, @"C:\x\" + fileName, disabled, displayName ?? Path.GetFileNameWithoutExtension(fileName), null, null);

    private static AddinSessionContext Session(string[]? tabs = null, LoadedAddin[]? loaded = null, UserAddinConfig? config = null) =>
        new(tabs ?? [], loaded ?? [], Lookup, config);

    // ── normalizeRequiredAddins ──────────────────────────────────────────────

    [Fact]
    public void Normalize_object_format_keeps_tab_file_and_url_and_blanks_become_null_or_empty()
    {
        var result = RequiredAddinRules.Normalize(new[]
        {
            new RequiredAddin { TabName = "Kinship", AddinFile = "Kinship.addin", Url = "https://k" },
            new RequiredAddin { TabName = "Bare", AddinFile = "", Url = null },
        });
        result.Should().Equal(
            new NormalizedRequiredAddin("Kinship", "Kinship.addin", "https://k"),
            new NormalizedRequiredAddin("Bare", null, ""));
    }

    [Fact]
    public void Normalize_legacy_string_format_resolves_through_the_lookup()
    {
        using var doc = JsonDocument.Parse("""["Lumion", "Unknown"]""");
        var result = RequiredAddinRules.Normalize(doc.RootElement, Lookup);
        result.Should().Equal(
            new NormalizedRequiredAddin("Lumion", "Lumion.addin", "https://lumion.com"),
            new NormalizedRequiredAddin("Unknown", null, ""));
    }

    [Fact]
    public void Normalize_raw_objects_keep_the_native_flag_and_mixed_arrays_work()
    {
        using var doc = JsonDocument.Parse("""[{"tabName":"FormIt","native":true},{"tabName":"Kinship","addinFile":"K.addin","url":"u"},"Enscape",42]""");
        var result = RequiredAddinRules.Normalize(doc.RootElement, Lookup);
        result.Should().Equal(
            new NormalizedRequiredAddin("FormIt", null, "", Native: true),
            new NormalizedRequiredAddin("Kinship", "K.addin", "u"),
            new NormalizedRequiredAddin("Enscape", "Enscape.addin", "https://enscape3d.com"));
    }

    [Fact]
    public void Normalize_a_non_array_gives_an_empty_list()
    {
        using var doc = JsonDocument.Parse("""{"tabName":"x"}""");
        RequiredAddinRules.Normalize(doc.RootElement, Lookup).Should().BeEmpty();
        RequiredAddinRules.Normalize((IEnumerable<RequiredAddin>?)null).Should().BeEmpty();
    }

    // ── BUILTIN_TABS / isAddinLoaded / getLoadedAddinName ───────────────────

    [Theory]
    [InlineData("Architecture")]
    [InlineData("Add-Ins")]
    [InlineData("RST")]
    [InlineData("FormIt Converter")]
    [InlineData("eTransmit")]
    [InlineData("Massing & Site")]
    public void Builtin_tabs_are_native_and_count_as_loaded(string tab)
    {
        RequiredAddinRules.IsNativeTab(tab).Should().BeTrue();
        RequiredAddinRules.IsAddinLoaded(tab, Session()).Should().BeTrue();
    }

    [Theory]
    [InlineData("architecture")]
    [InlineData("Kinship")]
    [InlineData(null)]
    public void Other_tabs_are_not_native(string? tab) => RequiredAddinRules.IsNativeTab(tab).Should().BeFalse();

    [Fact]
    public void A_live_ribbon_tab_containing_or_contained_by_the_name_counts_case_insensitively()
    {
        RequiredAddinRules.IsAddinLoaded("Enscape", Session(tabs: ["Enscape™ 4.0"])).Should().BeTrue();
        RequiredAddinRules.IsAddinLoaded("pyRevit Tools", Session(tabs: ["PYREVIT"])).Should().BeTrue();
        RequiredAddinRules.IsAddinLoaded("Enscape", Session(tabs: ["", "Lumion"])).Should().BeFalse();
    }

    [Fact]
    public void A_manifest_display_name_or_file_stem_counts_including_disabled_manifests()
    {
        RequiredAddinRules.IsAddinLoaded("Kinship", Session(loaded: [Loaded("Kinship.addin")])).Should().BeTrue();
        RequiredAddinRules.IsAddinLoaded("Kinship", Session(loaded: [Loaded("x.addin", displayName: "Kinship for Revit")])).Should().BeTrue();
        // The JS matched every scanned manifest, disabled ones too; the port keeps that.
        RequiredAddinRules.IsAddinLoaded("Kinship", Session(loaded: [Loaded("Kinship.addin", disabled: true)])).Should().BeTrue();
        RequiredAddinRules.IsAddinLoaded("Kinship", Session(loaded: [Loaded("Other.addin")])).Should().BeFalse();
    }

    [Fact]
    public void A_file_name_with_the_disabled_suffix_is_stripped_before_matching()
    {
        var loaded = new LoadedAddin("Twin.addin.RSTdisabled", "p", true, "", null, null);
        RequiredAddinRules.IsAddinLoaded("Twin", Session(loaded: [loaded])).Should().BeTrue();
        RequiredAddinRules.GetLoadedAddinName("Twin", Session(loaded: [loaded])).Should().Be("Twin.addin.RSTdisabled");
    }

    [Fact]
    public void The_lookup_file_stem_matches_an_installed_file_with_a_longer_name()
    {
        // Registry "Lumion.addin"; the install ships "LumionLiveSync.addin" under a vendor display name.
        var session = Session(loaded: [Loaded("LumionLiveSync.addin", displayName: "Act-3D Live")]);
        RequiredAddinRules.IsAddinLoaded("Lumion", session).Should().BeTrue();
        RequiredAddinRules.GetLoadedAddinName("Lumion", session).Should().Be("Act-3D Live");
    }

    [Fact]
    public void No_scanned_manifests_and_no_ribbon_match_is_not_loaded()
    {
        RequiredAddinRules.IsAddinLoaded("Lumion", Session()).Should().BeFalse();
        RequiredAddinRules.GetLoadedAddinName("Lumion", Session()).Should().BeEmpty();
    }

    [Fact]
    public void Ribbon_and_manifest_matching_agree_with_RequiredAddinQa_tier_3()
    {
        // The "Loaded" pill and the QA must agree whether an add-in counts as installed.
        var manifests = new[]
        {
            FakeAddinPasses.Manifest("LumionLiveSync.addin", name: "Lumion LiveSync"),
            FakeAddinPasses.Manifest("Enscape.addin"),
        };
        var source = new AddinSearchPath(@"C:\Addins", AddinPathKind.UserAddins, ReadOnly: false);
        var ribbon = new[] { "Kinship Tools" };
        var cases = new[] { "Lumion", "Enscape", "Kinship", "Twinmotion" };
        var required = cases.Select(t => new RequiredAddin { TabName = t }).ToList();

        var qa = RequiredAddinQa.Classify(manifests.Select(m => (m, source)), required, ribbon);
        var session = new AddinSessionContext(ribbon, manifests.Select(LoadedAddin.FromManifest).ToList(), new Dictionary<string, AddinLookupEntry>());
        foreach (var (tab, result) in cases.Zip(qa))
        {
            var loaded = RequiredAddinRules.IsAddinLoaded(tab, session);
            loaded.Should().Be(result.Status == RequiredAddinStatus.InstalledActive, tab);
        }
    }

    // ── the four-state row ───────────────────────────────────────────────────

    [Fact]
    public void Native_row_says_Revit_Builtin_with_no_link_or_badge()
    {
        var row = RequiredAddinRules.Describe(new NormalizedRequiredAddin("Architecture", "ignored.addin", "https://x"), Session());
        row.Should().Be(new RequiredAddinRow("Architecture", "Revit Built-in", null, null, AddinRowStatus.Native, "Native",
            "Built-in Revit feature — always available"));
    }

    [Fact]
    public void The_native_flag_alone_makes_a_row_native()
    {
        var row = RequiredAddinRules.Describe(new NormalizedRequiredAddin("Custom Tab", null, "", Native: true), Session());
        row.Status.Should().Be(AddinRowStatus.Native);
    }

    [Fact]
    public void Loaded_row_names_the_matched_manifest_and_keeps_the_info_link()
    {
        var session = Session(loaded: [Loaded("Enscape.addin", displayName: "Enscape Render")]);
        var row = RequiredAddinRules.Describe(new NormalizedRequiredAddin("Enscape", null, ""), session);
        row.FileColumn.Should().Be("Enscape Render", "no addinFile: the matched name fills the file column");
        row.Status.Should().Be(AddinRowStatus.Loaded);
        row.StatusLabel.Should().Be("Loaded");
        row.StatusTooltip.Should().Be("Loaded in current Revit session (Enscape Render)");
        row.InfoUrl.Should().Be("https://enscape3d.com", "the lookup's URL backs a profile without one");
        row.Origin.Should().BeNull();
    }

    [Fact]
    public void Loaded_by_the_ribbon_alone_has_no_matched_name()
    {
        var row = RequiredAddinRules.Describe(new NormalizedRequiredAddin("Enscape", "Enscape.addin", "https://e"), Session(tabs: ["Enscape"]));
        row.FileColumn.Should().Be("Enscape.addin");
        row.StatusTooltip.Should().Be("Loaded in current Revit session");
    }

    [Fact]
    public void Not_found_row_with_a_url_says_click_to_download()
    {
        var row = RequiredAddinRules.Describe(new NormalizedRequiredAddin("Lumion", null, ""), Session());
        row.FileColumn.Should().Be("Lumion.addin", "no file and no match: the lookup's file");
        row.Status.Should().Be(AddinRowStatus.NotFound);
        row.StatusLabel.Should().Be("Not Found");
        row.StatusTooltip.Should().Be("Click to download");
        row.InfoUrl.Should().Be("https://lumion.com");
    }

    [Fact]
    public void Not_found_row_without_a_url_says_not_found_on_this_machine()
    {
        var row = RequiredAddinRules.Describe(new NormalizedRequiredAddin("Mystery", null, ""), Session());
        row.FileColumn.Should().BeEmpty();
        row.StatusTooltip.Should().Be("Not found on this machine");
        row.InfoUrl.Should().BeNull();
    }

    [Fact]
    public void Installed_and_Disabled_come_from_the_user_config_with_their_origin_badge()
    {
        var config = new UserAddinConfig(new Dictionary<string, UserAddinConfigEntry>
        {
            ["Mystery"] = new(true, "third-party", "Mystery.addin"),
            ["Gone"] = new(false, "custom", null),
            ["Builtin"] = new(true, "native", null),
        });
        var installed = RequiredAddinRules.Describe(new NormalizedRequiredAddin("Mystery", null, "https://m"), Session(config: config));
        installed.Should().Be(new RequiredAddinRow("Mystery", "", "https://m", "third-party", AddinRowStatus.Installed, "Installed", "Installed (Mystery.addin)"));

        var disabled = RequiredAddinRules.Describe(new NormalizedRequiredAddin("Gone", null, ""), Session(config: config));
        disabled.Status.Should().Be(AddinRowStatus.Disabled);
        disabled.StatusTooltip.Should().Be("Add-in is disabled (.addin.RSTdisabled) — enable via Restore All Add-ins");
        disabled.Origin.Should().Be("custom");

        // A "native" origin hides both the badge and the link.
        var native = RequiredAddinRules.Describe(new NormalizedRequiredAddin("Builtin", null, "https://b"), Session(config: config));
        native.InfoUrl.Should().BeNull();
        native.Origin.Should().BeNull();
        native.StatusTooltip.Should().Be("Installed (Builtin)");
    }

    [Fact]
    public void The_get_user_config_stub_never_reaches_Installed_or_Disabled()
    {
        var rows = new[] { "Lumion", "Kinship", "Mystery" }
            .Select(t => RequiredAddinRules.Describe(new NormalizedRequiredAddin(t, null, ""), Session(config: UserAddinConfig.Empty)));
        rows.Should().OnlyContain(r => r.Status == AddinRowStatus.NotFound && r.Origin == null);
    }

    // ── getNotFoundAddins and the warning ────────────────────────────────────

    [Fact]
    public void Not_found_skips_native_loaded_and_configured_add_ins()
    {
        var profile = new Profile
        {
            RequiredAddins =
            [
                new RequiredAddin { TabName = "Architecture" },
                new RequiredAddin { TabName = "Kinship" },
                new RequiredAddin { TabName = "Lumion" },
                new RequiredAddin { TabName = "Configured" },
            ],
        };
        var config = new UserAddinConfig(new Dictionary<string, UserAddinConfigEntry> { ["Configured"] = new(false, null, null) });
        var notFound = RequiredAddinRules.NotFound(profile, Session(loaded: [Loaded("Kinship.addin")], config: config));
        notFound.Select(a => a.TabName).Should().Equal("Lumion");
    }

    [Fact]
    public void Not_found_warning_names_the_version_or_your_version()
    {
        RequiredAddinRules.NotFoundWarning("Lumion", "2026").Should().Be(
            "Lumion not found. If you experience issues with using tools from this add-in please install for Revit 2026 and retry.");
        RequiredAddinRules.NotFoundWarning("Lumion", "").Should().Contain("please install for Revit your version and retry.");
    }
}
