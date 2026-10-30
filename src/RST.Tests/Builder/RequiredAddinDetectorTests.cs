// rst-c's own (not vendored): the xUnit port of tests/ui/required-addins.test.mjs (spec #16, WU4,
// AC5), every case, against RequiredAddinDetector (the C# port of profile_builder.html
// computeRequiredAddins) and the shipped addin_lookup.json, plus the rest of the rule.
//
// The bug the Node test locked down: built-in Revit tools (origin Native) were recorded as required
// third-party add-ins because detection relied on a BUILTIN_PANELS allowlist that can never list
// every built-in panel. A profile placing "Color Schemes" (panel "Room & Area") and "Color Fill
// Legend" (panel "Color Fill") loaded with two phantom "Not Installed" entries. The command's origin
// is authoritative: a Native (or Autodesk, SC-001) command is never a dependency.

using System.IO;
using FluentAssertions;
using RST.Core.Builder;
using RST.Core.Loader;
using RST.Core.Profiles;
using Xunit;

namespace RST.Tests.Builder;

public class RequiredAddinDetectorTests
{
    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RST-C.sln"))) dir = dir.Parent;
        dir.Should().NotBeNull("the repo root (RST-C.sln) must be found from the test output");
        return dir!.FullName;
    }

    /// <summary>The shipped Assets/lookup/addin_lookup.json, as the Node test read it.</summary>
    internal static IReadOnlyDictionary<string, AddinLookupEntry> ShippedLookup() =>
        AddinLookupService.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "src", "RST.UI", "Assets", "lookup", "addin_lookup.json")));

    private static Slot Tool(string commandId, string? tab, string? panel = null) =>
        new() { SlotType = "tool", CommandId = commandId, SourceTab = tab, SourcePanel = panel };

    private static CatalogCommand Cmd(string id, string? tab, string? panel, string origin) => new(id, "", origin, tab, panel);

    // The profile that reproduced the bug on the VM (the Node test's fixture).
    private static Profile BugProfile() => new()
    {
        Panels =
        [
            new Panel
            {
                Name = "Color Tools",
                Slots =
                [
                    Tool("CustomCtrl_%CustomCtrl_%pyRevit%Analysis%ColorSplasher", "pyRevit", "Analysis"),
                    Tool("ID_SETTINGS_COLORFILLSCHEMES", "Architecture", "Room & Area"),
                    Tool("ID_OBJECTS_ROOM_FILL", "Annotate", "Color Fill"),
                ],
            },
            new Panel { Name = "Kinship Library", Slots = [Tool("CustomCtrl_%Add-Ins%Kinship%KinshipUploadThis2Library", "Kinship")] },
            new Panel { Name = "Panel 3", Slots = [Tool("CustomCtrl_%DiRootsOne%Data IO%OneParameter", "DiRootsOne", "Data IO")] },
            // An Autodesk-published tool on its own tab and panel, in neither allowlist (SC-001).
            new Panel { Name = "Autodesk Tools", Slots = [Tool("ID_DYNAMO_PLAYER", "Dynamo", "Visual Programming Extras")] },
        ],
    };

    private static readonly CatalogCommand[] BugCatalog =
    [
        Cmd("ID_SETTINGS_COLORFILLSCHEMES", "Architecture", "Room & Area", "Native"),
        Cmd("ID_OBJECTS_ROOM_FILL", "Annotate", "Color Fill", "Native"),
        Cmd("ID_DYNAMO_PLAYER", "Dynamo", "Visual Programming Extras", "Autodesk"),
        Cmd("CustomCtrl_%CustomCtrl_%pyRevit%Analysis%ColorSplasher", "pyRevit", "Analysis", "Custom"),
        Cmd("CustomCtrl_%Add-Ins%Kinship%KinshipUploadThis2Library", "Kinship", null, "Custom"),
        Cmd("CustomCtrl_%DiRootsOne%Data IO%OneParameter", "DiRootsOne", "Data IO", "Custom"),
    ];

    private static List<string> BugTabs() =>
        RequiredAddinDetector.Compute(BugProfile(), BugCatalog, ShippedLookup()).Select(r => r.TabName).OrderBy(t => t, StringComparer.Ordinal).ToList();

    // ---- the Node test's checks, one for one ----

    [Fact]
    public void Excludes_the_built_in_panel_Room_and_Area() => BugTabs().Should().NotContain("Room & Area");

    [Fact]
    public void Excludes_the_built_in_panel_Color_Fill() => BugTabs().Should().NotContain("Color Fill");

    [Fact]
    public void Excludes_the_Autodesk_origin_tab_Dynamo() => BugTabs().Should().NotContain("Dynamo");

    [Fact]
    public void Excludes_the_Autodesk_origin_panel_Visual_Programming_Extras() => BugTabs().Should().NotContain("Visual Programming Extras");

    [Fact]
    public void Keeps_third_party_pyRevit() => BugTabs().Should().Contain("pyRevit");

    [Fact]
    public void Keeps_third_party_Kinship() => BugTabs().Should().Contain("Kinship");

    [Fact]
    public void Keeps_third_party_DiRootsOne() => BugTabs().Should().Contain("DiRootsOne");

    [Fact]
    public void Emits_exactly_the_three_real_third_party_tabs() => BugTabs().Should().Equal("DiRootsOne", "Kinship", "pyRevit");

    [Fact]
    public void Origin_Native_short_circuits_regardless_of_panel_name()
    {
        var profile = new Profile { Panels = [new Panel { Slots = [Tool("ID_FOO", "Manage", "Some Unlisted Panel")] }] };
        RequiredAddinDetector.Compute(profile, [Cmd("ID_FOO", "Manage", "Some Unlisted Panel", "Native")], ShippedLookup()).Should().BeEmpty();
    }

    [Fact]
    public void Origin_Autodesk_short_circuits_regardless_of_tab_and_panel_name()
    {
        var profile = new Profile { Panels = [new Panel { Slots = [Tool("ID_BAR", "Generative Design", "Studies")] }] };
        RequiredAddinDetector.Compute(profile, [Cmd("ID_BAR", "Generative Design", "Studies", "Autodesk")], ShippedLookup()).Should().BeEmpty();
    }

    // ---- the rest of computeRequiredAddins ----

    [Fact]
    public void Entries_are_enriched_from_the_lookup_and_keep_first_seen_order_tabs_before_panels()
    {
        var profile = new Profile
        {
            Panels =
            [
                new Panel { Slots = [Tool("a", "Architecture", "DiRoots Suite"), Tool("b", "pyRevit", "Analysis"), Tool("c", "Kinship"), Tool("d", "pyRevit", "Other")] },
            ],
        };
        var result = RequiredAddinDetector.Compute(profile, [], ShippedLookup());
        result.Select(r => r.TabName).Should().Equal("pyRevit", "Kinship", "DiRoots Suite");
        result[0].AddinFile.Should().Be("pyRevit.addin");
        result[0].Url.Should().Be("https://github.com/pyrevitlabs/pyRevit/releases");
        result[1].AddinFile.Should().Be("Kinship.addin");
        result[1].Url.Should().Be("", "an entry whose url is empty bakes \"\"");
        // "DiRoots Suite" is not a key; the fuzzy pass finds key "DiRoots" inside it first.
        result[2].AddinFile.Should().Be("DiRoots.addin");
        result[2].Url.Should().Be("https://diroots.com");
    }

    [Fact]
    public void An_unknown_name_gets_no_file_and_an_empty_url()
    {
        var profile = new Profile { Panels = [new Panel { Slots = [Tool("x", "Zzqx Tools")] }] };
        var only = RequiredAddinDetector.Compute(profile, [], ShippedLookup()).Single();
        only.TabName.Should().Be("Zzqx Tools");
        only.AddinFile.Should().BeNull();
        only.Url.Should().Be("");
    }

    [Theory]
    [InlineData("Custom", null)]
    [InlineData("CustomCtrl_Something", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("Architecture", null)]
    [InlineData("Architecture", "Macros")]
    [InlineData("Manage", "External Tools")]
    [InlineData("Annotate", "Structure")]
    public void Custom_tabs_missing_tabs_and_built_in_panels_are_skipped(string? tab, string? panel)
    {
        var profile = new Profile { Panels = [new Panel { Slots = [Tool("x", tab, panel)] }] };
        RequiredAddinDetector.Compute(profile, [], ShippedLookup()).Should().BeEmpty();
    }

    [Fact]
    public void A_third_party_panel_on_a_built_in_tab_is_the_add_in()
    {
        var profile = new Profile { Panels = [new Panel { Slots = [Tool("x", "Add-Ins", "Enscape")] }] };
        var only = RequiredAddinDetector.Compute(profile, [Cmd("x", "Add-Ins", "Enscape", "ThirdParty")], ShippedLookup()).Single();
        only.TabName.Should().Be("Enscape");
        only.AddinFile.Should().Be("Enscape.addin");
    }

    [Fact]
    public void A_tool_uses_its_own_tab_and_the_catalogue_only_for_origin()
    {
        // The slot says "Lumion"; the catalogue (another tab) is consulted for the origin only.
        var profile = new Profile { Panels = [new Panel { Slots = [Tool("x", "Lumion")] }] };
        RequiredAddinDetector.Compute(profile, [Cmd("x", "Elsewhere", null, "ThirdParty")], ShippedLookup())
            .Select(r => r.TabName).Should().Equal("Lumion");
    }

    [Fact]
    public void Stack_tools_resolve_tab_panel_and_origin_through_the_catalogue_only()
    {
        var profile = new Profile
        {
            Panels = [new Panel { Slots = [new Slot { SlotType = "stack", Name = "S" }, new Slot { SlotType = "stack", Name = "Missing" }] }],
            Stacks = new()
            {
                ["S"] = new Stack
                {
                    Tools =
                    [
                        new Slot { Name = "a", CommandId = "a" },          // Revizto (catalogue)
                        new Slot { Name = "b", CommandId = "b" },          // native: skipped
                        new Slot { Name = "c", CommandId = "unknown" },   // not in the catalogue: skipped
                    ],
                },
            },
        };
        var catalog = new[] { Cmd("a", "Revizto", "Issues", "ThirdParty"), Cmd("b", "Architecture", "Build", "Native") };
        RequiredAddinDetector.Compute(profile, catalog, ShippedLookup()).Select(r => r.TabName).Should().Equal("Revizto");
    }

    [Fact]
    public void A_later_duplicate_catalogue_id_wins()
    {
        var profile = new Profile { Panels = [new Panel { Slots = [Tool("x", "Twinmotion")] }] };
        RequiredAddinDetector.Compute(profile, [Cmd("x", "Twinmotion", null, "ThirdParty"), Cmd("x", "Twinmotion", null, "Native")], ShippedLookup())
            .Should().BeEmpty();
    }

    [Fact]
    public void Enrich_matches_a_display_name_and_an_exact_key_first()
    {
        var lookup = ShippedLookup();
        RequiredAddinDetector.Enrich("Lumion", lookup).Should().Be(("Lumion.addin", "https://lumion.com"));
        // Key "DynamoPlayer" is exact; the fuzzy pass would have hit "Dynamo" first.
        RequiredAddinDetector.Enrich("DynamoPlayer", lookup).Should().Be(("DynamoPlayerForRevit.addin", ""));
        // "Hilti PROFIS" is not a key: key "Hilti" is inside it.
        RequiredAddinDetector.Enrich("Hilti PROFIS", lookup).AddinFile.Should().Be("Hilti.addin");
        // "BIM Track" is the display name of key "BIMTracker".
        RequiredAddinDetector.Enrich("bim track", lookup).AddinFile.Should().Be("BIMTracker.addin");
        RequiredAddinDetector.Enrich("anything", null).Should().Be(((string?)null, ""));
    }

    /// <summary>
    /// The VM's "VM Test" finding (WU5): ribbon-only built-in commands (no PostableCommand, so the
    /// scanner leaves their origin Unknown) on built-in tabs are recorded by their panel names
    /// ("Build", "Select", "Model"). This is v2.2.0's behavior too: the expected list below is what
    /// v2.2.0's own computeRequiredAddins (`git show v2.2.0:src/RST.UI/Assets/profile_builder.html`,
    /// run in Node over get_catalog's DTO of this same scanned catalogue, with v2.2.0's
    /// addin_lookup.json) returned. The scanner, get_catalog's reshaping and the lookup are unchanged
    /// since v2.2.0, so the WPF Builder reproduces it exactly; the behavior is pre-existing and kept.
    /// </summary>
    [Fact]
    public void Ribbon_only_built_in_commands_give_the_same_required_addins_as_v220()
    {
        var scanned = new[]
        {
            new RST.Core.Scanning.ScannedCommand("ID_OBJECTS_WALL", "Wall", RST.Core.Scanning.CommandOrigin.Native, null, null, "Architecture", "Build"),
            new RST.Core.Scanning.ScannedCommand("ID_OBJECTS_FLOOR", "Floor", RST.Core.Scanning.CommandOrigin.Native, null, null, "Architecture", "Build"),
            new RST.Core.Scanning.ScannedCommand("ID_INPLACE_WALL", "In-Place Wall", RST.Core.Scanning.CommandOrigin.Unknown, null, null, "Architecture", "Build"),
            new RST.Core.Scanning.ScannedCommand("ID_BUTTON_SELECT", "Modify", RST.Core.Scanning.CommandOrigin.Unknown, null, null, "Modify", "Select"),
            new RST.Core.Scanning.ScannedCommand("ID_PLACE_GROUP", "Place Model Group", RST.Core.Scanning.CommandOrigin.Unknown, null, null, "Architecture", "Model"),
            new RST.Core.Scanning.ScannedCommand("ID_DYNAMO_PLAYER", "Dynamo Player", RST.Core.Scanning.CommandOrigin.Autodesk, null, null, "Manage", "Visual Programming"),
            new RST.Core.Scanning.ScannedCommand("ID_INSIGHT", "Insight", RST.Core.Scanning.CommandOrigin.Autodesk, null, null, "Analyze", "Insight"),
            new RST.Core.Scanning.ScannedCommand("Enscape.Start", "Start Enscape", RST.Core.Scanning.CommandOrigin.Custom, null, null, "Enscape", "Enscape"),
            new RST.Core.Scanning.ScannedCommand("Kinship.Open", "Open Library", RST.Core.Scanning.CommandOrigin.Custom, null, null, "Add-Ins", "Kinship"),
        };
        var catalog = CatalogCommand.FromScanned(scanned);
        var profile = new Profile
        {
            Panels =
            [
                new Panel { Slots = catalog.Select(BuilderRules.ToolSlot).ToList() },
                new Panel { Slots = [BuilderRules.UrlSlot("Wiki", "https://example.com")] },
            ],
        };
        profile.Panels[1].Slots[0].IconFile = "pack:house_purple";

        var result = RequiredAddinDetector.Compute(profile, catalog, ShippedLookup());

        // v2.2.0's output, verbatim:
        // [{"tabName":"Enscape","addinFile":"Enscape.addin","url":"https://enscape3d.com"},
        //  {"tabName":"Kinship","addinFile":"Kinship.addin","url":""},
        //  {"tabName":"Build","addinFile":null,"url":""},{"tabName":"Select","addinFile":null,"url":""},
        //  {"tabName":"Model","addinFile":null,"url":""}]
        result.Select(r => (r.TabName, r.AddinFile, r.Url)).Should().Equal(
            ("Enscape", "Enscape.addin", "https://enscape3d.com"),
            ("Kinship", "Kinship.addin", ""),
            ("Build", (string?)null, ""),
            ("Select", (string?)null, ""),
            ("Model", (string?)null, ""));
        catalog.Where(c => c.Origin == "Native").Select(c => c.Id).Should().Equal("ID_OBJECTS_WALL", "ID_OBJECTS_FLOOR");
    }
}
