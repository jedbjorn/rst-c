// rst-c's own (not vendored): drag and drop onto the panel cards' tiles (feature #18, spec #19,
// WU3; the stack rules of decisions #18 and #19). The window reports where the pointer is over a
// tile, a stack group or a stack's row; Plan turns that into what a drop would do and the marker
// that shows it, and Apply runs it through the PanelStacks operations (which own the model):
//   - the middle of a loose tool: a new stack of both (the dropped tool second); its leading or
//     trailing edge: inserted before or after it on the panel;
//   - a stack's row: the tool joins the stack before or after that row (a row of the same stack
//     moves within it); the stack's own middle (its header and padding): joins at the end; a full
//     stack refuses a fourth tool (the Refused marker, "A stack holds 2–3 tools. This one is
//     full."); the stack's leading or trailing edge: inserted before or after the stack;
//   - a whole stack (dragged by its header) never goes inside another stack: over a tile, a stack
//     or a row it is inserted before or after by the half it is over;
//   - the card outside the tiles: appended to the panel's end;
//   - a loose tool or a whole stack dropped where it already is (its neighbour's adjacent edge or
//     half) is not accepted, as a stack row's is (WU3 review N3);
//   - a drag or a target that is no longer where the card showed it (the source slot and the
//     expected target are checked by reference) is Stale: nothing changes (WU3 review N1).
// Tiles are laid out left to right, so the edges are the leading and trailing edges of a tile or a
// stack group (a quarter of its width, at most 24 px); a stack's rows are stacked, so before and
// after a row are its upper and lower halves. A catalogue command (and, since WU4, a Custom Tools
// row) becomes a fresh slot on every drop (the operations find slots by reference: WU1 review F3).

using RST.Core.Profiles;

namespace RST.Core.Builder;

/// <summary>Which part of a tile or a stack group the pointer is over, across its width.</summary>
public enum TileZone
{
    /// <summary>The leading edge: insert before it.</summary>
    Before,

    /// <summary>The middle: stack on a tool, join a stack.</summary>
    Middle,

    /// <summary>The trailing edge: insert after it.</summary>
    After,
}

/// <summary>What a drop on a tile does.</summary>
public enum TileDropKind
{
    /// <summary>Not accepted (a tile over itself, a panel drag, a stale index).</summary>
    None,

    /// <summary>A new stack of the target tool and the dropped one (<see cref="PanelStacks.StackOnTool"/>).</summary>
    MakeStack,

    /// <summary>The tool joins the target stack (<see cref="PanelStacks.JoinStack"/>).</summary>
    JoinStack,

    /// <summary>The target stack is full: refused, nothing changes.</summary>
    Full,

    /// <summary>Inserted before the target slot (<see cref="PanelStacks.InsertBeside"/>).</summary>
    InsertBefore,

    /// <summary>Inserted after the target slot.</summary>
    InsertAfter,

    /// <summary>Appended to the panel (a drop on the card outside the tiles, <see cref="PanelStacks.Append"/>).</summary>
    Append,

    /// <summary>
    /// The dragged slot or the target is no longer where the card showed it (the working copy changed
    /// underneath): refused, nothing changes; the cards should be redrawn (WU3 review N1).
    /// </summary>
    Stale,
}

/// <summary>
/// Where the pointer is: the panel and slot of a loose tool or a stack, and for a stack's row its
/// index (else -1). <paramref name="X"/> is across the tile or the whole stack group
/// (<paramref name="Width"/> wide); <paramref name="Y"/> is down the row (<paramref name="Height"/>
/// tall), used only for a row. <paramref name="Expected"/> is the slot the card showed at that place
/// (the loose tool or the stack slot): when given, a plan checks it is still there (WU3 review N1).
/// </summary>
public readonly record struct TileHit(int Panel, int Slot, int Row, double X, double Width, double Y = 0, double Height = 0, Slot? Expected = null)
{
    /// <summary>Over a loose tool or a stack group (not one of its rows).</summary>
    public static TileHit On(int panel, int slot, double x, double width, Slot? expected = null) => new(panel, slot, -1, x, width, Expected: expected);

    /// <summary>Over row <paramref name="row"/> of a stack: <paramref name="x"/> across the group, <paramref name="y"/> down the row.</summary>
    public static TileHit OnRow(int panel, int slot, int row, double x, double width, double y, double height, Slot? expected = null) =>
        new(panel, slot, row, x, width, y, height, expected);
}

