// Spec #35 (Search-It: runs per file type), AC2 and AC4: which showing tools are listed, badged or left out, on a fake
// census index, a fake runs-in list, fake contexts and a fake live gate. The Ribbon Tool Census itself is the real one
// in SearchRunnableCensusTests (the spec's use cases on the committed tables).

using FluentAssertions;
using RST.Core.Census;
using RST.Core.Logging;
using RST.Core.Scanning;
using RST.Core.Search;
using RST.Tests.Logging;
using Xunit;

namespace RST.Tests.Search;

internal static class FakeCensus
{
    public const string ContextsTsv =
        "context\tkind\tcategory\tplacement\tview_type\tview\ttemplates\n" +
        "project/CeilingPlan\tproject\t\t\tCeilingPlan\tLevel 1\tdefault.rte\n" +
        "project/Elevation\tproject\t\t\tElevation\tSouth\tdefault.rte\n" +
        "project/FloorPlan\tproject\t\t\tFloorPlan\tLevel 1\tdefault.rte\n" +
        "project/Section\tproject\t\t\tSection\tSection 1\tdefault.rte\n" +
        "project/ThreeD\tproject\t\t\tThreeD\t3D View 1\tdefault.rte\n" +
        "project/DrawingSheet\tproject\t\t\tDrawingSheet\tUnnamed\tdefault.rte\n" +
        "family/OST_GenericModel/OneLevelBased/FloorPlan\tfamily\tOST_GenericModel\tOneLevelBased\tFloorPlan\tRef. Level\tgm.rft\n" +
        "family/OST_GenericModel/OneLevelBased/ThreeD\tfamily\tOST_GenericModel\tOneLevelBased\tThreeD\tView 1\tgm.rft\n" +
        "family/OST_LightingFixtures/OneLevelBased/FloorPlan\tfamily\tOST_LightingFixtures\tOneLevelBased\tFloorPlan\tRef. Level\tlf.rft\n" +
        "family/OST_LightingFixtures/OneLevelBased/ThreeD\tfamily\tOST_LightingFixtures\tOneLevelBased\tThreeD\tView 1\tlf.rft\n";

    public static CensusContexts Contexts => CensusContexts.Parse(2026, ContextsTsv);

    public static CensusIndex Index(params string[] rows) =>
        CensusIndex.Parse(2026, "context\tid\ttab\tpanel\tpath\tmark\treason\n" + string.Join("\n", rows));

    public static CensusRunsIn RunsIn(params string[] pairs) =>
        CensusRunsIn.Parse(2026, CensusRunsIn.Header + "\n" + string.Join("\n", pairs));

    public static SearchCensus Project(string viewType, CensusIndex index, CensusRunsIn runsIn) =>
        SearchCensus.For(index, runsIn, Contexts, new CensusDocument(false, null, null, viewType))!;

    public static SearchCensus Family(string category, string placement, string viewType, CensusIndex index, CensusRunsIn runsIn) =>
        SearchCensus.For(index, runsIn, Contexts, new CensusDocument(true, category, placement, viewType))!;
}

public class SearchRunnableTests
{
    // A row the index needs to be non-empty, about a tool no test lists.
    private const string Filler = "project/FloorPlan\tID_FILLER\tArchitecture\tBuild\t\tnot-drawn\t";

    private static RibbonHit Hit(string id, string text, string tabId, string panel, string? path = "", bool postable = true) =>
        new(id, text, RibbonContainerKind.Panel, RibbonLeafKind.Button, null, tabId, panel, tabId, path, postable);

    /// <summary>A visible-panel state: the tab's visibility by tab id (every tab visible unless named).</summary>
    private static Func<SearchCatalogPlace, PlaceState> Visible(params string[] hiddenTabs) =>
        p => new PlaceState(TabVisible: !hiddenTabs.Contains(p.TabId), PanelVisible: true);

    private static string Show(SearchTool t) =>
        $"{t.Id} [{t.Mark}{(t.MustRunIn.Count > 0 ? ": " + string.Join(", ", t.MustRunIn) : "")}] "
        + string.Join(" ; ", t.Places.Select(p => p.Label + " @ " + p.Location));

    // ---- each row of the Runnable Only table ------------------------------------------------------

