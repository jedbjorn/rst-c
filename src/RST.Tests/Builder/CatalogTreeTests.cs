// rst-c's own (not vendored): the Builder's catalogue index and filter (spec #16, WU4, AC5),
// against profile_builder.html indexCatalog / buildTabEl / renderCatalog, and get_catalog's
// host-tab promotion (LoaderBridge.GetCatalog).

using FluentAssertions;
using RST.Core.Builder;
using RST.Core.Scanning;
using RST.Tests.Scanning;
using Xunit;

namespace RST.Tests.Builder;

public class CatalogTreeTests
{
    private static CatalogCommand Cmd(string id, string name, string? tab, string? panel, string origin = "ThirdParty") =>
        new(id, name, origin, tab, panel);

    private static readonly CatalogCommand[] Sample =
    [
        Cmd("ID_WALL", "Wall", "Architecture", "Build", "Native"),
        Cmd("ID_DOOR", "Door", "Architecture", "Build", "Native"),
        Cmd("ID_LEVEL", "Level", "Architecture", "Datum", "Native"),
        Cmd("ID_BEAM", "Beam", "Structure", "Structure", "Native"),
        Cmd("Kin.Open", "Open Library", "Kinship", null),
        Cmd("Ens.Start", "Start Enscape", "Enscape", "Enscape"),
        Cmd("Ens.Sync", "Live Sync", "Enscape", "Enscape"),
        Cmd("Ens.Assets", "Asset Library", "Enscape", "Assets"),
        Cmd("Odd", "", null, null, "Custom"),
    ];

    private static string Shape(CatalogNode n) => n.Label + (n.Children.Count == 0 ? "" : "[" + string.Join(",", n.Children.Select(Shape)) + "]");

    [Theory]
    [InlineData(2026)]
    [InlineData(2027)]
    public void Captured_placements_have_unique_keys_distinct_totals_and_no_Create_tab(int year)
    {
        var scanned = new RibbonCapture(year).Merge();
        var entries = CatalogCommand.FromScanned(scanned);
        var tree = CatalogTree.Build(entries);
        var commands = tree.NativeGroup!.Children.SelectMany(t => t.Children).SelectMany(p => p.Children).ToArray();
        commands.Select(c => c.Key).Should().OnlyHaveUniqueItems();
        commands.Should().HaveCount(scanned.Sum(c => c.Placements!.Count));
        tree.CommandCount.Should().Be(scanned.Count);
        tree.Filter("").Count(tree.NativeGroup).Should().Be(scanned.Count);
        tree.NativeGroup.Children.Should().NotContain(t => t.Label == "Create");
        foreach (var tab in tree.NativeGroup.Children)
        {
            tree.Filter("").Count(tab).Should().Be(entries.Count(e => e.SourceTab == tab.Label));
            foreach (var panel in tab.Children)
                tree.Filter("").Count(panel).Should().Be(panel.Children.Count);
        }
        var wall = tree.Filter("ID_OBJECTS_WALL");
        commands.Where(wall.IsVisible).Should().HaveCount(2);
        wall.Count(tree.NativeGroup).Should().Be(1);
    }

    [Fact]
    public void Eligible_Select_commands_are_listed_under_every_tab_and_filter_matches_each_label()
    {
        var entries = CatalogCommand.FromScanned([
            new ScannedCommand("ID_SHARED", "Select home", CommandOrigin.Native, null, null, "Architecture", "Select",
                [new("Architecture", "Select", "First home", "Architecture"),
                 new("Structure", "Select", "Second home", "Structure"),
                 new("Modify", "Select", "Third home", "Modify")])]);
        var tree = CatalogTree.Build(entries);
        tree.CommandCount.Should().Be(1);
        var group = tree.NativeGroup!;
        tree.Filter("").Count(group).Should().Be(1);
        group.Children.Should().HaveCount(3);
        foreach (var tab in group.Children)
        {
            var panel = tab.Children.Single();
            panel.Label.Should().Be("Select");
            var command = panel.Children.Single();
            command.Key.Should().Be($"cmd:{tab.Label}/Select/ID_SHARED");
            tree.Filter(command.Label).Count(group).Should().Be(1);
            tree.Filter(command.Label).IsVisible(command).Should().BeTrue();
        }
        tree.Filter("First home").IsVisible(group.Children.Single(t => t.Label == "Structure")).Should().BeFalse();
        var byId = tree.Filter("id_shared");
        group.Children.Should().OnlyContain(t => byId.IsVisible(t));
    }

