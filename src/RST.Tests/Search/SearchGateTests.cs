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

    // ---- which item a click asks, and whether it asks --------------------------------------------

    private static RibbonHit Hit(string id, string tabId, string panel, bool postable = true) =>
        new(id, id, RibbonContainerKind.Panel, RibbonLeafKind.Button, null, tabId, panel, tabId, "", postable);

    private static SearchTool Only(IReadOnlyList<SearchCatalogTool> catalog, params string[] hiddenTabs) =>
        SearchRunnable.List(catalog, false, p => new PlaceState(!hiddenTabs.Contains(p.TabId), true), _ => true, null).Tools.Single();

    [Fact]
    public void A_postable_tool_only_on_hidden_project_tabs_is_posted_without_asking()
    {
        // Beam, with the Structure tab hidden in Revit's options (spec #33 AC12, decision #125).
        var beam = Only(SearchCatalog.Build([], [Hit("ID_OBJECTS_BEAM", "Structure", "Structure")]), "Structure");
        beam.GateAtClick.Should().BeFalse();
        beam.Postable.Should().BeTrue();
    }

    [Fact]
    public void A_non_postable_tool_only_on_hidden_project_tabs_is_asked_and_so_refused()
    {
        var zone = Only(SearchCatalog.Build([], [Hit("ID_RBS_HVAC_ZONE", "Analyze", "Spaces", postable: false)]), "Analyze");
        zone.GateAtClick.Should().BeTrue("route A needs CanExecute, and SearchGate never asks a hidden tab's item");
    }

    [Fact]
    public void A_click_asks_the_first_listed_place_on_a_visible_tab()
    {
        // Structural Column: listed first on the hidden Structure tab, also on Architecture › Build.
        var column = Only(SearchCatalog.Build([],
            [Hit("ID_OBJECTS_STRUCTURAL_COLUMN", "Structure", "Structure"), Hit("ID_OBJECTS_STRUCTURAL_COLUMN", "Architecture", "Build")]), "Structure");
        column.Location.Should().Be("Structure › Structure");
        column.RunHandle.Should().Be(1, "the gate is asked on the visible tab's item");
        column.GateAtClick.Should().BeTrue("a postable tool with a place on a visible tab is asked too (spec #35, Running a Tool, 4)");
    }
}
