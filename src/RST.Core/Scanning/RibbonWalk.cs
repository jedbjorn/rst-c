// RibbonWalk.cs — the Revit-free half of the live ribbon walk (SC-027).
//
// RST.Engine's RibbonScanner maps each Autodesk.Windows item to a RibbonNode (container, leaf or
// ignored); this walker does the rest on plain data, so it is unit-tested without Revit:
//   - descends every container generically: split buttons (Revit's UIFramework.RvtMenuSplitButton
//     is one), menu and checklist buttons, radio groups, any other list button, stacks and fold
//     panels, and menu items that carry their own sub-menu (UIFramework.RvtLargeMenuItem);
//   - visits each panel's dialog launcher (the arrow in the panel title);
//   - decides per leaf: collect it, or leave it out as an on/off state control (below);
//   - guards against cycles (a container met again on its own path) and runaway depth;
//   - keeps the counts RibbonScanner logs once per scan.
//
// Before SC-027 the walk descended RibbonSplitButton and RibbonRowPanel only and emitted
// RibbonButton only. A RibbonMenuItem (and so Revit's RvtLargeMenuItem) and a dialog launcher are
// RibbonCommandItems, not RibbonButtons, and a RibbonChecklistButton is not a split button, so
// Manage > Additional Settings, View > Windows > User Interface and the panel arrows never reached
// the catalog. They have no tab in the PostableCommand seed either, so the catalog's headless
// filter dropped them (C1, Pass B, shared/evidence/v31-main/passB/README.md).
//
// On/off state controls stay OUT of the catalog (Planner decision, SC-027): a profile button can
// run a command but cannot show a check state, so a toggle placed on a profile would be a button
// that silently flips something with no feedback. Left out deliberately, and counted:
//   - check boxes (RibbonCheckBox), wherever they sit (the Select panel's "Select links" …);
//   - every entry of a radio group (RibbonRadioButtonGroup, incl. UIFramework.SplitRadioGroup:
//     the four Show Mass modes);
//   - toggle buttons and check boxes inside a checklist (RibbonChecklistButton: ViewCube,
//     Navigation Bar, Status Bar …). The checklist's plain command entries (RibbonMenuItem:
//     Browser Organization, Keyboard Shortcuts) are collected.
// Toggle buttons in ordinary places (a panel, a stack, a split button: Thin Lines, Show Mass,
// Multi-Point Routing …) were collected before SC-027 and still are: many of them start a mode
// rather than flip a setting, and removing them would drop tools existing profiles may use.
//
// Postability: an Id is opaque and RevitCommandId.LookupCommandId(id) resolves it (see
// RibbonScanner). Kinds the walk reaches for the first time with SC-027 (menu items, checklist and
// sub-menu entries, dialog launchers) are emitted only when the caller's isPostable accepts the
// Id; the ones refused are counted as "not postable". Kinds the walk already reached are emitted
// exactly as before.

using System;
using System.Collections.Generic;
using System.Text;

namespace RST.Core.Scanning;

/// <summary>The container a leaf was reached through.</summary>
public enum RibbonContainerKind
{
    /// <summary>A panel's own items (main row and slide-out).</summary>
    Panel,
    /// <summary>RibbonRowPanel, incl. RibbonFoldPanel: stacked and fold-out rows.</summary>
    Stack,
    /// <summary>RibbonSplitButton, incl. UIFramework.RvtMenuSplitButton (Manage &gt; Additional Settings).</summary>
    SplitButton,
    /// <summary>RibbonMenuButton (for example UIFramework.SwitchWindowMenuButton).</summary>
    MenuButton,
    /// <summary>RibbonChecklistButton (View &gt; Windows &gt; User Interface).</summary>
    Checklist,
    /// <summary>RibbonRadioButtonGroup, incl. UIFramework.SplitRadioGroup (the Show Mass modes).</summary>
    RadioGroup,
    /// <summary>Any other RibbonListButton.</summary>
    ListButton,
    /// <summary>A RibbonMenuItem with its own items (UIFramework.RvtLargeMenuItem "Annotations").</summary>
    SubMenu,
    /// <summary>RibbonPanelSource.DialogLauncher, the arrow in a panel's title bar.</summary>
    DialogLauncher,
}

