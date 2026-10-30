// rst-c's own (not vendored): stack authoring on the panel cards (feature #18, spec #19, AC2):
// create, join, refuse a fourth, leave a 3-stack, dissolve a 2-stack in place, move a whole stack
// (never into another), New Stack, Edit Stack, un-stack, "Stack N" names, every normaliser case,
// the 4+ Save refusal, and no orphan stack or broken reference after any operation or save.

using System.IO;
using FluentAssertions;
using RST.Core.Builder;
using RST.Core.Loader;
using RST.Core.Profiles;
using Xunit;

namespace RST.Tests.Builder;

public sealed class PanelStacksTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rst-stacks-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private static Slot Tool(string id) => new() { SlotType = "tool", Name = id, CommandId = id };

    /// <summary>Panels of tools by id; "A+B" makes a stack of A and B.</summary>
    private static Profile Make(params string[][] panels)
    {
        var p = new Profile { Id = "id-1", ProfileName = "Arch", Tab = "Studio" };
        for (var i = 0; i < panels.Length; i++)
        {
            var panel = new Panel { Name = "P" + i, Color = "#4f8ef7" };
            foreach (var entry in panels[i])
            {
                if (!entry.Contains('+'))
                {
                    panel.Slots.Add(Tool(entry));
                    continue;
                }
                var name = PanelStacks.NextStackName(p);
                p.Stacks[name] = new Stack { Tools = entry.Split('+').Select(Tool).ToList() };
                panel.Slots.Add(PanelStacks.StackSlot(name));
            }
            p.Panels.Add(panel);
        }
        return p;
    }

    /// <summary>"P0:A,[B C] | P1:D": each panel's slots, a stack as its tools in brackets.</summary>
    private static string Shape(Profile p) => string.Join(" | ", p.Panels.Select(x => x.Name + ":" + string.Join(",", x.Slots.Select(s =>
        PanelStacks.IsStack(s) ? "[" + string.Join(" ", PanelStacks.ToolsOf(p, s).Select(t => t.Name)) + "]" : s.Name))));

    /// <summary>Every stack slot names its own stack of 2-3 tools; no stack is shared or orphaned; Validate passes.</summary>
    private static void AssertConsistent(Profile p)
    {
        var refs = p.Panels.SelectMany(x => x.Slots).Where(PanelStacks.IsStack).Select(s => s.Name).ToList();
        refs.Should().OnlyHaveUniqueItems("no stack is shared");
        refs.Should().BeEquivalentTo(p.Stacks.Keys, "every stack is placed once and every reference resolves");
        p.Stacks.Values.Should().NotContain(s => s.Tools.Count < 2 || s.Tools.Count > 3);
        ProfileSerializer.Validate(p).Should().BeEmpty();
    }

    // ── create ──

    [Fact]
    public void A_tool_dropped_on_the_middle_of_a_loose_tool_makes_a_stack_in_its_place_the_dropped_tool_second()
    {
        var p = Make(["A", "B", "C"]);
        PanelStacks.StackOnTool(p, TileSource.New(Tool("X")), 0, 1).Should().Be(StackResult.Done);
        Shape(p).Should().Be("P0:A,[B X],C");
        p.Stacks.Keys.Should().Equal("Stack 1");
        AssertConsistent(p);
    }

    [Theory]
    [InlineData(0, 2, "P0:B,[C A]")]  // from above: the target's index shifts
    [InlineData(2, 0, "P0:[A C],B")]  // from below
    public void A_loose_tool_from_the_same_panel_stacks_onto_another(int from, int target, string expected)
    {
        var p = Make(["A", "B", "C"]);
        PanelStacks.StackOnTool(p, TileSource.Tile(0, from), 0, target).Should().Be(StackResult.Done);
        Shape(p).Should().Be(expected);
        AssertConsistent(p);
    }

    [Fact]
    public void A_row_of_a_2_stack_stacked_onto_a_loose_tool_dissolves_its_old_stack()
    {
        var p = Make(["A+B", "C"]);
        PanelStacks.StackOnTool(p, TileSource.Tile(0, 0, 1), 0, 1).Should().Be(StackResult.Done);
        Shape(p).Should().Be("P0:A,[C B]");
        AssertConsistent(p);
    }

    [Fact]
    public void Stacking_refuses_a_whole_stack_a_stack_target_and_does_nothing_on_itself()
    {
        var p = Make(["A+B", "C"]);
        var before = ProfileEditing.Snapshot(p);
        PanelStacks.StackOnTool(p, TileSource.Tile(0, 0), 0, 1).Should().Be(StackResult.Refused, "a stack never goes inside another stack");
        PanelStacks.StackOnTool(p, TileSource.New(Tool("X")), 0, 0).Should().Be(StackResult.Refused, "a stack is joined, not stacked onto");
        PanelStacks.StackOnTool(p, TileSource.Tile(0, 1), 0, 1).Should().Be(StackResult.Unchanged);
        PanelStacks.StackOnTool(p, TileSource.New(Tool("X")), 5, 0).Should().Be(StackResult.Refused);
        ProfileEditing.Snapshot(p).Should().Be(before);
    }

    // ── join, refuse a fourth ──

    [Fact]
    public void A_tool_joins_a_stack_at_the_end_or_at_a_row()
    {
        var p = Make(["A+B", "C"]);
        PanelStacks.JoinStack(p, TileSource.Tile(0, 1), 0, 0, at: 1).Should().Be(StackResult.Done);
        Shape(p).Should().Be("P0:[A C B]");
        AssertConsistent(p);

        var q = Make(["A+B"]);
        PanelStacks.JoinStack(q, TileSource.New(Tool("X")), 0, 0).Should().Be(StackResult.Done);
        Shape(q).Should().Be("P0:[A B X]");
        AssertConsistent(q);
    }

    [Fact]
    public void A_fourth_tool_is_refused_and_nothing_changes()
    {
        var p = Make(["A+B+C", "D"], ["E+F+G"]);
        var before = ProfileEditing.Snapshot(p);
        PanelStacks.JoinStack(p, TileSource.Tile(0, 1), 0, 0).Should().Be(StackResult.Full);
        PanelStacks.JoinStack(p, TileSource.New(Tool("X")), 0, 0).Should().Be(StackResult.Full);
        PanelStacks.JoinStack(p, TileSource.Tile(1, 0, 0), 0, 0).Should().Be(StackResult.Full);
        ProfileEditing.Snapshot(p).Should().Be(before, "a refused drop leaves the source where it was");
        PanelStacks.FullMessage.Should().Be("A stack holds 2–3 tools. This one is full.");
        AssertConsistent(p);
    }

    [Fact]
    public void A_row_dropped_on_its_own_stack_reorders_it()
    {
        var p = Make(["A+B+C"]);
        PanelStacks.JoinStack(p, TileSource.Tile(0, 0, 0), 0, 0, at: 3).Should().Be(StackResult.Done, "a full stack still reorders");
        Shape(p).Should().Be("P0:[B C A]");
        PanelStacks.JoinStack(p, TileSource.Tile(0, 0, 2), 0, 0, at: 0).Should().Be(StackResult.Done);
        Shape(p).Should().Be("P0:[A B C]");
        PanelStacks.JoinStack(p, TileSource.Tile(0, 0, 1), 0, 0, at: 1).Should().Be(StackResult.Unchanged);
        AssertConsistent(p);
    }

    [Fact]
    public void Joining_refuses_a_whole_stack_and_a_target_that_is_not_a_stack()
    {
        var p = Make(["A+B", "C+D", "E"]);
        PanelStacks.JoinStack(p, TileSource.Tile(0, 1), 0, 0).Should().Be(StackResult.Refused);
        PanelStacks.JoinStack(p, TileSource.New(Tool("X")), 0, 2).Should().Be(StackResult.Refused);
        Shape(p).Should().Be("P0:[A B],[C D],E");
        AssertConsistent(p);
    }

    [Fact]
    public void A_row_from_a_3_stack_joining_another_stack_leaves_2()
    {
        var p = Make(["A+B+C"], ["D+E"]);
        PanelStacks.JoinStack(p, TileSource.Tile(0, 0, 1), 1, 0).Should().Be(StackResult.Done);
        Shape(p).Should().Be("P0:[A C] | P1:[D E B]");
        AssertConsistent(p);
    }

    // ── leave, dissolve, move ──

    [Fact]
    public void A_tool_dragged_out_of_a_3_stack_leaves_2()
    {
        var p = Make(["A+B+C", "D"]);
        PanelStacks.InsertBeside(p, TileSource.Tile(0, 0, 0), 0, 1, after: true).Should().Be(StackResult.Done);
        Shape(p).Should().Be("P0:[B C],D,A");
        AssertConsistent(p);
    }

    [Fact]
    public void A_tool_dragged_out_of_a_2_stack_dissolves_it_and_the_other_stays_in_its_place()
    {
        var p = Make(["X", "A+B", "C"], ["D"]);
        PanelStacks.Append(p, TileSource.Tile(0, 1, 0), 1).Should().Be(StackResult.Done);
        Shape(p).Should().Be("P0:X,B,C | P1:D,A");
        p.Stacks.Should().BeEmpty();
        AssertConsistent(p);
    }

    [Theory]
    [InlineData(false, "P0:X,A,B,C")]  // before the stack it left: before the tool that replaced it
    [InlineData(true, "P0:X,B,A,C")]
    public void A_row_dropped_beside_its_own_dissolving_stack_lands_beside_the_remaining_tool(bool after, string expected)
    {
        var p = Make(["X", "A+B", "C"]);
        PanelStacks.InsertBeside(p, TileSource.Tile(0, 1, 0), 0, 1, after).Should().Be(StackResult.Done);
        Shape(p).Should().Be(expected);
        AssertConsistent(p);
    }

    [Fact]
    public void A_whole_stack_moves_beside_a_slot_or_to_a_panel_end_and_stays_whole()
    {
        var p = Make(["A+B", "C", "D"], ["E"]);
        PanelStacks.InsertBeside(p, TileSource.Tile(0, 0), 0, 2, after: true).Should().Be(StackResult.Done);
        Shape(p).Should().Be("P0:C,D,[A B] | P1:E");
        PanelStacks.Append(p, TileSource.Tile(0, 2), 1).Should().Be(StackResult.Done);
        Shape(p).Should().Be("P0:C,D | P1:E,[A B]");
        PanelStacks.InsertBeside(p, TileSource.Tile(1, 1), 1, 1, after: false).Should().Be(StackResult.Unchanged);
        PanelStacks.Append(p, TileSource.Tile(1, 1), 1).Should().Be(StackResult.Unchanged);
        AssertConsistent(p);
    }

    [Fact]
    public void A_new_tool_dropped_on_an_edge_is_inserted_on_the_panel()
    {
        var p = Make(["A+B", "C"]);
        PanelStacks.InsertBeside(p, TileSource.New(Tool("X")), 0, 0, after: false).Should().Be(StackResult.Done);
        Shape(p).Should().Be("P0:X,[A B],C");
        AssertConsistent(p);
    }

    // ── New Stack, Edit Stack, un-stack, remove a row ──

    [Fact]
    public void New_Stack_takes_the_first_pick_s_place_in_pick_order()
    {
        var p = Make(["A", "B+C", "D", "E"]);
        PanelStacks.LooseTools(p, 0).Should().Equal(0, 2, 3);
        PanelStacks.NewStack(p, 0, [3, 0]).Should().Be(StackResult.Done);
        Shape(p).Should().Be("P0:[B C],D,[E A]");
        AssertConsistent(p);

        var q = Make(["A", "B", "C"]);
        PanelStacks.NewStack(q, 0, [1, 2, 0]).Should().Be(StackResult.Done);
        Shape(q).Should().Be("P0:[B C A]");
        AssertConsistent(q);
    }

    [Fact]
    public void New_Stack_needs_2_to_3_distinct_loose_tools()
    {
        var p = Make(["A", "B+C", "D", "E", "F"]);
        var before = ProfileEditing.Snapshot(p);
        PanelStacks.NewStack(p, 0, [0]).Should().Be(StackResult.Refused);
        PanelStacks.NewStack(p, 0, [0, 2, 3, 4]).Should().Be(StackResult.Full);
        PanelStacks.NewStack(p, 0, [0, 0]).Should().Be(StackResult.Refused);
        PanelStacks.NewStack(p, 0, [0, 1]).Should().Be(StackResult.Refused, "a stack is not a loose tool");
        PanelStacks.NewStack(p, 0, [0, 9]).Should().Be(StackResult.Refused);
        ProfileEditing.Snapshot(p).Should().Be(before);
        AssertConsistent(p);
    }

    [Fact]
    public void Edit_Stack_reorders_unticks_and_adds()
    {
        var p = Make(["X", "A+B+C", "Y"]);
        PanelStacks.EditStack(p, 0, 1, [StackPick.Row(2), StackPick.Row(0), StackPick.Row(1)]).Should().Be(StackResult.Done);
        Shape(p).Should().Be("P0:X,[C A B],Y");

        // Untick A and B: they go back on the panel right after the stack, in order; add X and Y.
        PanelStacks.EditStack(p, 0, 1, [StackPick.Row(0), StackPick.Loose(2)]).Should().Be(StackResult.Done);
        Shape(p).Should().Be("P0:X,[C Y],A,B");
        PanelStacks.EditStack(p, 0, 1, [StackPick.Loose(0), StackPick.Row(1)]).Should().Be(StackResult.Done);
        Shape(p).Should().Be("P0:[X Y],C,A,B");
        AssertConsistent(p);
    }

    [Fact]
    public void Edit_Stack_with_fewer_than_2_picks_un_stacks_and_refuses_a_fourth()
    {
        var p = Make(["A+B+C", "D"]);
        var before = ProfileEditing.Snapshot(p);
        PanelStacks.EditStack(p, 0, 0, [StackPick.Row(0), StackPick.Row(1), StackPick.Row(2), StackPick.Loose(1)]).Should().Be(StackResult.Full);
        PanelStacks.EditStack(p, 0, 0, [StackPick.Row(0), StackPick.Row(1), StackPick.Row(2)]).Should().Be(StackResult.Unchanged);
        PanelStacks.EditStack(p, 0, 0, [StackPick.Row(0), StackPick.Loose(0)]).Should().Be(StackResult.Refused, "the stack itself is not a loose tool");
        ProfileEditing.Snapshot(p).Should().Be(before);

        PanelStacks.EditStack(p, 0, 0, [StackPick.Row(1)]).Should().Be(StackResult.Done);
        Shape(p).Should().Be("P0:A,B,C,D", "fewer than 2 removes the stack; its tools stay, in order");
        p.Stacks.Should().BeEmpty();
        AssertConsistent(p);
    }

    [Fact]
    public void Un_stack_puts_the_tools_back_in_order_and_never_removes_them()
    {
        var p = Make(["X", "A+B+C", "Y"]);
        PanelStacks.Unstack(p, 0, 1).Should().Be(StackResult.Done);
        Shape(p).Should().Be("P0:X,A,B,C,Y");
        p.Stacks.Should().BeEmpty();
        PanelStacks.Unstack(p, 0, 0).Should().Be(StackResult.Refused);
        AssertConsistent(p);
    }

    [Fact]
    public void Removing_a_row_leaves_2_or_turns_a_2_stack_into_a_plain_tool()
    {
        var p = Make(["A+B+C", "X"]);
        PanelStacks.RemoveRow(p, 0, 0, 1).Should().Be(StackResult.Done);
        Shape(p).Should().Be("P0:[A C],X");
        AssertConsistent(p);
        PanelStacks.RemoveRow(p, 0, 0, 0).Should().Be(StackResult.Done);
        Shape(p).Should().Be("P0:C,X");
        p.Stacks.Should().BeEmpty();
        AssertConsistent(p);
    }

    // ── names ──

    [Fact]
    public void Stack_names_are_Stack_N_unique_in_the_profile()
    {
        var p = Make(["A", "B", "C", "D", "E", "F"]);
        p.Stacks["stack 2"] = new Stack { Tools = [Tool("Q"), Tool("R")] };
        p.Panels[0].Slots.Add(PanelStacks.StackSlot("stack 2"));
        PanelStacks.NewStack(p, 0, [0, 1]);
        PanelStacks.NewStack(p, 0, [1, 2]);
        p.Stacks.Keys.Should().Equal("stack 2", "Stack 1", "Stack 3");
        AssertConsistent(p);
        PanelStacks.Unstack(p, 0, 0);
        PanelStacks.NextStackName(p).Should().Be("Stack 1", "a freed name is reused");
        AssertConsistent(p);
    }

    // ── normaliser ──

    private sealed class RecordingLog : ILoaderLog
    {
        public List<string> Warnings { get; } = [];

        public void Debug(Exception? error, string template, params object?[] args) { }

        public void Information(string template, params object?[] args) { }

        public void Warning(Exception? error, string template, params object?[] args) => Warnings.Add(string.Join(" ", args));

        public void Error(Exception? error, string template, params object?[] args) { }
    }

    [Fact]
    public void A_clean_profile_is_not_changed_by_the_normaliser()
    {
        var p = Make(["A+B", "C"], ["D+E+F"]);
        var before = ProfileEditing.Snapshot(p);
        var log = new RecordingLog();
        var report = PanelStacks.Normalize(p, log);
        report.Changed.Should().BeFalse();
        report.Notes.Should().BeEmpty();
        log.Warnings.Should().BeEmpty();
        ProfileEditing.Snapshot(p).Should().Be(before);
    }

    [Fact]
    public void A_shared_stack_is_split_so_each_slot_has_its_own_copy()
    {
        var p = Make(["A+B"], ["C"]);
        p.Panels[1].Slots.Add(PanelStacks.StackSlot("Stack 1"));
        ProfileSerializer.Validate(p).Should().BeEmpty("the read path lets a shared stack through");
        var log = new RecordingLog();
        var report = PanelStacks.Normalize(p, log);
        report.Changed.Should().BeTrue();
        report.Notes.Should().ContainSingle().Which.Should().Contain("shared").And.Contain("Stack 2");
        log.Warnings.Should().HaveCount(1);
        Shape(p).Should().Be("P0:[A B] | P1:C,[A B]");
        p.Panels[1].Slots[1].Name.Should().Be("Stack 2");
        p.Stacks["Stack 2"].Tools[0].Should().NotBeSameAs(p.Stacks["Stack 1"].Tools[0], "the copy is independent");
        PanelStacks.RemoveRow(p, 1, 1, 0);
        Shape(p).Should().Be("P0:[A B] | P1:C,B");
        AssertConsistent(p);
    }

    [Fact]
    public void An_empty_or_one_tool_stack_becomes_nothing_or_a_plain_tool()
    {
        var p = Make(["X"]);
        p.Stacks["empty"] = new Stack { Tools = [] };
        p.Stacks["one"] = new Stack { Tools = [Tool("A")] };
        p.Panels[0].Slots.InsertRange(0, [PanelStacks.StackSlot("empty"), PanelStacks.StackSlot("one")]);
        var report = PanelStacks.Normalize(p);
        report.Changed.Should().BeTrue();
        report.Notes.Should().HaveCount(2);
        report.Notes[0].Should().Contain("\"empty\" was empty");
        report.Notes[1].Should().Contain("\"one\" held one tool");
        Shape(p).Should().Be("P0:A,X");
        AssertConsistent(p);
    }

    [Fact]
    public void A_shared_one_tool_stack_becomes_a_plain_tool_in_each_place()
    {
        var p = Make(["X"], ["Y"]);
        p.Stacks["one"] = new Stack { Tools = [Tool("A")] };
        p.Panels[0].Slots.Add(PanelStacks.StackSlot("one"));
        p.Panels[1].Slots.Add(PanelStacks.StackSlot("one"));
        PanelStacks.Normalize(p).Changed.Should().BeTrue();
        Shape(p).Should().Be("P0:X,A | P1:Y,A");
        AssertConsistent(p);
    }

    [Fact]
    public void A_reference_to_a_missing_stack_is_dropped_and_an_orphan_stack_removed()
    {
        var p = Make(["A", "B+C"]);
        p.Panels[0].Slots.Insert(1, PanelStacks.StackSlot("ghost"));
        p.Stacks["unplaced"] = new Stack { Tools = [Tool("Q"), Tool("R")] };
        var log = new RecordingLog();
        var report = PanelStacks.Normalize(p, log);
        report.Changed.Should().BeTrue();
        report.Notes.Should().HaveCount(2);
        report.Notes[0].Should().Contain("\"ghost\" does not exist");
        report.Notes[1].Should().Contain("\"unplaced\" was not on any panel");
        log.Warnings.Should().HaveCount(2);
        Shape(p).Should().Be("P0:A,[B C]");
        AssertConsistent(p);
    }

    [Fact]
    public void The_normaliser_leaves_a_4_tool_stack_for_the_save_refusal()
    {
        var p = Make(["A+B+C"]);
        p.Stacks["Stack 1"].Tools.Add(Tool("D"));
        PanelStacks.Normalize(p).Changed.Should().BeFalse();
        PanelStacks.SaveBlocker(p).Should().Contain("Panel \"P0\"").And.Contain("4 tools");
    }

    // ── save ──

    [Fact]
    public void Save_refuses_a_placed_4_tool_stack_naming_its_panel_and_writes_nothing()
    {
        var p = Make(["X"], ["A+B+C"]);
        p.Panels[1].Name = "Links";
        p.Stacks["Stack 1"].Tools.Add(Tool("D"));
        var result = new ProfileSaveService(_dir).Save(p);
        result.Ok.Should().BeFalse();
        result.Error.Should().Be("Panel \"Links\" has a stack of 4 tools. A stack holds 2–3 tools; move some out before saving.");
        Directory.Exists(_dir).Should().BeFalse();
    }

    [Fact]
    public void Save_never_writes_an_orphan_stack()
    {
        var p = Make(["A+B", "C"]);
        p.Stacks["unplaced"] = new Stack { Tools = [Tool("Q"), Tool("R"), Tool("S"), Tool("T")] };
        var result = new ProfileSaveService(_dir).Save(p);
        result.Ok.Should().BeTrue("an orphan, even a 4+ one, is dropped rather than refused");
        var back = ProfileStore.List(_dir).Single().Profile;
        back.Stacks.Keys.Should().Equal("Stack 1");
        p.Stacks.Should().ContainKey("unplaced", "the editor's copy is not changed by the save");
    }

    [Fact]
    public void A_sequence_of_operations_saves_and_reads_back_consistently()
    {
        var p = Make(["A", "B", "C", "D"], ["E"]);
        PanelStacks.StackOnTool(p, TileSource.Tile(0, 1), 0, 0);            // [A B],C,D
        PanelStacks.JoinStack(p, TileSource.Tile(0, 2), 0, 0);              // [A B D],C
        PanelStacks.JoinStack(p, TileSource.Tile(1, 0), 0, 0);              // full
        PanelStacks.InsertBeside(p, TileSource.Tile(0, 0, 0), 1, 0, true);  // [B D],C | E,A
        PanelStacks.NewStack(p, 1, [1, 0]);                                 // | [A E]
        PanelStacks.Append(p, TileSource.Tile(1, 0), 0);                    // [B D],C,[A E] |
        PanelStacks.EditStack(p, 0, 0, [StackPick.Row(1), StackPick.Loose(1)]); // [D C],B,[A E]
        PanelStacks.Unstack(p, 0, 2);                                       // [D C],B,A,E
        Shape(p).Should().Be("P0:[D C],B,A,E | P1:");
        AssertConsistent(p);

        var result = new ProfileSaveService(_dir).Save(p);
        result.Error.Should().BeNull();
        var back = ProfileStore.List(_dir).Single().Profile;
        Shape(back).Should().Be(Shape(p));
        AssertConsistent(back);
    }
}
