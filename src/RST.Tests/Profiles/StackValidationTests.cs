// rst-c's own (not vendored): spec #17 (Ribbon Stacks), AC2 / T-A2 and T-A3. Validation of stack
// items (type and commandId), the empty stack name and the orphan-stack warning; the save drops
// orphans; and a profile with 2- and 3-item stacks round-trips byte-stable with sourceTab,
// sourcePanel and iconFile kept on every stack tool.

using System.IO;
using FluentAssertions;
using RST.Core.Builder;
using RST.Core.Loader;
using RST.Core.Profiles;
using Xunit;

namespace RST.Tests.Profiles;

public class StackValidationTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Profiles", name);

    private static Slot Tool(string name) => new() { SlotType = "tool", Name = name, CommandId = "ID_" + name.ToUpperInvariant() };

    private static Profile WithStack(string name, params Slot[] tools) => new()
    {
        SchemaVersion = 1,
        ProfileName = "X",
        Tab = "X",
        Stacks = { [name] = new Stack { Tools = [.. tools] } },
        Panels = { new Panel { Name = "P", Color = "#4f8ef7", Slots = { new Slot { SlotType = "stack", Name = name } } } },
    };

    [Fact]
    public void A_valid_stack_has_no_errors_or_warnings()
    {
        var p = WithStack("Draw", Tool("Line"), Tool("Arc"), new Slot { SlotType = "tool", Name = "Help", CommandId = "URL:https://x.test" });
        ProfileSerializer.Validate(p).Should().BeEmpty();
        ProfileSerializer.Warnings(p).Should().BeEmpty();
    }

    [Fact]
    public void A_stack_tool_needs_type_tool()
    {
        var p = WithStack("Draw", Tool("Line"), new Slot { SlotType = "stack", Name = "Nested", CommandId = "ID_X" });
        ProfileSerializer.Validate(p).Should().ContainSingle()
            .Which.Should().Be("profile.stacks[\"Draw\"].tools[1].type: must be 'tool' (got 'stack')");
    }

    [Fact]
    public void A_stack_tool_needs_a_commandId()
    {
        var p = WithStack("Draw", new Slot { SlotType = "tool", Name = "Empty" }, Tool("Arc"));
        ProfileSerializer.Validate(p).Should().ContainSingle()
            .Which.Should().Be("profile.stacks[\"Draw\"].tools[0].commandId: required for a stack tool");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_stack_name_is_an_error(string name)
    {
        var p = WithStack(name, Tool("Line"), Tool("Arc"));
        ProfileSerializer.Validate(p).Should().ContainSingle()
            .Which.Should().Be($"profile.stacks[\"{name}\"]: stack name required, non-empty");
    }

    [Fact]
    public void Read_refuses_a_profile_whose_stack_tool_has_no_commandId()
    {
        var json = ProfileSerializer.WriteString(WithStack("Draw", Tool("Line"), Tool("Arc"))).Replace("\"commandId\": \"ID_ARC\"", "\"commandId\": \"\"");
        var act = () => ProfileSerializer.ReadString(json);
        act.Should().Throw<ProfileLoadException>().WithMessage("*tools[1].commandId*");
    }

    [Fact]
    public void An_orphan_stack_is_a_warning_not_an_error()
    {
        var p = WithStack("Placed", Tool("Line"), Tool("Arc"));
        p.Stacks["Loose"] = new Stack { Tools = [Tool("Wall"), Tool("Door")] };

        ProfileSerializer.Validate(p).Should().BeEmpty();
        ProfileSerializer.Warnings(p).Should().Equal("profile.stacks[\"Loose\"]: not referenced by any panel slot");
        ProfileSerializer.OrphanStacks(p).Should().Equal("Loose");

        // Read accepts it (a warning never hides a profile from the pickers).
        ProfileSerializer.ReadString(ProfileSerializer.WriteString(p)).Stacks.Keys.Should().Equal("Placed", "Loose");
    }

    [Fact]
    public void RemoveOrphanStacks_drops_only_the_unreferenced_stacks()
    {
        var p = WithStack("Placed", Tool("Line"), Tool("Arc"));
        p.Stacks["Loose"] = new Stack { Tools = [Tool("Wall"), Tool("Door")] };
        p.Stacks["Also loose"] = new Stack { Tools = [Tool("Wall")] };

        ProfileEditing.RemoveOrphanStacks(p).Should().Equal("Loose", "Also loose");
        p.Stacks.Keys.Should().Equal("Placed");
        ProfileEditing.RemoveOrphanStacks(p).Should().BeEmpty();
    }

    [Fact]
    public void The_save_removes_orphan_stacks_logs_them_and_writes_a_valid_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rst-stack-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var p = WithStack("Placed", Tool("Line"), Tool("Arc"));
            // An orphan that would fail validation (one tool) does not block the save: it is dropped first.
            p.Stacks["Loose"] = new Stack { Tools = [Tool("Wall")] };
            var log = new RecordingLog();

            var outcome = new ProfileSaveService(dir, log).Save(p);

            outcome.Ok.Should().BeTrue(outcome.Error);
            outcome.Saved!.Stacks.Keys.Should().Equal("Placed");
            p.Stacks.Keys.Should().Equal(["Placed", "Loose"], "the service saves a copy; the caller's profile is untouched");
            log.Warnings.Should().ContainSingle(w => w.Contains("stack {Stack} is not on any panel"));
            using var fs = File.OpenRead(Path.Combine(dir, outcome.FileName!));
            ProfileSerializer.Read(fs).Stacks.Keys.Should().Equal("Placed");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_profile_with_two_and_three_item_stacks_round_trips_byte_stable_keeping_the_stack_tool_fields()
    {
        using var stream = File.OpenRead(FixturePath("stacks.json"));
        var first = ProfileSerializer.Read(stream);

        first.Stacks.Keys.Should().Equal("Build", "Links");
        first.Stacks["Build"].Tools.Should().HaveCount(3);
        first.Stacks["Links"].Tools.Should().HaveCount(2);
        foreach (var tool in first.Stacks.Values.SelectMany(s => s.Tools))
        {
            tool.SourceTab.Should().NotBeNullOrEmpty();
            tool.SourcePanel.Should().NotBeNullOrEmpty();
            tool.IconFile.Should().StartWith("pack:");
        }

        var written1 = ProfileSerializer.WriteString(first);
        var second = ProfileSerializer.ReadString(written1);
        var written2 = ProfileSerializer.WriteString(second);

        written2.Should().Be(written1, "a second write is byte-identical");
        var wall = second.Stacks["Build"].Tools[0];
        (wall.Name, wall.CommandId, wall.SourceTab, wall.SourcePanel, wall.IconFile)
            .Should().Be(("Wall", "ID_OBJECTS_WALL", "Architecture", "Build", "pack:building_office_blue"));
        var help = second.Stacks["Links"].Tools[1];
        (help.CommandId, help.SourceTab, help.SourcePanel, help.IconFile)
            .Should().Be(("URL:https://help.autodesk.com", "Custom", "Custom", "pack:question_light_grey"));

        // The Builder's editing copy keeps them too.
        var editing = ProfileEditing.ForEditing(second);
        ProfileSerializer.WriteString(editing).Should().Contain("\"sourcePanel\": \"Build\"");
        editing.Stacks["Build"].Tools.Select(t => t.IconFile).Should().Equal("pack:building_office_blue", "pack:house_green", "pack:box_open_purple");
    }

    [Fact]
    public void RSTify_never_offers_a_stack_tools_source_tab()
    {
        using var stream = File.OpenRead(FixturePath("stacks.json"));
        var p = ProfileSerializer.Read(stream);
        RstifyRules.SourceTabs(p).Should().Contain("Architecture").And.NotContain("Custom");
        RstifyRules.TabsToOffer(p, ["Architecture", "Structure", "Insert", "Studio Stacks"]).Tabs.Should().Equal("Insert");
    }

    private sealed class RecordingLog : ILoaderLog
    {
        public List<string> Warnings { get; } = [];

        public void Debug(Exception? error, string template, params object?[] args) { }

        public void Information(string template, params object?[] args) { }

        public void Warning(Exception? error, string template, params object?[] args) => Warnings.Add(template);

        public void Error(Exception? error, string template, params object?[] args) { }
    }
}
