// rst-c's own (not vendored): the Builder's panel reorder arithmetic (spec #16, WU5, AC5), against
// profile_builder.html's card handlers (2346-2358). The catalogue and slot drops on cards and tiles
// follow the stack rules since feature #18 (WU3): TileDropsTests.

using FluentAssertions;
using RST.Core.Builder;
using RST.Core.Profiles;
using Xunit;

namespace RST.Tests.Builder;

public class PanelDropsTests
{
    private static Profile Make(params string[][] panels) => new()
    {
        Panels = panels.Select((slots, i) => new Panel
        {
            Name = "P" + i,
            Slots = slots.Select(s => new Slot { SlotType = "tool", Name = s, CommandId = s }).ToList(),
        }).ToList(),
    };

    private static string Shape(Profile p) => string.Join(" | ", p.Panels.Select(x => x.Name + ":" + string.Join(",", x.Slots.Select(s => s.Name))));

    private static readonly CatalogCommand Wall = new("ID_WALL", "Wall", "Native", "Architecture", "Build");

    // ── panel reorder (2350-2358) ──

    [Theory]
    [InlineData(0, 2, true, "P1: | P0: | P2:", 1)]   // down, before: index shifts left
    [InlineData(0, 2, false, "P1: | P2: | P0:", 2)]  // down, after
    [InlineData(2, 0, true, "P2: | P0: | P1:", 0)]   // up, before
    [InlineData(2, 0, false, "P0: | P2: | P1:", 1)]  // up, after
    [InlineData(0, 1, false, "P1: | P0: | P2:", 1)]  // onto the neighbour's lower half
    [InlineData(1, 0, true, "P1: | P0: | P2:", 0)]
    public void A_panel_lands_before_or_after_the_target_card(int from, int target, bool upper, string expected, int selected)
    {
        var p = Make([], [], []);
        PanelDrops.DropOnPanel(p, BuilderDrag.FromPanel(from), target, upper).Should().Be(selected);
        Shape(p).Should().Be(expected);
    }

    [Fact]
    public void A_panel_dropped_on_itself_does_nothing()
    {
        var p = Make([], []);
        PanelDrops.DropOnPanel(p, BuilderDrag.FromPanel(1), 1, true).Should().BeNull();
        Shape(p).Should().Be("P0: | P1:");
    }

    [Fact]
    public void Only_a_panel_reorders_panels()
    {
        var p = Make(["a"], ["b"]);
        PanelDrops.DropOnPanel(p, BuilderDrag.FromCatalog(Wall), 1, true).Should().BeNull("a command follows TileDrops");
        PanelDrops.DropOnPanel(p, BuilderDrag.FromSlot(0, 0), 1, true).Should().BeNull("a tile follows TileDrops");
        Shape(p).Should().Be("P0:a | P1:b");
    }

    [Fact]
    public void Markers_follow_the_page()
    {
        PanelDrops.CardMarker(BuilderDrag.FromPanel(0), 1, true).Should().Be(DropMarker.Before);
        PanelDrops.CardMarker(BuilderDrag.FromPanel(0), 1, false).Should().Be(DropMarker.After);
        PanelDrops.CardMarker(BuilderDrag.FromPanel(1), 1, true).Should().Be(DropMarker.None);
        PanelDrops.CardMarker(BuilderDrag.FromCatalog(Wall), 1, true).Should().Be(DropMarker.None);
        PanelDrops.CardMarker(BuilderDrag.FromSlot(1, 0), 1, true).Should().Be(DropMarker.None);
        PanelDrops.CardMarker(null, 1, true).Should().Be(DropMarker.None);
    }

    [Fact]
    public void Stale_indices_change_nothing()
    {
        var p = Make(["a"]);
        PanelDrops.DropOnPanel(p, BuilderDrag.FromPanel(5), 0, true).Should().BeNull();
        PanelDrops.DropOnPanel(p, BuilderDrag.FromPanel(0), 3, true).Should().BeNull();
        Shape(p).Should().Be("P0:a");
    }
}
