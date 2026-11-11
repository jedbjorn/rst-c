// rst-c's own (not vendored): what Search-It can find (v3.4). It must be exactly what the Profile
// Builder's catalogue offers: same source, same shaping function, so the two can never disagree.

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
        // Every Builder row is accounted for: one tool, plus its other places.
        tools.Sum(t => 1 + t.OtherPlaces).Should().Be(builder.Count);
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
        var top = search.Search(null, "algn")[0].Tool;
        top.Id.Should().Be("ID_ALIGN");
        top.DisplayName.Should().Be("Align");
        top.Location.Should().Be("Modify › Modify");
    }

    [Fact]
    public void A_tool_in_several_places_is_one_row_with_its_first_place_and_a_count()
    {
        var tools = SearchIndex.Build(
        [
            Scanned("ID_OBJECTS_WALL", "Wall: Architectural",
                ("Architecture", "Build", "Wall: Architectural"), ("Structure", "Structure", "Wall: Architectural"), ("Massing & Site", "Model by Face", "Wall")),
            Scanned("ID_ALIGN", "Align", ("Modify", "Modify", "Align")),
        ]);

        tools.Should().HaveCount(2);
        var wall = tools[0];
        wall.Should().Be(new SearchTool("ID_OBJECTS_WALL", "Wall: Architectural", "Architecture", "Build", 2));
        wall.Location.Should().Be("Architecture › Build");
        wall.MoreText.Should().Be("+2 more places");
        tools[1].OtherPlaces.Should().Be(0);
        tools[1].MoreText.Should().BeEmpty();
    }

    [Fact]
    public void The_same_id_scanned_twice_is_still_one_row()
    {
        var tools = SearchIndex.Build(
        [
            Scanned("ID_ALIGN", "Align", ("Modify", "Modify", "Align")),
            Scanned("ID_ALIGN", "Align again", ("View", "Graphics", "Align again")),
        ]);
        tools.Should().ContainSingle().Which.Should().Be(new SearchTool("ID_ALIGN", "Align", "Modify", "Modify", 1));
        tools[0].MoreText.Should().Be("+1 more place");
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
