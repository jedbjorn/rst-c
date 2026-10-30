// rst-c's own (not vendored): the ribbon layout planner (feature #15, spec #17). It turns a
// profile's panels into the ordered list of items the ribbon builder renders: large buttons, stacks
// of 2-3 small buttons, and the slots it skips, each with a reason the builder logs. Pure, so the
// rules are tested on Linux (RST.Tests/Ribbon/RibbonLayoutPlannerTests); the Engine only maps the
// result onto AdWindows (RST.Engine/Ribbon/RibbonItemFactory).
//
// Rules:
//   - A panel tool slot (type "tool" with a commandId) becomes a large button with a wrapped label
//     (ButtonLabel.Wrap) and a 32 px icon.
//   - A stack slot (type "stack") refers to profile.stacks by name. Only tool items (a Revit command
//     or a URL: button) can sit in a stack. Invalid items are skipped, each with a reason; the stack
//     is rendered only when 2 or 3 valid items remain, as Revit's own AddStackedItems allows. It is
//     never drawn as 1 or 4 items. Stacked buttons are small, with single-line labels and 16 px icons.
//   - Anything else (an unknown type, a tool with no commandId, an unknown stack) is skipped with a
//     reason.
//
// Ids: a rendered item takes the next number in its panel, so a profile without stacks keeps the
// button ids it had before stacks (RST_ProfileBtn_<panel>_<n>). A stack is RST_ProfileStack_<panel>_<n>
// and its buttons RST_ProfileBtn_<panel>_<n>_<k>.

using RST.Core.Profiles;

namespace RST.Core.Ribbon;

/// <summary>What a ribbon button does when clicked: post a Revit command, or open a URL.</summary>
public enum SlotKind
{
    Command,
    Url,
}

/// <summary>A button's dispatch target: the kind, the command id or URL, and the label for messages.</summary>
public sealed record SlotTarget(SlotKind Kind, string Payload, string DisplayName)
{
    /// <summary>The prefix a URL button's command id carries (<c>URL:https://…</c>).</summary>
    public const string UrlPrefix = "URL:";

    /// <summary>A slot's command id as a target: <c>URL:</c> (any case) opens the rest as a URL; anything else is a Revit command.</summary>
    public static SlotTarget Parse(string commandId, string? displayName)
    {
        ArgumentNullException.ThrowIfNull(commandId);
        return commandId.StartsWith(UrlPrefix, StringComparison.OrdinalIgnoreCase)
            ? new SlotTarget(SlotKind.Url, commandId[UrlPrefix.Length..], displayName ?? "")
            : new SlotTarget(SlotKind.Command, commandId, displayName ?? "");
    }

    /// <summary>The button's tooltip: "Open: &lt;url&gt;" or "Posts: &lt;command id&gt;".</summary>
    public string ToolTip => Kind == SlotKind.Url ? "Open: " + Payload : "Posts: " + Payload;
}

/// <summary>How big a planned button is drawn: Large (32 px, wrapped label) or Small (16 px, one line, in a stack).</summary>
public enum PlannedButtonSize
{
    Large,
    Small,
}

/// <summary>One button the ribbon builder creates.</summary>
/// <param name="Source">The slot (a panel tool, or a stack's tool) it comes from.</param>
/// <param name="Target">What a click does.</param>
/// <param name="Label">The text: wrapped for a large button, a single line for a small one.</param>
/// <param name="Id">The AdWindows item id.</param>
/// <param name="Size">Large, or Small in a stack.</param>
public sealed record PlannedButton(Slot Source, SlotTarget Target, string Label, string Id, PlannedButtonSize Size)
{
    /// <summary>The slot's <c>iconFile</c> (a <c>pack:</c> value, or null).</summary>
    public string? IconFile => Source.IconFile;

    /// <summary>The slot's command id (a Revit command, or <c>URL:…</c>).</summary>
    public string CommandId => Source.CommandId ?? "";
}

/// <summary>One entry of a panel's layout, in slot order.</summary>
public abstract record RibbonLayoutItem(int SlotIndex);

/// <summary>A panel tool slot drawn as a large button.</summary>
public sealed record LargeButtonItem(int SlotIndex, PlannedButton Button) : RibbonLayoutItem(SlotIndex);