    [Fact]
    public void A_tool_that_runs_here_at_a_drawn_place_is_listed_unbadged_and_runs()
    {
        var catalog = SearchCatalog.Build([], [Hit("ID_OBJECTS_ROOM", "Room", "Architecture", "Room & Area")]);
        var census = FakeCensus.Project("FloorPlan", FakeCensus.Index(Filler), FakeCensus.RunsIn("project/FloorPlan\tID_OBJECTS_ROOM"));

        var list = SearchRunnable.List(catalog, isFamily: false, Visible(), _ => true, census);

        var room = list.Tools.Single();
        room.Mark.Should().Be(SearchToolMark.None);
        room.CanRun.Should().BeTrue();
        room.Location.Should().Be("Architecture › Room & Area");
        room.RunHandle.Should().Be(0);
        list.LeftOut.Should().Be(0);
        list.Census.Should().BeSameAs(census);
    }

    [Fact]
    public void A_tool_that_runs_here_only_from_places_Revit_does_not_draw_is_badged_NOT_IN_GUI_and_runs()
    {
        // Site Settings: the Model Site panel's launcher, modelled but not drawn; Revit's check says it can run.
        RibbonHit[] hits = [Hit("ID_SITE_SETTINGS", "Site Settings", "MassingSite", "Model Site", "DialogLauncher", postable: false)];
        var catalog = SearchCatalog.Build([], hits);
        var index = FakeCensus.Index("project/FloorPlan\tID_SITE_SETTINGS\tMassingSite\tModel Site\tDialogLauncher\tnot-drawn\tnot drawn");

        var list = SearchRunnable.List(catalog, false, Visible(), _ => true, FakeCensus.Project("FloorPlan", index, FakeCensus.RunsIn("project/FloorPlan\tID_X")));

        var tool = list.Tools.Single();
        tool.Mark.Should().Be(SearchToolMark.NotInGui);
        tool.CanRun.Should().BeTrue();
        tool.Postable.Should().BeFalse("it runs through route A");
        tool.Location.Should().Be("MassingSite › Model Site", "the place shown is the modelled place");
        tool.RunHandle.Should().Be(0);
        list.NotInGuiCount.Should().Be(1);
    }

    [Fact]
    public void A_tool_that_cannot_run_here_but_runs_in_another_view_of_the_file_is_badged_Must_Run_In_and_never_runs()
    {
        // Level in a floor plan: greyed live; the census saw it start in an elevation and a section.
        var catalog = SearchCatalog.Build([], [Hit("ID_OBJECTS_LEVEL", "Level", "Architecture", "Datum", "/RibbonFoldPanel()")]);
        var runsIn = FakeCensus.RunsIn("project/Section\tID_OBJECTS_LEVEL", "project/Elevation\tID_OBJECTS_LEVEL",
            "family/OST_GenericModel/OneLevelBased/FloorPlan\tID_OBJECTS_LEVEL");

        var list = SearchRunnable.List(catalog, false, Visible(), _ => false, FakeCensus.Project("FloorPlan", FakeCensus.Index(Filler), runsIn));

        var level = list.Tools.Single();
        Show(level).Should().Be("ID_OBJECTS_LEVEL [MustRunIn: Elevation, Section] Level @ Architecture › Datum");
        level.CanRun.Should().BeFalse("a Must Run In card never runs");
        level.RunHandle.Should().BeNull();
        SearchViews.MustRunInBadge(level.MustRunIn).Should().Be("Must Run In Elevation, Section");
        SearchViews.MustRunInToolTip(level.MustRunIn).Should().Be("Runs in: Elevation, Section. Switch to one of these views to run this tool.");
        list.MustRunInCount.Should().Be(1);
    }

    [Fact]
    public void A_tool_that_runs_nowhere_in_this_file_is_not_listed()
    {
        // Electrical Connector in a casework-like family: greyed; the census saw it start only in other families.
        var catalog = SearchCatalog.Build([], [Hit("ID_RBS_ADD_ELECTRICAL_CONNECTOR", "Electrical Connector", "Home_Family", "Connectors")]);
        var runsIn = FakeCensus.RunsIn("family/OST_LightingFixtures/OneLevelBased/FloorPlan\tID_RBS_ADD_ELECTRICAL_CONNECTOR",
            "project/FloorPlan\tID_RBS_ADD_ELECTRICAL_CONNECTOR");

        var list = SearchRunnable.List(catalog, true, Visible(), _ => false,
            FakeCensus.Family("OST_GenericModel", "OneLevelBased", "FloorPlan", FakeCensus.Index(Filler), runsIn));

        list.Tools.Should().BeEmpty("no other view of this family runs it, and other files are not named");
        list.LeftOut.Should().Be(1);
    }

