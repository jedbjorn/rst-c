// Spec #33 (Search-It — tools for the open file): the search catalogue and the showing rule, on the
// Work Unit 1 captures (a project, a generic model family and an annotation family; SearchCapture)
// and on small hand-made scans.

using FluentAssertions;
using RST.Core.Logging;
using RST.Core.Scanning;
using RST.Core.Search;
using RST.Tests.Logging;
using Xunit;

namespace RST.Tests.Search;

public class SearchCatalogTests
{
    private static string Show(SearchTool tool) =>
        tool.Id + ": " + string.Join(" ; ", tool.Places.Select(p => p.Label + " @ " + p.Location));

    private static RibbonHit Hit(string id, string text, string tabId, string tab, string panel) =>
        new(id, text, RibbonContainerKind.Panel, RibbonLeafKind.Button, null, tab, panel, tabId);

    // ---- AC2: a family lists its own tools ------------------------------------------------------

    [Fact]
    public void A_generic_model_family_lists_the_family_editors_tools_and_no_project_tools()
    {
        var capture = new SearchCapture(SearchCapture.GenericModelFamily);
        var list = capture.List(isFamily: true);
        list.IsFamily.Should().BeTrue();

        var extrusion = list.Tools.Single(t => t.Id == "ID_OBJECTS_EXTRUSION");
        extrusion.DisplayName.Should().Be("Solid Extrusion");
        extrusion.Location.Should().Be("Create › Forms");
        foreach (var id in new[] { "ID_LOAD_INTO_PROJECTS", "ID_LOAD_INTO_PROJECTS_CLOSE" })
            list.Tools.Single(t => t.Id == id).Places.Select(p => p.Location).Should().Equal("Family Editor");
        list.Tools.Single(t => t.Id == "ID_LOAD_INTO_PROJECTS").DisplayName.Should().Be("Load into Project");
        list.Tools.Single(t => t.Id == "ID_LOAD_INTO_PROJECTS_CLOSE").DisplayName.Should().Be("Load into Project and Close");

        list.Tools.Select(t => t.Id).Should().NotContain("ID_OBJECTS_WALL", "Wall is on project tabs only");
        list.Tools.Select(t => t.Id).Should().NotContain("ID_TRUSS_TOP_CHORD_CURVE", "Create › Detail is hidden in a generic model family");

        // What the user types (the spec's VM row, on captured data).
        var search = new ToolSearch(list.Tools);
        search.Search(null, "extru").Select(h => h.Name).Should().BeEquivalentTo("Solid Extrusion", "Void Extrusion");
        search.Search(null, "wall").Select(h => h.Name).Should().NotContain(n => n.StartsWith("Wall:"));
        search.Search(null, "load").Select(h => h.Name).Should().Contain("Load into Project");

        // Every listed place is one Revit shows in that family: a visible tab and a visible panel.
        var shown = list.Tools.SelectMany(t => t.Places).Select(p => p.Tab).Distinct();
        shown.Should().BeEquivalentTo(new[] { "Create", "Insert", "Annotate", "View", "Manage", "Modify", "Add-Ins", "DiRootsOne", null });
        foreach (var tool in capture.Catalog())
        {
            var showing = tool.Places.Where(p => capture.StateOf(p) is { TabVisible: true, PanelVisible: true }).ToArray();
            var listed = list.Tools.SingleOrDefault(t => t.Id == tool.Id);
            (listed?.Places ?? []).Should().Equal(showing.Select(p => p.ToSearchPlace()), tool.Id);
        }
        list.Tools.Should().HaveCount(265);
    }

    [Fact]
    public void An_annotation_family_has_no_Solid_Extrusion_and_no_Annotate_tab()
    {
        var list = new SearchCapture(SearchCapture.AnnotationFamily).List(isFamily: true);
        list.Tools.Select(t => t.Id).Should().NotContain("ID_OBJECTS_EXTRUSION", "Create › Forms is hidden in an annotation family");
        new ToolSearch(list.Tools).Search(null, "extru").Should().BeEmpty();
        list.Tools.Single(t => t.Id == "ID_LOAD_INTO_PROJECTS").Location.Should().Be("Family Editor");
        list.Tools.SelectMany(t => t.Places).Select(p => p.Tab).Should().NotContain("Annotate");
        list.Tools.Select(t => t.Id).Should().NotContain("ID_OBJECTS_WALL");
    }

