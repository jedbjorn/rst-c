// SlotRegistry.cs — runtime mapping from slot index to its target.
//
// PushButtonData binds a button to a fixed IExternalCommand class. To
// route N buttons to N different Revit commands without N hand-coded
// command classes, we mint a pool of N empty Slot## classes (see
// Slots.cs) that all forward to SlotCommandBase.Execute, which looks
// up its target by Index here.
//
// Filled by RibbonBuilder at OnStartup; read by Slot## instances each
// time Revit invokes their Execute().

using System;

namespace RST.Engine.Ribbon;

public enum SlotKind { Command, Url }

public sealed record SlotTarget(SlotKind Kind, string Payload, string DisplayName);

public static class SlotRegistry
{
    /// <summary>Upper bound on profile button count. Bump and regenerate Slots.cs when raised.</summary>
    public const int Capacity = 64;

    private static readonly SlotTarget?[] Slots = new SlotTarget?[Capacity];

    public static void Set(int index, SlotTarget target)
    {
        if (index < 0 || index >= Capacity)
            throw new ArgumentOutOfRangeException(nameof(index), $"slot index out of range (0..{Capacity - 1})");
        Slots[index] = target;
    }

    public static SlotTarget? Get(int index) =>
        index >= 0 && index < Capacity ? Slots[index] : null;

    public static void Clear()
    {
        for (int i = 0; i < Slots.Length; i++) Slots[i] = null;
    }
}
