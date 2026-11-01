using System;
using RST.Core.Tray;
using Xunit;

namespace RST.Tests.Tray;

public sealed class TrayNavigationTests
{
    [Theory]
    [InlineData(true, false, TrayPage.Updates, true, true)]
    [InlineData(false, false, TrayPage.Updates, true, false)]
    [InlineData(true, true, TrayPage.Updates, true, false)]
    [InlineData(true, false, TrayPage.AddIns, true, false)]
    [InlineData(true, false, TrayPage.Apps, true, false)]
    [InlineData(true, false, TrayPage.Health, true, false)]
    [InlineData(true, false, TrayPage.Settings, true, false)]
    [InlineData(true, false, TrayPage.Updates, false, false)]
    public void Self_update_presentation_requires_a_visible_Updates_note(
        bool visible, bool minimized, TrayPage page, bool hasNote, bool expected) =>
        Assert.Equal(expected, TrayNavigation.CanPresentSelfUpdateResult(visible, minimized, page, hasNote));

    [Fact]
    public void Pages_are_in_decision_76_tab_order() =>
        Assert.Equal(new[] { TrayPage.AddIns, TrayPage.Updates, TrayPage.Apps, TrayPage.Health, TrayPage.Settings },
            Enum.GetValues<TrayPage>());

    [Theory]
    [InlineData(TrayPage.AddIns)]
    [InlineData(TrayPage.Updates)]
    [InlineData(TrayPage.Apps)]
    [InlineData(TrayPage.Health)]
    [InlineData(TrayPage.Settings)]
    public void An_ordinary_fresh_open_ignores_the_last_page(TrayPage lastPage)
    {
        // Non-hidden start, icon, menu Open and single-instance show signal all request no target.
        Assert.Equal(TrayPage.AddIns, TrayNavigation.PageOnOpen(null, windowShown: false, lastPage));
    }

    [Theory]
    [InlineData(TrayPage.AddIns)]
    [InlineData(TrayPage.Updates)]
    [InlineData(TrayPage.Apps)]
    [InlineData(TrayPage.Health)]
    [InlineData(TrayPage.Settings)]
    public void An_ordinary_activation_keeps_the_shown_page(TrayPage currentPage) =>
        Assert.Equal(currentPage, TrayNavigation.PageOnOpen(null, windowShown: true, currentPage));

    [Theory]
    [InlineData(TrayPage.Updates, false)]
    [InlineData(TrayPage.Updates, true)]
    [InlineData(TrayPage.Settings, false)]
    [InlineData(TrayPage.Settings, true)]
    public void Targeted_opens_select_the_target_whether_or_not_the_window_is_shown(TrayPage target, bool shown)
    {
        // Menu Check for updates and notification clicks target Updates; menu Settings targets Settings.
        foreach (var current in Enum.GetValues<TrayPage>())
            Assert.Equal(target, TrayNavigation.PageOnOpen(target, shown, current));
    }

    [Fact]
    public void A_theme_rebuild_preserves_its_explicit_page()
    {
        foreach (var current in Enum.GetValues<TrayPage>())
            Assert.Equal(current, TrayNavigation.PageOnOpen(current, windowShown: false, current));
    }
}
