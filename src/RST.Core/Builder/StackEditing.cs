// rst-c's own (not vendored): the Builder's stack authoring rules (feature #15, spec #17, decision
// #16 item 5), a port of profile_builder.html's stack functions (c5b0004a, 2017-2066): addStack
// ("Stack N"), renameStack (a duplicate name is refused, and every slot that refers to the stack is
// rewritten), deleteStack (the slots that refer to it go too), addToolToStack and
// removeToolFromStack, plus addStackRefToSelectedPanel. Changes from the page: a stack holds 2 or 3
// tools while it is authored (a fourth is refused; fewer than 2 blocks Save), its tools keep
// sourceTab, sourcePanel and iconFile like panel tools (BuilderRules.ToolSlot), a renamed stack
// keeps its place in the stacks' order, and its tools can be reordered.

using RST.Core.Profiles;
using RST.Core.Ribbon;

namespace RST.Core.Builder;

/// <summary>What a stack rename did.</summary>
public enum StackRename
{
    /// <summary>Renamed; the slots that referred to it now use the new name.</summary>
    Renamed,

    /// <summary>The new name (trimmed) is the old one, or blank: nothing changed.</summary>
    Unchanged,

    /// <summary>Another stack has that name: refused.</summary>
    Duplicate,

    /// <summary>No stack has the old name.</summary>
    NotFound,
}

public static class StackEditing
{
    /// <summary>The most tools a stack holds (Revit's 2-3 stacked items).</summary>
    public const int MaxTools = RibbonLayoutPlanner.MaxStackItems;

    /// <summary>The fewest tools a stack must hold to be saved.</summary>
    public const int MinTools = RibbonLayoutPlanner.MinStackItems;

    /// <summary>addStack: the first free "Stack N" (N from 1).</summary>
    public static string NewStackName(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var n = 1;
        while (profile.Stacks.ContainsKey("Stack " + n)) n++;
        return "Stack " + n;
    }

    /// <summary>Adds an empty stack named "Stack N" and returns its name.</summary>
    public static string AddStack(Profile profile)
    {
        var name = NewStackName(profile);
        profile.Stacks[name] = new Stack { Tools = [] };
        return name;
    }

    /// <summary>
    /// renameStack: trims the new name; blank or unchanged does nothing, a name another stack has is
    /// refused; otherwise the stack moves to the new name at the same place in the order, and every
    /// panel slot that referred to the old name is rewritten.
    /// </summary>
    public static StackRename Rename(Profile profile, string oldName, string? newName, out string finalName)
    {
        ArgumentNullException.ThrowIfNull(profile);
        finalName = oldName;
        if (!profile.Stacks.ContainsKey(oldName)) return StackRename.NotFound;
        var name = (newName ?? "").Trim();
        if (name.Length == 0 || name == oldName) return StackRename.Unchanged;
        if (profile.Stacks.ContainsKey(name)) return StackRename.Duplicate;

        var reordered = new Dictionary<string, Stack>(profile.Stacks.Count);
        foreach (var (key, value) in profile.Stacks) reordered[key == oldName ? name : key] = value;
        profile.Stacks = reordered;
        foreach (var panel in profile.Panels)
            foreach (var slot in panel.Slots)
                if (slot is { SlotType: "stack" } && slot.Name == oldName) slot.Name = name;
        finalName = name;
        return StackRename.Renamed;
    }

    /// <summary>deleteStack: removes the stack and every panel slot that refers to it. Returns how many slots went.</summary>
    public static int Delete(Profile profile, string name)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Stacks.Remove(name);
        var removed = 0;
        foreach (var panel in profile.Panels)
            removed += panel.Slots.RemoveAll(s => s is { SlotType: "stack" } && s.Name == name);
        return removed;
    }

    /// <summary>True when the stack already holds <see cref="MaxTools"/> tools (a fourth is refused).</summary>
    public static bool IsFull(Stack stack) => (stack?.Tools.Count ?? 0) >= MaxTools;

    /// <summary>True when the stack holds fewer than <see cref="MinTools"/> tools (flagged; it blocks Save).</summary>
    public static bool IsUnderfilled(Stack stack) => (stack?.Tools.Count ?? 0) < MinTools;

    /// <summary>
    /// addToolToStack: appends the command as a tool slot (with its source tab and panel, as a panel
    /// tool). False, and nothing changes, when the stack is full or unknown.
    /// </summary>
    public static bool AddTool(Profile profile, string stackName, CatalogCommand command)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(command);
        if (!profile.Stacks.TryGetValue(stackName, out var stack) || IsFull(stack)) return false;
        stack.Tools.Add(BuilderRules.ToolSlot(command));
        return true;
    }

    /// <summary>removeToolFromStack.</summary>
    public static void RemoveTool(Profile profile, string stackName, int index)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Stacks.TryGetValue(stackName, out var stack) && index >= 0 && index < stack.Tools.Count)
            stack.Tools.RemoveAt(index);
    }

    /// <summary>Moves a tool up (-1) or down (+1) in its stack.</summary>
    public static void MoveTool(Profile profile, string stackName, int index, int delta)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!profile.Stacks.TryGetValue(stackName, out var stack)) return;
        var to = index + delta;
        if (index < 0 || index >= stack.Tools.Count || to < 0 || to >= stack.Tools.Count) return;
        var tool = stack.Tools[index];
        stack.Tools.RemoveAt(index);
        stack.Tools.Insert(to, tool);
    }

    /// <summary>The panel slot that places a stack: <c>{ type: "stack", name }</c>.</summary>
    public static Slot StackSlot(string name) => new() { SlotType = "stack", Name = name };

    /// <summary>True when some panel slot refers to the stack.</summary>
    public static bool IsPlaced(Profile profile, string name)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.Panels.Any(p => p.Slots.Any(s => s is { SlotType: "stack" } && s.Name == name));
    }

    /// <summary>The first stack (in order) with fewer than 2 tools, or null: Save is blocked while one exists.</summary>
    public static string? FirstUnderfilled(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        foreach (var (name, stack) in profile.Stacks)
            if (IsUnderfilled(stack)) return name;
        return null;
    }
}
