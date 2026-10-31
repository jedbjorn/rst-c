// RibbonScanner.cs — walk the live Revit ribbon to enumerate commands.
//
// The Id surfaced by an Autodesk.Windows ribbon item is treated as opaque:
// RevitCommandId.LookupCommandId(id) is the universal resolver for both
// built-in ("ID_BUTTON_*") and add-in IDs. Empirically (Revit 2026 probe,
// 272/272 round-trip) custom add-in IDs come back with a double prefix
// (CustomCtrl_%CustomCtrl_%Tab%Panel%Button); LookupCommandId handles
// either shape, so do not parse the Id — round-trip only.
//
// The walk also surfaces (Tab, Panel) for each button, so profiles can
// reference a command by source location when the same DLL ships multiple
// buttons with similar names. CommandCatalog uses this to ENRICH
// BuiltinCommandScanner entries (which only carry the Id, no tab/panel)
// — so "Wall" gets bucketed under Architecture > Build instead of
// landing in a "(unknown)" group in the catalog tree.
//
// SC-027: the walk itself (which containers are descended, which leaves are
// collected, which on/off state controls are left out, cycle and depth
// guards, the per-scan counts) lives in RST.Core.Scanning.RibbonWalker, on
// plain data. This file only maps AdWindows types onto it (Describe) and
// supplies the tab filter and the Revit postability check. Every container
// AdWindows has is walked: RibbonListButton (split, menu, checklist, radio
// group and any subclass, incl. Revit's UIFramework.RvtMenuSplitButton and
// SplitRadioGroup), RibbonRowPanel (stacks, fold panels), RibbonMenuItem
// with a sub-menu (UIFramework.RvtLargeMenuItem), and each panel's
// RibbonPanelSource.DialogLauncher. RibbonList (combos, galleries) is not:
// its entries are values (a type, a design option), not commands.
//
// Order (SC-027 review B1): CommandCatalog files a command under the first
// (tab, panel) it sees. Many dialog launchers repeat a command that is also
// a panel item on a later tab (Structure > Structure's arrow is Structural
// Settings, which lives on Manage > Settings), so every launcher hit is held
// back and yielded after all tabs' panel items. A command that exists only
// as a launcher (Text Types) still gets its own tab and panel.
//
// Reliability (review N4): a throw from a tab (its title or panels), a panel
// (its source, title, items or launcher) or a single item skips only that
// tab, panel or item, logs a Warning naming the tab and panel, and the scan
// goes on. Hits of a skipped tab or panel are not counted as collected.
//
// Excluded tabs:
//   - ModeRestrictedTabs — contextual editing modes (family editor, in-place
//                          model/mass, MEP zone); commands aren't usable
//                          from the main app context, so we drop them.
//
// NOT excluded any more: BuiltinTabs (Architecture, Structure, …). Walking
// them was previously skipped to avoid duplicates of BuiltinCommandScanner
// output, but the catalog merge handles that now and gains tab/panel
// enrichment in exchange. Same applies to host tabs (Add-Ins, FormIt) per
// PR #10.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Windows;
using RST.Core.Scanning;
using RST.Engine.Ribbon;
using RST.Core.Logging;

namespace RST.Engine.Scanning;

