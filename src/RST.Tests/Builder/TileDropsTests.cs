// rst-c's own (not vendored): drag and drop onto the panel cards' tiles (feature #18, spec #19,
// WU3, AC2 and AC5's rules): every row of the spec's stack table, planned from where the pointer is
// (the edge and middle zones of a tile or a stack, a stack row's halves) and applied through the
// PanelStacks operations, with the markers each shows and the profile left consistent after each.

using FluentAssertions;
using RST.Core.Builder;
using RST.Core.Profiles;
using Xunit;

namespace RST.Tests.Builder;

public class TileDropsTests
{
    private const double W = 100;   // a tile
    private const double SW = 200;  // a stack group
    private const double RH = 24;   // a stack row

    private static Slot Tool(string id) => new() { SlotType = "tool", Name = id, CommandId = id };

    private static readonly CatalogCommand Wall = new("ID_WALL", "Wall", "Native", "Architecture", "Build");

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

    private static string Shape(Profile p) => string.Join(" | ", p.Panels.Select(x => x.Name + ":" + string.Join(",", x.Slots.Select(s =>
        PanelStacks.IsStack(s) ? "[" + string.Join(" ", PanelStacks.ToolsOf(p, s).Select(t => t.Name)) + "]" : s.Name))));

    private static void AssertConsistent(Profile p)
    {
        var refs = p.Panels.SelectMany(x => x.Slots).Where(PanelStacks.IsStack).Select(s => s.Name).ToList();
        refs.Should().OnlyHaveUniqueItems();
        refs.Should().BeEquivalentTo(p.Stacks.Keys);
        p.Stacks.Values.Should().NotContain(s => s.Tools.Count < 2 || s.Tools.Count > 3);
        ProfileSerializer.Validate(p).Should().BeEmpty();
    }

    /// <summary>Plans the drop, applies it, and returns the plan and result.</summary>
    private static (TileDrop Plan, StackResult Result) Drop(Profile p, BuilderDrag drag, TileHit hit)
    {
        var plan = TileDrops.Plan(p, drag, hit);
        var result = TileDrops.Apply(p, drag, plan);
        AssertConsistent(p);
        return (plan, result);
    }

    private static TileHit Middle(int panel, int slot) => TileHit.On(panel, slot, W / 2, W);

    private static TileHit LeftEdge(int panel, int slot, double width = W) => TileHit.On(panel, slot, 2, width);

    private static TileHit RightEdge(int panel, int slot, double width = W) => TileHit.On(panel, slot, width - 2, width);

    private static TileHit Row(int panel, int slot, int row, bool upper) => TileHit.OnRow(panel, slot, row, SW / 2, SW, upper ? 2 : RH - 2, RH);

    // ── zones ──

    [Theory]
    [InlineData(0, 100, TileZone.Before)]
    [InlineData(23.9, 100, TileZone.Before)]
    [InlineData(24, 100, TileZone.Middle)]
    [InlineData(50, 100, TileZone.Middle)]
    [InlineData(76.1, 100, TileZone.After)]
    [InlineData(23, 200, TileZone.Before)]   // edges are at most 24 px on a wide stack group
    [InlineData(100, 200, TileZone.Middle)]
    [InlineData(177, 200, TileZone.After)]
    [InlineData(5, 0, TileZone.Middle)]      // not laid out yet
    public void Zones_are_a_quarter_of_the_width_at_most_24_px(double x, double width, TileZone expected) =>
        TileDrops.ZoneOf(x, width).Should().Be(expected);

    // ── the stack table ──

    [Fact]
    public void A_tool_on_the_middle_of_a_loose_tool_makes_a_stack_in_its_place_the_dropped_tool_second()
    {
        var p = Make(["A", "B", "C"]);
        var (plan, result) = Drop(p, BuilderDrag.FromSlot(0, 2), Middle(0, 0));
        plan.Kind.Should().Be(TileDropKind.MakeStack);
        plan.Marker.Should().Be(DropMarker.Over);
        result.Should().Be(StackResult.Done);
        Shape(p).Should().Be("P0:[A C],B");
    }

    [Fact]
    public void A_catalogue_command_on_the_middle_of_a_loose_tool_makes_a_stack_with_a_fresh_slot_each_time()
    {
        var p = Make(["A", "B"]);
        var drag = BuilderDrag.FromCatalog(Wall);
        Drop(p, drag, Middle(0, 0)).Result.Should().Be(StackResult.Done);
        Drop(p, drag, Middle(0, 1)).Result.Should().Be(StackResult.Done);
        Shape(p).Should().Be("P0:[A Wall],[B Wall]");
        var walls = p.Stacks.Values.Select(s => s.Tools[1]).ToList();
        walls[0].Should().NotBeSameAs(walls[1], "every drop places its own slot (WU1 review F3)");
    }