    [Fact]
    public void Host_promotion_runs_for_each_home_and_keys_use_literal_tabs()
    {
        var entries = CatalogCommand.FromScanned([
            new ScannedCommand("CustomCtrl_A", "Go", CommandOrigin.Custom, "Acme.addin", "Acme.dll", "Add-Ins", "Acme",
                [new("Add-Ins", "Acme", "First", "Add-Ins"), new("FormIt", "Acme", "Second", "FormIt")])], out var promoted);
        promoted.Should().Be(2);
        entries.Select(e => e.SourceTab).Should().Equal("Acme", "Acme");
        entries.Select(e => e.HostTab).Should().Equal("Add-Ins", "FormIt");
        entries.Select(e => e.Label).Should().Equal("First", "Second");
        entries.Should().OnlyContain(e => e.AddinFile == "Acme.addin" && e.AssemblyPath == "Acme.dll");
        var tree = CatalogTree.Build(entries);
        tree.CommandCount.Should().Be(1);
        tree.Roots[0].Children[0].Children.Select(c => c.Key).Should().Equal("cmd:Add-Ins/Acme/CustomCtrl_A", "cmd:FormIt/Acme/CustomCtrl_A");
    }

    [Fact]
    public void Native_commands_group_under_Autodesk_Native_and_other_tabs_sit_at_the_top_sorted()
    {
        var tree = CatalogTree.Build(Sample);
        tree.CommandCount.Should().Be(9);
        tree.Roots.Select(r => r.Label).Should().Equal("Autodesk Native", "(unknown)", "Enscape", "Kinship");
        Shape(tree.Roots[0]).Should().Be("Autodesk Native[Architecture[Build[Wall,Door],Datum[Level]],Structure[Structure[Beam]]]");
        // Panels sorted ordinal ("Assets" before "Enscape"); commands keep catalogue order.
        Shape(tree.Roots[2]).Should().Be("Enscape[Assets[Asset Library],Enscape[Start Enscape,Live Sync]]");
        // A command with no tab or panel reads "(unknown)"; with no display name its label is the id.
        Shape(tree.Roots[1]).Should().Be("(unknown)[(unknown)[Odd]]");
        Shape(tree.Roots[3]).Should().Be("Kinship[(unknown)[Open Library]]");
        tree.NativeGroup.Should().BeSameAs(tree.Roots[0]);
    }

    [Fact]
    public void Sorting_is_ordinal_as_JavaScript_sort()
    {
        var tree = CatalogTree.Build([Cmd("1", "a", "beta", "p"), Cmd("2", "b", "Alpha", "p"), Cmd("3", "c", "Zeta", "p"), Cmd("4", "d", "_x", "p")]);
        tree.Roots.Select(r => r.Label).Should().Equal("Alpha", "Zeta", "_x", "beta");
        tree.NativeGroup.Should().BeNull("no command is native");
    }

    [Fact]
    public void Keys_follow_the_page_tab_and_panel_keys()
    {
        var tree = CatalogTree.Build(Sample);
        var enscape = tree.Roots[2];
        enscape.Key.Should().Be("tab:Enscape");
        enscape.Children[0].Key.Should().Be("panel:Enscape/Assets");
        tree.Roots[0].Key.Should().Be(CatalogTree.NativeGroupKey);
        tree.Roots[0].Children[0].Key.Should().Be("tab:Architecture");
    }