/// <summary>What a leaf is.</summary>
public enum RibbonLeafKind
{
    /// <summary>RibbonButton (and a list button that is also a command, see <see cref="RibbonWalkRules.ContainerIsAlsoACommand"/>).</summary>
    Button,
    /// <summary>RibbonToggleButton.</summary>
    ToggleButton,
    /// <summary>RibbonCheckBox.</summary>
    CheckBox,
    /// <summary>RibbonMenuItem (incl. UIFramework.RvtLargeMenuItem) without a sub-menu.</summary>
    MenuItem,
    /// <summary>Any other RibbonCommandItem (a dialog launcher is usually one).</summary>
    CommandItem,
}

public enum RibbonLeafDecision
{
    Collect,
    /// <summary>An on/off state control, left out of the catalog on purpose.</summary>
    ExcludeToggle,
}

public enum RibbonNodeRole
{
    /// <summary>Not a tool: separators, row breaks, labels, text boxes, combos and galleries.</summary>
    Ignore,
    Container,
    Leaf,
}

/// <summary>One ribbon item as the walker sees it.</summary>
/// <param name="Role">Container, leaf or ignored.</param>
/// <param name="ContainerKind">For a container: the kind its children are reached through.</param>
/// <param name="LeafKind">For a leaf: what it is. For a container that is also a command: Button.</param>
/// <param name="Id">The item's command Id, opaque.</param>
/// <param name="DisplayName">The ribbon text (or name) to show.</param>
/// <param name="Children">For a container: its items.</param>
public readonly record struct RibbonNode<TNode>(
    RibbonNodeRole Role,
    RibbonContainerKind ContainerKind,
    RibbonLeafKind LeafKind,
    string? Id,
    string? DisplayName,
    IEnumerable<TNode?>? Children)
    where TNode : class
{
    public static RibbonNode<TNode> Ignored => new(RibbonNodeRole.Ignore, default, default, null, null, null);

    public static RibbonNode<TNode> Leaf(RibbonLeafKind kind, string? id, string? displayName) =>
        new(RibbonNodeRole.Leaf, default, kind, id, displayName, null);

    public static RibbonNode<TNode> Container(RibbonContainerKind kind, IEnumerable<TNode?>? children, string? id = null, string? displayName = null) =>
        new(RibbonNodeRole.Container, kind, RibbonLeafKind.Button, id, displayName, children);
}

/// <summary>A collected leaf.</summary>
public readonly record struct RibbonHit(string Id, string DisplayName, RibbonContainerKind Container, RibbonLeafKind Leaf);

public static class RibbonWalkRules
{
    /// <summary>
    /// Deepest container nesting the walk follows. Revit 2026/2027 nest at most 2 (a fold panel
    /// holding a split button; a menu holding a sub-menu); 8 leaves headroom for add-ins.
    /// </summary>
    public const int MaxDepth = 8;

    /// <summary>Collect a leaf, or leave it out as an on/off state control (see the file header).</summary>
    public static RibbonLeafDecision Decide(RibbonLeafKind leaf, RibbonContainerKind container)
    {
        if (leaf == RibbonLeafKind.CheckBox) return RibbonLeafDecision.ExcludeToggle;
        // A radio group is a single state with several values: every entry sets that state.
        if (container == RibbonContainerKind.RadioGroup) return RibbonLeafDecision.ExcludeToggle;
        // A checklist's toggles show and hide parts of the UI; its plain entries are commands.
        if (container == RibbonContainerKind.Checklist && leaf == RibbonLeafKind.ToggleButton)
            return RibbonLeafDecision.ExcludeToggle;
        return RibbonLeafDecision.Collect;
    }

