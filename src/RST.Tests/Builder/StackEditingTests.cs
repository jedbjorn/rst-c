// rst-c's own (not vendored): spec #17 (Ribbon Stacks), the Builder's stack authoring rules
// (RST.Core.Builder.StackEditing) and the stack drops in PanelDrops. The view-model and the real
// window are covered in RST.RuntimeTests/Builder/ProfileBuilderStackTests (AC3).

using FluentAssertions;
using RST.Core.Builder;
using RST.Core.Profiles;
using Xunit;

namespace RST.Tests.Builder;

public class StackEditingTests
{
    private static CatalogCommand Cmd(string id, string label, string tab = "Architecture", string panel = "Build") =>
        new(id, label, "Native", tab, panel);

    private static Profile Fresh() => new()
    {
        ProfileName = "P",
        Tab = "T",
        Panels = [new Panel { Name = "A", Slots = [] }, new Panel { Name = "B", Slots = [new Slot { Name = "Move", CommandId = "ID_MOVE" }] }],
    };

    [Fact]
    public void AddStack_names_the_first_free_Stack_N()
    {
        var p = Fresh();
        StackEditing.AddStack(p).Should().Be("Stack 1");
        StackEditing.AddStack(p).Should().Be("Stack 2");
        p.Stacks.Remove("Stack 1");
        StackEditing.AddStack(p).Should().Be("Stack 1");
        p.Stacks.Keys.Should().BeEquivalentTo("Stack 1", "Stack 2");
        p.Stacks["Stack 1"].Tools.Should().BeEmpty();
    }

    [Fact]
    public void AddTool_stores_the_command_like_a_panel_tool_and_refuses_a_fourth()
    {
        var p = Fresh();
        var name = StackEditing.AddStack(p);
        StackEditing.AddTool(p, name, Cmd("ID_OBJECTS_WALL", "Wall")).Should().BeTrue();
        StackEditing.AddTool(p, name, Cmd("ID_OBJECTS_DOOR", "Door")).Should().BeTrue();
        StackEditing.AddTool(p, name, Cmd("ID_OBJECTS_WINDOW", "Window")).Should().BeTrue();
        StackEditing.IsFull(p.Stacks[name]).Should().BeTrue();

        StackEditing.AddTool(p, name, Cmd("ID_OBJECTS_LEVEL", "Level")).Should().BeFalse("a stack holds 2 or 3 tools");
        p.Stacks[name].Tools.Select(t => t.Name).Should().Equal("Wall", "Door", "Window");

        var wall = p.Stacks[name].Tools[0];
        (wall.SlotType, wall.CommandId, wall.SourceTab, wall.SourcePanel).Should().Be(("tool", "ID_OBJECTS_WALL", "Architecture", "Build"));
        StackEditing.AddTool(p, "Nope", Cmd("X", "X")).Should().BeFalse();
    }

    [Fact]
    public void Underfilled_is_fewer_than_two()
    {
        var p = Fresh();
        var name = StackEditing.AddStack(p);
        StackEditing.IsUnderfilled(p.Stacks[name]).Should().BeTrue();
        StackEditing.FirstUnderfilled(p).Should().Be(name);
        StackEditing.AddTool(p, name, Cmd("A", "A"));
        StackEditing.IsUnderfilled(p.Stacks[name]).Should().BeTrue();
        StackEditing.AddTool(p, name, Cmd("B", "B"));
        StackEditing.IsUnderfilled(p.Stacks[name]).Should().BeFalse();
        StackEditing.FirstUnderfilled(p).Should().BeNull();
    }