    [Fact]
    public void An_empty_filter_counts_every_command()
    {
        var tree = CatalogTree.Build(Sample);
        var f = tree.Filter("   ");
        f.IsActive.Should().BeFalse();
        f.Count(tree.Roots[0]).Should().Be(4);
        f.Count(tree.Roots[2]).Should().Be(3);
        f.Count(tree.Roots[2].Children[1]).Should().Be(2);
        tree.Roots.Should().OnlyContain(r => f.IsVisible(r));
    }

    [Fact]
    public void The_filter_matches_name_or_id_case_insensitively_and_counts_matches_per_node()
    {
        var tree = CatalogTree.Build(Sample);
        var f = tree.Filter("  LIB ");
        f.Query.Should().Be("lib");
        f.IsActive.Should().BeTrue();
        f.AnyMatch.Should().BeTrue();
        var enscape = tree.Roots[2];
        f.Count(enscape).Should().Be(1);
        f.IsVisible(enscape.Children[0]).Should().BeTrue();   // Assets: "Asset Library"
        f.IsVisible(enscape.Children[1]).Should().BeFalse();  // Enscape panel: no match
        f.Count(tree.Roots[3]).Should().Be(1);                // Kinship: "Open Library"
        f.IsVisible(tree.Roots[0]).Should().BeFalse("no native command matches: the group is left out");

        var byId = tree.Filter("id_be");
        byId.Count(tree.Roots[0]).Should().Be(1);
        byId.IsVisible(tree.Roots[0].Children[1]).Should().BeTrue();
        byId.IsVisible(tree.Roots[0].Children[0]).Should().BeFalse();
    }

    [Fact]
    public void No_match_reports_it()
    {
        var tree = CatalogTree.Build(Sample);
        var f = tree.Filter("zzz");
        f.AnyMatch.Should().BeFalse();
        tree.Roots.Should().OnlyContain(r => !f.IsVisible(r));
    }

    [Fact]
    public void A_command_without_display_name_matches_by_id_only()
    {
        var c = Cmd("ABC_1", "", "T", "P");
        CatalogTree.Matches(c, "abc").Should().BeTrue();
        CatalogTree.Matches(c, "").Should().BeTrue();
        CatalogTree.Matches(c, "zz").Should().BeFalse();
    }

    [Fact]
    public void An_empty_catalogue_has_no_roots()
    {
        var tree = CatalogTree.Build([]);
        tree.Roots.Should().BeEmpty();
        tree.CommandCount.Should().Be(0);
        tree.Filter("x").AnyMatch.Should().BeFalse();
    }

    [Fact]
    public void Get_catalog_promotes_host_tab_commands_to_their_panel()
    {
        var scanned = new[]
        {
            new ScannedCommand("K", "Kinship Open", CommandOrigin.ThirdParty, "Kinship.addin", @"C:\k.dll", "Add-Ins", "Kinship"),
            new ScannedCommand("H", "Bare", CommandOrigin.ThirdParty, null, null, "Add-Ins", null),
            new ScannedCommand("W", "Wall", CommandOrigin.Native, null, null, "Architecture", "Build"),
        };
        var catalog = CatalogCommand.FromScanned(scanned, out var promoted);
        promoted.Should().Be(1);
        catalog[0].Should().Be(new CatalogCommand("K", "Kinship Open", "ThirdParty", "Kinship", null, "Add-Ins", "Kinship", "Kinship.addin", @"C:\k.dll"));
        catalog[1].SourceTab.Should().Be("Add-Ins", "a host-tab command without a panel keeps the host tab");
        catalog[1].HostTab.Should().Be("Add-Ins");
        catalog[2].Should().Be(new CatalogCommand("W", "Wall", "Native", "Architecture", "Build"));
    }
}