    // ---- AC3: a project lists today's tools, less hidden panels ----------------------------------

    [Fact]
    public void A_project_lists_todays_tools_less_places_on_hidden_panels_plus_the_mode_only_ids_Revit_posts()
    {
        var capture = new SearchCapture(SearchCapture.Project);
        var today = SearchIndex.Build(capture.BuilderCatalog());
        var now = capture.List(isFamily: false);
        now.IsFamily.Should().BeFalse();

        // (a) The panels Revit hides in that context (a plan view, nothing selected), outside the family-editor tabs.
        var hidden = capture.Catalog().SelectMany(t => t.Places)
            .Where(p => !p.OnFamilyEditorTab && !capture.StateOf(p).PanelVisible)
            .Select(p => (p.Tab, p.Panel)).Distinct().ToArray();
        hidden.Should().BeEquivalentTo(new[] { ("Modify", "Draw"), ("Modify", "Work Plane") });
        // (b) The mode-only id the Builder leaves out by name and Revit lets add-ins post, with a project place.
        string[] kept = ["ID_LOAD_INTO_PROJECTS"];

        var expected = today
            .Select(t => (t.Id, Places: t.Places.Where(p => !hidden.Contains((p.Tab!, p.Panel!))).ToArray()))
            .Where(t => t.Places.Length > 0).ToArray();
        var actual = now.Tools.Where(t => !kept.Contains(t.Id)).ToArray();
        actual.Select(t => t.Id).Should().Equal(expected.Select(t => t.Id), "same ids in the same order");
        for (var i = 0; i < expected.Length; i++)
            actual[i].Places.Should().Equal(expected[i].Places, "same names and places: {0}", expected[i].Id);

        // (a), named: four tools sit on Modify › Draw only; six more lose their Modify place and nothing else.
        today.Select(t => t.Id).Except(now.Tools.Select(t => t.Id)).Should().BeEquivalentTo(
            "IDC_DRAW_ON_FACE", "IDC_DRAW_ON_WORKPLANE", "ID_OBJECTS_REBAR_CURVE", "ID_MAJOR_SEGMENT");
        today.Where(t => now.Tools.SingleOrDefault(n => n.Id == t.Id) is { } n && n.Places.Count != t.Places.Count)
            .Select(t => t.Id).Should().BeEquivalentTo("ID_OBJECTS_CLINE", "ID_PICK_WORKPLANE", "ID_SKETCH_PLANE_TOOL",
                "ID_OBJECTS_REFERENCE_CURVE", "ID_WORKPLANE_VIEW", "ID_SKETCH_GRID_VIS");
        // (b), named: listed under its project name, where the seed's order puts it.
        now.Tools.Select(t => t.Id).Except(today.Select(t => t.Id)).Should().Equal(kept);
        Show(now.Tools.Single(t => t.Id == "ID_LOAD_INTO_PROJECTS")).Should().Be("ID_LOAD_INTO_PROJECTS: "
            + "Load as Group into Open Projects @ Architecture › Model ; Load as Group into Open Projects @ Structure › Model");
        var seedOrder = capture.Seeds.Select(s => s.Id).ToList();
        now.Tools.Select(t => seedOrder.IndexOf(t.Id)).Where(i => i >= 0).Should().BeInAscendingOrder();

        // A tab the user has hidden still counts in a project: Structure is hidden on the captured seat.
        var structural = now.Tools.Single(t => t.Id == "ID_OBJECTS_STRUCTURAL_WALL");
        Show(structural).Should().Contain("Wall: Structural @ Structure › Structure");
        capture.Catalog().Single(t => t.Id == structural.Id).Places.Where(p => p.Tab == "Structure")
            .Should().OnlyContain(p => !capture.StateOf(p).TabVisible);
        // Tools with no place on a visible tab are listed too (Beam is on Structure only).
        var onHiddenTabsOnly = capture.Catalog().Where(t => t.Places.Where(p => !p.OnFamilyEditorTab) is var project
            && project.Any() && project.All(p => !capture.StateOf(p).TabVisible)).Select(t => t.Id).ToArray();
        onHiddenTabsOnly.Should().Contain("ID_OBJECTS_BEAM").And.HaveCountGreaterThan(100);
        now.Tools.Select(t => t.Id).Should().Contain(onHiddenTabsOnly);
        // No family-editor place shows in a project.
        now.Tools.Select(t => t.Id).Should().NotContain(["ID_OBJECTS_EXTRUSION", "ID_LOAD_INTO_PROJECTS_CLOSE"]);
        now.Tools.SelectMany(t => t.Places).Should().OnlyContain(p => p.Tab != "Create" && p.Tab != null);
    }

