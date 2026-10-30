// rst-c's own (not vendored): the Builder's drag-and-drop rules (spec #16, WU5), a port of the
// index arithmetic in profile_builder.html renderCanvas' card handlers (2346-2376) and
// makeSlotEl's slot handlers (2471-2504). Behavior is unchanged:
//   - a panel dropped on another panel's card lands before or after it (the cursor's half), the
//     target index shifting left by one when the panel came from above; it becomes the selection;
//   - a catalogue command dropped on a card is appended to that panel as a tool slot;
//   - a slot dropped on another panel's card moves to that panel's end (on its own card: nothing);
//   - a catalogue command dropped on a slot is inserted before or after that slot;
//   - a slot dropped on a slot moves before or after it, in the same panel or across panels, the
//     insertion index shifting left by one when it came from above in the same panel; dropping a
//     slot on itself does nothing.
// Each drop that changes the profile selects the target panel.
// Stacks (feature #15, spec #17): a stack card dragged onto a panel card is placed at that panel's
// end, and onto a slot row before or after it (a { type: "stack", name } slot), like a catalogue
// command; a catalogue command dropped on a stack card goes into the stack (StackEditing.AddTool).

using RST.Core.Profiles;

namespace RST.Core.Builder;

/// <summary>What is being dragged: a catalogue command, a slot (panel and slot index) or a panel (its header).</summary>
public enum DragKind
{
    Catalog,
    Slot,
    Panel,

    /// <summary>A stack card from the Stacks section (placed into a panel as a stack slot).</summary>
    Stack,
}

/// <summary>The drag descriptor (the page's <c>drag</c> variable): the kind and its source.</summary>
public sealed record BuilderDrag(DragKind Kind, CatalogCommand? Command = null, int PanelIndex = -1, int SlotIndex = -1, string? StackName = null)
{
    public static BuilderDrag FromStack(string stackName) => new(DragKind.Stack, StackName: stackName ?? throw new ArgumentNullException(nameof(stackName)));

    public static BuilderDrag FromCatalog(CatalogCommand command) => new(DragKind.Catalog, command ?? throw new ArgumentNullException(nameof(command)));

    public static BuilderDrag FromSlot(int panelIndex, int slotIndex) => new(DragKind.Slot, null, panelIndex, slotIndex);

    public static BuilderDrag FromPanel(int panelIndex) => new(DragKind.Panel, null, panelIndex);
}

/// <summary>Where a drag may land on a target, and how the target marks it.</summary>
public enum DropMarker
{
    None,

    /// <summary>A line above the target (<c>drop-before</c>).</summary>
    Before,

    /// <summary>A line below the target (<c>drop-after</c>).</summary>
    After,

    /// <summary>The whole card highlighted (<c>drag-over</c>).</summary>
    Over,
}

public static class PanelDrops
{
    /// <summary>
    /// The card's dragover: a panel (not itself) marks before or after by the cursor's half; a
    /// catalogue command, a slot or a stack highlights the card; anything else is not accepted.
    /// </summary>
    public static DropMarker CardMarker(BuilderDrag? drag, int targetPanel, bool upperHalf) => drag?.Kind switch
    {
        DragKind.Panel when drag.PanelIndex != targetPanel => upperHalf ? DropMarker.Before : DropMarker.After,
        DragKind.Catalog or DragKind.Slot or DragKind.Stack => DropMarker.Over,
        _ => DropMarker.None,
    };

    /// <summary>A slot's dragover: a slot, a catalogue command or a stack marks before or after by the cursor's half.</summary>
    public static DropMarker SlotMarker(BuilderDrag? drag, bool upperHalf) => drag?.Kind switch
    {
        DragKind.Slot or DragKind.Catalog or DragKind.Stack => upperHalf ? DropMarker.Before : DropMarker.After,
        _ => DropMarker.None,
    };

    /// <summary>
    /// A drop on panel card <paramref name="targetPanel"/>. Returns the panel to select, or null
    /// when the drop changed nothing.
    /// </summary>
    public static int? DropOnPanel(Profile profile, BuilderDrag drag, int targetPanel, bool upperHalf)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(drag);
        var panels = profile.Panels;
        if (targetPanel < 0 || targetPanel >= panels.Count) return null;
        switch (drag.Kind)
        {
            case DragKind.Panel when drag.PanelIndex != targetPanel && InRange(drag.PanelIndex, panels.Count):
            {
                var to = targetPanel + (upperHalf ? 0 : 1);
                var moved = panels[drag.PanelIndex];
                panels.RemoveAt(drag.PanelIndex);
                if (drag.PanelIndex < to) to--;
                panels.Insert(to, moved);
                return to;
            }
            case DragKind.Catalog when drag.Command is not null:
                panels[targetPanel].Slots.Add(BuilderRules.ToolSlot(drag.Command));
                return targetPanel;
            case DragKind.Stack when drag.StackName is { } stack && profile.Stacks.ContainsKey(stack):
                panels[targetPanel].Slots.Add(StackEditing.StackSlot(stack));
                return targetPanel;
            case DragKind.Slot when drag.PanelIndex != targetPanel && SlotInRange(profile, drag):
            {
                var source = panels[drag.PanelIndex].Slots;
                var slot = source[drag.SlotIndex];
                source.RemoveAt(drag.SlotIndex);
                panels[targetPanel].Slots.Add(slot);
                return targetPanel;
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// A drop on slot <paramref name="targetSlot"/> of panel <paramref name="targetPanel"/>.
    /// Returns the panel to select, or null when the drop changed nothing.
    /// </summary>
    public static int? DropOnSlot(Profile profile, BuilderDrag drag, int targetPanel, int targetSlot, bool upperHalf)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(drag);
        var panels = profile.Panels;
        if (targetPanel < 0 || targetPanel >= panels.Count) return null;
        var insertAt = targetSlot + (upperHalf ? 0 : 1);
        switch (drag.Kind)
        {
            case DragKind.Catalog when drag.Command is not null:
                panels[targetPanel].Slots.Insert(Math.Clamp(insertAt, 0, panels[targetPanel].Slots.Count), BuilderRules.ToolSlot(drag.Command));
                return targetPanel;
            case DragKind.Stack when drag.StackName is { } stack && profile.Stacks.ContainsKey(stack):
                panels[targetPanel].Slots.Insert(Math.Clamp(insertAt, 0, panels[targetPanel].Slots.Count), StackEditing.StackSlot(stack));
                return targetPanel;
            case DragKind.Slot when SlotInRange(profile, drag):
            {
                if (drag.PanelIndex == targetPanel && drag.SlotIndex == targetSlot) return null;
                var source = panels[drag.PanelIndex].Slots;
                var slot = source[drag.SlotIndex];
                source.RemoveAt(drag.SlotIndex);
                // Removed from the same panel above the insertion point: the index shifts left by one.
                if (drag.PanelIndex == targetPanel && drag.SlotIndex < insertAt) insertAt--;
                var target = panels[targetPanel].Slots;
                target.Insert(Math.Clamp(insertAt, 0, target.Count), slot);
                return targetPanel;
            }
            default:
                return null;
        }
    }

    private static bool InRange(int index, int count) => index >= 0 && index < count;

    private static bool SlotInRange(Profile profile, BuilderDrag drag) =>
        InRange(drag.PanelIndex, profile.Panels.Count) && InRange(drag.SlotIndex, profile.Panels[drag.PanelIndex].Slots.Count);
}