    /// <summary>
    /// True for the list buttons that RibbonScanner emitted as commands themselves before SC-027
    /// (any RibbonListButton that is not a split button or a radio group was a plain RibbonButton
    /// to that walk). They stay emitted, so no existing catalog entry disappears; their children
    /// are now walked as well.
    /// </summary>
    public static bool ContainerIsAlsoACommand(RibbonContainerKind kind) =>
        kind is RibbonContainerKind.MenuButton or RibbonContainerKind.Checklist or RibbonContainerKind.ListButton;

    /// <summary>
    /// True when the walk before SC-027 already emitted this leaf: a RibbonButton (toggle or not)
    /// straight on a panel, in a stack or in a split button. Everything else is newly reached
    /// and must pass the postability check.
    /// </summary>
    public static bool ReachedBeforeSc027(RibbonLeafKind leaf, RibbonContainerKind container) =>
        leaf is RibbonLeafKind.Button or RibbonLeafKind.ToggleButton
        && container is RibbonContainerKind.Panel or RibbonContainerKind.Stack or RibbonContainerKind.SplitButton;

    /// <summary>The first of text, name and Id that is not blank.</summary>
    public static string DisplayNameFor(string? text, string? name, string id)
    {
        if (!string.IsNullOrWhiteSpace(text)) return text!;
        if (!string.IsNullOrWhiteSpace(name)) return name!;
        return id;
    }
}

/// <summary>Counts for the one Information line RibbonScanner logs per scan.</summary>
public sealed class RibbonScanCounts
{
    private static readonly RibbonContainerKind[] Kinds = (RibbonContainerKind[])Enum.GetValues(typeof(RibbonContainerKind));

    private readonly int[] _collected = new int[Kinds.Length];
    private readonly int[] _excluded = new int[Kinds.Length];

    public int TabsTotal { get; set; }
    public int TabsWalked { get; set; }
    public int MenuItemsCollected { get; private set; }
    public int NotPostable { get; private set; }
    public int Cycles { get; private set; }
    public int DepthCapped { get; private set; }

    public int Collected(RibbonContainerKind kind) => _collected[(int)kind];
    public int ExcludedAsToggle(RibbonContainerKind kind) => _excluded[(int)kind];

    public int CollectedTotal { get { var n = 0; foreach (var c in _collected) n += c; return n; } }
    public int ExcludedTotal { get { var n = 0; foreach (var c in _excluded) n += c; return n; } }

    internal void AddCollected(RibbonContainerKind kind, RibbonLeafKind leaf)
    {
        _collected[(int)kind]++;
        if (leaf == RibbonLeafKind.MenuItem) MenuItemsCollected++;
    }

    internal void AddExcluded(RibbonContainerKind kind) => _excluded[(int)kind]++;
    internal void AddNotPostable() => NotPostable++;
    internal void AddCycle() => Cycles++;
    internal void AddDepthCapped() => DepthCapped++;

    /// <summary>
    /// The scan summary, every kind always present so two scans (or two Revit years) compare
    /// field by field. For example:
    /// <c>tabs 14/16; collected 700 (panel=120, stack=250, splitButton=210, …, dialogLauncher=13; menu items 18); excluded as toggles 80 (…); not postable 0; cycles 0; depth capped 0</c>
    /// </summary>
    public string Summary()
    {
        var sb = new StringBuilder();
        sb.Append("tabs ").Append(TabsWalked).Append('/').Append(TabsTotal)
          .Append("; collected ").Append(CollectedTotal).Append(" (");
        AppendKinds(sb, _collected);
        sb.Append("; menu items ").Append(MenuItemsCollected)
          .Append("); excluded as toggles ").Append(ExcludedTotal).Append(" (");
        AppendKinds(sb, _excluded);
        sb.Append("); not postable ").Append(NotPostable)
          .Append("; cycles ").Append(Cycles)
          .Append("; depth capped ").Append(DepthCapped);
        return sb.ToString();
    }