internal static class RibbonScanner
{
    public static IEnumerable<ScannedCommand> Enumerate()
    {
        RibbonControl? ribbon;
        IEnumerable<RibbonTab?>? tabs;
        try
        {
            ribbon = ComponentManager.Ribbon;
            tabs = ribbon?.Tabs;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "RibbonScanner: ComponentManager.Ribbon could not be read — no add-in commands will be enumerated");
            return Array.Empty<ScannedCommand>();
        }
        if (ribbon is null)
        {
            Log.Warning("RibbonScanner: ComponentManager.Ribbon is null — no add-in commands will be enumerated");
            return Array.Empty<ScannedCommand>();
        }
        return EnumerateTabs(tabs, IsPostable);
    }

    /// <summary>
    /// The walk over the given tabs (the live ribbon's, or a test's): every tab's panel items in
    /// ribbon order, then every dialog launcher. <paramref name="isPostable"/> is asked only for
    /// kinds the walk reaches since SC-027 (see RibbonWalker). Logs one Information line with the
    /// scan's counts.
    /// </summary>
    internal static IReadOnlyList<ScannedCommand> EnumerateTabs(
        IEnumerable<RibbonTab?>? tabs, Func<string, bool> isPostable, RibbonScanCounts? counts = null)
    {
        counts ??= new RibbonScanCounts();
        var walker = new RibbonWalker<RibbonItem>(Describe, isPostable, counts);
        var items = new List<ScannedCommand>();
        var launchers = new List<ScannedCommand>();

        var tabsSkippedRestricted = 0;
        var tabsSkippedRst = 0;

        foreach (var tab in ReadAll(tabs, "the ribbon's tabs", counts))
        {
            if (tab is null) continue;
            counts.TabsTotal++;

            string? tabTitle;
            List<RibbonPanel?> panels;
            try
            {
                tabTitle = tab.Title;
                if (ModeRestrictedTabs.Contains(tabTitle)) { tabsSkippedRestricted++; continue; }
                // The active profile's tab, if any. RST built those buttons this
                // session, so reading them back would surface circular entries.
                // The permanent RST tools panel is intentionally not excluded:
                // users may place Builder, Loader, RSTify, or Health on a profile.
                if (RstManagedTabs.Contains(tabTitle))     { tabsSkippedRst++; continue; }
                panels = tab.Panels is { } tabPanels ? new List<RibbonPanel?>(tabPanels) : new List<RibbonPanel?>();
            }
            catch (Exception ex)
            {
                counts.TabsSkipped++;
                Log.Warning(ex, "RibbonScanner: skipped tab {Tab}", SafeTitle(tab));
                continue;
            }
            counts.TabsWalked++;

            var perTab = 0;
            foreach (var panel in panels)
            {
                string? panelTitle;
                IEnumerable<RibbonItem?>? panelItems;
                RibbonItem? launcher;
                try
                {
                    var source = panel?.Source;
                    if (source is null) continue;
                    panelTitle = source.Title;
                    panelItems = source.Items;
                    launcher = source.DialogLauncher;
                }
                catch (Exception ex)
                {
                    counts.PanelsSkipped++;
                    Log.Warning(ex, "RibbonScanner: skipped a panel on {Tab}", tabTitle);
                    continue;
                }

                Action<Exception, string> onSkip = (ex, what) =>
                    Log.Warning(ex, "RibbonScanner: skipped {Item} on {Tab} / {Panel}", what, tabTitle, panelTitle);

                foreach (var hit in walker.WalkItems(panelItems, onSkip))
                {
                    items.Add(ToCommand(hit, tabTitle, panelTitle));
                    perTab++;
                }
                // Held back: see "Order (B1)" in the file header.
                foreach (var hit in walker.WalkDialogLauncher(launcher, onSkip))
                {
                    launchers.Add(ToCommand(hit, tabTitle, panelTitle));
                    perTab++;
                }
            }
            Log.Debug("RibbonScanner: tab={Tab} → {Count} buttons", tabTitle, perTab);
        }

        Log.Debug("RibbonScanner: skippedRestricted={Restricted}, skippedRst={Rst}",
                  tabsSkippedRestricted, tabsSkippedRst);
        // SC-027: one line per scan, so a VM run or a new Revit year can be checked from the log.
        Log.Information("RibbonScanner: {Summary}", counts.Summary());

        items.AddRange(launchers);
        return items;
    }

    private static ScannedCommand ToCommand(RibbonHit hit, string? tabTitle, string? panelTitle) =>
        new(
            Id: hit.Id,
            DisplayName: hit.DisplayName,
            // SC-007 (spec #18): Revit's own buttons on its own tabs are Native; an add-in's
            // CustomCtrl_ button is Custom wherever it sits.
            Origin: OriginClassifier.ClassifyRibbonButton(tabTitle, hit.Id),
            AddinFile: null,
            AssemblyPath: null,
            SourceTab: tabTitle,
            SourcePanel: panelTitle);

    /// <summary>Reads a sequence item by item; a throw ends it there, logged and counted.</summary>
    private static List<T> ReadAll<T>(IEnumerable<T>? source, string what, RibbonScanCounts counts)
    {
        var list = new List<T>();
        if (source is null) return list;
        try
        {
            foreach (var item in source) list.Add(item);
        }
        catch (Exception ex)
        {
            counts.TabsSkipped++;
            Log.Warning(ex, "RibbonScanner: stopped reading {What} after {Count}", what, list.Count);
        }
        return list;
    }

    private static string SafeTitle(RibbonTab tab)
    {
        try { return tab.Title ?? tab.Id ?? "?"; }
        catch { return "?"; }
    }

    /// <summary>
    /// Maps an AdWindows item onto the walker's plain model. Order matters: the list-button
    /// subclasses before RibbonListButton, RibbonToggleButton and RibbonListButton before
    /// RibbonButton, the RibbonCommandItem subclasses before RibbonCommandItem. A throw here skips
    /// only this item (RibbonWalker).
    /// </summary>
    internal static RibbonNode<RibbonItem> Describe(RibbonItem item)
    {
        switch (item)
        {
            case RibbonSplitButton split:          // incl. UIFramework.RvtMenuSplitButton
                return RibbonNode<RibbonItem>.Container(RibbonContainerKind.SplitButton, split.Items);
            case RibbonChecklistButton checklist:
                return RibbonNode<RibbonItem>.Container(RibbonContainerKind.Checklist, checklist.Items, checklist.Id, NameOf(checklist));
            case RibbonRadioButtonGroup radio:     // incl. UIFramework.SplitRadioGroup (blank Id)
                // The header with an Id was emitted before SC-027 (review B2); its entries are not.
                return RibbonNode<RibbonItem>.Container(RibbonContainerKind.RadioGroup, radio.Items, radio.Id, NameOf(radio));
            case RibbonMenuButton menu:            // incl. UIFramework.SwitchWindowMenuButton
                return RibbonNode<RibbonItem>.Container(RibbonContainerKind.MenuButton, menu.Items, menu.Id, NameOf(menu));
            case RibbonListButton list:
                return RibbonNode<RibbonItem>.Container(RibbonContainerKind.ListButton, list.Items, list.Id, NameOf(list));
            case RibbonRowPanel row:               // incl. RibbonFoldPanel
                return RibbonNode<RibbonItem>.Container(RibbonContainerKind.Stack, row.Items);
            case RibbonMenuItem menuItem when menuItem.Items is { } subItems && subItems.Any():
                // A sub-menu (UIFramework.RvtLargeMenuItem "Annotations"): its header only opens it.
                return RibbonNode<RibbonItem>.Container(RibbonContainerKind.SubMenu, subItems);
            case RibbonToggleButton toggle:
                return RibbonNode<RibbonItem>.Leaf(RibbonLeafKind.ToggleButton, toggle.Id, NameOf(toggle), IsCheckable(toggle));
            case RibbonButton button:
                return RibbonNode<RibbonItem>.Leaf(RibbonLeafKind.Button, button.Id, NameOf(button), IsCheckable(button));
            case RibbonCheckBox checkBox:
                return RibbonNode<RibbonItem>.Leaf(RibbonLeafKind.CheckBox, checkBox.Id, NameOf(checkBox), IsCheckable(checkBox));
            case RibbonMenuItem menuItem:          // incl. UIFramework.RvtLargeMenuItem
                return RibbonNode<RibbonItem>.Leaf(RibbonLeafKind.MenuItem, menuItem.Id, NameOf(menuItem), IsCheckable(menuItem));
            case RibbonCommandItem command:
                return RibbonNode<RibbonItem>.Leaf(RibbonLeafKind.CommandItem, command.Id, NameOf(command), IsCheckable(command));
            default:
                // Separators, row and panel breaks, labels, text boxes, combos, galleries.
                return RibbonNode<RibbonItem>.Ignored;
        }
    }

    private static string NameOf(RibbonItem item) =>
        RibbonWalkRules.DisplayNameFor(item.Text, item.Name, item.Id ?? "");

    /// <summary>
    /// RibbonCommandItem.IsCheckable (AdWindows 2025.0.2, 2026.0.4, 2027.0.0). Only a checklist
    /// entry is judged by it (RibbonWalkRules.Decide); an unreadable value counts as not checkable,
    /// so it never costs an item.
    /// </summary>
    private static bool IsCheckable(RibbonCommandItem item)
    {
        try { return item.IsCheckable; }
        catch { return false; }
    }

    private static bool IsPostable(string id)
    {
        try { return Autodesk.Revit.UI.RevitCommandId.LookupCommandId(id) is not null; }
        catch { return false; }
    }
}
