// rst-c's own (not vendored): what Search-It can find (v3.4). It must be exactly what the Profile
// Builder's catalogue offers, under every name the Builder shows: same source, same shaping function,
// so the two can never disagree.

using FluentAssertions;
using RST.Core.Builder;
using RST.Core.Scanning;
using RST.Core.Search;
using RST.Tests.Scanning;
using Xunit;

namespace RST.Tests.Search;

public class SearchIndexTests
{
    private static ScannedCommand Scanned(string id, string name, params (string Tab, string Panel, string Label)[] homes) =>
        new(id, name, CommandOrigin.Native, null, null, homes[0].Tab, homes[0].Panel,
            homes.Select(h => new CommandPlacement(h.Tab, h.Panel, h.Label, null)).ToArray());

    [Theory]
    [InlineData(2026)]
    [InlineData(2027)]
    public void Search_It_offers_exactly_the_commands_the_Builder_catalogue_offers(int year)
    {
        var scanned = new RibbonCapture(year).Merge();
        var builder = CatalogCommand.FromScanned(scanned);
        var tools = SearchIndex.Build(scanned);

        tools.Select(t => t.Id).Should().OnlyHaveUniqueItems();
        tools.Select(t => t.Id).Should().BeEquivalentTo(builder.Select(c => c.Id).Distinct());
        tools.Should().HaveCountGreaterThan(300, "the capture holds Revit's whole ribbon");
        // Every Builder row is a place of its tool, in the Builder's order, with the Builder's label.
        tools.Sum(t => t.Places.Count).Should().Be(builder.Count);
        foreach (var tool in tools)
            tool.Places.Select(p => p.Label).Should().Equal(builder.Where(c => c.Id == tool.Id).Select(c => c.Label));
    }

    /// <summary>
    /// Label coverage, not just folding: typing any name the Builder lists a command under finds that
    /// command, shown under that very name. (Before, only a command's first name could be found: "Room
    /// Tag" and "MEP Fabrication Parts" could not.)
    /// </summary>
    [Theory]
    [InlineData(2026)]
    [InlineData(2027)]
    public void Every_name_the_Builder_lists_finds_its_command_under_that_name(int year)
    {
        var scanned = new RibbonCapture(year).Merge();
        var search = new ToolSearch(SearchIndex.Build(scanned));
        var rows = CatalogCommand.FromScanned(scanned);
        rows.Select(r => r.Id).Distinct().Count().Should().BeLessThan(rows.Select(r => (r.Id, r.Label)).Distinct().Count(),
            "the capture holds commands with more than one name");

        var missed = new List<string>();
        foreach (var (id, label) in rows.Select(r => (r.Id, r.Label)).Distinct())
        {
            var hit = search.Search(null, label).FirstOrDefault(h => h.Tool.Id == id);
            if (hit is null || hit.Name != label || hit.Match?.Kind != MatchKind.Exact) missed.Add($"{id}: \"{label}\" → {hit?.Name ?? "(not found)"}");
        }
        missed.Should().BeEmpty();
    }

    [Theory]
    [InlineData("ID_OBJECTS_ROOM_TAG", "Tag Room", "Architecture › Room & Area", "Room Tag", "Annotate › Tag")]
    [InlineData("ID_OBJECTS_AREA_TAG", "Tag Area", "Architecture › Room & Area", "Area Tag", "Annotate › Tag")]
    public void The_2026_ribbon_finds_a_command_by_either_of_its_names(string id, string first, string firstPlace, string other, string otherPlace)
    {
        var search = new ToolSearch(SearchIndex.Build(new RibbonCapture(2026).Merge()));

        var byOther = search.Search(null, other)[0];
        byOther.Tool.Id.Should().Be(id);
        byOther.Name.Should().Be(other);
        byOther.Place.Location.Should().Be(otherPlace);
        byOther.Tool.OtherPlaces.Should().BeGreaterThan(0);

        var byFirst = search.Search(null, first)[0];
        byFirst.Tool.Id.Should().Be(id);
        byFirst.Name.Should().Be(first);
        byFirst.Place.Location.Should().Be(firstPlace);

        // One row for the command, whatever is typed.
        search.Search(null, "tag").Count(h => h.Tool.Id == id).Should().BeLessThanOrEqualTo(1);
        search.Search(null, null).Count(h => h.Tool.Id == id).Should().Be(1);
    }

    /// <summary>
    /// ID_TOGGLE_FABPART_BROWSER is "Fabrication Part" on Systems › Fabrication and "MEP Fabrication Parts" in
    /// View › Windows › User Interface. The second is a checklist entry, which the ribbon walk does not
    /// collect, so the catalogue (and the Builder) holds the command under its Systems name only. Search-It
    /// follows the catalogue: it finds the command by that name, and offers no name the Builder does not.
    /// </summary>
    [Fact]
    public void A_name_the_catalogue_does_not_hold_is_not_invented()
    {
        var scanned = new RibbonCapture(2026).Merge();
        var builder = CatalogCommand.FromScanned(scanned).Where(c => c.Id == "ID_TOGGLE_FABPART_BROWSER").ToList();
        builder.Select(c => c.Label).Should().Equal("Fabrication Part");

        var search = new ToolSearch(SearchIndex.Build(scanned));
        var hit = search.Search(null, "fabrication part")[0];
        hit.Tool.Id.Should().Be("ID_TOGGLE_FABPART_BROWSER");
        hit.Place.Location.Should().Be("Systems › Fabrication");
        search.Search(null, "fabrication parts")[0].Tool.Id.Should().Be("ID_TOGGLE_FABPART_BROWSER", "one letter over is still found");
        search.Search(null, "MEP Fabrication Parts").Should().NotContain(h => h.Name == "MEP Fabrication Parts");
    }