    [Fact]
    public void Rename_rewrites_every_reference_keeps_the_order_and_refuses_a_duplicate()
    {
        var p = Fresh();
        StackEditing.AddStack(p);
        StackEditing.AddStack(p);
        StackEditing.AddStack(p);
        p.Panels[0].Slots.Add(StackEditing.StackSlot("Stack 2"));
        p.Panels[1].Slots.Add(StackEditing.StackSlot("Stack 2"));
        p.Panels[1].Slots.Add(StackEditing.StackSlot("Stack 3"));

        StackEditing.Rename(p, "Stack 2", "  Draw  ", out var final).Should().Be(StackRename.Renamed);
        final.Should().Be("Draw");
        p.Stacks.Keys.Should().Equal("Stack 1", "Draw", "Stack 3");
        p.Panels.SelectMany(x => x.Slots).Where(s => s.SlotType == "stack").Select(s => s.Name).Should().Equal("Draw", "Draw", "Stack 3");

        StackEditing.Rename(p, "Draw", "Stack 3", out final).Should().Be(StackRename.Duplicate);
        final.Should().Be("Draw");
        p.Stacks.Keys.Should().Equal("Stack 1", "Draw", "Stack 3");

        StackEditing.Rename(p, "Draw", "   ", out _).Should().Be(StackRename.Unchanged);
        StackEditing.Rename(p, "Draw", "Draw", out _).Should().Be(StackRename.Unchanged);
        StackEditing.Rename(p, "Missing", "X", out _).Should().Be(StackRename.NotFound);
    }

    [Fact]
    public void Delete_removes_the_stack_and_its_references()
    {
        var p = Fresh();
        var name = StackEditing.AddStack(p);
        p.Panels[0].Slots.Add(StackEditing.StackSlot(name));
        p.Panels[1].Slots.Add(StackEditing.StackSlot(name));
        StackEditing.IsPlaced(p, name).Should().BeTrue();

        StackEditing.Delete(p, name).Should().Be(2);

        p.Stacks.Should().BeEmpty();
        p.Panels[1].Slots.Select(s => s.Name).Should().Equal("Move");
        StackEditing.IsPlaced(p, name).Should().BeFalse();
    }

    [Fact]
    public void Tools_reorder_and_remove()
    {
        var p = Fresh();
        var name = StackEditing.AddStack(p);
        foreach (var c in new[] { "A", "B", "C" }) StackEditing.AddTool(p, name, Cmd(c, c));

        StackEditing.MoveTool(p, name, 2, -1);
        p.Stacks[name].Tools.Select(t => t.Name).Should().Equal("A", "C", "B");
        StackEditing.MoveTool(p, name, 0, -1);
        StackEditing.MoveTool(p, name, 2, +1);
        p.Stacks[name].Tools.Select(t => t.Name).Should().Equal(["A", "C", "B"], "edge moves change nothing");

        StackEditing.RemoveTool(p, name, 1);
        p.Stacks[name].Tools.Select(t => t.Name).Should().Equal("A", "B");
        StackEditing.RemoveTool(p, name, 9);
        p.Stacks[name].Tools.Should().HaveCount(2);
    }

    [Fact]
    public void A_stack_dropped_on_a_card_is_appended_and_on_a_slot_inserted()
    {
        var p = Fresh();
        var name = StackEditing.AddStack(p);
        var drag = BuilderDrag.FromStack(name);

        PanelDrops.CardMarker(drag, 0, upperHalf: true).Should().Be(DropMarker.Over);
        PanelDrops.SlotMarker(drag, upperHalf: true).Should().Be(DropMarker.Before);
        PanelDrops.SlotMarker(drag, upperHalf: false).Should().Be(DropMarker.After);

        PanelDrops.DropOnPanel(p, drag, 0, upperHalf: true).Should().Be(0);
        p.Panels[0].Slots.Should().ContainSingle().Which.Should().BeEquivalentTo(new Slot { SlotType = "stack", Name = name });

        PanelDrops.DropOnSlot(p, drag, 1, 0, upperHalf: true).Should().Be(1);
        p.Panels[1].Slots.Select(s => (s.SlotType, s.Name)).Should().Equal(("stack", name), ("tool", "Move"));

        PanelDrops.DropOnPanel(p, BuilderDrag.FromStack("Unknown"), 0, true).Should().BeNull("an unknown stack is never placed");
    }
}
