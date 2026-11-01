// SC-030: names and every ribbon home from the rc.3 captures, with #63 and SC-027 unchanged.

using FluentAssertions;
using RST.Core.Scanning;
using Xunit;

namespace RST.Tests.Scanning;

public class CatalogMergeTests
{
    [Theory]
    [InlineData(2026)]
    [InlineData(2027)]
    public void Native_names_follow_each_ribbon_placement(int year)
    {
        var fixture = new RibbonCapture(year);
        var catalog = fixture.Merge();
        catalog.Single(c => c.Id == "ID_OBJECTS_BEAM").DisplayName.Should().Be("Structural Framing: Beam");
        catalog.Single(c => c.Id == "ID_OBJECTS_WALL").DisplayName.Should().Be("Wall: Architectural");
        catalog.Single(c => c.Id == "ID_SPOT_ELEVATION").DisplayName.Should().Be("Spot Elevation");
        var openings = catalog.Single(c => c.Id == "ID_CREATE_WALL_OPENING_TB").Placements!;
        openings.Select(p => p.Tab).Should().Equal("Architecture", "Structure");
        openings.Should().OnlyContain(p => p.Label == "Wall Opening");

        foreach (var command in catalog)
        {
            command.EnumName.Should().Be(fixture.Seeds.Single(s => s.Id == command.Id).EnumName);
            var primary = command.Placements![0];
            (command.SourceTab, command.SourcePanel, command.DisplayName).Should().Be((primary.Tab, primary.Panel, primary.Label));
            foreach (var placement in command.Placements)
            {
                var row = fixture.Hits.First(h => h.Id == command.Id && h.SourceTab == placement.Tab
                    && h.SourcePanel == placement.Panel && !ModeRestrictedTabs.ContainsId(h.TabId));
                var raw = string.IsNullOrWhiteSpace(row.Name) ? row.Text : row.Name;
                raw.Should().NotBeNullOrWhiteSpace("these captures never need the enum fallback");
                var expected = string.Join(" ", raw!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                placement.Label.Should().Be(expected);
                if (expected != command.EnumName) placement.Label.Should().NotBe(command.EnumName);
            }
        }
    }

    [Theory]
    [InlineData(2026, 53, 47)]
    [InlineData(2027, 43, 37)]
    public void Structure_keeps_every_captured_catalog_id_except_the_six_rejected_by_63(int year, int before, int after)
    {
        var fixture = new RibbonCapture(year);
        string[] rejected = ["ID_BUTTON_SELECT", "ID_INPLACE_WALL", "ID_OBJECTS_PLACE_GROUP",
            "ID_STRUCTURAL_AUTO_CONNECTION", "ID_STRUCT_CONNECTIVITY_EDIT_MODE_START", "ID_STRUCT_CONNECTIVITY_RESET"];
        var expected = fixture.Rows.Where(r => r.Tab == "Structure" && fixture.SeedIds.Contains(r.Id)).Select(r => r.Id).Distinct().ToArray();
        expected.Should().HaveCount(after);
        fixture.Rows.Where(r => r.Tab == "Structure" && rejected.Contains(r.Id)).Select(r => r.Id).Distinct()
            .Should().BeEquivalentTo(rejected);
        (expected.Length + rejected.Length).Should().Be(before);
        fixture.Merge().Where(c => c.Placements!.Any(p => p.Tab == "Structure")).Select(c => c.Id)
            .Should().BeEquivalentTo(expected);
        fixture.Merge().Select(c => c.Id).Should().NotContain(rejected);
    }

    [Theory]
    [InlineData(2026)]
    [InlineData(2027)]
    public void Exactly_ten_mode_tab_ids_are_excluded_and_every_eligible_visible_home_survives(int year)
    {
        var fixture = new RibbonCapture(year);
        var excluded = fixture.Rows.Where(r => ModeRestrictedTabs.ContainsId(r.TabId)).Select(r => r.TabId).Distinct();
        excluded.Should().BeEquivalentTo("Home_Family", "Insert_AnnotationDetailModelMassConceptualProfileTrussFamily",
            "Annotate_ModelMassFamily", "View_Family", "Manage_Family", "Second_Modify",
            "InPlaceModelFamilyTab", "InPlaceMassFamilyTab", "InPlaceZoneFamilyTab", "FamilyEditorTab");
        var catalog = fixture.Merge();
        var placements = catalog.SelectMany(c => c.Placements!.Select(p => (c.Id, p.Tab, p.Panel, p.TabId))).ToArray();
        placements.Should().OnlyContain(p => p.Tab != "Create" && !ModeRestrictedTabs.ContainsId(p.TabId));
        foreach (var hit in fixture.Hits.Where(h => fixture.Rows.Any(r => r.TabId == h.TabId && r.Visible)))
            placements.Should().Contain((hit.Id, hit.SourceTab!, hit.SourcePanel!, hit.TabId));

        // AC3's View claim conflicts with SC-027: Properties is a checklist toggle on View.
        var properties = catalog.Single(c => c.Id == "ID_TOGGLE_PROPERTIES_PALETTE");
        properties.SourceTab.Should().Be("Modify");
        properties.Placements!.Select(p => p.Tab).Should().Equal("Modify");
        fixture.Rows.Should().Contain(r => r.Id == properties.Id && r.Tab == "View"
            && r.Type == "Autodesk.Windows.RibbonToggleButton" && r.Path.Contains("RibbonChecklistButton"));
        fixture.Hits.Should().NotContain(h => h.Id == properties.Id && h.SourceTab == "View");
        // Select's Modify is outside PostableCommand; its checkboxes are excluded by SC-027.
        catalog.SelectMany(c => c.Placements!).Should().NotContain(p => p.Panel == "Select");
    }

    [Theory]
    [InlineData(2026)]
    [InlineData(2027)]
    public void Launchers_follow_all_panel_items_and_duplicate_homes_keep_the_first(int year)
    {
        var fixture = new RibbonCapture(year);
        var command = fixture.Merge().Single(c => c.Id == "ID_STRUCTURAL_SETTINGS");
        command.Placements!.Select(p => p.Tab).Should().Equal("Manage", "Structure");
        command.SourceTab.Should().Be("Manage");
        foreach (var c in fixture.Merge())
            c.Placements!.Select(p => (p.Tab, p.Panel)).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Ribbon_only_Concrete_command_is_native_only_from_2027()
    {
        var fixture = new RibbonCapture(2027);
        var hit = fixture.Hits.Single(h => h.TabId == "Concrete" && h.Id == "ID_REBAR_PLACE");
        // An empty seed exercises ribbon classification instead of inheriting the seed's Native origin.
        CatalogMerge.Merge([], [hit], revitYear: 2027).Single().Origin.Should().Be(CommandOrigin.Native);
        CatalogMerge.Merge([], [hit], revitYear: 2026).Single().Origin.Should().Be(CommandOrigin.Unknown);
        CatalogMerge.Merge([], [hit]).Single().Origin.Should().Be(CommandOrigin.Unknown);
    }

    [Fact]
    public void Merge_preserves_seed_metadata_first_home_and_per_placement_labels_and_drops_headless()
    {
        ScannedCommand[] seed = [new("ID_W", "Old", CommandOrigin.Native, "native.addin", "native.dll", null, null, EnumName: "ArchitecturalWall"),
            new("ID_HEADLESS", "Headless", CommandOrigin.Native, null, null, null, null)];
        var hits = new[]
        {
            Hit("ID_W", "Wall", "Architecture", "Build", "Wall: Architectural"),
            Hit("ID_W", "Later duplicate", "Architecture", "Build"),
            Hit("ID_W", "Wall", "Structure", "Structure", "Structural home"),
            Hit("ID_EMPTY", "Empty", "", "P"),
            Hit("CustomCtrl_A", "  Add\tin  ", "Architecture", "Extra", "Other name"),
        };
        var merged = CatalogMerge.Merge(seed, hits);
        merged.Select(c => c.Id).Should().Equal("ID_W", "CustomCtrl_A");
        var wall = merged[0];
        wall.Placements!.Select(p => p.Label).Should().Equal("Wall: Architectural", "Structural home");
        wall.AddinFile.Should().Be("native.addin");
        wall.AssemblyPath.Should().Be("native.dll");
        wall.EnumName.Should().Be("ArchitecturalWall");
        merged[1].DisplayName.Should().Be("Add in");
        merged[1].Origin.Should().Be(CommandOrigin.Custom);
        merged[1].EnumName.Should().BeNull();
    }

    [Fact]
    public void Seed_aliases_keep_the_last_enum_name_without_duplicating_the_command()
    {
        var seed = new ScannedCommand("ID_ALIAS", "Old", CommandOrigin.Native, null, null, null, null, EnumName: "OldName");
        var merged = CatalogMerge.Merge([seed, seed with { EnumName = "NewName" }], [Hit(seed.Id, "", "T", "P")]);
        merged.Should().ContainSingle();
        merged[0].EnumName.Should().Be("NewName");
        merged[0].DisplayName.Should().Be("New Name");
    }

    private static RibbonHit Hit(string id, string text, string tab, string panel, string? name = null) =>
        new(id, text, RibbonContainerKind.Panel, RibbonLeafKind.Button, name, tab, panel, tab);
}

/// <summary>Trimmed capture rows replayed as hits, in scan order, after the existing collection rules.</summary>
internal sealed class RibbonCapture
{
    internal sealed record Row(string TabId, string Tab, string Panel, string Path, string Type, string Id,
        string Text, string Name, bool Visible, bool Contextual);

    public RibbonCapture(int year)
    {
        Seeds = Read($"seed-{year}.tsv").Select(c => new ScannedCommand(c[0], c[1], CommandOrigin.Native,
            null, null, null, null, EnumName: c[1])).ToArray();
        SeedIds = Seeds.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        Rows = Read($"ribbon-{year}.tsv").Select(c => new Row(c[0], c[1], c[2], c[3], c[4], c[5], c[6], c[7],
            bool.Parse(c[8]), bool.Parse(c[9]))).ToArray();
        Hits = Rows.Where(r => !r.Contextual).Select(ToHit).OfType<RibbonHit>()
            .Where(h => PostCommandRule.LetsAddinsPost(h.Id, SeedIds.Contains)
                && RibbonWalkRules.Decide(h.Leaf, h.Container) == RibbonLeafDecision.Collect)
            .OrderBy(h => h.Container == RibbonContainerKind.DialogLauncher).ToArray();
    }

    public ScannedCommand[] Seeds { get; }
    public HashSet<string> SeedIds { get; }
    public Row[] Rows { get; }
    public RibbonHit[] Hits { get; }
    public IReadOnlyList<ScannedCommand> Merge() => CatalogMerge.Merge(Seeds, Hits);

    private static IEnumerable<string[]> Read(string file) => File.ReadLines(Path.Combine(AppContext.BaseDirectory,
        "Fixtures", "Ribbon", file)).Where(l => !l.StartsWith('#')).Skip(1).Select(l => l.Split('\t'));

    private static RibbonHit? ToHit(Row row)
    {
        var leaf = row.Type switch
        {
            "Autodesk.Windows.RibbonButton" => RibbonLeafKind.Button,
            "Autodesk.Windows.RibbonToggleButton" => RibbonLeafKind.ToggleButton,
            "Autodesk.Windows.RibbonCheckBox" => RibbonLeafKind.CheckBox,
            "Autodesk.Windows.RibbonCommandItem" => RibbonLeafKind.CommandItem,
            "Autodesk.Windows.RibbonMenuItem" or "UIFramework.RvtLargeMenuItem" => RibbonLeafKind.MenuItem,
            "Autodesk.Windows.RibbonChecklistButton" or "UIFramework.SwitchWindowMenuButton" => RibbonLeafKind.Button,
            _ => (RibbonLeafKind?)null,
        };
        if (leaf is null) return null;
        var container = row.Path == "DialogLauncher" ? RibbonContainerKind.DialogLauncher
            : row.Path.Contains("RadioGroup") ? RibbonContainerKind.RadioGroup
            : row.Path.Contains("RibbonChecklistButton") ? RibbonContainerKind.Checklist
            : row.Path.Contains("RvtLargeMenuItem") ? RibbonContainerKind.SubMenu
            : row.Path.Contains("SplitButton") ? RibbonContainerKind.SplitButton
            : row.Path.Contains("RowPanel") || row.Path.Contains("FoldPanel") ? RibbonContainerKind.Stack
            : RibbonContainerKind.Panel;
        return new RibbonHit(row.Id, row.Text, container, leaf.Value, row.Name, row.Tab, row.Panel, row.TabId);
    }
}
