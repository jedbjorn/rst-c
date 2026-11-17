// Spec #35, review of PR #231 (fixes 1 and 2): every live CanExecute / Execute on a ribbon item goes through
// SearchGate, which reads the item's tab and panel again first and never asks an item that is not showing (asking a
// hidden family tab's item crashed Revit, WU1c and WU1d); and a click asks the gate for postable tools too.

using FluentAssertions;
using RST.Core.Scanning;
using RST.Core.Search;
using Xunit;

namespace RST.Tests.Search;

public sealed class SearchGateTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void An_item_whose_tab_or_panel_is_not_showing_is_never_asked(bool tabVisible, bool panelVisible)
    {
        var asked = 0;
        var answer = SearchGate.Ask(() => tabVisible, () => panelVisible, () => { asked++; return true; }, out var error);
        answer.Should().Be(GateAnswer.NotShowing);
        asked.Should().Be(0);
        error.Should().BeNull();
        SearchGate.AllowsClick(answer).Should().BeFalse("the amber message");
        SearchGate.AllowsExecute(answer).Should().BeFalse();
    }

    [Fact]
    public void The_tab_is_read_before_the_panel_and_the_handler_last()
    {
        var order = new List<string>();
        SearchGate.Ask(() => { order.Add("tab"); return false; }, () => { order.Add("panel"); return true; }, () => { order.Add("ask"); return true; }, out _);
        order.Should().Equal("tab");
        order.Clear();
        SearchGate.Ask(() => { order.Add("tab"); return true; }, () => { order.Add("panel"); return true; }, () => { order.Add("ask"); return true; }, out _);
        order.Should().Equal("tab", "panel", "ask");
    }

    [Theory]
    [InlineData(true, GateAnswer.Runs, true, true)]
    [InlineData(false, GateAnswer.Greyed, false, false)]
    [InlineData(null, GateAnswer.NothingToAsk, true, false)]
    public void A_showing_item_is_asked_and_the_answer_decides_click_and_execute(bool? canExecute, GateAnswer expected, bool click, bool execute)
    {
        var answer = SearchGate.Ask(() => true, () => true, () => canExecute, out _);
        answer.Should().Be(expected);
        SearchGate.AllowsClick(answer).Should().Be(click);
        SearchGate.AllowsExecute(answer).Should().Be(execute);
    }

    [Fact]
    public void A_throw_from_any_read_is_Threw_with_the_exception_and_never_escapes()
    {
        var boom = new InvalidOperationException("ribbon gone");
        SearchGate.Ask(() => throw boom, () => true, () => true, out var e1).Should().Be(GateAnswer.Threw);
        e1.Should().BeSameAs(boom);
        SearchGate.Ask(() => true, () => true, () => throw boom, out var e2).Should().Be(GateAnswer.Threw);
        e2.Should().BeSameAs(boom);
        SearchGate.AllowsClick(GateAnswer.Threw).Should().BeFalse();
        SearchGate.AllowsExecute(GateAnswer.Threw).Should().BeFalse();
    }

    // ---- decision #127: a click chooses its place from fresh visibility ---------------------------

    private static RibbonHit Hit(string id, string tabId, string panel, bool postable = true, string path = "") =>
        new(id, id, RibbonContainerKind.Panel, RibbonLeafKind.Button, null, tabId, panel, tabId, path, postable);

    /// <summary>A fake ribbon: tab and panel visibility per handle, read now; records what the gate was asked.</summary>
    private sealed class Ribbon
    {
        public HashSet<int> HiddenTabs { get; } = [];
        public HashSet<int> HiddenPanels { get; } = [];
        public List<int> Asked { get; } = [];
        public bool? Answer { get; set; } = true;

        public (bool Tab, bool Panel) Now(int h) => (!HiddenTabs.Contains(h), !HiddenPanels.Contains(h));

        public (GateAnswer, Exception?) Ask(int h)
        {
            Asked.Add(h);
            return (SearchGate.Ask(() => Now(h).Tab, () => Now(h).Panel, () => Answer, out var e), e);
        }

        public ClickDecision Click(SearchTool tool) => SearchClick.Decide(tool, Now, Ask);
    }

    [Fact]
    public void The_unfiltered_fallback_chooses_at_the_click_once_visibility_has_recovered()
    {
        var catalog = SearchCatalog.Build([],
        [
            Hit("ID_SITE_SETTINGS", "MassingSite", "Model Site", postable: false, path: "DialogLauncher"),
            Hit("ID_OBJECTS_WALL", "Architecture", "Build"),
        ]);
        var list = SearchShowing.Unfiltered(catalog, isFamily: false);
        var ribbon = new Ribbon();

        // The ribbon could not be read when the list was made; at the click it can.
        var wall = ribbon.Click(list.Tools.Single(t => t.Id == "ID_OBJECTS_WALL"));
        wall.Route.Should().Be(ClickRoute.Run, "a postable tool is asked too (Running a Tool, 4)");
        wall.Handle.Should().Be(1);
        var site = ribbon.Click(list.Tools.Single(t => t.Id == "ID_SITE_SETTINGS"));
        (site.Route, site.Handle).Should().Be((ClickRoute.Run, 0));
        ribbon.Asked.Should().Equal(1, 0);

        ribbon.Answer = false;
        ribbon.Click(list.Tools.Single(t => t.Id == "ID_OBJECTS_WALL")).Route.Should().Be(ClickRoute.Refuse, "greyed now: the amber message");
    }

    [Fact]
    public void A_hidden_drawn_place_and_a_visible_not_drawn_place_run_on_the_visible_one()
    {
        // The census marks the visible place's path not drawn; the drawn one is on a tab that does not show now.
        var catalog = SearchCatalog.Build([],
        [
            Hit("ID_T", "Structure", "Structure"),                     // 0: drawn, tab hidden now
            Hit("ID_T", "Architecture", "Build", path: "/SlideOut"),   // 1: not drawn, showing
        ]);
        var census = FakeCensus.Project("FloorPlan",
            FakeCensus.Index("project/FloorPlan\tID_T\tArchitecture\tBuild\t/SlideOut\tnot-drawn\t"), FakeCensus.RunsIn("project/FloorPlan\tID_T"));
        var tool = SearchRunnable.List(catalog, false, p => new PlaceState(p.TabId != "Structure", true), _ => true, census).Tools.Single();
        tool.RunPlaces.Select(p => (p.Handle, p.Drawn)).Should().Equal((0, true), (1, false));

        var ribbon = new Ribbon();
        ribbon.HiddenTabs.Add(0);
        var click = ribbon.Click(tool);

        (click.Route, click.Handle).Should().Be((ClickRoute.Run, 1), "the first place showing now, drawn ones first");
        ribbon.Asked.Should().Equal([1], "the hidden drawn place is never asked");

        // When the drawn place shows again, it is preferred.
        ribbon.HiddenTabs.Clear();
        ribbon.Click(tool).Handle.Should().Be(0);
    }

    [Theory]
    [InlineData(true, ClickRoute.PostWithoutAsking)]
    [InlineData(false, ClickRoute.Refuse)]
    public void A_tool_only_on_user_hidden_project_tabs_is_posted_without_asking_if_postable(bool postable, ClickRoute expected)
    {
        // Beam (postable) or HVAC Zone (not), with the tab hidden in Revit's options (spec #33 AC12).
        var catalog = SearchCatalog.Build([], [Hit("ID_TOOL", "Structure", "Structure", postable)]);
        var tool = SearchRunnable.List(catalog, false, _ => new PlaceState(false, true), _ => true, null).Tools.Single();
        var ribbon = new Ribbon();
        ribbon.HiddenTabs.Add(0);

        var click = ribbon.Click(tool);

        click.Route.Should().Be(expected);
        ribbon.Asked.Should().BeEmpty("a hidden tab's item is never asked");
    }

    [Fact]
    public void Nothing_showing_on_a_visible_tab_or_in_a_family_gives_the_amber_message()
    {
        var catalog = SearchCatalog.Build([], [Hit("ID_TOOL", "Modify", "Draw")]);
        var tool = SearchRunnable.List(catalog, false, _ => new PlaceState(true, true), _ => true, null).Tools.Single();
        var ribbon = new Ribbon();
        ribbon.HiddenPanels.Add(0);    // the tab shows, the panel does not: not a user-hidden tab
        ribbon.Click(tool).Route.Should().Be(ClickRoute.Refuse);

        var inFamily = SearchRunnable.List(SearchCatalog.Build([], [Hit("ID_TOOL", "Structure", "Structure")]), true,
            _ => new PlaceState(true, true), _ => true, null).Tools.Single();
        inFamily.InFamily.Should().BeTrue();
        var family = new Ribbon();
        family.HiddenTabs.Add(0);      // Revit hides project tabs in a family; the user did not
        family.Click(inFamily).Route.Should().Be(ClickRoute.Refuse);
    }

    [Fact]
    public void A_Must_Run_In_tool_has_no_run_places_and_is_refused()
    {
        var tool = new SearchTool("ID_OBJECTS_LEVEL", "Level", "Architecture", "Datum") { Mark = SearchToolMark.MustRunIn, MustRunIn = ["Elevation"] };
        new Ribbon().Click(tool).Route.Should().Be(ClickRoute.Refuse);
    }

    [Fact]
    public void A_gate_that_throws_at_the_click_is_refused_with_the_error()
    {
        var tool = SearchRunnable.List(SearchCatalog.Build([], [Hit("ID_TOOL", "Architecture", "Build")]), false,
            _ => new PlaceState(true, true), _ => true, null).Tools.Single();
        var boom = new InvalidOperationException("UIFramework");
        var click = SearchClick.Decide(tool, _ => (true, true), _ => (SearchGate.Ask(() => true, () => true, () => throw boom, out var e), e));
        click.Route.Should().Be(ClickRoute.Refuse);
        click.Error.Should().BeSameAs(boom);
    }
}
