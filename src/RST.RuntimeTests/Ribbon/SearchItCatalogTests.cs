// Spec #33 (Search-It lists the tools of the open file), the Engine half: RibbonScanner walks the
// family-editor tabs and keeps the live tab, panel and item behind each hit; SearchItCatalog reads
// their state when Search-It opens. On real Autodesk.Windows objects built outside Revit, as
// RibbonScannerWalkTests does. The rule itself (which state shows a place) is unit-tested on plain
// data and on the Work Unit 1 captures in RST.Tests/Search/SearchCatalogTests.
//
// The fake ribbon has the shape of a Revit 2026 session (tab ids from the Work Unit 1 capture):
//   Architecture (Architecture)     Build: Wall · Model: Model Line
//   Modify (Modify)                 Geometry: Join · Draw: Model Line
//   Add-Ins (Add-Ins)               Kinship: Open Library (an add-in's button)
//   Create (Home_Family)            Forms: Extrusion · Model: Model Line
//   Family Editor (FamilyEditorTab) Family Editor: Load into Project
//   In-Place Model (an ordinary id) In-Place Editor: Finish Model  ← a mode tab known by title only

using System.Windows.Input;
using Autodesk.Windows;
using FluentAssertions;
using RST.Core.Logging;
using RST.Core.Scanning;
using RST.Core.Search;
using RST.Engine.Scanning;
using RST.RuntimeTests.Support;
using Xunit;

namespace RST.RuntimeTests.Ribbon;

// The theme collection runs alone, so the process-wide Log.Logger swap sees only these tests.
[Collection(WpfThemeCollection.Name)]
public sealed class SearchItCatalogTests
{
    private const string AddinId = "CustomCtrl_%CustomCtrl_%Add-Ins%Kinship%Open";

