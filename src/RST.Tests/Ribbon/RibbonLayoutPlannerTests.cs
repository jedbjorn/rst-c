// rst-c's own (not vendored): spec #17 (Ribbon Stacks), AC1 / T-A1. The layout planner gives the
// expected sequence of large buttons, stacks and skips for 2- and 3-item stacks, 1- and 4-item
// stacks, an invalid item, URL items, an unknown reference and a mixed order; every skip carries a
// reason. Also the ids, the single-line labels and the panel width estimate.

using FluentAssertions;
using RST.Core.Profiles;
using RST.Core.Ribbon;
using Xunit;

namespace RST.Tests.Ribbon;

public class RibbonLayoutPlannerTests
{
    private static Slot Tool(string name, string? commandId = null, string type = "tool") =>
        new() { SlotType = type, Name = name, CommandId = commandId ?? "ID_" + name.ToUpperInvariant().Replace(' ', '_') };

    private static Slot StackRef(string name) => new() { SlotType = "stack", Name = name };

    private static Profile With(Dictionary<string, Stack> stacks, params Slot[] slots) => new()
    {
        ProfileName = "P",
        Tab = "T",
        Stacks = stacks,
        Panels = [new Panel { Name = "Main", Slots = [.. slots] }],
    };

    private static Stack Of(params Slot[] tools) => new() { Tools = [.. tools] };

    /// <summary>The layout as short strings: "L:Name", "S:Stack[a,b]", "X:reason".</summary>
    private static List<string> Shape(PanelLayout layout) => layout.Items.Select(i => i switch
    {
        LargeButtonItem l => "L:" + l.Button.Source.Name,
        StackItem s => "S:" + s.StackName + "[" + string.Join(",", s.Buttons.Select(b => b.Source.Name)) + "]",
        SkippedItem x => "X:" + x.Reason,
        _ => "?",
    }).ToList();

    private static PanelLayout PlanOne(Profile p) => RibbonLayoutPlanner.Plan(p).Should().ContainSingle().Subject;

    [Fact]
    public void A_two_and_a_three_item_stack_render_as_stacks_of_small_buttons()
    {
        var p = With(new()
        {
            ["Two"] = Of(Tool("Line"), Tool("Arc")),
            ["Three"] = Of(Tool("Wall"), Tool("Door"), Tool("Window")),
        }, StackRef("Two"), StackRef("Three"));

        var layout = PlanOne(p);

        Shape(layout).Should().Equal("S:Two[Line,Arc]", "S:Three[Wall,Door,Window]");
        layout.Skipped.Should().BeEmpty();
        layout.ButtonCount.Should().Be(5);
        layout.StackCount.Should().Be(2);
        var three = (StackItem)layout.Items[1];
        three.Buttons.Should().OnlyContain(b => b.Size == PlannedButtonSize.Small);
        three.Buttons.Select(b => b.Target.Payload).Should().Equal("ID_WALL", "ID_DOOR", "ID_WINDOW");
    }

    [Fact]
    public void A_one_item_and_a_four_item_stack_are_skipped_with_a_reason_never_drawn_as_1_or_4()
    {
        var p = With(new()
        {
            ["One"] = Of(Tool("Line")),
            ["Four"] = Of(Tool("A"), Tool("B"), Tool("C"), Tool("D")),
            ["Empty"] = Of(),
        }, StackRef("One"), StackRef("Four"), StackRef("Empty"));

        var layout = PlanOne(p);

        layout.Rendered.Should().BeEmpty();
        Shape(layout).Should().Equal(
            "X:stack 'One' has 1 valid item; a stack needs 2 or 3",
            "X:stack 'Four' has 4 valid items; a stack needs 2 or 3",
            "X:stack 'Empty' has 0 valid items; a stack needs 2 or 3");
        layout.Skipped.Select(s => s.StackName).Should().Equal("One", "Four", "Empty");
        layout.Skipped.Should().OnlyContain(s => s.ToolIndex == null && !string.IsNullOrWhiteSpace(s.Reason));
    }