/// <summary>A stack slot drawn as 2 or 3 small buttons, one per row.</summary>
public sealed record StackItem(int SlotIndex, string StackName, string Id, IReadOnlyList<PlannedButton> Buttons) : RibbonLayoutItem(SlotIndex);

/// <summary>
/// Something the builder does not draw, and why: a whole slot (<see cref="ToolIndex"/> null), or one
/// item of a stack (<see cref="StackName"/> and <see cref="ToolIndex"/> set).
/// </summary>
public sealed record SkippedItem(int SlotIndex, string Reason, string? StackName = null, int? ToolIndex = null) : RibbonLayoutItem(SlotIndex);

/// <summary>One panel's layout: its items in slot order (skips included, so each can be logged).</summary>
public sealed record PanelLayout(int PanelIndex, string Name, IReadOnlyList<RibbonLayoutItem> Items)
{
    /// <summary>The items the ribbon shows (large buttons and stacks), in order.</summary>
    public IEnumerable<RibbonLayoutItem> Rendered => Items.Where(i => i is not SkippedItem);

    /// <summary>The skipped slots and stack items.</summary>
    public IEnumerable<SkippedItem> Skipped => Items.OfType<SkippedItem>();

    /// <summary>The number of buttons drawn (a stack counts each of its buttons).</summary>
    public int ButtonCount => Items.Sum(i => i switch
    {
        LargeButtonItem => 1,
        StackItem s => s.Buttons.Count,
        _ => 0,
    });

    /// <summary>The number of stacks drawn.</summary>
    public int StackCount => Items.Count(i => i is StackItem);
}

public static class RibbonLayoutPlanner
{
    /// <summary>The fewest items a stack may hold (Revit's AddStackedItems: 2 or 3).</summary>
    public const int MinStackItems = 2;

    /// <summary>The most items a stack may hold.</summary>
    public const int MaxStackItems = 3;

    /// <summary>Every panel's layout, in panel order.</summary>
    public static IReadOnlyList<PanelLayout> Plan(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var panels = new List<PanelLayout>(profile.Panels.Count);
        for (var i = 0; i < profile.Panels.Count; i++) panels.Add(PlanPanel(profile, profile.Panels[i], i));
        return panels;
    }