    [Fact]
    public void Not_runnable_wins_over_not_drawn_and_over_Revits_check()
    {
        // HVAC Zone in a floor plan: Revit says runnable; the census says it does nothing here.
        var catalog = SearchCatalog.Build([], [Hit("ID_RBS_HVAC_ZONE", "Zone", "Analyze", "Spaces")]);
        var index = FakeCensus.Index("project/FloorPlan\tID_RBS_HVAC_ZONE\tAnalyze\tSpaces\t\tnot-runnable\tjournal: cannot be invoked in this context",
            "project/FloorPlan\tID_RBS_HVAC_ZONE\tAnalyze\tSpaces\t\tnot-drawn\tnot drawn");
        var census = FakeCensus.Project("FloorPlan", index, FakeCensus.RunsIn("project/FloorPlan\tID_X"));

        SearchRunnable.List(catalog, false, Visible(), _ => true, census).Tools.Should().BeEmpty("it runs nowhere in this file");

        // Runs in another view: Must Run In only, never NOT IN GUI.
        var elsewhere = FakeCensus.Project("FloorPlan", index, FakeCensus.RunsIn("project/ThreeD\tID_RBS_HVAC_ZONE"));
        var tool = SearchRunnable.List(catalog, false, Visible(), _ => true, elsewhere).Tools.Single();
        tool.Mark.Should().Be(SearchToolMark.MustRunIn);
        tool.MustRunIn.Should().Equal("3D View");
    }

    [Fact]
    public void Census_greyed_never_hides_a_tool_Revits_live_check_says_runs()
    {
        // Copy, with a selection: the census ran with nothing selected and recorded it greyed everywhere (no runs-in,
        // no exceptions row). Revit's check now says it runs.
        var catalog = SearchCatalog.Build([], [Hit("ID_EDIT_COPY", "Copy", "Modify", "Modify")]);
        var census = FakeCensus.Project("FloorPlan", FakeCensus.Index(Filler), FakeCensus.RunsIn("project/FloorPlan\tID_X"));

        SearchRunnable.List(catalog, false, Visible(), _ => true, census).Tools.Single().Mark.Should().Be(SearchToolMark.None);
        SearchRunnable.List(catalog, false, Visible(), _ => false, census).Tools.Should().BeEmpty("with nothing selected it runs nowhere");
    }

    [Fact]
    public void A_tool_with_drawn_and_not_drawn_places_shows_a_drawn_place_first_and_counts_only_drawn_places()
    {
        RibbonHit[] hits =
        [
            Hit("ID_T", "Hidden copy", "Architecture", "Build", "/RibbonSplitButton(ID_T_RibbonListButton)"),
            Hit("ID_T", "Drawn", "Structure", "Structure", ""),
            Hit("ID_T", "Also drawn", "Modify", "Geometry", ""),
            Hit("ID_T", "Not drawn", "Annotate", "Detail", "/SlideOut"),
        ];
        var catalog = SearchCatalog.Build([], hits);
        var index = FakeCensus.Index(
            "project/FloorPlan\tID_T\tArchitecture\tBuild\t/RibbonSplitButton(ID_T_RibbonListButton)\tnot-drawn\t",
            "project/FloorPlan\tID_T\tAnnotate\tDetail\t/SlideOut\tnot-drawn\t");

        var tool = SearchRunnable.List(catalog, false, Visible(), _ => true, FakeCensus.Project("FloorPlan", index, FakeCensus.RunsIn("project/FloorPlan\tID_T")))
            .Tools.Single();

        tool.Mark.Should().Be(SearchToolMark.None, "a tool with a drawn place gets no NOT IN GUI badge");
        tool.Places.Select(p => p.Location).Should().Equal("Structure › Structure", "Modify › Geometry");
        tool.OtherPlaces.Should().Be(1, "\"+N more places\" counts drawn places only");
        tool.RunHandle.Should().Be(1, "a click runs the first drawn place");
    }

