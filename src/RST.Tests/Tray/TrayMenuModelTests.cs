// rst-c's own: the RST tray icon's menu entries and the command mapping (spec #20 §1; SC-043). The native menu
// itself is exercised on Windows by RST.RuntimeTests (TrayMenuTests).

using System.Linq;
using FluentAssertions;
using RST.Core.Tray;
using Xunit;

namespace RST.Tests.Tray;

public sealed class TrayMenuModelTests
{
    private static readonly TrayMenuTexts Texts = new("Open RST", "Check for updates", "Settings", "Quit");

    [Fact]
    public void Entries_are_Open_Check_Settings_Quit_with_Open_the_default()
    {
        var entries = TrayMenuModel.Entries(Texts, canCheckForUpdates: true);
        entries.Select(e => e.Command).Should().Equal(
            TrayMenuCommand.Open, TrayMenuCommand.CheckForUpdates, TrayMenuCommand.None,
            TrayMenuCommand.Settings, TrayMenuCommand.None, TrayMenuCommand.Quit);
        entries.Where(e => !e.IsSeparator).Select(e => e.Text).Should().Equal("Open RST", "Check for updates", "Settings", "Quit");
        entries.Where(e => e.IsDefault).Select(e => e.Command).Should().Equal(TrayMenuCommand.Open);
        entries.Where(e => !e.IsSeparator).Should().OnlyContain(e => e.Enabled);
        ((int)TrayMenuCommand.None).Should().Be(0, "TrackPopupMenuEx returns 0 when the menu is dismissed");
    }

    [Fact]
    public void Check_for_updates_follows_CanCheckForUpdates()
    {
        var entries = TrayMenuModel.Entries(Texts, canCheckForUpdates: false);
        entries.Single(e => e.Command == TrayMenuCommand.CheckForUpdates).Enabled.Should().BeFalse();
        entries.Where(e => !e.IsSeparator && e.Command != TrayMenuCommand.CheckForUpdates).Should().OnlyContain(e => e.Enabled);
    }

    [Fact]
    public void Chosen_maps_the_returned_id_and_ignores_dismissal_unknown_ids_and_disabled_items()
    {
        var entries = TrayMenuModel.Entries(Texts, canCheckForUpdates: false);
        TrayMenuModel.Chosen(entries, 1).Should().Be(TrayMenuCommand.Open);
        TrayMenuModel.Chosen(entries, 3).Should().Be(TrayMenuCommand.Settings);
        TrayMenuModel.Chosen(entries, 4).Should().Be(TrayMenuCommand.Quit);
        TrayMenuModel.Chosen(entries, 0).Should().Be(TrayMenuCommand.None, "0: the user dismissed the menu");
        TrayMenuModel.Chosen(entries, 2).Should().Be(TrayMenuCommand.None, "Check for updates is disabled here");
        TrayMenuModel.Chosen(entries, 99).Should().Be(TrayMenuCommand.None);
    }
}
