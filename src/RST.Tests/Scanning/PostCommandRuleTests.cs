// SC-051 (decision #63): the catalogue lists only ids Revit lets an add-in post, PostableCommand
// members and external commands (RST.Core.Scanning.PostCommandRule). The ids below are the rc.3
// dumps' (shared/evidence/v31-rc3/c1/20xx/catalog.tsv).

using FluentAssertions;
using RST.Core.Scanning;
using Xunit;

namespace RST.Tests.Scanning;

public sealed class PostCommandRuleTests
{
    private static readonly HashSet<string> Seed = new(StringComparer.Ordinal)
    {
        "ID_SETTINGS_LINE_STYLES", "ID_KEYBOARD_SHORTCUT_DIALOG", "ID_STRUCTURAL_SETTINGS", "ID_OBJECTS_WALL",
    };

    [Theory]
    [InlineData("CustomCtrl_%CustomCtrl_%Add-Ins%RST%RST_Builder", true)]
    [InlineData("CustomCtrl_%PanelParts%Split", true)]
    [InlineData("CustomCtrl_%CustomCtrl_%CustomCtrl_%DiRootsOne% Point Cloud%Select Points%Select Points", true)]
    // A manifest-registered command (<AddIn Type="Command">, Add-Ins > External Tools) may carry
    // the manifest's ClientId GUID as its id, with or without braces, in either case.
    [InlineData("3f7c4a9e-2b1d-4e8f-9a6c-5d0e1f2a3b4c", true)]
    [InlineData("{3F7C4A9E-2B1D-4E8F-9A6C-5D0E1F2A3B4C}", true)]
    [InlineData("3f7c4a9e-2b1d-4e8f-9a6c-5d0e1f2a3b4", false)]
    [InlineData("3f7c4a9e2b1d4e8f9a6c5d0e1f2a3b4c", false)]
    [InlineData("customctrl_%Acme%Go", false)]
    [InlineData("ID_SETTINGS_TEXT", false)]
    [InlineData("Kinship.Open", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void External_command_ids_are_the_ones_Revit_gives_add_in_buttons(string? id, bool expected) =>
        PostCommandRule.IsExternalCommandId(id).Should().Be(expected);

    [Theory]
    // PostableCommand members: Line Styles and Keyboard Shortcuts post in rc.3, and stay.
    [InlineData("ID_SETTINGS_LINE_STYLES", true)]
    [InlineData("ID_KEYBOARD_SHORTCUT_DIALOG", true)]
    [InlineData("ID_STRUCTURAL_SETTINGS", true)]
    // External commands, whether or not the seed holds them.
    [InlineData("CustomCtrl_%CustomCtrl_%Add-Ins%eTransmit%eTransmit", true)]
    [InlineData("{3F7C4A9E-2B1D-4E8F-9A6C-5D0E1F2A3B4C}", true)]
    // Revit's own tools that PostableCommand does not hold: PostCommand refuses them.
    [InlineData("ID_SETTINGS_TEXT", false)]
    [InlineData("ID_INPLACE_WALL", false)]
    [InlineData("ID_BUTTON_SELECT", false)]
    [InlineData("HID_APPLICATION_ELEMENTS_RibbonListButton", false)]
    [InlineData("RibbonButton_ManageConnectionToARevitServerAccelerator", false)]
    // A button an add-in drew straight into AdWindows: not a Revit command at all.
    [InlineData("Kinship.Open", false)]
    // Case matters, as it does for Revit's own lookup and the catalogue's merge.
    [InlineData("id_settings_line_styles", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Revit_lets_add_ins_post_PostableCommand_members_and_external_commands_only(string? id, bool expected) =>
        PostCommandRule.LetsAddinsPost(id, Seed.Contains).Should().Be(expected);

    [Fact]
    public void A_seed_check_that_throws_counts_as_not_postable()
    {
        PostCommandRule.LetsAddinsPost("ID_OBJECTS_WALL", _ => throw new InvalidOperationException("broke")).Should().BeFalse();
        PostCommandRule.LetsAddinsPost("CustomCtrl_%Acme%Go", _ => throw new InvalidOperationException("broke")).Should().BeTrue();
    }

    [Fact]
    public void The_seed_check_is_required() =>
        FluentActions.Invoking(() => PostCommandRule.LetsAddinsPost("ID_OBJECTS_WALL", null!))
            .Should().Throw<ArgumentNullException>();
}