    [Fact]
    public void A_place_is_not_drawn_only_when_every_copy_of_it_is()
    {
        // Revit keeps a hidden copy of some containers in the same panel; the census marks the exact path.
        RibbonHit[] hits =
        [
            Hit("ID_RBS_ADD_ELECTRICAL_CONNECTOR", "Electrical Connector", "Home_Family", "Connectors", "/RibbonSplitButton()"),
            Hit("ID_RBS_ADD_ELECTRICAL_CONNECTOR", "Electrical Connector", "Home_Family", "Connectors", "/RibbonFoldPanel()"),
        ];
        var catalog = SearchCatalog.Build([], hits);
        catalog.Single().Places.Single().Copies.Should().Equal(new SearchPlaceCopy(0, "/RibbonSplitButton()"), new SearchPlaceCopy(1, "/RibbonFoldPanel()"));
        var index = FakeCensus.Index("family/OST_LightingFixtures/OneLevelBased/FloorPlan\tID_RBS_ADD_ELECTRICAL_CONNECTOR\tHome_Family\tConnectors\t/RibbonSplitButton()\tnot-drawn\t");
        var census = FakeCensus.Family("OST_LightingFixtures", "OneLevelBased", "FloorPlan", index,
            FakeCensus.RunsIn("family/OST_LightingFixtures/OneLevelBased/FloorPlan\tID_RBS_ADD_ELECTRICAL_CONNECTOR"));

        var tool = SearchRunnable.List(catalog, true, Visible(), _ => true, census).Tools.Single();

        tool.Mark.Should().Be(SearchToolMark.None, "the fold panel's copy is drawn");
        tool.RunHandle.Should().Be(1, "route A runs the drawn copy");
    }

    // ---- the live gate ----------------------------------------------------------------------------

    [Fact]
    public void A_place_on_a_hidden_project_tab_is_not_asked_and_counts_as_runnable()
    {
        // Structural Column with the Structure tab hidden in Revit's options (spec #33, AC12).
        var catalog = SearchCatalog.Build([], [Hit("ID_OBJECTS_STRUCTURAL_COLUMN", "Structural Column", "Structure", "Structure")]);
        var asked = new List<string>();

        var list = SearchRunnable.List(catalog, false, Visible("Structure"), p => { asked.Add(p.Tab); return false; },
            FakeCensus.Project("FloorPlan", FakeCensus.Index(Filler), FakeCensus.RunsIn("project/FloorPlan\tID_X")));

        asked.Should().BeEmpty("a place on a hidden tab is never asked");
        list.Tools.Single().Mark.Should().Be(SearchToolMark.None);
    }

    [Fact]
    public void A_Revit_tool_is_asked_once_at_its_first_place_on_a_visible_tab_and_the_answer_holds_on_hidden_tabs()
    {
        // Level on Architecture (visible) and on Structure (hidden by the user): greyed in a floor plan.
        var catalog = SearchCatalog.Build([],
        [
            Hit("ID_OBJECTS_LEVEL", "Level", "Structure", "Datum", "/RibbonFoldPanel()"),
            Hit("ID_OBJECTS_LEVEL", "Level", "Architecture", "Datum", "/RibbonFoldPanel()"),
        ]);
        var asked = new List<string>();
        var census = FakeCensus.Project("FloorPlan", FakeCensus.Index(Filler), FakeCensus.RunsIn("project/Elevation\tID_OBJECTS_LEVEL"));

        var level = SearchRunnable.List(catalog, false, Visible("Structure"), p => { asked.Add(p.Tab); return false; }, census).Tools.Single();

        asked.Should().Equal("Architecture");
        level.Mark.Should().Be(SearchToolMark.MustRunIn, "Revit's answer is the id's, so the hidden tab's place does not make it runnable");
        level.Places.Select(p => p.Location).Should().Equal("Structure › Datum", "Architecture › Datum");
    }