    [Fact]
    public void A_tool_on_the_middle_of_a_stack_joins_it_at_the_end()
    {
        var p = Make(["A+B", "C", "D"]);
        var (plan, result) = Drop(p, BuilderDrag.FromSlot(0, 1), TileHit.On(0, 0, SW / 2, SW));
        plan.Kind.Should().Be(TileDropKind.JoinStack);
        plan.Marker.Should().Be(DropMarker.Over);
        plan.MarkerRow.Should().Be(-1);
        result.Should().Be(StackResult.Done);
        Shape(p).Should().Be("P0:[A B C],D");
    }

    [Theory]
    [InlineData(0, true, "P0:[C A B],D")]
    [InlineData(0, false, "P0:[A C B],D")]
    [InlineData(1, false, "P0:[A B C],D")]
    public void A_tool_on_a_stack_row_s_edge_joins_before_or_after_that_row(int row, bool upper, string expected)
    {
        var p = Make(["A+B", "C", "D"]);
        var (plan, result) = Drop(p, BuilderDrag.FromSlot(0, 1), Row(0, 0, row, upper));
        plan.Kind.Should().Be(TileDropKind.JoinStack);
        plan.Marker.Should().Be(upper ? DropMarker.Before : DropMarker.After);
        plan.MarkerRow.Should().Be(row);
        result.Should().Be(StackResult.Done);
        Shape(p).Should().Be(expected);
    }

    [Fact]
    public void A_fourth_tool_is_refused_with_the_refusal_marker()
    {
        var p = Make(["A+B+C", "D"]);
        var before = Shape(p);
        foreach (var hit in new[] { TileHit.On(0, 0, SW / 2, SW), Row(0, 0, 1, upper: true) })
        {
            var (plan, result) = Drop(p, BuilderDrag.FromSlot(0, 1), hit);
            plan.Kind.Should().Be(TileDropKind.Full);
            plan.Marker.Should().Be(DropMarker.Refused);
            plan.MarkerRow.Should().Be(-1, "the whole stack shows the refusal");
            plan.Accepted.Should().BeFalse();
            result.Should().Be(StackResult.Full);
        }
        Drop(p, BuilderDrag.FromCatalog(Wall), TileHit.On(0, 0, SW / 2, SW)).Result.Should().Be(StackResult.Full);
        Shape(p).Should().Be(before);
        PanelStacks.FullMessage.Should().Be("A stack holds 2–3 tools. This one is full.");
    }

    [Theory]
    [InlineData(true, "P0:C,A,B")]
    [InlineData(false, "P0:A,C,B")]
    public void A_tool_on_the_edge_of_a_loose_tool_is_inserted_before_or_after_it(bool left, string expected)
    {
        var p = Make(["A", "B", "C"]);
        var (plan, _) = Drop(p, BuilderDrag.FromSlot(0, 2), left ? LeftEdge(0, 0) : RightEdge(0, 0));
        plan.Kind.Should().Be(left ? TileDropKind.InsertBefore : TileDropKind.InsertAfter);
        plan.Marker.Should().Be(left ? DropMarker.Before : DropMarker.After);
        Shape(p).Should().Be(expected);
    }

    [Theory]
    [InlineData(true, "P0:C,[A B],D")]
    [InlineData(false, "P0:[A B],C,D")]
    public void A_tool_on_the_edge_of_a_stack_is_inserted_beside_it_not_in_it(bool left, string expected)
    {
        var p = Make(["A+B", "C", "D"]);
        var (plan, _) = Drop(p, BuilderDrag.FromSlot(0, 1), left ? LeftEdge(0, 0, SW) : RightEdge(0, 0, SW));
        plan.Kind.Should().Be(left ? TileDropKind.InsertBefore : TileDropKind.InsertAfter);
        Shape(p).Should().Be(expected);
    }

    [Fact]
    public void A_full_stack_still_takes_a_tool_beside_it()
    {
        var p = Make(["A+B+C", "D"]);
        Drop(p, BuilderDrag.FromSlot(0, 1), LeftEdge(0, 0, SW)).Plan.Kind.Should().Be(TileDropKind.InsertBefore);
        Shape(p).Should().Be("P0:D,[A B C]");
    }