/// <summary>
/// A planned drop: what it does to which slot (<see cref="At"/> is the row a join goes before; null
/// joins at the end) and the marker that shows it, on the slot (<see cref="MarkerRow"/> -1) or on
/// one of its rows.
/// </summary>
public readonly record struct TileDrop(TileDropKind Kind, int Panel, int Slot, int? At, DropMarker Marker, int MarkerRow = -1)
{
    public static readonly TileDrop None = new(TileDropKind.None, -1, -1, null, DropMarker.None);

    public static readonly TileDrop Stale = new(TileDropKind.Stale, -1, -1, null, DropMarker.None);

    /// <summary>The drop would change the profile (Full, None and Stale do not).</summary>
    public bool Accepted => Kind is not (TileDropKind.None or TileDropKind.Full or TileDropKind.Stale);
}

public static class TileDrops
{
    /// <summary>The edge zones: a quarter of the tile's width…</summary>
    public const double EdgeFraction = 0.25;

    /// <summary>…but at most this wide (a stack group is twice a tile's width).</summary>
    public const double MaxEdge = 24;

    /// <summary>Which zone <paramref name="x"/> falls in across something <paramref name="width"/> wide.</summary>
    public static TileZone ZoneOf(double x, double width)
    {
        if (width <= 0) return TileZone.Middle;
        var edge = Math.Min(width * EdgeFraction, MaxEdge);
        return x < edge ? TileZone.Before : x > width - edge ? TileZone.After : TileZone.Middle;
    }

    /// <summary>
    /// What the drag drops: a fresh tool slot for a catalogue command or a Custom Tools row (new on
    /// every call), the tile it came from for a slot drag, null for a panel drag.
    /// </summary>
    public static TileSource? SourceOf(BuilderDrag? drag) => drag?.Kind switch
    {
        DragKind.Catalog when drag.Command is not null => TileSource.New(BuilderRules.ToolSlot(drag.Command)),
        DragKind.CustomTool when drag.Tool is not null => TileSource.New(CustomTools.SlotFor(drag.Tool)),
        DragKind.Slot => TileSource.Tile(drag.PanelIndex, drag.SlotIndex, drag.Row),
        _ => null,
    };

    /// <summary>What a drop at <paramref name="hit"/> would do, and its marker.</summary>
    public static TileDrop Plan(Profile profile, BuilderDrag? drag, TileHit hit)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (drag is null || drag.Kind == DragKind.Panel) return TileDrop.None;
        if (!SlotAt(profile, hit.Panel, hit.Slot, out var target)) return hit.Expected is null ? TileDrop.None : TileDrop.Stale;
        if (hit.Expected is { } expected && !ReferenceEquals(expected, target)) return TileDrop.Stale;
        if (!Dragged(profile, drag, out var moving, out var wholeStack)) return drag.Source is null ? TileDrop.None : TileDrop.Stale;
        if (drag.Source is { } source && !ReferenceEquals(source, moving)) return TileDrop.Stale;

        var zone = ZoneOf(hit.X, hit.Width);
        var half = hit.X < hit.Width / 2 ? TileZone.Before : TileZone.After;
        var sameSlot = drag.Kind == DragKind.Slot && drag.PanelIndex == hit.Panel && drag.SlotIndex == hit.Slot;

        // A whole stack goes beside, never inside: by the half it is over.
        if (wholeStack) return sameSlot ? TileDrop.None : Insert(drag, hit, half);

        if (PanelStacks.IsTool(target))
        {
            if (hit.Row >= 0) return TileDrop.None;
            if (ReferenceEquals(moving, target)) return TileDrop.None;
            return zone == TileZone.Middle
                ? new TileDrop(TileDropKind.MakeStack, hit.Panel, hit.Slot, null, DropMarker.Over)
                : Insert(drag, hit, zone);
        }