    [Fact]
    public void A_hidden_family_tab_is_never_asked_and_an_add_in_answers_place_by_place()
    {
        const string addin = "CustomCtrl_%CustomCtrl_%Add-Ins%Vendor%Tool";
        var catalog = SearchCatalog.Build([],
        [
            Hit("ID_OBJECTS_EXTRUSION", "Extrusion", "Home_Family", "Forms"),
            Hit(addin, "Tool", "Add-Ins", "Vendor"),
            Hit(addin, "Tool elsewhere", "Vendor", "Tools"),
        ]);
        var asked = new List<string>();
        bool? Gate(SearchCatalogPlace p)
        {
            asked.Add(p.TabId!);
            return p.TabId != "Add-Ins";
        }

        // In a project the family-editor tab is hidden: its place does not show and is never asked.
        var list = SearchRunnable.List(catalog, false, Visible("Home_Family"), Gate, null);

        asked.Should().Equal("Add-Ins", "Vendor");
        list.Tools.Select(Show).Should().Equal($"{addin} [None] Tool elsewhere @ Vendor › Tools");
    }

    // ---- Must Run In names ------------------------------------------------------------------------

    [Fact]
    public void Must_Run_In_names_same_file_views_only_never_the_current_or_fallback_view_in_order_with_plus_N()
    {
        var catalog = SearchCatalog.Build([], [Hit("ID_T", "Tool", "Architecture", "Build")]);
        var runsIn = FakeCensus.RunsIn(
            "project/ThreeD\tID_T", "project/FloorPlan\tID_T", "project/DrawingSheet\tID_T", "project/Section\tID_T",
            "project/CeilingPlan\tID_T", "project/Elevation\tID_T",
            "family/OST_GenericModel/OneLevelBased/FloorPlan\tID_T");

        // An area plan uses the floor plan's rows: the floor plan is the current view and is not named.
        var census = FakeCensus.Project("AreaPlan", FakeCensus.Index(Filler), runsIn);
        census.Key.IsFallback.Should().BeTrue();
        var tool = SearchRunnable.List(catalog, false, Visible(), _ => false, census).Tools.Single();

        tool.MustRunIn.Should().Equal("Ceiling Plan", "Elevation", "Section", "3D View", "Sheet");
        SearchViews.MustRunInBadge(tool.MustRunIn).Should().Be("Must Run In Ceiling Plan, Elevation +3");
        SearchViews.MustRunInToolTip(tool.MustRunIn).Should().Be(
            "Runs in: Ceiling Plan, Elevation, Section, 3D View, Sheet. Switch to one of these views to run this tool.");

        // A family names its own category and placement's views only.
        var family = FakeCensus.Family("OST_GenericModel", "OneLevelBased", "ThreeD", FakeCensus.Index(Filler), runsIn);
        var inFamily = SearchRunnable.List(SearchCatalog.Build([], [Hit("ID_T", "Tool", "Home_Family", "Forms")]), true, Visible(), _ => false, family)
            .Tools.Single();
        inFamily.MustRunIn.Should().Equal("Floor Plan");
    }

    [Fact]
    public void The_view_names_follow_the_specs_table()
    {
        SearchViews.Ordered(["Legend", "DraftingView", "Schedule", "DrawingSheet", "ThreeD", "Section", "Elevation", "EngineeringPlan", "CeilingPlan", "FloorPlan", "Odd"])
            .Should().Equal("Floor Plan", "Ceiling Plan", "Structural Plan", "Elevation", "Section", "3D View", "Sheet", "Schedule",
                "Drafting View", "Legend", "Odd");
        SearchViews.MustRunInBadge(["Elevation"]).Should().Be("Must Run In Elevation");
        SearchViews.MustRunInBadge([]).Should().BeEmpty();
        SearchViews.NotInGui.Should().Be("NOT IN GUI");
        SearchViews.CantRunHere("Site Settings", "Massing & Site › Model Site")
            .Should().Be("This tool can't be run with RST here: Site Settings (Massing & Site › Model Site).");
    }

    // ---- order, no index, no key ------------------------------------------------------------------

