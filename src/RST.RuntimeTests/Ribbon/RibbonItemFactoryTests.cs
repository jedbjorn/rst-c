// rst-c's own (not vendored): spec #17 (Ribbon Stacks), T-A7 and AC4. Real AdWindows items built
// outside Revit from the Nice3point AdWindows reference (copied next to the tests): the Engine's
// mapping of the Core layout (ProfileTabBuilder.BuildPanel → RibbonItemFactory) gives a large
// button per tool slot and a RibbonRowPanel of Standard buttons separated by RibbonRowBreak per
// stack, each button with its own click handler, single-line labels and a 16 px image. Also the
// 16 px pack icon path (IconAssets.ResolveSlotIcon16). The in-Revit rendering itself is proven on
// the VM (AC5).

using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Autodesk.Windows;
using FluentAssertions;
using RST.Core.Profiles;
using RST.Core.Ribbon;
using RST.Engine.Ribbon;
using RST.RuntimeTests.Support;
using Xunit;
using ProfilePanel = RST.Core.Profiles.Panel;

namespace RST.RuntimeTests.Ribbon;

public sealed class RibbonItemFactoryTests
{
    private sealed class RecordingCommand(SlotTarget target, List<SlotTarget> log) : ICommand
    {
        public SlotTarget Target { get; } = target;

        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => log.Add(Target);
    }

    private static Slot Tool(string name, string commandId, string? icon = null) =>
        new() { SlotType = "tool", Name = name, CommandId = commandId, IconFile = icon };

    /// <summary>
    /// A panel of: a large button, a 3-item stack of Revit commands, a 2-item stack mixing a
    /// command and a URL tool, and a 1-item stack (skipped).
    /// </summary>
    private static Profile StackProfile() => new()
    {
        ProfileName = "StackTest",
        Tab = "StackTest",
        Stacks = new()
        {
            ["Build"] = new Stack { Tools = [Tool("Wall", "ID_OBJECTS_WALL", "pack:building_office_blue"), Tool("Door", "ID_OBJECTS_DOOR", "pack:house_green"), Tool("Window", "ID_OBJECTS_WINDOW")] },
            ["Mixed"] = new Stack { Tools = [Tool("Level", "ID_OBJECTS_LEVEL"), Tool("Revit Help Pages", "URL:https://help.autodesk.com")] },
            ["Lonely"] = new Stack { Tools = [Tool("Grid", "ID_OBJECTS_GRID")] },
        },
        Panels =
        [
            new ProfilePanel
            {
                Name = "Model", Color = "#4f8ef7",
                Slots =
                [
                    Tool("Structural Column", "ID_OBJECTS_COLUMN", "pack:move_green"),
                    new Slot { SlotType = "stack", Name = "Build" },
                    new Slot { SlotType = "stack", Name = "Mixed" },
                    new Slot { SlotType = "stack", Name = "Lonely" },
                ],
            },
        ],
    };

    [Fact]
    public void A_panel_layout_maps_to_large_buttons_and_row_panels_of_standard_buttons() => Screenshots.RunSta(() =>
    {
        var clicks = new List<SlotTarget>();
        var profile = StackProfile();
        var layout = RibbonLayoutPlanner.PlanPanel(profile, profile.Panels[0], 0);

        var panel = ProfileTabBuilder.BuildPanel(layout, t => new RecordingCommand(t, clicks));

        panel.Source.Title.Should().Be("Model");
        panel.Source.Items.Should().HaveCount(3, "one large button and two stacks; the 1-item stack is skipped");

        var large = panel.Source.Items[0].Should().BeOfType<RibbonButton>().Subject;
        large.Size.Should().Be(RibbonItemSize.Large);
        large.Orientation.Should().Be(Orientation.Vertical);
        large.Text.Should().Be("Structural\nColumn", "large buttons keep the wrapped label");
        large.Id.Should().Be("RST_ProfileBtn_000_000");
        large.LargeImage.Should().NotBeNull();
        large.Image.Should().BeSameAs(large.LargeImage);
        large.ToolTip.Should().Be("Posts: ID_OBJECTS_COLUMN");

        var build = panel.Source.Items[1].Should().BeOfType<RibbonRowPanel>().Subject;
        build.Id.Should().Be("RST_ProfileStack_000_001");
        build.Items.Should().HaveCount(5, "three buttons separated by two row breaks");
        build.Items[1].Should().BeOfType<RibbonRowBreak>();
        build.Items[3].Should().BeOfType<RibbonRowBreak>();
        var buttons = RibbonItemFactory.ButtonsOf(build);
        buttons.Select(b => b.Text).Should().Equal("Wall", "Door", "Window");
        buttons.Select(b => b.Id).Should().Equal("RST_ProfileBtn_000_001_0", "RST_ProfileBtn_000_001_1", "RST_ProfileBtn_000_001_2");
        foreach (var b in buttons)
        {
            b.Size.Should().Be(RibbonItemSize.Standard);
            b.Orientation.Should().Be(Orientation.Horizontal);
            b.ShowText.Should().BeTrue();
            b.ShowImage.Should().BeTrue();
            var image = b.Image.Should().BeAssignableTo<BitmapSource>().Subject;
            image.PixelWidth.Should().Be(16, "a stacked button draws a 16 px image");
            image.PixelHeight.Should().Be(16);
        }

        var mixed = panel.Source.Items[2].Should().BeOfType<RibbonRowPanel>().Subject;
        mixed.Items.Should().HaveCount(3);
        var mixedButtons = RibbonItemFactory.ButtonsOf(mixed);
        mixedButtons.Select(b => b.Text).Should().Equal("Level", "Revit Help Pages");
        mixedButtons[1].Text.Should().NotContain("\n", "small buttons have single-line labels");
        mixedButtons[1].ToolTip.Should().Be("Open: https://help.autodesk.com");

        // Each button dispatches through its own handler, with its own target.
        foreach (var b in buttons.Concat(mixedButtons).Prepend(large)) b.CommandHandler.Execute(null);
        clicks.Select(c => (c.Kind, c.Payload)).Should().Equal(
            (SlotKind.Command, "ID_OBJECTS_COLUMN"),
            (SlotKind.Command, "ID_OBJECTS_WALL"),
            (SlotKind.Command, "ID_OBJECTS_DOOR"),
            (SlotKind.Command, "ID_OBJECTS_WINDOW"),
            (SlotKind.Command, "ID_OBJECTS_LEVEL"),
            (SlotKind.Url, "https://help.autodesk.com"));
    });