    [Fact]
    public void An_invalid_item_is_skipped_and_the_stack_drawn_only_while_2_or_3_valid_items_remain()
    {
        var p = With(new()
        {
            // 3 items, one without a command id: drawn with the 2 valid ones.
            ["Keeps"] = Of(Tool("Wall"), new Slot { Name = "Broken" }, Tool("Door")),
            // 2 items, one a nested stack: 1 valid item left, so the whole stack is skipped.
            ["Drops"] = Of(Tool("Level"), Tool("Nested", "X", type: "stack")),
        }, StackRef("Keeps"), StackRef("Drops"));

        var layout = PlanOne(p);

        Shape(layout).Should().Equal(
            "X:stack 'Keeps' item 1 ('Broken'): no commandId",
            "S:Keeps[Wall,Door]",
            "X:stack 'Drops' item 1 ('Nested'): type must be 'tool' (got 'stack')",
            "X:stack 'Drops' has 1 valid item; a stack needs 2 or 3");
        layout.Skipped.Select(s => s.ToolIndex).Should().Equal(1, 1, null);
    }

    [Fact]
    public void URL_items_can_be_stacked_and_dispatch_as_URLs()
    {
        var p = With(new()
        {
            ["Mixed"] = Of(Tool("Level"), Tool("Help", "URL:https://help.autodesk.com"), Tool("Mail", "url:mailto:it@example.com")),
        }, StackRef("Mixed"));

        var stack = (StackItem)PlanOne(p).Items.Single();

        stack.Buttons.Select(b => (b.Target.Kind, b.Target.Payload)).Should().Equal(
            (SlotKind.Command, "ID_LEVEL"),
            (SlotKind.Url, "https://help.autodesk.com"),
            (SlotKind.Url, "mailto:it@example.com"));
        stack.Buttons[1].Target.ToolTip.Should().Be("Open: https://help.autodesk.com");
        stack.Buttons[0].Target.ToolTip.Should().Be("Posts: ID_LEVEL");
    }

    [Fact]
    public void An_unknown_stack_reference_a_nameless_stack_slot_an_unknown_type_and_a_tool_without_command_are_skipped()
    {
        var p = With(new() { ["Real"] = Of(Tool("A"), Tool("B")) },
            StackRef("Missing"), StackRef(""), new Slot { SlotType = "pulldown", Name = "P" }, new Slot { Name = "No command" });

        Shape(PlanOne(p)).Should().Equal(
            "X:stack 'Missing' not found in profile.stacks",
            "X:stack slot has no name",
            "X:unknown slot type 'pulldown'",
            "X:tool slot has no commandId");
    }

    [Fact]
    public void Mixed_order_of_large_buttons_and_stacks_follows_the_slots_and_numbers_the_ids()
    {
        var p = With(new()
        {
            ["Draw"] = Of(Tool("Line"), Tool("Arc"), Tool("Circle")),
            ["Nav"] = Of(Tool("Back"), Tool("Forward")),
        }, Tool("Move"), StackRef("Draw"), new Slot { Name = "Skipped" }, Tool("Copy"), StackRef("Nav"), Tool("Rotate"));

        var layout = RibbonLayoutPlanner.PlanPanel(p, p.Panels[0], 4);

        Shape(layout).Should().Equal("L:Move", "S:Draw[Line,Arc,Circle]", "X:tool slot has no commandId", "L:Copy", "S:Nav[Back,Forward]", "L:Rotate");
        layout.Items.Select(i => i.SlotIndex).Should().Equal(0, 1, 2, 3, 4, 5);

        // A rendered item takes the next number; a skip takes none.
        var ids = layout.Rendered.Select(i => i switch
        {
            LargeButtonItem l => l.Button.Id,
            StackItem s => s.Id + "=" + string.Join("|", s.Buttons.Select(b => b.Id)),
            _ => "",
        });
        ids.Should().Equal(
            "RST_ProfileBtn_004_000",
            "RST_ProfileStack_004_001=RST_ProfileBtn_004_001_0|RST_ProfileBtn_004_001_1|RST_ProfileBtn_004_001_2",
            "RST_ProfileBtn_004_002",
            "RST_ProfileStack_004_003=RST_ProfileBtn_004_003_0|RST_ProfileBtn_004_003_1",
            "RST_ProfileBtn_004_004");
    }