    [Fact]
    public void Among_equal_matches_runnable_cards_come_before_Must_Run_In_cards()
    {
        var mustRunIn = new SearchTool("ID_A", "Level", "Architecture", "Datum") { Mark = SearchToolMark.MustRunIn, MustRunIn = ["Elevation"] };
        var runs = new SearchTool("ID_B", "Level", "Structure", "Datum");
        var notInGui = new SearchTool("ID_C", "Level", "Massing", "Site") { Mark = SearchToolMark.NotInGui };

        new ToolSearch([mustRunIn, runs, notInGui]).Search(null, "level").Select(h => h.Tool.Id).Should().Equal("ID_C", "ID_B", "ID_A");
        new ToolSearch([mustRunIn, runs]).Search(null, "").Select(h => h.Tool.Id).Should().Equal("ID_B", "ID_A");
        // A better match still wins over a runnable one.
        var exact = new SearchTool("ID_D", "Lev", "Architecture", "Datum") { Mark = SearchToolMark.MustRunIn, MustRunIn = ["Elevation"] };
        new ToolSearch([runs, exact]).Search(null, "lev").First().Tool.Id.Should().Be("ID_D");
    }

    [Fact]
    public void With_no_index_or_no_key_the_census_removes_badges_and_labels_nothing_and_the_live_gate_still_applies()
    {
        RibbonHit[] hits = [Hit("ID_OBJECTS_LEVEL", "Level", "Architecture", "Datum"), Hit("ID_SITE_SETTINGS", "Site Settings", "MassingSite", "Model Site", "DialogLauncher")];
        var catalog = SearchCatalog.Build([], hits);
        var index = FakeCensus.Index("project/FloorPlan\tID_SITE_SETTINGS\tMassingSite\tModel Site\tDialogLauncher\tnot-drawn\t");
        var runsIn = FakeCensus.RunsIn("project/Elevation\tID_OBJECTS_LEVEL");

        // No key: a view the census did not run and the fallback map does not cover.
        SearchCensus.For(index, runsIn, FakeCensus.Contexts, new CensusDocument(false, null, null, "Rendering")).Should().BeNull();
        // No index for the year (Revit 2025).
        SearchCensus.For(CensusIndex.Empty(2025), runsIn, FakeCensus.Contexts, new CensusDocument(false, null, null, "FloorPlan")).Should().BeNull();
        SearchCensus.For(2025, new CensusDocument(false, null, null, "FloorPlan")).Should().BeNull();

        bool? Gate(SearchCatalogPlace p) => p.Label != "Level";
        var list = SearchRunnable.List(catalog, false, Visible(), Gate, census: null);

        list.Tools.Select(Show).Should().Equal("ID_SITE_SETTINGS [None] Site Settings @ MassingSite › Model Site");
        list.LeftOut.Should().Be(1, "Revit's live check still leaves out the greyed Level");
        list.Census.Should().BeNull();
    }

    [Fact]
    public void A_place_that_does_not_show_is_counted_hidden_and_never_judged()
    {
        var catalog = SearchCatalog.Build([], [Hit("ID_T", "Tool", "Architecture", "Build")]);
        var list = SearchRunnable.List(catalog, false, _ => new PlaceState(true, PanelVisible: false), _ => throw new InvalidOperationException("asked"), null);
        list.Tools.Should().BeEmpty();
        list.Hidden.Should().Be(1);
        list.LeftOut.Should().Be(0);
    }

    // ---- AC3: non-postable ids are in the search catalogue only ----------------------------------

    [Fact]
    public void A_non_postable_hit_makes_a_non_postable_search_tool()
    {
        ScannedCommand[] seed = [new("ID_OBJECTS_WALL", "Wall", CommandOrigin.Native, null, null, null, null)];
        RibbonHit[] hits = [Hit("ID_OBJECTS_WALL", "Wall", "Architecture", "Build"), Hit("ID_SETTINGS_TEXT", "Text Types", "Annotate", "Text", "DialogLauncher", postable: false)];

        var catalog = SearchCatalog.Build(seed, hits);

        catalog.Select(t => (t.Id, t.Postable)).Should().Equal(("ID_OBJECTS_WALL", true), ("ID_SETTINGS_TEXT", false));
        // The Builder's merge is given postable hits only (RibbonScan.BuilderHits): it never sees Text Types.
        CatalogMerge.Merge(seed, hits.Where(h => h.Postable).ToList()).Select(c => c.Id).Should().Equal("ID_OBJECTS_WALL");
    }
}