    private static void AppendKinds(StringBuilder sb, int[] counts)
    {
        for (var i = 0; i < Kinds.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            var name = Kinds[i].ToString();
            sb.Append(char.ToLowerInvariant(name[0])).Append(name, 1, name.Length - 1).Append('=').Append(counts[i]);
        }
    }
}

/// <summary>
/// Walks one ribbon panel at a time. <typeparamref name="TNode"/> is Autodesk.Windows.RibbonItem in
/// RST.Engine and a plain test type in RST.Tests.
/// </summary>
public sealed class RibbonWalker<TNode> where TNode : class
{
    private readonly Func<TNode, RibbonNode<TNode>> _describe;
    private readonly Func<string, bool> _isPostable;
    private readonly HashSet<TNode> _onPath = new(ReferenceEqualityComparer.Instance);

    public RibbonScanCounts Counts { get; }

    /// <param name="describe">Maps an item to its role, kind, Id, text and children.</param>
    /// <param name="isPostable">Accepts an Id that Revit can post; asked only for newly reached kinds.</param>
    /// <param name="counts">Where the scan's counts go; shared across panels and tabs.</param>
    public RibbonWalker(Func<TNode, RibbonNode<TNode>> describe, Func<string, bool> isPostable, RibbonScanCounts? counts = null)
    {
        _describe = describe ?? throw new ArgumentNullException(nameof(describe));
        _isPostable = isPostable ?? throw new ArgumentNullException(nameof(isPostable));
        Counts = counts ?? new RibbonScanCounts();
    }

    /// <summary>The collected leaves of one panel: its items (slide-out included) and its dialog launcher.</summary>
    public List<RibbonHit> WalkPanel(IEnumerable<TNode?>? items, TNode? dialogLauncher)
    {
        var hits = new List<RibbonHit>();
        if (items is not null)
            foreach (var item in items)
                Visit(item, RibbonContainerKind.Panel, 0, hits);
        if (dialogLauncher is not null)
            Visit(dialogLauncher, RibbonContainerKind.DialogLauncher, 0, hits);
        return hits;
    }

    private void Visit(TNode? item, RibbonContainerKind container, int depth, List<RibbonHit> hits)
    {
        if (item is null) return;
        var node = _describe(item);
        switch (node.Role)
        {
            case RibbonNodeRole.Leaf:
                Emit(node.LeafKind, node.Id, node.DisplayName, container, hits);
                return;

            case RibbonNodeRole.Container:
                if (RibbonWalkRules.ContainerIsAlsoACommand(node.ContainerKind))
                    Emit(RibbonLeafKind.Button, node.Id, node.DisplayName, container, hits);
                if (node.Children is null) return;
                if (depth >= RibbonWalkRules.MaxDepth) { Counts.AddDepthCapped(); return; }
                if (!_onPath.Add(item)) { Counts.AddCycle(); return; }
                try
                {
                    foreach (var child in node.Children)
                        Visit(child, node.ContainerKind, depth + 1, hits);
                }
                finally
                {
                    _onPath.Remove(item);
                }
                return;

            default:
                return;
        }
    }

    private void Emit(RibbonLeafKind leaf, string? id, string? displayName, RibbonContainerKind container, List<RibbonHit> hits)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (RibbonWalkRules.Decide(leaf, container) == RibbonLeafDecision.ExcludeToggle)
        {
            Counts.AddExcluded(container);
            return;
        }
        if (!RibbonWalkRules.ReachedBeforeSc027(leaf, container) && !SafeIsPostable(id!))
        {
            Counts.AddNotPostable();
            return;
        }
        Counts.AddCollected(container, leaf);
        hits.Add(new RibbonHit(id!, string.IsNullOrWhiteSpace(displayName) ? id! : displayName!, container, leaf));
    }

    private bool SafeIsPostable(string id)
    {
        try { return _isPostable(id); }
        catch { return false; }
    }
}
