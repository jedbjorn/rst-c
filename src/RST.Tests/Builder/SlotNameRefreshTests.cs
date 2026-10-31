using FluentAssertions;
using RST.Core.Builder;
using RST.Core.Profiles;
using RST.Core.Scanning;
using Xunit;

namespace RST.Tests.Builder;

public class SlotNameRefreshTests
{
    private static readonly ScannedCommand Wall = new(
        "ID_OBJECTS_WALL", "Wall: Architectural", CommandOrigin.Native, null, null, "Architecture", "Build",
        [new("Architecture", "Build", "Wall: Architectural", null), new("Structure", "Structure", "Wall", null)],
        "ArchitecturalWall");

    private static Slot Tool(string name = "ArchitecturalWall", string? tab = "Architecture", string? panel = "Build") => new()
    {
        CommandId = Wall.Id, Name = name, SourceTab = tab, SourcePanel = panel,
    };

    private static Profile Profile(params Slot[] tools) => new() { Panels = [new Panel { Slots = [.. tools] }] };

    [Fact]
    public void Loose_and_stack_labels_refresh_by_placement_without_changing_their_sources()
    {
        var loose = Tool();
        var stacked = Tool(tab: "Structure", panel: "Structure");
        var typed = Tool("My Wall");
        var profile = Profile(loose, new Slot { SlotType = "stack", Name = "Walls" });
        profile.Stacks["Walls"] = new Stack { Tools = [stacked, typed] };

        SlotNameRefresh.Apply(profile, [Wall]).Should().Be(2);
        loose.Name.Should().Be("Wall: Architectural");
        stacked.Name.Should().Be("Wall");
        typed.Name.Should().Be("My Wall");
        loose.SourceTab.Should().Be("Architecture");
        loose.SourcePanel.Should().Be("Build");
        stacked.SourceTab.Should().Be("Structure");
        stacked.SourcePanel.Should().Be("Structure");
        SlotNameRefresh.Apply(profile, [Wall]).Should().Be(0, "the refresh is idempotent");
    }

    [Theory]
    [InlineData("Structure", "Other", "Wall: Architectural")]
    [InlineData("Missing", "Structure", "Wall: Architectural")]
    [InlineData(null, null, "Wall: Architectural")]
    [InlineData("Structure", "Structure", "Wall")]
    public void Match_requires_both_tab_and_panel_else_the_primary_label_is_used(string? tab, string? panel, string expected)
    {
        var tool = Tool(tab: tab, panel: panel);
        SlotNameRefresh.Apply(Profile(tool), [Wall]).Should().Be(1);
        tool.Name.Should().Be(expected);
    }

    [Theory]
    [InlineData("My Wall")]
    [InlineData("architecturalwall")]
    [InlineData("ArchitecturalWall ")]
    [InlineData("Wall: Architectural")]
    public void Other_names_are_kept_exactly(string name)
    {
        var tool = Tool(name);
        SlotNameRefresh.Apply(Profile(tool), [Wall]).Should().Be(0);
        tool.Name.Should().Be(name);
    }

    [Fact]
    public void Unknown_ids_addins_url_email_and_non_tool_slots_are_untouched()
    {
        var slots = new[] { Tool(), Tool(), Tool(), Tool(), Tool() };
        slots[0].CommandId = "Unknown";
        slots[1].CommandId = "CustomCtrl_Addin";
        slots[2].CommandId = "URL:https://example.com";
        slots[3].CommandId = "URL:mailto:a@example.com";
        slots[4].SlotType = "stack";
        ScannedCommand[] catalog = [Wall,
            Wall with { Id = slots[1].CommandId!, Origin = CommandOrigin.ThirdParty },
            Wall with { Id = slots[2].CommandId! }, Wall with { Id = slots[3].CommandId! }];
        SlotNameRefresh.Apply(Profile(slots), catalog).Should().Be(0);
        slots.Should().OnlyContain(s => s.Name == "ArchitecturalWall");
    }

    [Fact]
    public void Empty_catalog_or_missing_enum_name_leaves_everything_unchanged()
    {
        var profile = Profile(Tool());
        var before = ProfileEditing.Snapshot(profile);
        SlotNameRefresh.Apply(profile, []).Should().Be(0);
        SlotNameRefresh.Apply(profile, null).Should().Be(0);
        SlotNameRefresh.Apply(profile, [Wall with { EnumName = null }]).Should().Be(0);
        ProfileEditing.Snapshot(profile).Should().Be(before);
    }
}