    [Fact]
    public void A_tool_dragged_out_of_a_3_tool_stack_leaves_2()
    {
        var p = Make(["A+B+C", "D"]);
        Drop(p, BuilderDrag.FromSlot(0, 0, row: 1), RightEdge(0, 1)).Result.Should().Be(StackResult.Done);
        Shape(p).Should().Be("P0:[A C],D,B");
    }

    [Fact]
    public void A_tool_dragged_out_of_a_2_tool_stack_dissolves_it_in_place()
    {
        var p = Make(["X", "A+B", "D"]);
        Drop(p, BuilderDrag.FromSlot(0, 1, row: 0), RightEdge(0, 2)).Result.Should().Be(StackResult.Done);
        Shape(p).Should().Be("P0:X,B,D,A");
        p.Stacks.Should().BeEmpty();
    }

    [Fact]
    public void A_row_dragged_onto_its_own_stack_s_edge_leaves_it_beside()
    {
        var p = Make(["A+B", "D"]);
        Drop(p, BuilderDrag.FromSlot(0, 0, row: 1), RightEdge(0, 0, SW)).Result.Should().Be(StackResult.Done);
        Shape(p).Should().Be("P0:A,B,D", "the stack dissolved in place and B went after it");
    }

    [Fact]
    public void A_row_dragged_onto_another_tool_makes_a_new_stack()
    {
        var p = Make(["A+B+C", "D"]);
        Drop(p, BuilderDrag.FromSlot(0, 0, row: 0), Middle(0, 1)).Plan.Kind.Should().Be(TileDropKind.MakeStack);
        Shape(p).Should().Be("P0:[B C],[D A]");
    }

    [Theory]
    [InlineData(0, 2, false, "P0:[B C A]")]   // first row after the last
    [InlineData(2, 0, true, "P0:[C A B]")]    // last row before the first
    public void A_row_moves_within_its_stack(int from, int row, bool upper, string expected)
    {
        var p = Make(["A+B+C"]);
        var (plan, result) = Drop(p, BuilderDrag.FromSlot(0, 0, row: from), Row(0, 0, row, upper));
        plan.Kind.Should().Be(TileDropKind.JoinStack, "a full stack still reorders its own rows");
        result.Should().Be(StackResult.Done);
        Shape(p).Should().Be(expected);
    }

    [Theory]
    [InlineData(1, true)]   // just before the next row: where it is
    [InlineData(0, false)]  // just after the previous row: where it is
    [InlineData(1, false)]  // over itself
    public void A_row_dropped_where_it_already_is_is_not_accepted(int row, bool upper)
    {
        var p = Make(["A+B+C"]);
        TileDrops.Plan(p, BuilderDrag.FromSlot(0, 0, row: 1), Row(0, 0, row, upper)).Kind.Should().Be(TileDropKind.None, "B is row 1");
    }

    [Theory]
    [InlineData(true, "P0:[C D],[A B]")]
    [InlineData(false, "P0:[A B],[C D]")]
    public void A_stack_dragged_by_its_header_moves_whole_and_never_inside_another(bool leftHalf, string expected)
    {
        var p = Make(["A+B", "C+D"]);
        // Over the other stack's middle, and over one of its rows: beside it by the half, never in it.
        var middle = TileHit.On(0, 0, leftHalf ? SW / 2 - 10 : SW / 2 + 10, SW);
        var plan = TileDrops.Plan(p, BuilderDrag.FromSlot(0, 1), middle);
        plan.Kind.Should().Be(leftHalf ? TileDropKind.InsertBefore : TileDropKind.InsertAfter);
        var onRow = TileHit.OnRow(0, 0, 0, middle.X, SW, 2, RH);
        TileDrops.Plan(p, BuilderDrag.FromSlot(0, 1), onRow).Kind.Should().Be(plan.Kind);
        TileDrops.Apply(p, BuilderDrag.FromSlot(0, 1), plan);
        AssertConsistent(p);
        Shape(p).Should().Be(expected);
        p.Stacks.Values.Should().OnlyContain(s => s.Tools.Count == 2);
    }

    [Fact]
    public void A_stack_on_the_middle_of_a_loose_tool_goes_beside_it()
    {
        var p = Make(["A", "B+C"]);
        Drop(p, BuilderDrag.FromSlot(0, 1), TileHit.On(0, 0, 30, W)).Plan.Kind.Should().Be(TileDropKind.InsertBefore);
        Shape(p).Should().Be("P0:[B C],A");
    }