    // ---- Search catalogue ------------------------------------------------------------------------

    [Fact]
    public void Mode_only_ids_are_decided_by_postability_not_by_name()
    {
        var catalog = new SearchCapture(SearchCapture.Project).Catalog();
        // The six Revit lets add-ins post (Work Unit 1) are kept; the seven it does not never pass the scan.
        ModeRestrictedCommandIds.Ids.Where(id => catalog.Any(t => t.Id == id)).Should().BeEquivalentTo(
            "ID_LOAD_INTO_PROJECTS", "ID_LOAD_INTO_PROJECTS_CLOSE", "ID_LOAD_INTO_PROJECTS_REBAR_SHAPE",
            "ID_LOAD_INTO_PROJECTS_CLOSE_REBAR_SHAPE", "ID_SHAPE_STATUS", "ID_TURN_ON_MULTI_PLANAR_SHAPE");
        ModeRestrictedCommandIds.Ids.Should().HaveCount(13);

        // Names come from the place's own ribbon item (decision #67).
        var load = catalog.Single(t => t.Id == "ID_LOAD_INTO_PROJECTS");
        load.Places.Select(p => (p.TabId, p.Label)).Should().Equal(
            ("Architecture", "Load as Group into Open Projects"),
            ("Structure", "Load as Group into Open Projects"),
            ("FamilyEditorTab", "Load into Project"));
    }

    [Fact]
    public void The_Builders_catalogue_does_not_notice_the_family_editor_hits()
    {
        // AC6: the scan now walks the family-editor tabs; CatalogMerge must give the Builder the same list.
        var capture = new SearchCapture(SearchCapture.Project);
        capture.Hits.Should().Contain(h => ModeRestrictedTabs.ContainsId(h.TabId));
        static string Row(ScannedCommand c) => $"{c.Id}|{c.DisplayName}|{c.Origin}|{c.EnumName}|{c.SourceTab}|{c.SourcePanel}|"
            + string.Join(";", c.Placements!.Select(p => $"{p.Tab}/{p.Panel}/{p.Label}/{p.TabId}"));
        var withFamilyTabs = CatalogMerge.Merge(capture.Seeds, capture.Hits);
        var without = CatalogMerge.Merge(capture.Seeds, capture.Hits.Where(h => !ModeRestrictedTabs.ContainsId(h.TabId)));
        withFamilyTabs.Select(Row).Should().Equal(without.Select(Row));

        var builder = capture.BuilderCatalog();
        builder.Should().HaveCount(531, "the catalogue v3.4.1 built in the captured session");
        builder.SelectMany(c => c.Placements!).Should().OnlyContain(p => !ModeRestrictedTabs.ContainsId(p.TabId));
        builder.Select(c => c.Id).Should().NotContain(["ID_OBJECTS_EXTRUSION", "ID_LOAD_INTO_PROJECTS"]);
    }