    [Fact]
    public void A_profile_without_stacks_keeps_its_button_ids() => Screenshots.RunSta(() =>
    {
        var profile = new Profile
        {
            ProfileName = "P", Tab = "T",
            Panels = [new ProfilePanel { Name = "A", Slots = [Tool("One", "ID_ONE"), new Slot { SlotType = "tool", Name = "No id" }, Tool("Two", "ID_TWO")] }],
        };
        var panel = ProfileTabBuilder.BuildPanel(RibbonLayoutPlanner.PlanPanel(profile, profile.Panels[0], 2), t => new RecordingCommand(t, []));
        panel.Source.Id.Should().Be("RST_ProfilePanel_002");
        panel.Source.Items.Cast<RibbonButton>().Select(b => b.Id).Should().Equal("RST_ProfileBtn_002_000", "RST_ProfileBtn_002_001");
    });

    [Fact]
    public void An_item_that_throws_while_built_is_skipped_and_the_rest_of_the_panel_still_builds() => Screenshots.RunSta(() =>
    {
        // Review N4: the Build stack's Door handler throws (as AdWindows might for a row panel in
        // Revit); the large button before it and the Mixed stack after it are still built.
        var profile = StackProfile();
        var layout = RibbonLayoutPlanner.PlanPanel(profile, profile.Panels[0], 0);
        var failed = new List<RibbonLayoutItem>();

        var panel = ProfileTabBuilder.BuildPanel(layout,
            t => t.Payload == "ID_OBJECTS_DOOR" ? throw new InvalidOperationException("boom") : new RecordingCommand(t, []),
            failed.Add);

        panel.Source.Items.Select(i => i.Id).Should().Equal("RST_ProfileBtn_000_000", "RST_ProfileStack_000_002");
        failed.Should().ContainSingle().Which.Should().BeOfType<StackItem>().Which.StackName.Should().Be("Build");
    });

    [Fact]
    public void The_factory_refuses_a_stack_of_one_or_four()
    {
        var one = new StackItem(0, "S", "id", [new PlannedButton(Tool("A", "A"), SlotTarget.Parse("A", "A"), "A", "a", PlannedButtonSize.Small)]);
        var act = () => RibbonItemFactory.Stack(one, _ => null, _ => null, b => new RecordingCommand(b.Target, []));
        act.Should().Throw<ArgumentException>();
    }

    // ── AC4: the 16 px icon path ─────────────────────────────────────────────

    [Theory]
    [InlineData("pack:move_green")]
    [InlineData("pack:move")]
    [InlineData("pack:link_external_purple")]
    public void ResolveSlotIcon16_returns_a_16_by_16_bitmap(string value)
    {
        var icon = IconAssets.ResolveSlotIcon16(value).Should().BeAssignableTo<BitmapSource>().Subject;
        icon.PixelWidth.Should().Be(16);
        icon.PixelHeight.Should().Be(16);
        IconAssets.ResolveSlotIcon(value).Should().BeAssignableTo<BitmapSource>().Which.PixelWidth.Should().Be(32,
            "the 32 px cache is separate from the 16 px one");
    }

    [Fact]
    public void ResolveSlotIcon16_is_cached_and_rejects_what_the_32_px_path_rejects()
    {
        IconAssets.ResolveSlotIcon16("pack:MOVE_GREEN").Should().BeSameAs(IconAssets.ResolveSlotIcon16("pack:move_green"));
        IconAssets.ResolveSlotIcon16("pack:../escape").Should().BeNull();
        IconAssets.ResolveSlotIcon16(null).Should().BeNull();
        IconAssets.ResolveSlotIcon16("pack:move_fuchsia").Should().BeNull("a missing file falls back to Default16");
        IconAssets.Default16.Should().BeAssignableTo<BitmapSource>().Which.PixelWidth.Should().Be(16);
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, System.Windows.Media.PixelFormats.Pbgra32, null, 0);
        var pixels = new byte[converted.PixelWidth * 4 * converted.PixelHeight];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        return pixels;
    }

    [Fact]
    public void Default16_has_a_visible_glyph_and_the_previews_draw_the_same_image()
    {
        // The VM found default_16.png a blank translucent tile while the previews drew "≡" (spec #17).
        var ribbon = IconAssets.Default16.Should().BeAssignableTo<BitmapSource>().Subject;
        Screenshots.DistinctPixels(ribbon).Should().BeGreaterThan(1, "the default carries a glyph, not a blank tile");
        var preview = RST.UI.Controls.PackIcons.Default16.Should().NotBeNull().And.BeAssignableTo<BitmapSource>().Subject;
        (preview.PixelWidth, preview.PixelHeight).Should().Be((16, 16));
        Pixels(preview).Should().Equal(Pixels(ribbon), "the previews draw the ribbon's own default_16.png");
    }
}