    [Theory]
    [InlineData(2026)]
    [InlineData(2027)]
    public void What_the_catalogue_leaves_out_cannot_be_searched(int year)
    {
        var capture = new RibbonCapture(year);
        var ids = SearchIndex.Build(capture.Merge()).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);

        // SC-051: Revit won't let add-ins post Text Types, so the Builder does not list it; nor does Search-It.
        capture.Rows.Should().Contain(r => r.Id == "ID_SETTINGS_TEXT");
        ids.Should().NotContain("ID_SETTINGS_TEXT");
        ids.Should().OnlyContain(id => PostCommandRule.LetsAddinsPost(id, capture.SeedIds.Contains));
        // Tools that live only on a mode tab (the family editor) are not offered either.
        var modeOnly = capture.Rows.GroupBy(r => r.Id)
            .Where(g => g.All(r => ModeRestrictedTabs.Contains(r.Tab) || ModeRestrictedTabs.ContainsId(r.TabId)))
            .Select(g => g.Key).ToList();
        modeOnly.Should().NotBeEmpty();
        ids.Should().NotIntersectWith(modeOnly);
    }

    [Fact]
    public void A_real_ribbon_finds_Align_from_a_typo_and_says_where_it_is()
    {
        var search = new ToolSearch(SearchIndex.Build(new RibbonCapture(2026).Merge()));
        var top = search.Search(null, "algn")[0];
        top.Tool.Id.Should().Be("ID_ALIGN");
        top.Name.Should().Be("Align");
        top.Place.Location.Should().Be("Modify › Modify");
    }

    [Fact]
    public void A_tool_in_several_places_is_one_tool_that_keeps_every_place()
    {
        var tools = SearchIndex.Build(
        [
            Scanned("ID_OBJECTS_WALL", "Wall: Architectural",
                ("Architecture", "Build", "Wall: Architectural"), ("Structure", "Structure", "Wall: Architectural"), ("Massing & Site", "Model by Face", "Wall")),
            Scanned("ID_ALIGN", "Align", ("Modify", "Modify", "Align")),
        ]);

        tools.Should().HaveCount(2);
        var wall = tools[0];
        wall.Id.Should().Be("ID_OBJECTS_WALL");
        wall.DisplayName.Should().Be("Wall: Architectural");
        wall.Location.Should().Be("Architecture › Build");
        wall.OtherPlaces.Should().Be(2);
        wall.Places.Should().Equal(
            new SearchPlace("Wall: Architectural", "Architecture", "Build"),
            new SearchPlace("Wall: Architectural", "Structure", "Structure"),
            new SearchPlace("Wall", "Massing & Site", "Model by Face"));
        tools[1].OtherPlaces.Should().Be(0);
    }

    [Fact]
    public void The_same_id_scanned_twice_is_still_one_tool()
    {
        var tools = SearchIndex.Build(
        [
            Scanned("ID_ALIGN", "Align", ("Modify", "Modify", "Align")),
            Scanned("ID_ALIGN", "Align again", ("View", "Graphics", "Align again")),
        ]);
        var tool = tools.Should().ContainSingle().Subject;
        tool.DisplayName.Should().Be("Align");
        tool.Places.Select(p => p.Label).Should().Equal("Align", "Align again");
    }

    [Fact]
    public void An_add_in_on_a_host_tab_shows_its_real_tab_and_panel()
    {
        // The Builder groups a host tab's commands by panel; "where it lives" is still Add-Ins › the panel.
        var tools = SearchIndex.Build([Scanned("CustomCtrl_%CustomCtrl_%Add-Ins%Kinship%Open", "Open Library", ("Add-Ins", "Kinship", "Open Library"))]);
        tools.Single().Location.Should().Be("Add-Ins › Kinship");
    }

    [Fact]
    public void The_old_single_placement_shape_and_missing_places_are_handled()
    {
        var tools = SearchIndex.Build(
        [
            new ScannedCommand("ID_A", "Alpha", CommandOrigin.Native, null, null, "Manage", "Settings"),
            new ScannedCommand("ID_B", "Beta", CommandOrigin.Native, null, null, "Manage", null),
            new ScannedCommand("ID_C", "", CommandOrigin.Native, null, null, null, null),
        ]);
        tools.Select(t => t.Location).Should().Equal("Manage › Settings", "Manage", "");
        tools[2].DisplayName.Should().Be("ID_C", "a nameless command shows its id, as in the Builder");
        SearchIndex.Build(null).Should().BeEmpty();
        SearchIndex.FromCatalog(null).Should().BeEmpty();
    }
}