    private sealed class Handler(Func<bool> canExecute) : ICommand
    {
        public int Asked { get; private set; }
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) { Asked++; return canExecute(); }
        public void Execute(object? parameter) { }
    }

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
        try
        {
            action();
        }
        finally
        {
            Log.Logger = previous;
        }
        return events;
    }

    private static RibbonButton Button(string id, string text) => new() { Id = id, Text = text };

    private static RibbonPanel Panel(string title, params RibbonItem[] items)
    {
        var source = new RibbonPanelSource { Title = title };
        foreach (var item in items) source.Items.Add(item);
        return new RibbonPanel { Source = source, IsVisible = true };
    }

    private static RibbonTab Tab(string title, string id, params RibbonPanel[] panels)
    {
        var tab = new RibbonTab { Title = title, Id = id, IsVisible = true };
        foreach (var panel in panels) tab.Panels.Add(panel);
        return tab;
    }

    /// <summary>The fake ribbon (file header), scanned once: what a Revit session holds after the first open.</summary>
    private sealed class Session
    {
        public RibbonButton Addin { get; } = Button(AddinId, "Open Library");
        public RibbonButton LoadIntoProject { get; } = Button("ID_LOAD_INTO_PROJECTS", "Load into Project");
        public RibbonTab Architecture { get; }
        public RibbonTab Modify { get; }
        public RibbonTab AddIns { get; }
        public RibbonTab FamilyCreate { get; }
        public RibbonTab FamilyEditor { get; }
        public RibbonTab InPlace { get; }
        public RibbonScanCounts Counts { get; } = new();
        public RibbonScanCounts FamilyCounts { get; } = new();
        public RibbonScan Scan { get; }
        public SearchItCatalog Catalog { get; }

        public Session()
        {
            Architecture = Tab("Architecture", "Architecture",
                Panel("Build", Button("ID_OBJECTS_WALL", "Wall")),
                Panel("Model", Button("ID_OBJECTS_CLINE", "Model Line")));
            Modify = Tab("Modify", "Modify",
                Panel("Geometry", Button("ID_JOIN", "Join")),
                Panel("Draw", Button("ID_OBJECTS_CLINE", "Model Line")));
            AddIns = Tab("Add-Ins", "Add-Ins", Panel("Kinship", Addin));
            FamilyCreate = Tab("Create", "Home_Family",
                Panel("Forms", Button("ID_OBJECTS_EXTRUSION", "Extrusion")),
                Panel("Model", Button("ID_OBJECTS_CLINE", "Model Line")));
            FamilyEditor = Tab("Family Editor", "FamilyEditorTab", Panel("Family Editor", LoadIntoProject));
            InPlace = Tab("In-Place Model", "InPlaceModel", Panel("In-Place Editor", Button("ID_FINISH_MODEL", "Finish Model")));

            Scan = RibbonScanner.ScanTabs([Architecture, Modify, AddIns, FamilyCreate, FamilyEditor, InPlace], _ => true, Counts,
                familyCounts: FamilyCounts);
            // Outside Revit there is no Revit ribbon handler to ask: Revit's own items answer "nothing to ask" unless a test says.
            Catalog = new SearchItCatalog(SearchCatalog.Build([], Scan.Hits), Scan.Handles) { NativeGate = _ => null };
        }

        /// <summary>The ribbon as Revit shows it with a project open: the family-editor tabs are hidden.</summary>
        public Session InAProject()
        {
            foreach (var tab in new[] { Architecture, Modify, AddIns }) tab.IsVisible = true;
            foreach (var tab in new[] { FamilyCreate, FamilyEditor }) tab.IsVisible = false;
            return this;
        }

        /// <summary>With a family open: the project tabs are hidden, Modify and Add-Ins stay.</summary>
        public Session InAFamily()
        {
            Architecture.IsVisible = false;
            foreach (var tab in new[] { Modify, AddIns, FamilyCreate, FamilyEditor }) tab.IsVisible = true;
            return this;
        }
    }

    private static IEnumerable<string> Ids(SearchList list) => list.Tools.Select(t => t.Id);

    private static IEnumerable<string> Places(SearchList list, string id) =>
        list.Tools.Single(t => t.Id == id).Places.Select(p => p.Location);

    // ── the scan ─────────────────────────────────────────────────────────

    [Fact]
    public void The_scan_walks_the_family_editor_tabs_and_keeps_them_from_the_Builder() => Screenshots.RunSta(() =>
    {
        var session = new Session();

        session.Scan.Hits.Select(h => (h.Id, h.TabId)).Should().Equal(
            ("ID_OBJECTS_WALL", "Architecture"), ("ID_OBJECTS_CLINE", "Architecture"),
            ("ID_JOIN", "Modify"), ("ID_OBJECTS_CLINE", "Modify"),
            (AddinId, "Add-Ins"),
            ("ID_OBJECTS_EXTRUSION", "Home_Family"), ("ID_OBJECTS_CLINE", "Home_Family"),
            ("ID_LOAD_INTO_PROJECTS", "FamilyEditorTab"));
        // A mode tab known only by its title is still not walked.
        session.Scan.Hits.Should().NotContain(h => h.Id == "ID_FINISH_MODEL");

        // The Builder's catalogue is merged from the hits it always had, and the main summary counts only those tabs.
        session.Scan.BuilderHits.Select(h => h.TabId).Should().Equal("Architecture", "Architecture", "Modify", "Modify", "Add-Ins");
        RibbonScanner.EnumerateTabs([session.Architecture, session.Modify, session.AddIns, session.FamilyCreate, session.FamilyEditor, session.InPlace], _ => true)
            .Should().Equal(session.Scan.BuilderHits);
        session.Counts.TabsTotal.Should().Be(6);
        session.Counts.TabsWalked.Should().Be(3);
        session.Counts.CollectedTotal.Should().Be(5);
        session.FamilyCounts.TabsWalked.Should().Be(2);
        session.FamilyCounts.CollectedTotal.Should().Be(3);
    });

    [Fact]
    public void Each_hit_keeps_the_live_tab_panel_and_item_it_came_from() => Screenshots.RunSta(() =>
    {
        var session = new Session();

        session.Scan.Handles.Should().HaveSameCount(session.Scan.Hits);
        for (var i = 0; i < session.Scan.Hits.Count; i++)
        {
            var (hit, handle) = (session.Scan.Hits[i], session.Scan.Handles[i]);
            handle.Item.Id.Should().Be(hit.Id);
            handle.Tab.Id.Should().Be(hit.TabId);
            handle.Panel.Source.Title.Should().Be(hit.SourcePanel);
            handle.Panel.Source.Items.Should().Contain(handle.Item);
            handle.Tab.Panels.Should().Contain(handle.Panel);
        }
        session.Scan.Handles.Select(h => h.Item).Should().Contain(new RibbonItem[] { session.Addin, session.LoadIntoProject });
    });

    [Fact]
    public void A_dialog_launcher_hit_keeps_its_handle_after_the_panel_items() => Screenshots.RunSta(() =>
    {
        var launcher = Button("ID_SETTINGS_STRUCTURAL", "Structural Settings");
        var panel = Panel("Structure", Button("ID_OBJECTS_BEAM", "Beam"));
        panel.Source.DialogLauncher = launcher;
        var structure = Tab("Structure", "Structure", panel);
        var manage = Tab("Manage", "Manage", Panel("Settings", Button("ID_SETTINGS_MATERIALS", "Materials")));

        var scan = RibbonScanner.ScanTabs([structure, manage], _ => true);

        scan.Hits.Select(h => h.Id).Should().Equal("ID_OBJECTS_BEAM", "ID_SETTINGS_MATERIALS", "ID_SETTINGS_STRUCTURAL");
        scan.Handles.Select(h => h.Item).Should().Equal(panel.Source.Items[0], manage.Panels[0].Source.Items[0], launcher);
        scan.Handles[2].Tab.Should().BeSameAs(structure);
        scan.Handles[2].Panel.Should().BeSameAs(panel);
    });

    // ── the state read when Search-It opens ──────────────────────────────

    [Fact]
    public void A_project_lists_project_places_and_a_family_lists_family_places_from_the_same_scan() => Screenshots.RunSta(() =>
    {
        var session = new Session();

        var project = session.InAProject().Catalog.ListShowing(isFamily: false);
        project.IsFamily.Should().BeFalse();
        Ids(project).Should().Equal("ID_OBJECTS_WALL", "ID_OBJECTS_CLINE", "ID_JOIN", AddinId);
        Places(project, "ID_OBJECTS_CLINE").Should().Equal("Architecture › Model", "Modify › Draw");

        // The same objects, in the state Revit puts them in for a family: nothing is scanned again.
        var family = session.InAFamily().Catalog.ListShowing(isFamily: true);
        family.IsFamily.Should().BeTrue();
        Ids(family).Should().Equal("ID_OBJECTS_CLINE", "ID_JOIN", AddinId, "ID_OBJECTS_EXTRUSION", "ID_LOAD_INTO_PROJECTS");
        Places(family, "ID_OBJECTS_CLINE").Should().Equal("Modify › Draw", "Create › Model");
        Places(family, "ID_LOAD_INTO_PROJECTS").Should().Equal("Family Editor");
    });

    [Fact]
    public void A_project_tab_the_user_has_hidden_still_counts_in_a_project_and_a_hidden_panel_never_does() => Screenshots.RunSta(() =>
    {
        var session = new Session().InAProject();
        session.Architecture.IsVisible = false;            // hidden in Revit's options
        session.Modify.Panels[1].IsVisible = false;        // Modify › Draw: shown only while a tool needs it

        var project = session.Catalog.ListShowing(isFamily: false);

        Ids(project).Should().Contain("ID_OBJECTS_WALL");
        Places(project, "ID_OBJECTS_CLINE").Should().Equal("Architecture › Model");
    });

    [Fact]
    public void A_greyed_add_in_button_is_not_listed_and_one_that_can_run_is() => Screenshots.RunSta(() =>
    {
        var session = new Session().InAProject();
        var canRun = true;
        var handler = new Handler(() => canRun);
        session.Addin.CommandHandler = handler;

        Ids(session.Catalog.ListShowing(isFamily: false)).Should().Contain(AddinId);
        handler.Asked.Should().Be(1, "the handler is asked once per place, when Search-It opens");

        canRun = false;
        var greyed = session.Catalog.ListShowing(isFamily: false);
        Ids(greyed).Should().Equal("ID_OBJECTS_WALL", "ID_OBJECTS_CLINE", "ID_JOIN");

        // No handler to ask (the button is drawn but not wired): it stays listed.
        session.Addin.CommandHandler = null;
        Ids(session.Catalog.ListShowing(isFamily: false)).Should().Contain(AddinId);
    });

    [Fact]
    public void Revits_own_buttons_own_handlers_are_not_asked() => Screenshots.RunSta(() =>
    {
        var session = new Session().InAProject();
        var handler = new Handler(() => false);
        // A few Revit buttons carry a handler (a UIFramework visibility binding); it does not say whether the tool runs.
        foreach (var handle in session.Scan.Handles)
            if (handle.Item is RibbonCommandItem item && item.Id != AddinId) item.CommandHandler = handler;

        var project = session.Catalog.ListShowing(isFamily: false);

        Ids(project).Should().Equal("ID_OBJECTS_WALL", "ID_OBJECTS_CLINE", "ID_JOIN", AddinId);
        handler.Asked.Should().Be(0);
    });

    // ── spec #35: Revit's live gate, the census path, ids Revit won't post ──

    [Fact]
    public void Revits_global_gate_is_asked_once_per_tool_and_only_for_items_on_visible_tabs() => Screenshots.RunSta(() =>
    {
        var session = new Session().InAFamily();
        var asked = new List<string>();
        var catalog = session.Catalog with
        {
            NativeGate = item =>
            {
                asked.Add(item.Id);
                return item.Id != "ID_JOIN";
            },
        };

        var family = catalog.ListShowing(isFamily: true);

        // Architecture is hidden in a family: its items are never asked. Model Line is asked once (Modify › Draw),
        // not again at Create › Model. Join is greyed and, with no census, left out.
        asked.Should().Equal("ID_OBJECTS_CLINE", "ID_JOIN", "ID_OBJECTS_EXTRUSION", "ID_LOAD_INTO_PROJECTS");
        Ids(family).Should().Equal("ID_OBJECTS_CLINE", AddinId, "ID_OBJECTS_EXTRUSION", "ID_LOAD_INTO_PROJECTS");
        family.LeftOut.Should().Be(1);
        family.Tools.Should().OnlyContain(t => t.Mark == SearchToolMark.None, "no census: nothing is badged");
    });

    [Fact]
    public void A_gate_that_throws_counts_as_runnable_and_is_logged_once_at_Debug() => Screenshots.RunSta(() =>
    {
        var session = new Session().InAProject();
        var catalog = session.Catalog with { NativeGate = _ => throw new InvalidOperationException("UIFramework") };

        SearchList? list = null;
        var events = CaptureLog(() => list = catalog.ListShowing(isFamily: false));

        Ids(list!).Should().Equal("ID_OBJECTS_WALL", "ID_OBJECTS_CLINE", "ID_JOIN", AddinId);
        events.Where(e => e.Level == LogLevel.Debug && e.Exception is InvalidOperationException).Should().ContainSingle();
        events.Should().NotContain(e => e.Level >= LogLevel.Warning);
    });

    [Fact]
    public void A_disabled_add_in_button_is_greyed_whatever_its_handler_says() => Screenshots.RunSta(() =>
    {
        var session = new Session().InAProject();
        var handler = new Handler(() => true);
        session.Addin.CommandHandler = handler;
        session.Addin.IsEnabled = false;

        Ids(session.Catalog.ListShowing(isFamily: false)).Should().NotContain(AddinId);
        handler.Asked.Should().Be(0, "IsEnabled is read first, as the census did");
    });

    [Fact]
    public void Each_hit_carries_the_census_path_of_its_place() => Screenshots.RunSta(() =>
    {
        var inSplit = Button("ID_OBJECTS_WALL", "Wall: Architectural");
        var split = new RibbonSplitButton { Id = "ID_OBJECTS_WALL_RibbonListButton", Text = "Wall" };
        split.Items.Add(inSplit);
        var fold = new RibbonFoldPanel();
        fold.Items.Add(Button("ID_OBJECTS_DOOR", "Door"));
        var launcher = Button("ID_SETTINGS_STRUCTURAL", "Structural Settings");
        var panel = Panel("Build", Button("ID_BUTTON_SELECT", "Modify"), split, fold, new RibbonPanelBreak(), Button("ID_SLIDE", "In the slide-out"));
        panel.Source.DialogLauncher = launcher;

        var scan = RibbonScanner.ScanTabs([Tab("Architecture", "Architecture", panel)], _ => true);

        scan.Hits.Select(h => (h.Id, h.Path)).Should().Equal(
            ("ID_BUTTON_SELECT", ""),
            ("ID_OBJECTS_WALL", "/RibbonSplitButton(ID_OBJECTS_WALL_RibbonListButton)"),
            ("ID_OBJECTS_DOOR", "/RibbonFoldPanel()"),
            ("ID_SLIDE", "/SlideOut"),
            ("ID_SETTINGS_STRUCTURAL", "DialogLauncher"));
    });

    [Fact]
    public void Ids_Revit_wont_post_reach_Search_It_only() => Screenshots.RunSta(() =>
    {
        var textTypes = Button("ID_SETTINGS_TEXT", "Text Types");
        var panel = Panel("Text", Button("ID_TEXT", "Text"));
        panel.Source.DialogLauncher = textTypes;
        var counts = new RibbonScanCounts();

        var scan = RibbonScanner.ScanTabs([Tab("Annotate", "Annotate", panel)], _ => true, counts, isPostableCommand: id => id != "ID_SETTINGS_TEXT");

        scan.Hits.Select(h => (h.Id, h.Postable)).Should().Equal(("ID_TEXT", true), ("ID_SETTINGS_TEXT", false));
        scan.BuilderHits.Select(h => h.Id).Should().Equal("ID_TEXT");
        counts.CollectedTotal.Should().Be(1, "the summary counts what the Builder gets");
        counts.OutsidePostableCommandIds.Should().Equal("ID_SETTINGS_TEXT");
        var catalog = new SearchItCatalog(SearchCatalog.Build([], scan.Hits), scan.Handles) { NativeGate = _ => true };
        var text = catalog.ListShowing(isFamily: false).Tools.Single(t => t.Id == "ID_SETTINGS_TEXT");
        text.Postable.Should().BeFalse();
        catalog.ItemAt(text.RunHandle).Should().BeSameAs(textTypes, "a click runs the ribbon item itself (route A)");
        catalog.ItemAt(null).Should().BeNull();
        catalog.ItemAt(99).Should().BeNull();
    });

    [Fact]
    public void A_handler_that_throws_counts_as_cannot_run_and_is_logged_once_at_Debug() => Screenshots.RunSta(() =>
    {
        var session = new Session().InAProject();
        // A second button of the same add-in, on another panel, whose handler throws too.
        var second = Button("CustomCtrl_%CustomCtrl_%Add-Ins%Kinship%Sync", "Sync");
        var tabs = new[] { session.Architecture, session.AddIns, Tab("Kinship", "Kinship", Panel("Library", second)) };
        var scan = RibbonScanner.ScanTabs(tabs, _ => true);
        var catalog = new SearchItCatalog(SearchCatalog.Build([], scan.Hits), scan.Handles) { NativeGate = _ => null };
        session.Addin.CommandHandler = new Handler(() => throw new InvalidOperationException("add-in bug"));
        second.CommandHandler = new Handler(() => throw new InvalidOperationException("add-in bug"));

        SearchList? list = null;
        var events = CaptureLog(() => list = catalog.ListShowing(isFamily: false));

        Ids(list!).Should().Equal("ID_OBJECTS_WALL", "ID_OBJECTS_CLINE");
        var debug = events.Where(e => e.Level == LogLevel.Debug && e.Exception is InvalidOperationException).ToList();
        debug.Should().ContainSingle("a throwing handler is logged once per open");
        events.Should().NotContain(e => e.Level >= LogLevel.Warning, "a handler that throws is not a failure to read the ribbon");
    });

    [Fact]
    public void State_that_cannot_be_read_gives_the_unfiltered_list_and_one_Warning() => Screenshots.RunSta(() =>
    {
        var session = new Session().InAFamily();
        // Handles that do not match the catalogue (a scan that lost its objects): every read fails.
        var broken = session.Catalog with { Handles = [] };

        // In a family: the whole catalogue, family-editor tabs included, not named a family's list.
        SearchList? family = null;
        var events = CaptureLog(() => family = broken.ListShowing(isFamily: true));

        family!.IsFamily.Should().BeFalse("the list mixes project and family tools, so the window does not name it a family's");
        Ids(family).Should().Equal("ID_OBJECTS_WALL", "ID_OBJECTS_CLINE", "ID_JOIN", AddinId, "ID_OBJECTS_EXTRUSION", "ID_LOAD_INTO_PROJECTS");
        Places(family, "ID_OBJECTS_CLINE").Should().Equal("Architecture › Model", "Modify › Draw", "Create › Model");
        events.Where(e => e.Level >= LogLevel.Warning).Should().ContainSingle()
            .Which.Level.Should().Be(LogLevel.Warning);

        // In a project: the tools outside the family-editor tabs.
        SearchList? project = null;
        events = CaptureLog(() => project = broken.ListShowing(isFamily: false));

        project!.IsFamily.Should().BeFalse();
        Ids(project).Should().Equal("ID_OBJECTS_WALL", "ID_OBJECTS_CLINE", "ID_JOIN", AddinId);
        Places(project, "ID_OBJECTS_CLINE").Should().Equal("Architecture › Model", "Modify › Draw");
        events.Where(e => e.Level >= LogLevel.Warning).Should().ContainSingle();
    });
}