        // A stack: its edges insert beside it; a row places the tool before or after that row; the rest joins at the end.
        if (zone != TileZone.Middle) return Insert(drag, hit, zone);
        var rows = PanelStacks.ToolsOf(profile, target);
        var from = IndexOf(rows, moving);
        if (hit.Row >= 0 && hit.Row < rows.Count)
        {
            var before = hit.Y < hit.Height / 2;
            var at = hit.Row + (before ? 0 : 1);
            if (from >= 0)
            {
                var to = from < at ? at - 1 : at;
                if (to == from) return TileDrop.None; // back where it is
            }
            else if (rows.Count >= PanelStacks.MaxTools)
            {
                return Refuse(hit);
            }
            return new TileDrop(TileDropKind.JoinStack, hit.Panel, hit.Slot, at, before ? DropMarker.Before : DropMarker.After, hit.Row);
        }
        if (from >= 0) return from == rows.Count - 1 ? TileDrop.None : new TileDrop(TileDropKind.JoinStack, hit.Panel, hit.Slot, null, DropMarker.Over);
        if (rows.Count >= PanelStacks.MaxTools) return Refuse(hit);
        return new TileDrop(TileDropKind.JoinStack, hit.Panel, hit.Slot, null, DropMarker.Over);
    }

    /// <summary>A drop on panel card <paramref name="panel"/> outside its tiles: a tool or a stack goes to its end.</summary>
    public static TileDrop PlanCard(Profile profile, BuilderDrag? drag, int panel)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (drag is null || drag.Kind == DragKind.Panel || panel < 0 || panel >= profile.Panels.Count) return TileDrop.None;
        if (!Dragged(profile, drag, out var moving, out _)) return drag.Source is null ? TileDrop.None : TileDrop.Stale;
        if (drag.Source is { } source && !ReferenceEquals(source, moving)) return TileDrop.Stale;
        // Already the panel's last slot: nothing to do.
        if (drag.Kind == DragKind.Slot && drag.Row < 0 && drag.PanelIndex == panel && drag.SlotIndex == profile.Panels[panel].Slots.Count - 1)
            return TileDrop.None;
        return new TileDrop(TileDropKind.Append, panel, -1, null, DropMarker.Over);
    }

    /// <summary>Runs a planned drop through the stack operations. Full and None change nothing.</summary>
    public static StackResult Apply(Profile profile, BuilderDrag drag, TileDrop plan)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(drag);
        if (plan.Kind == TileDropKind.Full) return StackResult.Full;
        if (SourceOf(drag) is not { } source) return StackResult.Unchanged;
        return plan.Kind switch
        {
            TileDropKind.MakeStack => PanelStacks.StackOnTool(profile, source, plan.Panel, plan.Slot),
            TileDropKind.JoinStack => PanelStacks.JoinStack(profile, source, plan.Panel, plan.Slot, plan.At),
            TileDropKind.InsertBefore => PanelStacks.InsertBeside(profile, source, plan.Panel, plan.Slot, after: false),
            TileDropKind.InsertAfter => PanelStacks.InsertBeside(profile, source, plan.Panel, plan.Slot, after: true),
            TileDropKind.Append => PanelStacks.Append(profile, source, plan.Panel),
            _ => StackResult.Unchanged,
        };
    }

    /// <summary>
    /// Inserted before or after the target slot. A loose tool or a whole stack dropped on its
    /// neighbour's adjacent edge (or half) would land back where it is: not accepted, no marker
    /// (WU3 review N3), as for a stack row.
    /// </summary>
    private static TileDrop Insert(BuilderDrag drag, TileHit hit, TileZone side)
    {
        var after = side != TileZone.Before;
        if (drag.Kind == DragKind.Slot && drag.Row < 0 && drag.PanelIndex == hit.Panel)
        {
            var at = hit.Slot + (after ? 1 : 0);
            if (drag.SlotIndex < at) at--;
            if (at == drag.SlotIndex) return TileDrop.None;
        }
        return after
            ? new TileDrop(TileDropKind.InsertAfter, hit.Panel, hit.Slot, null, DropMarker.After)
            : new TileDrop(TileDropKind.InsertBefore, hit.Panel, hit.Slot, null, DropMarker.Before);
    }

    private static TileDrop Refuse(TileHit hit) => new(TileDropKind.Full, hit.Panel, hit.Slot, null, DropMarker.Refused);

    /// <summary>The dragged slot (a new tool for a catalogue command or a Custom Tools row), and whether it is a whole stack.</summary>
    private static bool Dragged(Profile profile, BuilderDrag drag, out Slot? moving, out bool wholeStack)
    {
        moving = null;
        wholeStack = false;
        switch (drag.Kind)
        {
            case DragKind.Catalog:
                return drag.Command is not null;
            case DragKind.CustomTool:
                return drag.Tool is not null;
            case DragKind.Slot when SlotAt(profile, drag.PanelIndex, drag.SlotIndex, out var slot):
                if (drag.Row < 0)
                {
                    moving = slot;
                    wholeStack = PanelStacks.IsStack(slot);
                    return true;
                }
                var rows = PanelStacks.ToolsOf(profile, slot);
                if (drag.Row >= rows.Count) return false;
                moving = rows[drag.Row];
                return true;
            default:
                return false;
        }
    }

    private static int IndexOf(IReadOnlyList<Slot> rows, Slot? slot)
    {
        if (slot is null) return -1;
        for (var i = 0; i < rows.Count; i++)
            if (ReferenceEquals(rows[i], slot)) return i;
        return -1;
    }

    private static bool SlotAt(Profile profile, int panel, int slot, out Slot found)
    {
        found = null!;
        if (panel < 0 || panel >= profile.Panels.Count) return false;
        var slots = profile.Panels[panel].Slots;
        if (slot < 0 || slot >= slots.Count || slots[slot] is null) return false;
        found = slots[slot];
        return true;
    }
}
