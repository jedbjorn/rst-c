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
        var ribbon = ComponentManager.Ribbon;
        if (ribbon is null)
        {
            Log.Warning("RibbonScanner: ComponentManager.Ribbon is null — no add-in commands will be enumerated");
            return Array.Empty<ScannedCommand>();
        }
        return EnumerateTabs(ribbon.Tabs, IsPostable);
    }

    /// <summary>
    /// The walk over the given tabs (the live ribbon's, or a test's). <paramref name="isPostable"/>
    /// is asked only for kinds the walk reaches since SC-027 (see RibbonWalker). Logs one
    /// Information line with the scan's counts once enumeration completes.
    /// </summary>
    internal static IEnumerable<ScannedCommand> EnumerateTabs(
        IEnumerable<RibbonTab?> tabs, Func<string, bool> isPostable, RibbonScanCounts? counts = null)
    {
        counts ??= new RibbonScanCounts();
        var walker = new RibbonWalker<RibbonItem>(Describe, isPostable, counts);

        var tabsSkippedRestricted = 0;
        var tabsSkippedRst = 0;

        foreach (var tab in tabs)
        {
            if (tab is null) continue;
            counts.TabsTotal++;
            if (ModeRestrictedTabs.Contains(tab.Title)) { tabsSkippedRestricted++; continue; }
            // The active profile's tab, if any. RST built those buttons this
            // session, so reading them back would surface circular entries.
            // The permanent RST tools panel is intentionally not excluded:
            // users may place Builder, Loader, RSTify, or Health on a profile.
            if (RstManagedTabs.Contains(tab.Title))     { tabsSkippedRst++; continue; }
            counts.TabsWalked++;

            var perTab = 0;
            foreach (var panel in tab.Panels)
            {
                var source = panel?.Source;
                if (source is null) continue;

                List<RibbonHit> hits;
                try
                {
                    hits = walker.WalkPanel(source.Items, DialogLauncherOf(source));
                }
                catch (Exception ex)
                {
                    // One odd panel must not cost the whole catalog.
                    Log.Warning(ex, "RibbonScanner: skipped panel {Tab} / {Panel}", tab.Title, source.Title);
                    continue;
                }

                foreach (var hit in hits)
                {
                    perTab++;
                    yield return new ScannedCommand(
                        Id: hit.Id,
                        DisplayName: hit.DisplayName,
                        // SC-007 (spec #18): Revit's own buttons on its own tabs are Native; an add-in's
                        // CustomCtrl_ button is Custom wherever it sits.
                        Origin: OriginClassifier.ClassifyRibbonButton(tab.Title, hit.Id),
                        AddinFile: null,
                        AssemblyPath: null,
                        SourceTab: tab.Title,
                        SourcePanel: source.Title);
                }
            }
            Log.Debug("RibbonScanner: tab={Tab} → {Count} buttons", tab.Title, perTab);
        }

        Log.Debug("RibbonScanner: skippedRestricted={Restricted}, skippedRst={Rst}",
                  tabsSkippedRestricted, tabsSkippedRst);
        // SC-027: one line per scan, so a VM run or a new Revit year can be checked from the log.
        Log.Information("RibbonScanner: {Summary}", counts.Summary());
    }

    /// <summary>
    /// Maps an AdWindows item onto the walker's plain model. Order matters: the list-button
    /// subclasses before RibbonListButton, RibbonToggleButton and RibbonListButton before
    /// RibbonButton, the RibbonCommandItem subclasses before RibbonCommandItem.
    /// </summary>
    internal static RibbonNode<RibbonItem> Describe(RibbonItem item)
    {
        switch (item)
        {
            case RibbonSplitButton split:          // incl. UIFramework.RvtMenuSplitButton
                return RibbonNode<RibbonItem>.Container(RibbonContainerKind.SplitButton, split.Items);
            case RibbonChecklistButton checklist:
                return RibbonNode<RibbonItem>.Container(RibbonContainerKind.Checklist, checklist.Items, checklist.Id, NameOf(checklist));
            case RibbonRadioButtonGroup radio:     // incl. UIFramework.SplitRadioGroup
                return RibbonNode<RibbonItem>.Container(RibbonContainerKind.RadioGroup, radio.Items);
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
                return RibbonNode<RibbonItem>.Leaf(RibbonLeafKind.ToggleButton, toggle.Id, NameOf(toggle));
            case RibbonButton button:
                return RibbonNode<RibbonItem>.Leaf(RibbonLeafKind.Button, button.Id, NameOf(button));
            case RibbonCheckBox checkBox:
                return RibbonNode<RibbonItem>.Leaf(RibbonLeafKind.CheckBox, checkBox.Id, NameOf(checkBox));
            case RibbonMenuItem menuItem:          // incl. UIFramework.RvtLargeMenuItem
                return RibbonNode<RibbonItem>.Leaf(RibbonLeafKind.MenuItem, menuItem.Id, NameOf(menuItem));
            case RibbonCommandItem command:
                return RibbonNode<RibbonItem>.Leaf(RibbonLeafKind.CommandItem, command.Id, NameOf(command));
            default:
                // Separators, row and panel breaks, labels, text boxes, combos, galleries.
                return RibbonNode<RibbonItem>.Ignored;
        }
    }

    private static string NameOf(RibbonItem item) =>
        RibbonWalkRules.DisplayNameFor(item.Text, item.Name, item.Id ?? "");

    private static RibbonItem? DialogLauncherOf(RibbonPanelSource source)
    {
        try { return source.DialogLauncher; }
        catch (Exception ex)
        {
            Log.Debug(ex, "RibbonScanner: no dialog launcher readable on {Panel}", source.Title);
            return null;
        }
    }

    private static bool IsPostable(string id)
    {
        try { return Autodesk.Revit.UI.RevitCommandId.LookupCommandId(id) is not null; }
        catch { return false; }
    }
}
