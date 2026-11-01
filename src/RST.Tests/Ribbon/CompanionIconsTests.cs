using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using RST.Core.Profiles;
using RST.Core.Ribbon;
using Xunit;

namespace RST.Tests.Ribbon;

public sealed class CompanionIconsTests
{
    public static IEnumerable<object[]> Commands()
    {
        foreach (var (button, icon) in new[] { ("GanttIt.Open", "gantt"), ("SplashIt.OpenSettings", "splash"), ("SplashIt.GroupTabs", "group") })
            foreach (var prefix in new[] { "CustomCtrl_%", "CustomCtrl_%CustomCtrl_%" })
                yield return [prefix + "Add-Ins%Companion%" + button, icon];
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public void Exact_terminal_buttons_resolve_both_prefix_shapes_sizes_and_themes(string command, string icon)
    {
        foreach (var size in new[] { 16, 32 })
            foreach (var dark in new[] { false, true })
                CompanionIcons.Resolve(new Slot { CommandId = command }, size, dark)
                    .Should().Be($"icons/companions/{icon}_{size}{(dark ? "_dark" : "")}.png");
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("pack:move_green", false)]
    [InlineData("pack:missing", false)]
    [InlineData("malformed", false)]
    [InlineData(" ", false)]
    public void Stored_value_alone_decides_whether_the_default_applies(string? stored, bool automatic)
    {
        var slot = new Slot { CommandId = "CustomCtrl_%Add-Ins%Gantt-It%GanttIt.Open", IconFile = stored };
        (CompanionIcons.Resolve(slot) is not null).Should().Be(automatic);
        slot.IconFile.Should().Be(stored);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("GanttIt.Open")]
    [InlineData("ID_OBJECTS_WALL")]
    [InlineData("CustomCtrl_%Add-Ins%Other%Other.Open")]
    [InlineData("CustomCtrl_%Add-Ins%GanttIt.Open%Other.Open")]
    [InlineData("CustomCtrl_%GanttIt.Open%Panel%Other.Open")]
    [InlineData("CustomCtrl_%Add-Ins%Panel%NotGanttIt.Open")]
    [InlineData("CustomCtrl_%Add-Ins%Panel%GanttIt.Open.More")]
    [InlineData("CustomCtrl_%Add-Ins%Panel%GanttIt.Open%Other")]
    [InlineData("CustomCtrl_%Add-Ins%Panel%SplashIt.OpenSettingsExtra")]
    [InlineData("CustomCtrl_%Add-Ins%Panel%OtherSplashIt.GroupTabs")]
    [InlineData("CustomCtrl_%Add-Ins%Panel%ganttit.open")]
    [InlineData("OtherCustomCtrl_%Add-Ins%Panel%GanttIt.Open")]
    [InlineData("CustomCtrl_%Panel%GanttIt.Open")]
    [InlineData("CustomCtrl_%%Panel%GanttIt.Open")]
    [InlineData("CustomCtrl_%Tab%%GanttIt.Open")]
    [InlineData("CustomCtrl_%CustomCtrl_%CustomCtrl_%Tab%Panel%GanttIt.Open")]
    [InlineData("URL:https://example.test/CustomCtrl_%Tab%Panel%GanttIt.Open")]
    [InlineData("URL:mailto:GanttIt.Open@example.test")]
    [InlineData("URL:CustomCtrl_%Tab%Panel%SplashIt.GroupTabs")]
    public void Near_misses_unrelated_commands_and_custom_links_have_no_companion_icon(string? command)
    {
        CompanionIcons.Resolve(new Slot { Name = "Gantt-It", CommandId = command }).Should().BeNull();
    }

    [Theory]
    [InlineData("stack")]
    [InlineData("other")]
    public void Only_tool_slots_qualify(string type)
    {
        CompanionIcons.Resolve(new Slot { SlotType = type, CommandId = "CustomCtrl_%Tab%Panel%GanttIt.Open" })
            .Should().BeNull();
    }

    [Fact]
    public void Rendering_loose_and_stacked_tools_preserves_the_profile_round_trip()
    {
        var profile = new Profile
        {
            ProfileName = "Companions", Tab = "Tools",
            Panels = [new Panel { Name = "Companions", Slots =
            [
                new Slot { Name = "Gantt", CommandId = "CustomCtrl_%Tab%Panel%GanttIt.Open" },
                new Slot { SlotType = "stack", Name = "Splash" },
            ] }],
            Stacks = new() { ["Splash"] = new Stack { Tools =
            [
                new Slot { Name = "Settings", CommandId = "CustomCtrl_%CustomCtrl_%Tab%Panel%SplashIt.OpenSettings" },
                new Slot { Name = "Group", CommandId = "CustomCtrl_%Tab%Panel%SplashIt.GroupTabs" },
            ] } },
        };
        var before = ProfileSerializer.WriteString(profile);
        var loaded = ProfileSerializer.ReadString(before);
        var layout = RibbonLayoutPlanner.PlanPanel(loaded, loaded.Panels[0], 0);
        var loose = layout.Items.OfType<LargeButtonItem>().Single().Button;
        var stack = layout.Items.OfType<StackItem>().Single();
        CompanionIcons.Resolve(loose.Source).Should().EndWith("gantt_32.png");
        stack.Buttons.Select(b => CompanionIcons.Resolve(b.Source, 16))
            .Should().Equal("icons/companions/splash_16.png", "icons/companions/group_16.png");
        ProfileSerializer.WriteString(loaded).Should().Be(before).And.NotContain("iconFile");
    }

    [Fact]
    public void Every_mapped_asset_is_present_in_engine_assets()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "RST-C.sln"))) root = root.Parent;
        root.Should().NotBeNull();
        foreach (var command in Commands())
            foreach (var size in new[] { 16, 32 })
                foreach (var dark in new[] { false, true })
                {
                    var path = CompanionIcons.Resolve(new Slot { CommandId = (string)command[0] }, size, dark)!;
                    File.Exists(Path.Combine(root!.FullName, "src", "RST.Engine", "Assets", path))
                        .Should().BeTrue($"{path} must ship with the Engine");
                }
    }
}
