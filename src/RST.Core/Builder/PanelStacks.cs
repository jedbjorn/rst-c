// rst-c's own (not vendored): stack authoring on the Builder's panel cards (feature #18, spec #19,
// decisions #18, #19 and #28). A stack is panel configuration: a { type: "stack", name } slot on a
// panel and its 2-3 tools in Profile.Stacks under that name. The Builder names stacks itself
// ("Stack N") and keeps one stack per stack slot, so every operation here leaves the profile with
// no orphan stack, no shared stack and no reference to a missing one. The rules:
//   - a tool dropped on the middle of a loose tool makes a stack of both in the target's place,
//     the dropped tool second;
//   - a tool dropped on a stack joins it if it has room (a fourth is refused: Full);
//   - a tool or a whole stack dropped on the edge of a slot is inserted before or after it;
//   - a tool taken out of a 3-tool stack leaves 2; out of a 2-tool stack, the stack dissolves and
//     the remaining tool becomes a plain slot in its place;
//   - a whole stack never goes inside another stack (Refused);
//   - New Stack: 2-3 loose tools in pick order, in the first pick's place;
//   - Edit Stack: reorder, untick (back on the panel after the stack), add loose tools; fewer than
//     2 picks un-stacks;
//   - × on a stack un-stacks: its tools go back on the panel in order. Removing a stack never
//     removes its tools.
// Normalize repairs a profile the Builder opens (each case logged); SaveBlocker is the 4+ refusal.
// Read validation (ProfileSerializer.Validate) is not relaxed (decision #28).

using RST.Core.Loader;
using RST.Core.Profiles;

namespace RST.Core.Builder;

/// <summary>What a stack operation did.</summary>
public enum StackResult
{
    /// <summary>The profile changed.</summary>
    Done,

    /// <summary>Nothing to do (a slot dropped on itself, an edit that keeps the stack as it is).</summary>
    Unchanged,

    /// <summary>The stack already holds 3 tools: "A stack holds 2–3 tools. This one is full." Nothing changed.</summary>
    Full,

    /// <summary>Not allowed or out of range (a stack into a stack, a pick that is not a loose tool). Nothing changed.</summary>
    Refused,
}

/// <summary>A tile on a panel card: the panel, the slot, and for a tool inside a stack its row (else -1).</summary>
public readonly record struct TileRef(int Panel, int Slot, int Row = -1)
{
    /// <summary>True for a tool inside a stack.</summary>
    public bool InStack => Row >= 0;
}

/// <summary>What is dropped: a new tool slot (a catalogue command or a custom tool), or a tile already on a card.</summary>
public sealed record TileSource(Slot? NewTool, TileRef? From)
{
    /// <summary>A new tool: <see cref="BuilderRules.ToolSlot"/> or <see cref="CustomTools.NewSlot"/>.</summary>
    public static TileSource New(Slot tool) => new(tool ?? throw new ArgumentNullException(nameof(tool)), null);

    /// <summary>A tile on a card: a loose tool, a whole stack (row -1 on a stack slot) or a stack's row.</summary>
    public static TileSource Tile(int panel, int slot, int row = -1) => new(null, new TileRef(panel, slot, row));
}

/// <summary>A tool the stack dialog lists: a row of the stack being edited, or a loose tool on its panel (slot index).</summary>
public readonly record struct StackPick(bool Member, int Index)
{
    public static StackPick Row(int row) => new(true, row);

    public static StackPick Loose(int slot) => new(false, slot);
}

/// <summary>What <see cref="PanelStacks.Normalize"/> did: changed or not, and one note per repair (also logged).</summary>
public sealed record StackNormalizeReport(bool Changed, IReadOnlyList<string> Notes);

public static class PanelStacks
{
    /// <summary>The fewest tools a stack holds.</summary>
    public const int MinTools = 2;

    /// <summary>The most tools a stack holds (Revit's stacked items).</summary>
    public const int MaxTools = 3;

    /// <summary>The refusal a fourth tool gets.</summary>
    public const string FullMessage = "A stack holds 2–3 tools. This one is full.";

    public const string StackType = "stack";

    public const string ToolType = "tool";

    /// <summary>The first "Stack N" (N from 1) no stack in the profile has, compared case-insensitively.</summary>
    public static string NextStackName(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var taken = new HashSet<string>(profile.Stacks.Keys, StringComparer.OrdinalIgnoreCase);
        var n = 1;
        while (taken.Contains("Stack " + n)) n++;
        return "Stack " + n;
    }

    /// <summary>True for a stack slot (<c>type: "stack"</c>).</summary>
    public static bool IsStack(Slot? slot) => slot is { SlotType: StackType };

