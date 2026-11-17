// Spec #35 (Search-It: runs per file type): the use cases of AC5 and AC6 on the committed census tables, as the
// embedded index, runs-in list and contexts give them. The live gate is what the census recorded Revit's own check
// answering in that context (full.tsv's gate column); the VM run checks the same cases in Revit itself.

using FluentAssertions;
using RST.Core.Census;
using RST.Core.Scanning;
using RST.Core.Search;
using Xunit;

namespace RST.Tests.Search;

public class SearchRunnableCensusTests
{
    private static RibbonHit Hit(string id, string text, string tabId, string panel, string path, bool postable = true) =>
        new(id, text, RibbonContainerKind.Panel, RibbonLeafKind.Button, null, tabId, panel, tabId, path, postable);

    private static SearchTool? Judge(int year, CensusDocument document, bool liveGate, params RibbonHit[] hits)
    {
        var census = SearchCensus.For(year, document);
        census.Should().NotBeNull("the census has a key for {0}", document);
        var list = SearchRunnable.List(SearchCatalog.Build([], hits), document.IsFamily,
            _ => new PlaceState(true, true), _ => liveGate, census);
        return list.Tools.SingleOrDefault();
    }

    private static readonly CensusDocument ProjectFloorPlan = new(false, null, null, "FloorPlan");

    public static TheoryData<int> Years => new() { 2026, 2027 };

    [Theory]
    [MemberData(nameof(Years))]
    public void Level_in_a_project_floor_plan_must_run_in_an_elevation_or_a_section(int year)
    {
        var level = Judge(year, ProjectFloorPlan, liveGate: false,
            Hit("ID_OBJECTS_LEVEL", "Level", "Architecture", "Datum", "/RibbonFoldPanel()"),
            Hit("ID_OBJECTS_LEVEL", "Level", "Structure", "Datum", "/RibbonFoldPanel()"));
        level.Should().NotBeNull();
        level!.Mark.Should().Be(SearchToolMark.MustRunIn);
        level.MustRunIn.Should().Equal("Elevation", "Section");
        SearchViews.MustRunInBadge(level.MustRunIn).Should().Be("Must Run In Elevation, Section");

        // In an elevation it runs.
        Judge(year, new CensusDocument(false, null, null, "Elevation"), true,
            Hit("ID_OBJECTS_LEVEL", "Level", "Architecture", "Datum", "/RibbonFoldPanel()"))!.Mark.Should().Be(SearchToolMark.None);
        // In a mass family's 3D view it runs; in a generic model family it runs nowhere.
        Judge(year, new CensusDocument(true, "OST_Mass", "WorkPlaneBased", "ThreeD"), true,
            Hit("ID_OBJECTS_LEVEL", "Level", "Home_Family", "Datum", "/RibbonFoldPanel()"))!.Mark.Should().Be(SearchToolMark.None);
        Judge(year, new CensusDocument(true, "OST_GenericModel", "OneLevelBased", "FloorPlan"), false,
            Hit("ID_OBJECTS_LEVEL", "Level", "Home_Family", "Datum", "/RibbonFoldPanel()")).Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(Years))]
    public void Electrical_Connector_runs_in_lighting_and_specialty_families_and_nowhere_in_casework(int year)
    {
        RibbonHit[] connector =
        [
            Hit("ID_RBS_ADD_ELECTRICAL_CONNECTOR", "Electrical Connector", "Home_Family", "Connectors", "/RibbonSplitButton()"),
            Hit("ID_RBS_ADD_ELECTRICAL_CONNECTOR", "Electrical Connector", "Home_Family", "Connectors", "/RibbonFoldPanel()"),
        ];
        foreach (var category in new[] { "OST_LightingFixtures", "OST_SpecialityEquipment" })
            Judge(year, new CensusDocument(true, category, "OneLevelBased", "FloorPlan"), true, connector)!.Mark
                .Should().Be(SearchToolMark.None, "{0}: the fold panel's copy is drawn", category);
        Judge(year, new CensusDocument(true, "OST_Casework", "OneLevelBased", "FloorPlan"), false, connector)
            .Should().BeNull("no casework view runs it");
    }

    [Theory]
    [MemberData(nameof(Years))]
    public void Room_runs_in_a_project_floor_plan_and_Extrusion_in_a_generic_model_family(int year)
    {
        Judge(year, ProjectFloorPlan, true,
            Hit("ID_OBJECTS_ROOM", "Room", "Architecture", "Room & Area", "/RibbonFoldPanel()/RibbonSplitButton(ID_OBJECTS_ROOM_RibbonListButton)"))!
            .Mark.Should().Be(SearchToolMark.None);
        Judge(year, new CensusDocument(true, "OST_GenericModel", "OneLevelBased", "FloorPlan"), true,
            Hit("ID_OBJECTS_EXTRUSION", "Extrusion", "Home_Family", "Forms", "/RibbonFoldPanel()"))!.Mark.Should().Be(SearchToolMark.None);
    }

    // 2026 only: Revit 2027 has no HVAC Zone on its ribbon (census/diff-2027-vs-2026.md), and AC6 does not run it.
    [Fact]
    public void HVAC_Zone_in_a_2026_project_floor_plan_is_not_listed_though_Revit_says_it_runs() =>
        Judge(2026, ProjectFloorPlan, liveGate: true, Hit("ID_RBS_HVAC_ZONE", "Zone", "Analyze", "Spaces", "/RibbonFoldPanel()", postable: false))
            .Should().BeNull("the census records it doing nothing here and running in no project view");

    [Theory]
    [MemberData(nameof(Years))]
    public void Site_Settings_in_a_project_floor_plan_is_NOT_IN_GUI_and_runs(int year)
    {
        var tool = Judge(year, ProjectFloorPlan, liveGate: true,
            Hit("ID_SITE_SETTINGS", "Site Settings", "MassingSite", "Model Site", "DialogLauncher", postable: false));
        tool.Should().NotBeNull();
        tool!.Mark.Should().Be(SearchToolMark.NotInGui);
        tool.CanRun.Should().BeTrue();
        tool.Postable.Should().BeFalse("Site Settings runs through route A");
    }
}
