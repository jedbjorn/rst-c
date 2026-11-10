// Decision #79: AccessoriesPanel on real Autodesk.Windows objects built outside Revit (as RibbonScannerWalkTests
// does). The fake Add-Ins tab copies what the sister apps create: one panel each, titled with the app's name,
// holding buttons whose ids end in the app's PushButtonData names. Which ids count is unit-tested on plain data
// in RST.Tests/Ribbon/AccessoryPanelsTests.

using Autodesk.Windows;
using FluentAssertions;
using RST.Core.Ribbon;
using RST.Engine.Ribbon;
using Xunit;

namespace RST.RuntimeTests.Ribbon;

public sealed class AccessoriesPanelTests
{
    private static RibbonButton Button(string panel, string name) =>
        new() { Id = $"CustomCtrl_%CustomCtrl_%Add-Ins%{panel}%{name}", Text = name };

    private static RibbonPanel Panel(string title, params RibbonItem[] items)
    {
        var source = new RibbonPanelSource { Title = title };
        foreach (var item in items) source.Items.Add(item);
        return new RibbonPanel { Source = source };
    }

    private static RibbonTab Tab(params RibbonPanel[] panels)
    {
        var tab = new RibbonTab { Title = "Add-Ins", Id = "Add-Ins" };
        foreach (var panel in panels) tab.Panels.Add(panel);
        return tab;
    }

    private static string[] Titles(RibbonTab tab) => tab.Panels.Where(p => p.IsVisible).Select(p => p.Source.Title).ToArray();

    private static RibbonPanelSource Accessories(RibbonTab tab) => tab.Panels.Single(p => p.Source.Id == AccessoryPanels.PanelId).Source;

    [Fact]
    public void Sister_buttons_move_to_one_panel_after_RST_in_registry_order_and_their_panels_are_hidden()
    {
        // Load order differs from registry order, and another add-in sits between them.
        var pick = Button("Pick-It", "PickIt.Pick");
        var settings = Button("Splash-It", "SplashIt.OpenSettings");
        var group = Button("Splash-It", "SplashIt.GroupTabs");
        var gantt = Button("Gantt-It", "GanttIt.Open");
        var tab = Tab(
            Panel("External Tools", Button("External Tools", "x")),
            Panel("Pick-It", pick),
            Panel("RST", Button("RST", "RST_Builder")),
            Panel("Splash-It", settings, group),
            Panel("Other", Button("Other", "Other.Run")),
            Panel("Gantt-It", gantt));

        AccessoriesPanel.Gather(tab, "RST").Should().Be(4);

        Titles(tab).Should().Equal("External Tools", "RST", AccessoryPanels.PanelTitle, "Other");
        Accessories(tab).Items.Should().Equal(gantt, settings, group, pick); // the same objects, so ids and commands are unchanged
        tab.Panels.Should().HaveCount(7, "emptied panels are hidden, not removed");
    }

    [Fact]
    public void With_no_sister_app_nothing_changes()
    {
        var tab = Tab(Panel("RST", Button("RST", "RST_Builder")), Panel("Other", Button("Other", "Other.Run")));

        AccessoriesPanel.Gather(tab, "RST").Should().Be(0);

        Titles(tab).Should().Equal("RST", "Other");
    }

    [Fact]
    public void A_panel_keeps_what_is_not_the_sister_apps_and_stays_visible()
    {
        // Another add-in's panel that shares the title: its button stays, and so does the panel.
        var foreign = Button("Gantt-It", "Other.Open");
        var gantt = Button("Gantt-It", "GanttIt.Open");
        var tab = Tab(Panel("RST", Button("RST", "RST_Builder")), Panel("Gantt-It", foreign, gantt));

        AccessoriesPanel.Gather(tab, "RST").Should().Be(1);

        Titles(tab).Should().Equal("RST", AccessoryPanels.PanelTitle, "Gantt-It");
        tab.Panels[2].Source.Items.Should().Equal(foreign);
        Accessories(tab).Items.Should().Equal(gantt);
    }

    [Fact]
    public void A_stack_holding_sister_buttons_moves_whole()
    {
        var row = new RibbonRowPanel();
        row.Items.Add(Button("Splash-It", "SplashIt.OpenSettings"));
        row.Items.Add(Button("Splash-It", "SplashIt.GroupTabs"));
        var tab = Tab(Panel("RST", Button("RST", "RST_Builder")), Panel("Splash-It", row));

        AccessoriesPanel.Gather(tab, "RST").Should().Be(1);

        Accessories(tab).Items.Should().Equal(row);
        Titles(tab).Should().Equal("RST", AccessoryPanels.PanelTitle);
    }

    [Fact]
    public void Running_it_again_changes_nothing()
    {
        var tab = Tab(Panel("RST", Button("RST", "RST_Builder")), Panel("Gantt-It", Button("Gantt-It", "GanttIt.Open")));
        AccessoriesPanel.Gather(tab, "RST").Should().Be(1);

        AccessoriesPanel.Gather(tab, "RST").Should().Be(0);

        Titles(tab).Should().Equal("RST", AccessoryPanels.PanelTitle);
        Accessories(tab).Items.Should().HaveCount(1);
    }
}