    /// <summary>One panel's layout.</summary>
    public static PanelLayout PlanPanel(Profile profile, Panel panel, int panelIndex)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(panel);
        var items = new List<RibbonLayoutItem>();
        var rendered = 0;
        var slots = panel.Slots ?? [];
        for (var s = 0; s < slots.Count; s++)
        {
            var slot = slots[s];
            if (slot is null)
            {
                items.Add(new SkippedItem(s, "empty slot"));
                continue;
            }
            switch (slot.SlotType)
            {
                case "tool":
                    if (ToolProblem(slot) is { } problem)
                    {
                        items.Add(new SkippedItem(s, problem));
                        break;
                    }
                    items.Add(new LargeButtonItem(s, Button(slot, ButtonId(panelIndex, rendered), PlannedButtonSize.Large)));
                    rendered++;
                    break;

                case "stack":
                    var stack = PlanStack(profile, slot, s, panelIndex, rendered);
                    items.AddRange(stack.Skips);
                    if (stack.Item is not null)
                    {
                        items.Add(stack.Item);
                        rendered++;
                    }
                    break;

                default:
                    items.Add(new SkippedItem(s, $"unknown slot type '{slot.SlotType}'"));
                    break;
            }
        }
        return new PanelLayout(panelIndex, panel.Name ?? "", items);
    }

    /// <summary>
    /// A stack slot's plan: the stack (or null when it is not drawn) and the skips that go with it,
    /// one per invalid item plus one for the whole stack when it is not drawn.
    /// </summary>
    public static (StackItem? Item, IReadOnlyList<SkippedItem> Skips) PlanStack(Profile profile, Slot slot, int slotIndex = 0, int panelIndex = 0, int itemNumber = 0)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(slot);
        var name = slot.Name ?? "";
        if (string.IsNullOrEmpty(name))
            return (null, [new SkippedItem(slotIndex, "stack slot has no name", name)]);
        if (profile.Stacks is null || !profile.Stacks.TryGetValue(name, out var stack) || stack is null)
            return (null, [new SkippedItem(slotIndex, $"stack '{name}' not found in profile.stacks", name)]);

        var skips = new List<SkippedItem>();
        var valid = new List<Slot>();
        var tools = stack.Tools ?? [];
        for (var t = 0; t < tools.Count; t++)
        {
            var tool = tools[t];
            var problem = tool is null ? "empty item" : StackToolProblem(tool);
            if (problem is not null)
            {
                skips.Add(new SkippedItem(slotIndex, $"stack '{name}' item {t} ('{tool?.Name}'): {problem}", name, t));
                continue;
            }
            valid.Add(tool!);
        }

        if (valid.Count is < MinStackItems or > MaxStackItems)
        {
            skips.Add(new SkippedItem(slotIndex,
                $"stack '{name}' has {valid.Count} valid item{(valid.Count == 1 ? "" : "s")}; a stack needs {MinStackItems} or {MaxStackItems}", name));
            return (null, skips);
        }

        var buttons = valid.Select((tool, k) => Button(tool, ButtonId(panelIndex, itemNumber) + "_" + k, PlannedButtonSize.Small)).ToList();
        return (new StackItem(slotIndex, name, StackId(panelIndex, itemNumber), buttons), skips);
    }

    /// <summary>A small (stacked) button's label: the name on one line (line breaks become spaces), trimmed.</summary>
    public static string SingleLine(string? text) =>
        string.Join(' ', (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Where(p => p.Length > 0));

    /// <summary>The AdWindows id of rendered item <paramref name="number"/> of panel <paramref name="panelIndex"/>.</summary>
    public static string ButtonId(int panelIndex, int number) => $"RST_ProfileBtn_{panelIndex:D3}_{number:D3}";

    /// <summary>The AdWindows id of a stack (its row panel).</summary>
    public static string StackId(int panelIndex, int number) => $"RST_ProfileStack_{panelIndex:D3}_{number:D3}";

    // ---- the panel width estimate (PanelStyling's rounded background) -------------------------

    /// <summary>A large button's width on the ribbon, about 96 px.</summary>
    public const double LargeButtonWidth = 96.0;

    /// <summary>The narrowest a stack is drawn.</summary>
    public const double MinStackWidth = 64.0;

    /// <summary>Roughly what one character of a small button's label takes (Segoe UI 9 pt).</summary>
    public const double SmallLabelCharWidth = 6.0;

    /// <summary>A small button's icon, the gap and its padding.</summary>
    public const double SmallButtonChrome = 16.0 + 4.0 + 10.0;

    /// <summary>A stack's estimated width: its longest label plus a small button's chrome, at least <see cref="MinStackWidth"/>.</summary>
    public static double EstimateStackWidth(StackItem stack)
    {
        ArgumentNullException.ThrowIfNull(stack);
        var longest = stack.Buttons.Count == 0 ? 0 : stack.Buttons.Max(b => b.Label.Length);
        return Math.Max(MinStackWidth, SmallButtonChrome + longest * SmallLabelCharWidth);
    }

    /// <summary>
    /// The panel's estimated width for its background: a large button is about 96 px and a stack
    /// as wide as its labels, plus 8 px of margin, and never under one large button. AdWindows has
    /// no ActualWidth, so this is an estimate.
    /// </summary>
    public static double EstimatePanelWidth(PanelLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var width = layout.Items.Sum(i => i switch
        {
            LargeButtonItem => LargeButtonWidth,
            StackItem s => EstimateStackWidth(s),
            _ => 0,
        });
        return Math.Max(LargeButtonWidth, width + 8.0);
    }

    // ---- validity -----------------------------------------------------------------------------

    /// <summary>Why a panel tool slot cannot be drawn, or null when it can.</summary>
    private static string? ToolProblem(Slot slot) =>
        string.IsNullOrEmpty(slot.CommandId) ? "tool slot has no commandId" : null;

    /// <summary>Why a stack item cannot be drawn (only a tool with a commandId can), or null when it can.</summary>
    public static string? StackToolProblem(Slot tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (tool.SlotType != "tool") return $"type must be 'tool' (got '{tool.SlotType}')";
        if (string.IsNullOrEmpty(tool.CommandId)) return "no commandId";
        return null;
    }

    private static PlannedButton Button(Slot slot, string id, PlannedButtonSize size)
    {
        var target = SlotTarget.Parse(slot.CommandId!, slot.Name);
        var label = size == PlannedButtonSize.Large ? ButtonLabel.Wrap(slot.Name) : SingleLine(slot.Name);
        return new PlannedButton(slot, target, label, id, size);
    }
}