    [Fact]
    public void Build_keeps_family_and_project_places_apart_and_applies_the_ban_list()
    {
        ScannedCommand[] seed = [new("ID_DIM", "Old", CommandOrigin.Native, null, null, null, null, EnumName: "AlignedDimension"),
            new("ID_BANNED", "Banned", CommandOrigin.Native, null, null, null, null),
            new("ID_HEADLESS", "Headless", CommandOrigin.Native, null, null, null, null)];
        RibbonHit[] hits =
        [
            Hit("ID_DIM", "Aligned", "Annotate", "Annotate", "Dimension"),
            Hit("ID_DIM", "Aligned again", "Annotate", "Annotate", "Dimension"),   // same home: the first wins
            Hit("ID_DIM", "Aligned", "Annotate_ModelMassFamily", "Annotate", "Dimension"),
            Hit("ID_DIM", "Aligned", "Modify", "Modify", "Measure"),
            Hit("ID_DIM", "Aligned", "Second_Modify", "Modify", "Measure"),
            Hit("ID_BANNED", "Banned", "Home_Family", "Create", "Forms"),
            Hit("CustomCtrl_%Tab%Panel%Tool", " My\tTool ", "Add-Ins", "Add-Ins", "Vendor"),
            Hit("ID_ZONE", "Zone tool", "SomeOtherId", "Zone", "Edit"),            // a mode tab by title only: out
            Hit("ID_EMPTY", "No panel", "Annotate", "Annotate", ""),
        ];

        var catalog = SearchCatalog.Build(seed, hits, isBanned: id => id == "ID_BANNED");

        catalog.Select(t => t.Id).Should().Equal("ID_DIM", "CustomCtrl_%Tab%Panel%Tool");
        var dim = catalog[0];
        dim.Places.Select(p => (p.Handle, p.TabId, p.Tab, p.Panel, p.OnFamilyEditorTab)).Should().Equal(
            (0, "Annotate", "Annotate", "Dimension", false),
            (2, "Annotate_ModelMassFamily", "Annotate", "Dimension", true),
            (3, "Modify", "Modify", "Measure", false),
            (4, "Second_Modify", "Modify", "Measure", true));
        dim.Places.Should().OnlyContain(p => p.Label == "Aligned");
        catalog[1].Places.Single().Should().Be(new SearchCatalogPlace(6, "Add-Ins", "Add-Ins", "Vendor", "My Tool"));
        SearchCatalog.Build(seed, hits).Select(t => t.Id).Should().Contain("ID_BANNED", "no ban list, no ban");
    }

    [Fact]
    public void The_Family_Editor_panel_is_shown_without_a_tab()
    {
        new SearchCatalogPlace(0, "FamilyEditorTab", "Family Editor", "Family Editor", "Load into Project")
            .ToSearchPlace().Should().Be(new SearchPlace("Load into Project", null, "Family Editor"));
        new SearchCatalogPlace(0, "Home_Family", "Create", "Forms", "Solid Extrusion")
            .ToSearchPlace().Location.Should().Be("Create › Forms");
    }

    // ---- AC4: the showing rule; a project template is a project ----------------------------------

    [Theory]
    // family-editor tab: the tab must show, whatever is open
    [InlineData(true, true, true, true, true)]
    [InlineData(true, true, false, true, false)]
    [InlineData(true, false, true, true, true)]
    [InlineData(true, false, false, true, false)]
    // any other tab, project (.rvt, or a .rte opened directly): a tab the user has hidden still counts
    [InlineData(false, false, true, true, true)]
    [InlineData(false, false, false, true, true)]
    // any other tab, family: the tab must show
    [InlineData(false, true, true, true, true)]
    [InlineData(false, true, false, true, false)]
    // a hidden panel never shows
    [InlineData(true, true, true, false, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, false, false, false, false)]
    [InlineData(false, true, true, false, false)]
    public void A_place_shows_by_its_panel_and_by_its_tab_where_the_rule_asks(bool onFamilyEditorTab, bool isFamily,
        bool tabVisible, bool panelVisible, bool expected) =>
        new PlaceState(tabVisible, panelVisible).Shows(onFamilyEditorTab, isFamily).Should().Be(expected);

    // ---- AC7: a greyed add-in tool is not listed -------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_addin_button_that_cannot_run_now_loses_its_place_and_nothing_else_changes(bool isFamily)
    {
        const string twoPlaces = "CustomCtrl_%Vendor%Tools%Two", onePlace = "CustomCtrl_%Vendor%Tools%One";
        RibbonHit[] hits =
        [
            Hit(twoPlaces, "Two", "Add-Ins", "Add-Ins", "Vendor"),
            Hit(twoPlaces, "Two elsewhere", "Vendor", "Vendor", "Tools"),
            Hit(onePlace, "One", "Add-Ins", "Add-Ins", "Vendor"),
            Hit("CustomCtrl_%Vendor%Tools%Ready", "Ready", "Add-Ins", "Add-Ins", "Vendor"),
            Hit("ID_NATIVE", "Native", "Modify", "Modify", "Modify"),
        ];
        var catalog = SearchCatalog.Build([], hits);
        // Handles 0 and 2 are greyed add-in buttons; 1 and 3 can run; the Revit tool has no handler.
        PlaceState State(SearchCatalogPlace p) => new(true, true, CanExecute: p.Handle switch { 0 or 2 => false, 4 => null, _ => true });

        var list = SearchShowing.List(catalog, isFamily, State);

        list.Tools.Select(Show).Should().Equal(
            $"{twoPlaces}: Two elsewhere @ Vendor › Tools",
            "CustomCtrl_%Vendor%Tools%Ready: Ready @ Add-Ins › Vendor",
            "ID_NATIVE: Native @ Modify › Modify");
        list.Tools[0].OtherPlaces.Should().Be(0, "\"+N more places\" counts showing places only");
        // With every button able to run, nothing is left out.
        SearchShowing.List(catalog, isFamily, _ => new PlaceState(true, true, CanExecute: true)).Tools.Should().HaveCount(4);
        new PlaceState(true, true, CanExecute: false).Shows(onFamilyEditorTab: false, isFamily).Should().BeFalse();
    }

