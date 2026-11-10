using FluentAssertions;
using RST.Core.Ribbon;
using Xunit;

namespace RST.Tests.Ribbon;

public sealed class AccessoryPanelsTests
{
    [Theory]
    [InlineData("Gantt-It", "CustomCtrl_%CustomCtrl_%Add-Ins%Gantt-It%GanttIt.Open")]
    [InlineData("Splash-It", "CustomCtrl_%CustomCtrl_%Add-Ins%Splash-It%SplashIt.OpenSettings")]
    [InlineData("Splash-It", "CustomCtrl_%CustomCtrl_%Add-Ins%Splash-It%SplashIt.GroupTabs")]
    [InlineData("Pick-It", "CustomCtrl_%CustomCtrl_%Add-Ins%Pick-It%PickIt.Pick")]
    [InlineData("Pick-It", "PickIt.Pick")]
    public void A_sister_apps_button_on_its_own_panel_is_gathered(string panel, string itemId) =>
        AccessoryPanels.IsSisterButton(panel, itemId).Should().BeTrue();

    [Theory]
    [InlineData("Gantt-It", "CustomCtrl_%CustomCtrl_%Add-Ins%Gantt-It%Other.Open")] // someone else's panel of the same title
    [InlineData("Gantt-It", "CustomCtrl_%CustomCtrl_%Add-Ins%Gantt-It%SplashIt.GroupTabs")] // the wrong app's button
    [InlineData("RST", "CustomCtrl_%CustomCtrl_%Add-Ins%RST%RST_Builder")]
    [InlineData("Tools", "CustomCtrl_%CustomCtrl_%Add-Ins%Tools%GanttIt.Open")] // a sister button anywhere else stays put
    [InlineData("gantt-it", "CustomCtrl_%CustomCtrl_%Add-Ins%gantt-it%GanttIt.Open")]
    [InlineData("Gantt-It", "CustomCtrl_%CustomCtrl_%Add-Ins%Gantt-It%GanttIt.")]
    [InlineData("Gantt-It", "")]
    [InlineData("Gantt-It", null)]
    [InlineData(null, "GanttIt.Open")]
    public void Anything_else_stays_where_it_is(string? panel, string? itemId) =>
        AccessoryPanels.IsSisterButton(panel, itemId).Should().BeFalse();

    [Fact]
    public void The_panels_are_gathered_in_registry_order() =>
        AccessoryPanels.SisterPanelTitles.Should().Equal("Gantt-It", "Splash-It", "Pick-It");
}