// Swaps the process-wide Log.Logger: runs alone, as the log facade's own tests do.
[Collection(LogFacadeTests.Collection)]
public sealed class SearchRunnableFallbackTests
{
    private sealed class ListSink(List<LogEvent> events) : ILogSink
    {
        public void Emit(LogEvent logEvent, IReadOnlyDictionary<string, object?> properties)
        {
            lock (events) events.Add(logEvent);
        }

        public void Dispose()
        {
        }
    }

    private static List<LogEvent> CaptureLog(Action action)
    {
        var events = new List<LogEvent>();
        var previous = Log.Logger;
        Log.Logger = new Logger(LogLevel.Verbose, new ListSink(events));
        try { action(); }
        finally { Log.Logger = previous; }
        return events;
    }

    private static RibbonHit Hit(string id, string text, string tabId, string panel) =>
        new(id, text, RibbonContainerKind.Panel, RibbonLeafKind.Button, null, tabId, panel, tabId, "");

    private static readonly IReadOnlyList<SearchCatalogTool> Catalog = SearchCatalog.Build([],
    [
        Hit("ID_OBJECTS_LEVEL", "Level", "Architecture", "Datum"),
        Hit("ID_OBJECTS_WALL", "Wall", "Architecture", "Build"),
        Hit("ID_HIDDEN", "Hidden", "Architecture", "Hidden panel"),
    ]);

    private static PlaceState Visibility(SearchCatalogPlace p) => new(true, p.Panel != "Hidden panel");

    private static SearchCensus Census() => FakeCensus.Project("FloorPlan",
        FakeCensus.Index("project/FloorPlan\tID_FILLER\tA\tB\t\tnot-drawn\t"), FakeCensus.RunsIn("project/Elevation\tID_OBJECTS_LEVEL"));

    [Fact]
    public void When_the_gate_throws_the_showing_tools_are_listed_unlabelled_with_one_Warning()
    {
        var boom = new InvalidOperationException("gate");
        SearchList? list = null;
        var events = CaptureLog(() => list = SearchRunnable.ListOrFallBack(Catalog, false, Visibility, _ => throw boom, Census));

        events.Should().ContainSingle().Which.Should().Match<LogEvent>(e => e.Level == LogLevel.Warning && e.Exception == boom);
        list!.Tools.Select(t => t.Id).Should().Equal("ID_OBJECTS_LEVEL", "ID_OBJECTS_WALL");
        list.Tools.Should().OnlyContain(t => t.Mark == SearchToolMark.None);
        list.Census.Should().BeNull();
    }

    [Theory]
    [InlineData("key")]
    [InlineData("index")]
    public void When_the_key_or_index_throws_the_live_gate_lists_unlabelled_with_one_Warning(string what)
    {
        var boom = new InvalidOperationException(what);
        SearchList? list = null;
        var events = CaptureLog(() => list = SearchRunnable.ListOrFallBack(Catalog, false, Visibility,
            p => p.Label != "Level", () => throw boom));

        events.Should().ContainSingle().Which.Should().Match<LogEvent>(e => e.Level == LogLevel.Warning && e.Exception == boom);
        list!.Tools.Select(t => t.Id).Should().Equal(["ID_OBJECTS_WALL"], "Revit's live check still leaves out the greyed Level");
        list.Census.Should().BeNull();
    }

    [Fact]
    public void When_the_ribbons_state_cannot_be_read_the_unfiltered_tools_are_listed_with_one_Warning()
    {
        SearchList? list = null;
        var events = CaptureLog(() => list = SearchRunnable.ListOrFallBack(Catalog, false, _ => throw new InvalidOperationException("ribbon"), _ => true, Census));
        events.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
        list!.Tools.Should().HaveCount(3);
    }

    [Fact]
    public void A_working_census_logs_nothing_and_labels_the_list()
    {
        SearchList? list = null;
        var events = CaptureLog(() => list = SearchRunnable.ListOrFallBack(Catalog, false, Visibility, p => p.Label != "Level", Census));
        events.Should().BeEmpty();
        list!.Tools.Single(t => t.Id == "ID_OBJECTS_LEVEL").Mark.Should().Be(SearchToolMark.MustRunIn);
        list.Hidden.Should().Be(1);
    }
}