    /// <summary>True for a tool slot (<c>type: "tool"</c>).</summary>
    public static bool IsTool(Slot? slot) => slot is { SlotType: ToolType };

    /// <summary>The tools of a stack slot (empty when it is not a stack slot or names no stack).</summary>
    public static IReadOnlyList<Slot> ToolsOf(Profile profile, Slot slot)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return IsStack(slot) && profile.Stacks.TryGetValue(slot.Name ?? "", out var stack) && stack?.Tools is { } tools ? tools : [];
    }

    /// <summary>The slot indices of the panel's loose tools (the New Stack dialog's list).</summary>
    public static IReadOnlyList<int> LooseTools(Profile profile, int panel)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!InRange(panel, profile.Panels.Count)) return [];
        var slots = profile.Panels[panel].Slots;
        return Enumerable.Range(0, slots.Count).Where(i => IsTool(slots[i])).ToList();
    }

    /// <summary>
    /// A drop on the middle of the loose tool at <paramref name="panel"/>/<paramref name="slot"/>: a
    /// new stack of both, in the target's place, the dropped tool second. A whole stack is refused
    /// (stacks never nest), as is a target that is not a loose tool; a tool dropped on itself is
    /// Unchanged.
    /// </summary>
    public static StackResult StackOnTool(Profile profile, TileSource dropped, int panel, int slot)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(dropped);
        if (!SlotAt(profile, panel, slot, out var target) || !IsTool(target)) return StackResult.Refused;
        if (!ResolveTool(profile, dropped, out var tool)) return StackResult.Refused;
        if (ReferenceEquals(tool, target)) return StackResult.Unchanged;

        Detach(profile, dropped, target);
        var slots = profile.Panels[panel].Slots;
        var at = slots.IndexOf(target);
        var name = NextStackName(profile);
        profile.Stacks[name] = new Stack { Tools = [target, tool] };
        slots[at] = StackSlot(name);
        return StackResult.Done;
    }

    /// <summary>
    /// A drop on the stack at <paramref name="panel"/>/<paramref name="slot"/> (its middle, or a
    /// row's edge): the tool joins it at <paramref name="at"/> (null: the end), if it has room. A row
    /// of the same stack moves within it. A whole stack is refused.
    /// </summary>
    public static StackResult JoinStack(Profile profile, TileSource dropped, int panel, int slot, int? at = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(dropped);
        if (!SlotAt(profile, panel, slot, out var target) || !IsStack(target)) return StackResult.Refused;
        if (!profile.Stacks.TryGetValue(target.Name ?? "", out var stack) || stack?.Tools is null) return StackResult.Refused;
        if (!ResolveTool(profile, dropped, out var tool)) return StackResult.Refused;

        var tools = stack.Tools;
        var from = tools.IndexOf(tool);
        if (from >= 0)
        {
            // A row of this stack: a reorder.
            var to = Math.Clamp(at ?? tools.Count, 0, tools.Count);
            if (from < to) to--;
            if (to == from) return StackResult.Unchanged;
            tools.RemoveAt(from);
            tools.Insert(to, tool);
            return StackResult.Done;
        }
        if (tools.Count >= MaxTools) return StackResult.Full;

        // The insertion point is held by the tool it goes before, so a detach elsewhere cannot shift it.
        var before = at is { } i && i >= 0 && i < tools.Count ? tools[i] : null;
        Detach(profile, dropped, target);
        var index = before is null ? tools.Count : tools.IndexOf(before);
        tools.Insert(index, tool);
        return StackResult.Done;
    }

    /// <summary>
    /// A drop on the edge of the slot at <paramref name="panel"/>/<paramref name="slot"/> (a loose
    /// tool or a stack): the dropped tool or whole stack is inserted before or after it on the panel.
    /// A row taken out of a stack leaves it (a 2-tool stack dissolves in place).
    /// </summary>
    public static StackResult InsertBeside(Profile profile, TileSource dropped, int panel, int slot, bool after)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(dropped);
        if (!SlotAt(profile, panel, slot, out var target)) return StackResult.Refused;
        if (!Resolve(profile, dropped, out var moving)) return StackResult.Refused;
        if (ReferenceEquals(moving, target)) return StackResult.Unchanged;

        var anchor = Detach(profile, dropped, target);
        var slots = profile.Panels[panel].Slots;
        var index = slots.IndexOf(anchor);
        slots.Insert(index < 0 ? slots.Count : index + (after ? 1 : 0), moving);
        return StackResult.Done;
    }

    /// <summary>A drop on a panel card (not on a tile): the dropped tool or whole stack goes to the panel's end.</summary>
    public static StackResult Append(Profile profile, TileSource dropped, int panel)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(dropped);
        if (!InRange(panel, profile.Panels.Count)) return StackResult.Refused;
        if (!Resolve(profile, dropped, out var moving)) return StackResult.Refused;
        var slots = profile.Panels[panel].Slots;
        if (dropped.From is { InStack: false } from && from.Panel == panel && from.Slot == slots.Count - 1) return StackResult.Unchanged;
        Detach(profile, dropped, null);
        slots.Add(moving);
        return StackResult.Done;
    }

    /// <summary>
    /// + New Stack: the loose tools at <paramref name="picks"/> (slot indices, in pick order, 2-3,
    /// distinct) become a stack in the first pick's place.
    /// </summary>
    public static StackResult NewStack(Profile profile, int panel, IReadOnlyList<int> picks)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(picks);
        if (!InRange(panel, profile.Panels.Count)) return StackResult.Refused;
        if (picks.Count > MaxTools) return StackResult.Full;
        if (picks.Count < MinTools || picks.Distinct().Count() != picks.Count) return StackResult.Refused;
        var slots = profile.Panels[panel].Slots;
        if (picks.Any(i => !InRange(i, slots.Count) || !IsTool(slots[i]))) return StackResult.Refused;

        var tools = picks.Select(i => slots[i]).ToList();
        var name = NextStackName(profile);
        profile.Stacks[name] = new Stack { Tools = tools };
        slots[picks[0]] = StackSlot(name);
        foreach (var tool in tools.Skip(1)) slots.Remove(tool);
        return StackResult.Done;
    }

    /// <summary>
    /// ✎ on a stack, saved: the stack holds <paramref name="picks"/> in that order (its own rows and
    /// loose tools of its panel). Rows not picked go back on the panel right after the stack, in their
    /// order; picked loose tools leave their place. Fewer than 2 picks un-stacks it
    /// (<see cref="Unstack"/>); more than 3 is Full.
    /// </summary>
    public static StackResult EditStack(Profile profile, int panel, int slot, IReadOnlyList<StackPick> picks)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(picks);
        if (!SlotAt(profile, panel, slot, out var target) || !IsStack(target)) return StackResult.Refused;
        if (!profile.Stacks.TryGetValue(target.Name ?? "", out var stack) || stack?.Tools is null) return StackResult.Refused;
        if (picks.Count > MaxTools) return StackResult.Full;
        if (picks.Distinct().Count() != picks.Count) return StackResult.Refused;
        var slots = profile.Panels[panel].Slots;
        var rows = stack.Tools;
        foreach (var pick in picks)
        {
            var ok = pick.Member ? InRange(pick.Index, rows.Count) : InRange(pick.Index, slots.Count) && IsTool(slots[pick.Index]);
            if (!ok) return StackResult.Refused;
        }
        if (picks.Count < MinTools) return Unstack(profile, panel, slot);

        var chosen = picks.Select(p => p.Member ? rows[p.Index] : slots[p.Index]).ToList();
        if (chosen.SequenceEqual(rows)) return StackResult.Unchanged;
        var unticked = rows.Where(r => !chosen.Contains(r)).ToList();
        foreach (var loose in picks.Where(p => !p.Member).Select(p => slots[p.Index]).ToList()) slots.Remove(loose);
        stack.Tools = chosen;
        slots.InsertRange(slots.IndexOf(target) + 1, unticked);
        return StackResult.Done;
    }

    /// <summary>× on a stack: its tools go back on the panel in order, in its place; the stack is removed.</summary>
    public static StackResult Unstack(Profile profile, int panel, int slot)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!SlotAt(profile, panel, slot, out var target) || !IsStack(target)) return StackResult.Refused;
        var tools = ToolsOf(profile, target).ToList();
        var slots = profile.Panels[panel].Slots;
        slots.RemoveAt(slot);
        slots.InsertRange(slot, tools);
        profile.Stacks.Remove(target.Name ?? "");
        return StackResult.Done;
    }

    /// <summary>
    /// × on a stack row: that tool leaves the open profile; a stack left with one tool becomes that
    /// plain tool in its place (none: the slot goes).
    /// </summary>
    public static StackResult RemoveRow(Profile profile, int panel, int slot, int row)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!SlotAt(profile, panel, slot, out var target) || !IsStack(target)) return StackResult.Refused;
        if (!profile.Stacks.TryGetValue(target.Name ?? "", out var stack) || stack?.Tools is null || !InRange(row, stack.Tools.Count))
            return StackResult.Refused;
        stack.Tools.RemoveAt(row);
        Collapse(profile, profile.Panels[panel].Slots, target);
        return StackResult.Done;
    }

    /// <summary>
    /// Removes every tool slot <paramref name="match"/> selects, loose or in a stack (Custom Tools'
    /// remove everywhere). A stack left with one tool becomes that plain tool; with none, it goes.
    /// Returns how many tool slots were removed.
    /// </summary>
    public static int RemoveTools(Profile profile, Func<Slot, bool> match)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(match);
        var removed = 0;
        foreach (var panel in profile.Panels)
            removed += panel.Slots.RemoveAll(s => IsTool(s) && match(s));

        // Each stack once (a saved file may still share one between slots), then every slot that
        // refers to a stack left with fewer than 2 tools: one tool becomes a plain copy of it, none
        // removes the slot.
        var shrunk = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, stack) in profile.Stacks)
        {
            var n = stack?.Tools?.RemoveAll(t => IsTool(t) && match(t)) ?? 0;
            removed += n;
            if (n > 0 && stack!.Tools.Count < MinTools) shrunk.Add(name);
        }
        foreach (var name in shrunk)
        {
            var left = profile.Stacks[name].Tools;
            foreach (var panel in profile.Panels)
            {
                var slots = panel.Slots;
                for (var i = slots.Count - 1; i >= 0; i--)
                {
                    if (!IsStack(slots[i]) || slots[i].Name != name) continue;
                    if (left.Count == 1) slots[i] = CopySlot(left[0]);
                    else slots.RemoveAt(i);
                }
            }
            profile.Stacks.Remove(name);
        }
        return removed;
    }

    /// <summary>The stacks no panel slot refers to, removed. Returns their names in stack order.</summary>
    public static IReadOnlyList<string> PruneUnreferenced(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var referenced = new HashSet<string>(
            profile.Panels.SelectMany(p => p.Slots ?? []).Where(IsStack).Select(s => s.Name ?? ""), StringComparer.Ordinal);
        var orphans = profile.Stacks.Keys.Where(k => !referenced.Contains(k)).ToList();
        foreach (var name in orphans) profile.Stacks.Remove(name);
        return orphans;
    }

    /// <summary>
    /// The 4+ refusal (decision #28): a stack slot whose stack holds more than 3 tools blocks Save
    /// with a message naming the panel. Null when every placed stack fits.
    /// </summary>
    public static string? SaveBlocker(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        foreach (var panel in profile.Panels)
            foreach (var slot in panel.Slots ?? [])
            {
                var n = IsStack(slot) ? ToolsOf(profile, slot).Count : 0;
                if (n > MaxTools)
                    return $"Panel \"{panel.Name}\" has a stack of {n} tools. A stack holds 2–3 tools; move some out before saving.";
            }
        return null;
    }

    /// <summary>
    /// The normaliser every profile the Builder opens goes through (decision #28), each repair
    /// logged and noted:
    ///   - a slot that refers to a stack that does not exist is dropped;
    ///   - two or more slots that refer to one stack: each slot after the first gets its own copy
    ///     under a new "Stack N" name;
    ///   - an empty stack's slot is removed; a one-tool stack's slot becomes that plain tool;
    ///   - a stack no slot refers to is removed.
    /// A stack of 4+ tools is left for <see cref="SaveBlocker"/>. Changed is true when anything was
    /// repaired (the editor then counts the profile as changed).
    /// </summary>
    public static StackNormalizeReport Normalize(Profile profile, ILoaderLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        log ??= NullLoaderLog.Instance;
        profile.Stacks ??= new();
        var notes = new List<string>();
        void Note(string text)
        {
            notes.Add(text);
            log.Warning(null, "Builder.normalize_stacks: {Profile}: {Note}", profile.ProfileName, text);
        }

        // Pass 1: references to missing stacks go; a shared stack is split, one copy per slot.
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var panel in profile.Panels)
        {
            panel.Slots ??= [];
            var slots = panel.Slots;
            for (var i = 0; i < slots.Count; i++)
            {
                var slot = slots[i];
                if (!IsStack(slot)) continue;
                var name = slot.Name ?? "";
                if (!profile.Stacks.TryGetValue(name, out var stack))
                {
                    slots.RemoveAt(i--);
                    Note($"panel \"{panel.Name}\": stack \"{name}\" does not exist; the reference was dropped");
                    continue;
                }
                if (stack is null) profile.Stacks[name] = stack = new Stack();
                stack.Tools ??= [];
                if (claimed.Add(name)) continue;
                var copyName = NextStackName(profile);
                profile.Stacks[copyName] = new Stack { Tools = stack.Tools.Select(CopySlot).ToList() };
                slots[i] = StackSlot(copyName);
                claimed.Add(copyName);
                Note($"panel \"{panel.Name}\": stack \"{name}\" was shared; this slot now has its own copy \"{copyName}\"");
            }
        }

        // Pass 2: an empty stack's slot goes; a one-tool stack's slot becomes that plain tool.
        foreach (var panel in profile.Panels)
        {
            foreach (var slot in panel.Slots.Where(IsStack).ToList())
            {
                var count = ToolsOf(profile, slot).Count;
                if (count >= MinTools) continue;
                Collapse(profile, panel.Slots, slot);
                Note(count == 0
                    ? $"panel \"{panel.Name}\": stack \"{slot.Name}\" was empty; removed"
                    : $"panel \"{panel.Name}\": stack \"{slot.Name}\" held one tool; it is now a plain tool");
            }
        }

        // Pass 3: a stack no slot refers to goes.
        foreach (var orphan in PruneUnreferenced(profile))
            Note($"stack \"{orphan}\" was not on any panel; removed");
        return new StackNormalizeReport(notes.Count > 0, notes);
    }

    /// <summary>The panel slot that places a stack: <c>{ type: "stack", name }</c>.</summary>
    public static Slot StackSlot(string name) => new() { SlotType = StackType, Name = name };

    private static Slot CopySlot(Slot s) => new()
    {
        SlotType = s.SlotType,
        Name = s.Name,
        BaseName = s.BaseName,
        CommandId = s.CommandId,
        SourceTab = s.SourceTab,
        SourcePanel = s.SourcePanel,
        IconFile = s.IconFile,
    };

    /// <summary>A stack slot left with fewer than 2 tools: one becomes the plain tool in its place, none removes it.</summary>
    private static void Collapse(Profile profile, List<Slot> slots, Slot stackSlot)
    {
        var name = stackSlot.Name ?? "";
        var tools = ToolsOf(profile, stackSlot);
        if (tools.Count >= MinTools) return;
        var at = slots.IndexOf(stackSlot);
        if (tools.Count == 1) slots[at] = tools[0];
        else slots.RemoveAt(at);
        profile.Stacks.Remove(name);
    }

    /// <summary>The dropped slot (a tool, or a whole stack slot) without moving it.</summary>
    private static bool Resolve(Profile profile, TileSource source, out Slot slot)
    {
        slot = null!;
        if (source.NewTool is { } tool)
        {
            slot = tool;
            return IsTool(tool);
        }
        if (source.From is not { } from || !SlotAt(profile, from.Panel, from.Slot, out var at)) return false;
        if (!from.InStack)
        {
            slot = at;
            return true;
        }
        var rows = ToolsOf(profile, at);
        if (!InRange(from.Row, rows.Count)) return false;
        slot = rows[from.Row];
        return true;
    }

    /// <summary>As <see cref="Resolve"/>, but only a tool: a whole stack never goes inside another stack.</summary>
    private static bool ResolveTool(Profile profile, TileSource source, out Slot tool) =>
        Resolve(profile, source, out tool) && IsTool(tool);

    /// <summary>
    /// Takes the dropped tile out of its place (a new tool has none). A row taken from a 2-tool stack
    /// dissolves it. Returns <paramref name="anchor"/>, or the plain tool that replaced it when the
    /// anchor was the stack that dissolved.
    /// </summary>
    private static Slot Detach(Profile profile, TileSource source, Slot? anchor)
    {
        if (source.From is not { } from) return anchor!;
        var slots = profile.Panels[from.Panel].Slots;
        var at = slots[from.Slot];
        if (!from.InStack)
        {
            slots.RemoveAt(from.Slot);
            return anchor!;
        }
        profile.Stacks[at.Name ?? ""].Tools.RemoveAt(from.Row);
        var remaining = ToolsOf(profile, at);
        var replacement = remaining.Count == 1 ? remaining[0] : null;
        Collapse(profile, slots, at);
        return ReferenceEquals(anchor, at) && replacement is not null ? replacement : anchor!;
    }

    private static bool SlotAt(Profile profile, int panel, int slot, out Slot found)
    {
        found = null!;
        if (!InRange(panel, profile.Panels.Count)) return false;
        var slots = profile.Panels[panel].Slots;
        if (!InRange(slot, slots.Count) || slots[slot] is null) return false;
        found = slots[slot];
        return true;
    }

    private static bool InRange(int index, int count) => index >= 0 && index < count;
}
