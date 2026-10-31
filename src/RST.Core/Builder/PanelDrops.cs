// rst-c's own (not vendored): the Builder's drag-and-drop rules (spec #16, WU5), a port of the
// index arithmetic in profile_builder.html renderCanvas' card handlers (2346-2376): a panel dropped
// on another panel's card lands before or after it (the cursor's half), the target index shifting
// left by one when the panel came from above; it becomes the selection.
// Feature #18 (spec #19, WU3): the panel cards hold tiles and stacks, so a dropped command or tile
// follows the stack rules in TileDrops (PanelStacks does the move); this file keeps the drag
// descriptor, the markers and the panel reorder. The page's slot-on-slot arithmetic (makeSlotEl,
// 2471-2504) is retired with the slot rows.

using RST.Core.Profiles;

namespace RST.Core.Builder;

/// <summary>
/// What is being dragged: a catalogue command, a slot (a loose tool, a whole stack by its header, or
/// a stack's row) or a panel (its header).
/// </summary>
public enum DragKind
{
    Catalog,
    Slot,
    Panel,
}

/// <summary>
/// The drag descriptor (the page's <c>drag</c> variable): the kind and its source. A slot drag names
/// the panel and slot, for a tool inside a stack its <paramref name="Row"/> (else -1), and the
/// <paramref name="Source"/> slot it was started from (the loose tool, the stack slot, or the
/// stack's tool): a drop checks it is still at that place before it moves anything (WU3 review N1).
/// </summary>
public sealed record BuilderDrag(DragKind Kind, CatalogCommand? Command = null, int PanelIndex = -1, int SlotIndex = -1, int Row = -1, Slot? Source = null)
{
    public static BuilderDrag FromCatalog(CatalogCommand command) => new(DragKind.Catalog, command ?? throw new ArgumentNullException(nameof(command)));

    /// <summary>A loose tool or a whole stack (<paramref name="row"/> -1), or a stack's row; <paramref name="source"/> is that slot as the card showed it.</summary>
    public static BuilderDrag FromSlot(int panelIndex, int slotIndex, int row = -1, Slot? source = null) =>
        new(DragKind.Slot, null, panelIndex, slotIndex, row, source);

    public static BuilderDrag FromPanel(int panelIndex) => new(DragKind.Panel, null, panelIndex);
}

/// <summary>Where a drag may land on a target, and how the target marks it.</summary>
public enum DropMarker
{
    None,

    /// <summary>A line before the target: above a card or a stack row, left of a tile or a stack (<c>drop-before</c>).</summary>
    Before,

    /// <summary>A line after the target: below a card or a stack row, right of a tile or a stack (<c>drop-after</c>).</summary>
    After,

    /// <summary>The whole target highlighted (<c>drag-over</c>): a card takes the drop; on a tile a stack forms; a stack takes the tool.</summary>
    Over,

    /// <summary>A full stack refuses the tool (feature #18, decision #19): the refusal marker.</summary>
    Refused,
}

public static class PanelDrops
{
    /// <summary>
    /// The card's dragover for a panel: a panel (not itself) marks before or after by the cursor's
    /// half. Anything else is not a panel reorder (tools and stacks follow <see cref="TileDrops"/>).
    /// </summary>
    public static DropMarker CardMarker(BuilderDrag? drag, int targetPanel, bool upperHalf) => drag?.Kind switch
    {
        DragKind.Panel when drag.PanelIndex != targetPanel => upperHalf ? DropMarker.Before : DropMarker.After,
        _ => DropMarker.None,
    };

    /// <summary>
    /// A panel dropped on panel card <paramref name="targetPanel"/>. Returns the panel to select, or
    /// null when the drop changed nothing (not a panel drag, the panel itself, a stale index).
    /// </summary>
    public static int? DropOnPanel(Profile profile, BuilderDrag drag, int targetPanel, bool upperHalf)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(drag);
        var panels = profile.Panels;
        if (drag.Kind != DragKind.Panel || drag.PanelIndex == targetPanel) return null;
        if (!InRange(targetPanel, panels.Count) || !InRange(drag.PanelIndex, panels.Count)) return null;
        var to = targetPanel + (upperHalf ? 0 : 1);
        var moved = panels[drag.PanelIndex];
        panels.RemoveAt(drag.PanelIndex);
        if (drag.PanelIndex < to) to--;
        panels.Insert(to, moved);
        return to;
    }

    private static bool InRange(int index, int count) => index >= 0 && index < count;
}
