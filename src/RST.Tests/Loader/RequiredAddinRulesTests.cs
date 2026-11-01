// rst-c's own (not vendored): RequiredAddinRules (spec #16, WU3, AC5), the C# port of
// profile_loader.html's normalizeRequiredAddins, BUILTIN_TABS, the add-in row and
// getNotFoundAddins; the row states (Native, Loaded, Disabled, Not Found, Locked) from the
// add-in inventory of spec #18 (feature #16, In Scope 7, T-B7).

using System.Text.Json;
using FluentAssertions;
using RST.Core.Activation;
using RST.Core.Loader;
using RST.Core.Profiles;
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

    private static InventoryItem Installed(string name, bool off = false, string? fileName = null, AdminLock adminLock = AdminLock.None, bool renamed = false) =>
        FakeAddinSwitching.Item(2026, name, fileName: fileName, off: off, adminLock: adminLock, renamed: renamed);

    private static AddinSessionContext Session(string[]? tabs = null, InventoryItem[]? installed = null) =>
        new(tabs ?? [], Lookup, 2026, installed ?? []);

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

    // ── BUILTIN_TABS and the ribbon ──────────────────────────────────────────

    [Theory]
    [InlineData("Architecture")]
    [InlineData("Add-Ins")]
    [InlineData("RST")]
    [InlineData("FormIt Converter")]
    [InlineData("eTransmit")]
    [InlineData("Massing & Site")]
    public void Builtin_tabs_are_native(string tab)
    {
        RequiredAddinRules.IsNativeTab(tab).Should().BeTrue();
        RequiredAddinRules.Describe(new NormalizedRequiredAddin(tab, null, ""), Session()).Status.Should().Be(AddinRowStatus.Native);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(2025, false)]
    [InlineData(2026, false)]
    [InlineData(2027, true)]
    [InlineData(2028, true)]
    public void Concrete_row_uses_the_session_year(int? year, bool native)
    {
        var required = new NormalizedRequiredAddin("Concrete", "AcmeConcrete.addin", "https://acme.example");
        var item = FakeAddinSwitching.Item(year ?? 2026, "Acme Concrete", fileName: "AcmeConcrete.addin");
        var installed = new AddinSessionContext(runningYear: year, installed: [item]);
        RequiredAddinRules.IsNativeTab("Concrete", year).Should().Be(native);
        RequiredAddinRules.Describe(required, installed).Status.Should().Be(native ? AddinRowStatus.Native : AddinRowStatus.Loaded);
        RequiredAddinRules.Describe(required, new AddinSessionContext(runningYear: year)).Status
            .Should().Be(native ? AddinRowStatus.Native : AddinRowStatus.NotFound);
        RequiredAddinRules.Matches(required, installed).Should().Equal(native ? [] : new[] { item });
    }

    [Theory]
    [InlineData("architecture")]
    [InlineData("Kinship")]
    [InlineData(null)]
    public void Other_tabs_are_not_native(string? tab) => RequiredAddinRules.IsNativeTab(tab).Should().BeFalse();

    [Fact]
    public void A_live_ribbon_tab_containing_or_contained_by_the_name_counts_case_insensitively()
    {
        RequiredAddinRules.IsOnRibbon("Enscape", Session(tabs: ["Enscape™ 4.0"])).Should().BeTrue();
        RequiredAddinRules.IsOnRibbon("pyRevit Tools", Session(tabs: ["PYREVIT"])).Should().BeTrue();
        RequiredAddinRules.IsOnRibbon("Enscape", Session(tabs: ["", "Lumion"])).Should().BeFalse();
    }

    // ── the row states (T-B7) ────────────────────────────────────────────────

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
    public void Loaded_row_names_the_matched_add_in_and_keeps_the_info_link()
    {
        var session = Session(installed: [Installed("Enscape Render", fileName: "Enscape.addin")]);
        var row = RequiredAddinRules.Describe(new NormalizedRequiredAddin("Enscape", null, ""), session);
        row.FileColumn.Should().Be("Enscape Render", "no addinFile: the matched name fills the file column");
        row.Status.Should().Be(AddinRowStatus.Loaded);
        row.StatusLabel.Should().Be("Loaded");
        row.StatusTooltip.Should().Be("Installed and on for Revit 2026 (Enscape Render)");
        row.InfoUrl.Should().Be("https://enscape3d.com", "the lookup's URL backs a profile without one");
        row.Origin.Should().BeNull();
    }

    [Fact]
    public void Loaded_by_the_ribbon_alone_when_nothing_installed_matches()
    {
        var row = RequiredAddinRules.Describe(new NormalizedRequiredAddin("Enscape", "Enscape.addin", "https://e"), Session(tabs: ["Enscape"]));
        row.FileColumn.Should().Be("Enscape.addin");
        row.Status.Should().Be(AddinRowStatus.Loaded);
        row.StatusTooltip.Should().Be("Loaded in current Revit session");
    }

    [Fact]
    public void A_switched_off_add_in_is_Disabled_never_Loaded_even_with_its_tab_still_on_the_ribbon()
    {
        // v3.0 matched disabled manifests and showed a green "Loaded" (investigation B8, D10).
        foreach (var item in new[] { Installed("Kinship", off: true), Installed("Kinship", renamed: true) })
        {
            var row = RequiredAddinRules.Describe(new NormalizedRequiredAddin("Kinship", "Kinship.addin", ""), Session(tabs: ["Kinship"], installed: [item]));
            row.Status.Should().Be(AddinRowStatus.Disabled);
            row.StatusLabel.Should().Be("Disabled");
            row.StatusTooltip.Should().StartWith("Kinship is switched off for Revit 2026.");
        }
    }

    [Fact]
    public void An_add_in_locked_off_by_the_administrator_is_Locked_and_one_locked_on_is_Loaded()
    {
        var locked = RequiredAddinRules.Describe(new NormalizedRequiredAddin("DiRoots", null, ""),
            Session(installed: [Installed("DiRoots.One", fileName: "DiRoots.One.addin", adminLock: AdminLock.AlwaysDisabled)]));
        locked.Status.Should().Be(AddinRowStatus.Locked);
        locked.StatusLabel.Should().Be("Locked");
        locked.StatusTooltip.Should().Be("DiRoots.One is locked off by your administrator for Revit 2026.");

        var on = RequiredAddinRules.Describe(new NormalizedRequiredAddin("DiRoots", null, ""),
            Session(installed: [Installed("DiRoots.One", fileName: "DiRoots.One.addin", adminLock: AdminLock.AlwaysEnabled)]));
        on.Status.Should().Be(AddinRowStatus.Loaded);
        on.StatusTooltip.Should().EndWith("; locked on by your administrator");
    }

    [Fact]
    public void One_matching_add_in_on_is_enough_for_Loaded()
    {
        var row = RequiredAddinRules.Describe(new NormalizedRequiredAddin("Kinship", null, ""),
            Session(installed: [Installed("Kinship", off: true), Installed("Kinship Library", fileName: "KinshipLibrary.addin")]));
        row.Status.Should().Be(AddinRowStatus.Loaded);
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
    public void The_rows_use_the_running_year_only()
    {
        var inventory = FakeAddinSwitching.InventoryOf(FakeAddinSwitching.Item(2027, "Kinship"));
        var row = RequiredAddinRules.Describe(new NormalizedRequiredAddin("Kinship", null, ""), AddinSessionContext.For("2026", inventory, [], Lookup));
        row.Status.Should().Be(AddinRowStatus.NotFound);
        AddinSessionContext.For("", inventory, [], Lookup).RunningYear.Should().BeNull();
    }

    [Fact]
    public void The_rows_and_the_planner_use_the_same_matcher()
    {
        // The pill, the Confirm dialog and the commit can never disagree (D3): the same items match.
        var items = new[] { Installed("Lumion LiveSync", fileName: "LumionLiveSync.addin"), Installed("Enscape"), Installed("Twinmotion") };
        var session = Session(installed: items);
        foreach (var tab in new[] { "Lumion", "Enscape", "Kinship" })
        {
            var req = new RequiredAddin { TabName = tab };
            RequiredAddinRules.Matches(new NormalizedRequiredAddin(tab, null, ""), session)
                .Should().Equal(RequiredAddinMatch.Match(req, items), tab);
        }
    }

    [Fact]
    public void The_pills_pass_the_add_in_id_and_the_native_flag_to_the_same_matcher_as_the_planner()
    {
        // Review N2: a profile carrying addinId, or native, gets the same answer from the pill and the plan.
        var probe = Installed("Probe", fileName: "Whatever.addin") with { AddinIds = ["d4c6e3e0-0000-4000-8000-525354500001"] };
        var session = Session(installed: [probe, Installed("RSTProbe Tools", fileName: "RSTProbeTools.addin")]);
        var profile = new Profile
        {
            RequiredAddins =
            [
                new RequiredAddin { TabName = "Elsewhere", AddinId = "D4C6E3E0-0000-4000-8000-525354500001" },
                new RequiredAddin { TabName = "RSTProbe", Native = true },
            ],
        };

        var rows = RequiredAddinRules.Rows(profile, session);
        rows.Select(r => r.Status).Should().Equal(AddinRowStatus.Loaded, AddinRowStatus.Native);

        var normalized = RequiredAddinRules.Normalize(profile.RequiredAddins);
        normalized[0].AddinId.Should().Be("D4C6E3E0-0000-4000-8000-525354500001");
        foreach (var (row, req) in normalized.Zip(profile.RequiredAddins))
            RequiredAddinRules.Matches(row, session).Should().Equal(RequiredAddinMatch.Match(req, session.Installed));
        RequiredAddinMatch.RequiredItems(profile.RequiredAddins, FakeAddinSwitching.InventoryOf([.. session.Installed]))
            .Select(i => i.Name).Should().Equal("Probe");
    }

    // ── getNotFoundAddins and the warning ────────────────────────────────────

    [Fact]
    public void Not_found_skips_native_installed_and_ribbon_add_ins()
    {
        var profile = new Profile
        {
            RequiredAddins =
            [
                new RequiredAddin { TabName = "Architecture" },
                new RequiredAddin { TabName = "Kinship" },
                new RequiredAddin { TabName = "Lumion" },
                new RequiredAddin { TabName = "Off" },
                new RequiredAddin { TabName = "Enscape" },
            ],
        };
        var notFound = RequiredAddinRules.NotFound(profile, Session(tabs: ["Enscape"], installed: [Installed("Kinship"), Installed("Off", off: true)]));
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