    [Fact]
    public void Large_buttons_wrap_their_label_and_small_buttons_keep_one_line()
    {
        var p = With(new() { ["S"] = Of(Tool("Structural Column"), Tool("Two\nLines")) },
            Tool("Structural Column"), StackRef("S"));

        var layout = PlanOne(p);

        ((LargeButtonItem)layout.Items[0]).Button.Label.Should().Be("Structural\nColumn");
        ((LargeButtonItem)layout.Items[0]).Button.Size.Should().Be(PlannedButtonSize.Large);
        ((StackItem)layout.Items[1]).Buttons.Select(b => b.Label).Should().Equal("Structural Column", "Two Lines");
    }

    [Fact]
    public void A_profile_without_stacks_keeps_the_ids_it_had_before_stacks()
    {
        var p = With(new(), Tool("A"), new Slot { Name = "No command" }, Tool("B"), Tool("C"));
        PlanOne(p).Rendered.Cast<LargeButtonItem>().Select(l => l.Button.Id)
            .Should().Equal("RST_ProfileBtn_000_000", "RST_ProfileBtn_000_001", "RST_ProfileBtn_000_002");
    }

    [Fact]
    public void Every_panel_is_planned_in_order_and_the_icon_file_travels_with_the_button()
    {
        var p = new Profile
        {
            ProfileName = "P", Tab = "T",
            Stacks = { ["S"] = Of(new Slot { Name = "A", CommandId = "ID_A", IconFile = "pack:move_green" }, Tool("B")) },
            Panels = [new Panel { Name = "One", Slots = [StackRef("S")] }, new Panel { Name = "Two", Slots = [Tool("C")] }],
        };

        var plan = RibbonLayoutPlanner.Plan(p);

        plan.Select(l => (l.PanelIndex, l.Name)).Should().Equal((0, "One"), (1, "Two"));
        ((StackItem)plan[0].Items[0]).Buttons[0].IconFile.Should().Be("pack:move_green");
    }

    [Fact]
    public void The_panel_width_estimate_counts_a_stack_by_its_labels()
    {
        var p = With(new()
        {
            ["Short"] = Of(Tool("A"), Tool("B")),
            ["Long"] = Of(Tool("A very long label"), Tool("B")),
        }, Tool("Move"), StackRef("Short"), StackRef("Long"));

        var layout = PlanOne(p);
        var shortStack = (StackItem)layout.Items[1];
        var longStack = (StackItem)layout.Items[2];

        RibbonLayoutPlanner.EstimateStackWidth(shortStack).Should().Be(RibbonLayoutPlanner.MinStackWidth);
        RibbonLayoutPlanner.EstimateStackWidth(longStack).Should().Be(RibbonLayoutPlanner.SmallButtonChrome + 17 * RibbonLayoutPlanner.SmallLabelCharWidth);
        RibbonLayoutPlanner.EstimatePanelWidth(layout).Should().Be(96 + 64 + RibbonLayoutPlanner.EstimateStackWidth(longStack) + 8);

        // Large buttons only: the previous estimate (96 px each, plus 8), never under one button.
        RibbonLayoutPlanner.EstimatePanelWidth(PlanOne(With(new(), Tool("A"), Tool("B")))).Should().Be(200);
        RibbonLayoutPlanner.EstimatePanelWidth(PlanOne(With(new()))).Should().Be(96);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("  Wall  ", "Wall")]
    [InlineData("Two\nLines", "Two Lines")]
    [InlineData("A\r\n\r\nB", "A B")]
    public void SingleLine_flattens_line_breaks(string? input, string expected) =>
        RibbonLayoutPlanner.SingleLine(input).Should().Be(expected);

    [Theory]
    [InlineData("ID_WALL", SlotKind.Command, "ID_WALL")]
    [InlineData("URL:https://x.test", SlotKind.Url, "https://x.test")]
    [InlineData("Url:x.test", SlotKind.Url, "x.test")]
    public void SlotTarget_parses_command_ids(string commandId, SlotKind kind, string payload)
    {
        var t = SlotTarget.Parse(commandId, "Label");
        t.Kind.Should().Be(kind);
        t.Payload.Should().Be(payload);
        t.DisplayName.Should().Be("Label");
    }
}