    [Fact]
    public void A_tool_takes_its_name_and_place_from_its_first_showing_place()
    {
        var catalog = SearchCatalog.Build([],
        [
            Hit("ID_T", "Hidden name", "Architecture", "Architecture", "Build"),
            Hit("ID_T", "Second name", "Annotate", "Annotate", "Tag"),
            Hit("ID_T", "Third name", "Modify", "Modify", "Create"),
        ]);
        var tool = SearchShowing.List(catalog, isFamily: false, p => new PlaceState(true, PanelVisible: p.Handle != 0)).Tools.Single();
        tool.DisplayName.Should().Be("Second name");
        tool.Location.Should().Be("Annotate › Tag");
        tool.OtherPlaces.Should().Be(1);
        SearchShowing.List(catalog, isFamily: false, _ => new PlaceState(true, false)).Tools.Should().BeEmpty("no showing place, not listed");
    }
}

// Swaps the process-wide Log.Logger: runs alone, as the log facade's own tests do.
[Collection(LogFacadeTests.Collection)]
public sealed class SearchShowingFallbackTests
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

    // ---- AC11: a failure reading the state never stops Search-It ---------------------------------

    [Theory]
    [InlineData(SearchCapture.Project, false)]
    [InlineData(SearchCapture.GenericModelFamily, true)]
    public void When_the_state_cannot_be_read_the_unfiltered_tools_are_listed_and_one_warning_is_logged(string context, bool isFamily)
    {
        var capture = new SearchCapture(context);
        var catalog = capture.Catalog();
        var boom = new InvalidOperationException("the ribbon is gone");
        var reads = 0;
        SearchList? list = null;

        var events = CaptureLog(() => list = SearchShowing.ListOrFallBack(catalog, isFamily,
            place => ++reads > 40 ? throw boom : capture.StateOf(place)));

        var warning = events.Should().ContainSingle().Subject;
        warning.Level.Should().Be(LogLevel.Warning);
        warning.Exception.Should().BeSameAs(boom);
        warning.MessageTemplate.Should().StartWith("Search-It: the ribbon's state could not be read");

        // The fallback: every tool with a place outside the family-editor tabs, with all of those places.
        list!.IsFamily.Should().BeFalse("the fallback is not a family's list");
        var fallback = list.Tools;
        fallback.Should().NotBeEmpty();
        var expected = catalog.Select(t => (t.Id, Places: t.Places.Where(p => !p.OnFamilyEditorTab).Select(p => p.ToSearchPlace()).ToArray()))
            .Where(t => t.Places.Length > 0).ToArray();
        fallback.Select(t => t.Id).Should().Equal(expected.Select(t => t.Id));
        for (var i = 0; i < expected.Length; i++) fallback[i].Places.Should().Equal(expected[i].Places);
        // It is the list Search-It showed before this delivery, plus the one mode-only id with a project place.
        var before = SearchIndex.Build(capture.BuilderCatalog());
        fallback.Where(t => t.Id != "ID_LOAD_INTO_PROJECTS").Select(t => t.Id).Should().Equal(before.Select(t => t.Id));
        fallback.Select(t => t.Id).Should().NotContain("ID_OBJECTS_EXTRUSION");
    }

    [Fact]
    public void A_readable_state_logs_nothing()
    {
        var capture = new SearchCapture(SearchCapture.GenericModelFamily);
        SearchList? list = null;
        var events = CaptureLog(() => list = SearchShowing.ListOrFallBack(capture.Catalog(), isFamily: true, capture.StateOf));
        events.Should().BeEmpty();
        list!.IsFamily.Should().BeTrue();
        list.Tools.Select(t => t.Id).Should().Equal(capture.List(isFamily: true).Tools.Select(t => t.Id));
    }
}