    [Fact]
    public void A_stack_over_itself_is_not_accepted()
    {
        var p = Make(["A+B", "C"]);
        TileDrops.Plan(p, BuilderDrag.FromSlot(0, 0), TileHit.On(0, 0, 5, SW)).Kind.Should().Be(TileDropKind.None);
        TileDrops.Plan(p, BuilderDrag.FromSlot(0, 0), Row(0, 0, 1, upper: false)).Kind.Should().Be(TileDropKind.None);
    }

    [Fact]
    public void A_catalogue_command_onto_a_stack_joins_it_if_it_has_room()
    {
        var p = Make(["A+B"]);
        Drop(p, BuilderDrag.FromCatalog(Wall), Row(0, 0, 0, upper: false)).Result.Should().Be(StackResult.Done);
        Shape(p).Should().Be("P0:[A Wall B]");
        Drop(p, BuilderDrag.FromCatalog(Wall), TileHit.On(0, 0, SW / 2, SW)).Plan.Kind.Should().Be(TileDropKind.Full);
        Shape(p).Should().Be("P0:[A Wall B]");
    }

    [Fact]
    public void Tiles_move_between_panels()
    {
        var p = Make(["A", "B+C"], ["D", "E+F"]);
        Drop(p, BuilderDrag.FromSlot(0, 0), Middle(1, 0)).Result.Should().Be(StackResult.Done);
        Drop(p, BuilderDrag.FromSlot(0, 0, row: 0), Row(1, 1, 1, upper: false)).Result.Should().Be(StackResult.Done);
        Shape(p).Should().Be("P0:C | P1:[D A],[E F B]");
        Drop(p, BuilderDrag.FromSlot(1, 1), LeftEdge(0, 0, W)).Result.Should().Be(StackResult.Done);
        Shape(p).Should().Be("P0:[E F B],C | P1:[D A]");
    }

    [Fact]
    public void A_tile_over_itself_is_not_accepted()
    {
        var p = Make(["A", "B"]);
        TileDrops.Plan(p, BuilderDrag.FromSlot(0, 1), Middle(0, 1)).Kind.Should().Be(TileDropKind.None);
        TileDrops.Plan(p, BuilderDrag.FromSlot(0, 1), LeftEdge(0, 1)).Kind.Should().Be(TileDropKind.None);
    }

    // ── the card outside the tiles ──

    [Fact]
    public void A_drop_on_the_card_appends_to_the_panel()
    {
        var p = Make(["A", "B+C"], ["D"]);
        var plan = TileDrops.PlanCard(p, BuilderDrag.FromSlot(0, 1, row: 1), 0);
        plan.Kind.Should().Be(TileDropKind.Append);
        plan.Marker.Should().Be(DropMarker.Over);
        TileDrops.Apply(p, BuilderDrag.FromSlot(0, 1, row: 1), plan).Should().Be(StackResult.Done);
        Shape(p).Should().Be("P0:A,B,C | P1:D", "dragged out of its 2-tool stack: dissolved, C at the end");

        TileDrops.PlanCard(p, BuilderDrag.FromSlot(0, 2), 0).Kind.Should().Be(TileDropKind.None, "already last");
        var catalog = TileDrops.PlanCard(p, BuilderDrag.FromCatalog(Wall), 1);
        TileDrops.Apply(p, BuilderDrag.FromCatalog(Wall), catalog);
        Shape(p).Should().Be("P0:A,B,C | P1:D,Wall");
        AssertConsistent(p);
    }

    [Fact]
    public void Panels_stale_indices_and_no_drag_are_not_tile_drops()
    {
        var p = Make(["A"]);
        TileDrops.Plan(p, BuilderDrag.FromPanel(0), Middle(0, 0)).Should().Be(TileDrop.None);
        TileDrops.Plan(p, null, Middle(0, 0)).Should().Be(TileDrop.None);
        TileDrops.Plan(p, BuilderDrag.FromSlot(0, 7), Middle(0, 0)).Should().Be(TileDrop.None);
        TileDrops.Plan(p, BuilderDrag.FromCatalog(Wall), Middle(3, 0)).Should().Be(TileDrop.None);
        TileDrops.PlanCard(p, BuilderDrag.FromPanel(0), 0).Should().Be(TileDrop.None);
        TileDrops.PlanCard(p, BuilderDrag.FromCatalog(Wall), 4).Should().Be(TileDrop.None);
        TileDrops.SourceOf(BuilderDrag.FromPanel(0)).Should().BeNull();
        Shape(p).Should().Be("P0:A");
    }
}
