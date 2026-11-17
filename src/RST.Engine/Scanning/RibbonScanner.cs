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
// as a launcher still gets its own tab and panel.
//
// SC-051 (decision #63): only what Revit lets an add-in post reaches the
// catalog: one of this Revit's PostableCommand ids (CommandCatalog passes
// BuiltinCommandScanner's seed) or an external command's id (CustomCtrl_ or a
// manifest GUID, see
// RST.Core.Scanning.PostCommandRule). Text Types (ID_SETTINGS_TEXT) and the
// other internal commands PostCommand refuses are left out, counted as
// "outside PostableCommand" in the summary and listed on their own Information
// line (ids LookupCommandId could not find get a separate "unresolved" line).
//
// Reliability (review N4): a throw from a tab (its title or panels), a panel
// (its source, title, items or launcher) or a single item skips only that
// tab, panel or item, logs a Warning naming the tab and panel, and the scan
// goes on. Hits of a skipped tab or panel are not counted as collected.
//
// Mode tabs (ModeRestrictedTabs):
//   - by title (Family Editor, In-Place Model/Mass, Zone): not walked.
//   - by id (the family-editor tabs: Create, Insert … of a family, the Family
//     Editor panel's tab, Second_Modify): walked since spec #33, for Search-It's
//     catalogue only. Their hits are in RibbonScan.Hits and never in
//     BuilderHits, so the Builder's catalogue gets the hits it always got. They
//     are counted apart (their own Information line), so the main summary line
//     still compares with earlier releases.
//
// Handles (spec #33): the scan keeps, beside each hit, the live tab, panel and
// item it came from. Revit keeps those objects for the whole session, so
// Search-It reads their state when it opens without walking the ribbon again.
//
// Spec #35 (Search-It runs per file type): every hit also carries its place path in
// the census's own form (RibbonHit.Path: "/SlideOut", "/<Type>(<id or text>)" per
// container, "DialogLauncher"), so the census's not-drawn rows can be matched to
// it; and ids Revit won't let add-ins post are kept for Search-It (RibbonHit.Postable
// false: a click runs them through the ribbon's own command path, decision #92).
// They are still counted and logged as left out, never collected, and never reach
// BuilderHits: the Builder's catalogue and the scan's summary line are unchanged.
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

/// <summary>The live ribbon objects behind one hit of a scan.</summary>
internal sealed record RibbonPlaceHandle(RibbonTab Tab, RibbonPanel Panel, RibbonItem Item);

/// <summary>One scan of the ribbon.</summary>
/// <param name="Hits">Every hit, the family-editor tabs' included: all tabs' panel items in ribbon order, then every dialog launcher.</param>
/// <param name="Handles">The live objects behind each hit, index for index.</param>
internal sealed record RibbonScan(IReadOnlyList<RibbonHit> Hits, IReadOnlyList<RibbonPlaceHandle> Handles)
{
    public static readonly RibbonScan Empty = new(Array.Empty<RibbonHit>(), Array.Empty<RibbonPlaceHandle>());

    /// <summary>
    /// The hits outside the family-editor tabs that Revit lets add-ins post, in order: what the Builder's catalogue is
    /// merged from (spec #35 keeps the others for Search-It only).
    /// </summary>
    public IReadOnlyList<RibbonHit> BuilderHits => Hits.Where(h => h.Postable && !ModeRestrictedTabs.ContainsId(h.TabId)).ToList();
}

internal static class RibbonScanner
{
    /// <param name="postableCommandIds">
    /// This Revit's PostableCommand ids (BuiltinCommandScanner). When given, the walk leaves out
    /// every id Revit would not let an add-in post (SC-051). Null or empty turns that check off
    /// (an empty seed would otherwise drop every Revit tool, so it is logged and ignored).
    /// </param>
    public static IEnumerable<RibbonHit> Enumerate(IReadOnlyCollection<string>? postableCommandIds = null, int? revitYear = null) =>
        Scan(postableCommandIds, revitYear).BuilderHits;

    /// <summary>The live ribbon's scan, with the family-editor tabs and the handles (spec #33). Parameters as <see cref="Enumerate"/>.</summary>
    public static RibbonScan Scan(IReadOnlyCollection<string>? postableCommandIds = null, int? revitYear = null)
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
            return RibbonScan.Empty;
        }
        if (ribbon is null)
        {
            Log.Warning("RibbonScanner: ComponentManager.Ribbon is null — no add-in commands will be enumerated");
            return RibbonScan.Empty;
        }
        Func<string, bool>? isPostableCommand = null;
        if (postableCommandIds is { Count: > 0 })
        {
            var set = postableCommandIds as ISet<string> ?? new HashSet<string>(postableCommandIds, StringComparer.Ordinal);
            isPostableCommand = set.Contains;
        }
        else if (postableCommandIds is not null)
        {
            Log.Warning("RibbonScanner: no PostableCommand ids were read, so tools Revit won't let add-ins run are not left out of this scan");
        }
        return ScanTabs(tabs, IsPostable, isPostableCommand: isPostableCommand, revitYear: revitYear);
    }

    /// <summary>
    /// <see cref="ScanTabs"/> without the family-editor tabs' hits: the Builder's view of the given tabs.
    /// </summary>
    internal static IReadOnlyList<RibbonHit> EnumerateTabs(
        IEnumerable<RibbonTab?>? tabs, Func<string, bool> resolves, RibbonScanCounts? counts = null,
        Func<string, bool>? isPostableCommand = null, int? revitYear = null) =>
        ScanTabs(tabs, resolves, counts, isPostableCommand, revitYear).BuilderHits;

    /// <summary>
    /// The walk over the given tabs (the live ribbon's, or a test's): every tab's panel items in
    /// ribbon order, then every dialog launcher. <paramref name="resolves"/> is asked only for
    /// kinds the walk reaches since SC-027; <paramref name="isPostableCommand"/>, when given, holds
    /// the PostableCommand ids every id is checked against (SC-051, see RibbonWalker). Logs one
    /// Information line with the scan's counts, and one per reason listing the ids left out.
    /// <paramref name="counts"/> gets the tabs outside the family editor; the family-editor tabs
    /// are counted in <paramref name="familyCounts"/> and logged on a line of their own.
    /// </summary>
    internal static RibbonScan ScanTabs(
        IEnumerable<RibbonTab?>? tabs, Func<string, bool> resolves, RibbonScanCounts? counts = null,
        Func<string, bool>? isPostableCommand = null, int? revitYear = null, RibbonScanCounts? familyCounts = null)
    {
        counts ??= new RibbonScanCounts();
        familyCounts ??= new RibbonScanCounts();
        var walker = new RibbonWalker<RibbonItem>(Describe, resolves, counts, isPostableCommand, keepNotPostable: true, recordPaths: true);
        var familyWalker = new RibbonWalker<RibbonItem>(Describe, resolves, familyCounts, isPostableCommand, keepNotPostable: true, recordPaths: true);
        var items = new List<(RibbonHit Hit, RibbonPlaceHandle Handle)>();
        var launchers = new List<(RibbonHit Hit, RibbonPlaceHandle Handle)>();
        var sources = new List<RibbonItem>();

        var tabsSkippedRestricted = 0;
        var tabsSkippedRst = 0;

        foreach (var tab in ReadAll(tabs, "the ribbon's tabs", counts))
        {
            if (tab is null) continue;
            counts.TabsTotal++;

            string? tabTitle;
            string? tabId;
            bool familyTab;
            List<RibbonPanel?> panels;
            try
            {
                tabTitle = tab.Title;
                tabId = tab.Id;
                familyTab = ModeRestrictedTabs.ContainsId(tabId);
                if (!familyTab && ModeRestrictedTabs.Contains(tabTitle)) { tabsSkippedRestricted++; continue; }
                // The active profile's tab, if any. RST built those buttons this
                // session, so reading them back would surface circular entries.
                // The permanent RST tools panel is intentionally not excluded:
                // users may place Builder, Loader, RSTify, or Health on a profile.
                if (!familyTab && RstManagedTabs.Contains(tabTitle)) { tabsSkippedRst++; continue; }
                panels = tab.Panels is { } tabPanels ? new List<RibbonPanel?>(tabPanels) : new List<RibbonPanel?>();
            }
            catch (Exception ex)
            {
                counts.TabsSkipped++;
                Log.Warning(ex, "RibbonScanner: skipped tab {Tab}", SafeTitle(tab));
                continue;
            }
            var tabWalker = familyTab ? familyWalker : walker;
            tabWalker.Counts.TabsWalked++;

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
                    tabWalker.Counts.PanelsSkipped++;
                    Log.Warning(ex, "RibbonScanner: skipped a panel on {Tab}", tabTitle);
                    continue;
                }

                Action<Exception, string> onSkip = (ex, what) =>
                    Log.Warning(ex, "RibbonScanner: skipped {Item} on {Tab} / {Panel}", what, tabTitle, panelTitle);

                sources.Clear();
                var found = tabWalker.WalkItems(panelItems, onSkip, sources);
                for (var i = 0; i < found.Count; i++)
                    items.Add((WithPlacement(found[i], tabTitle, panelTitle, tabId), new RibbonPlaceHandle(tab, panel!, sources[i])));
                perTab += found.Count;
                // Held back: see "Order (B1)" in the file header.
                sources.Clear();
                found = tabWalker.WalkDialogLauncher(launcher, onSkip, sources);
                for (var i = 0; i < found.Count; i++)
                    launchers.Add((WithPlacement(found[i], tabTitle, panelTitle, tabId), new RibbonPlaceHandle(tab, panel!, sources[i])));
                perTab += found.Count;
            }
            Log.Debug("RibbonScanner: tab={Tab} → {Count} buttons", tabTitle, perTab);
        }

        Log.Debug("RibbonScanner: revit={Year}, skippedRestricted={Restricted}, skippedRst={Rst}",
                  revitYear, tabsSkippedRestricted, tabsSkippedRst);
        // SC-027: one line per scan, so a VM run or a new Revit year can be checked from the log.
        Log.Information("RibbonScanner: {Summary}", counts.Summary());
        LogLeftOut(counts, "RibbonScanner");
        // Spec #33: the family-editor tabs, for Search-It's catalogue only.
        Log.Information("RibbonScanner: family-editor tabs (Search-It only): {Summary}", familyCounts.Summary());
        LogLeftOut(familyCounts, "RibbonScanner, family-editor tabs");

        items.AddRange(launchers);
        return new RibbonScan(items.Select(i => i.Hit).ToList(), items.Select(i => i.Handle).ToList());
    }

    // SC-051: one line per reason, so each list can be checked on its own.
    private static void LogLeftOut(RibbonScanCounts counts, string scan)
    {
        if (counts.OutsidePostableCommandIds.Count > 0)
            Log.Information(scan + ": left out {Count} ids Revit won't let add-ins run (outside PostableCommand): {Ids}",
                            counts.OutsidePostableCommandIds.Count, string.Join(", ", counts.OutsidePostableCommandIds));
        if (counts.UnresolvedIds.Count > 0)
            Log.Information(scan + ": left out {Count} ids Revit couldn't find (unresolved): {Ids}",
                            counts.UnresolvedIds.Count, string.Join(", ", counts.UnresolvedIds));
    }

    private static RibbonHit WithPlacement(RibbonHit hit, string? tabTitle, string? panelTitle, string? tabId) =>
        hit with { SourceTab = tabTitle, SourcePanel = panelTitle, TabId = tabId };

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
            case RibbonPanelBreak:
                // Spec #35: the items after it are in the panel's slide-out (the census's "/SlideOut").
                return RibbonNode<RibbonItem>.PanelBreak;
            case RibbonSplitButton split:          // incl. UIFramework.RvtMenuSplitButton
                return RibbonNode<RibbonItem>.Container(RibbonContainerKind.SplitButton, split.Items) with { PathSegment = Segment(split) };
            case RibbonChecklistButton checklist:
                return RibbonNode<RibbonItem>.Container(RibbonContainerKind.Checklist, checklist.Items, checklist.Id, checklist.Text, checklist.Name)
                    with { PathSegment = Segment(checklist) };
            case RibbonRadioButtonGroup radio:     // incl. UIFramework.SplitRadioGroup (blank Id)
                // The header with an Id was emitted before SC-027 (review B2); its entries are not.
                return RibbonNode<RibbonItem>.Container(RibbonContainerKind.RadioGroup, radio.Items, radio.Id, radio.Text, radio.Name)
                    with { PathSegment = Segment(radio) };
            case RibbonMenuButton menu:            // incl. UIFramework.SwitchWindowMenuButton
                return RibbonNode<RibbonItem>.Container(RibbonContainerKind.MenuButton, menu.Items, menu.Id, menu.Text, menu.Name)
                    with { PathSegment = Segment(menu) };
            case RibbonListButton list:
                return RibbonNode<RibbonItem>.Container(RibbonContainerKind.ListButton, list.Items, list.Id, list.Text, list.Name)
                    with { PathSegment = Segment(list) };
            case RibbonRowPanel row:               // incl. RibbonFoldPanel
                return RibbonNode<RibbonItem>.Container(RibbonContainerKind.Stack, row.Items) with { PathSegment = Segment(row) };
            case RibbonMenuItem menuItem when menuItem.Items is { } subItems && subItems.Any():
                // A sub-menu (UIFramework.RvtLargeMenuItem "Annotations"): its header only opens it.
                return RibbonNode<RibbonItem>.Container(RibbonContainerKind.SubMenu, subItems) with { PathSegment = Segment(menuItem) };
            case RibbonToggleButton toggle:
                return RibbonNode<RibbonItem>.Leaf(RibbonLeafKind.ToggleButton, toggle.Id, toggle.Text, IsCheckable(toggle), toggle.Name);
            case RibbonButton button:
                return RibbonNode<RibbonItem>.Leaf(RibbonLeafKind.Button, button.Id, button.Text, IsCheckable(button), button.Name);
            case RibbonCheckBox checkBox:
                return RibbonNode<RibbonItem>.Leaf(RibbonLeafKind.CheckBox, checkBox.Id, checkBox.Text, IsCheckable(checkBox), checkBox.Name);
            case RibbonMenuItem menuItem:          // incl. UIFramework.RvtLargeMenuItem
                return RibbonNode<RibbonItem>.Leaf(RibbonLeafKind.MenuItem, menuItem.Id, menuItem.Text, IsCheckable(menuItem), menuItem.Name);
            case RibbonCommandItem command:
                return RibbonNode<RibbonItem>.Leaf(RibbonLeafKind.CommandItem, command.Id, command.Text, IsCheckable(command), command.Name);
            default:
                // Separators, row and panel breaks, labels, text boxes, combos, galleries.
                return RibbonNode<RibbonItem>.Ignored;
        }
    }

    /// <summary>
    /// A container's part of a place path, as the census writes it (src/RST.Census/Ribbon.cs, Rec): "/", the
    /// container's runtime type name, then its Id in brackets, or its text (Text, else Name, else Id) when the Id is
    /// empty; tabs and line breaks become spaces and the bracketed part is trimmed.
    /// </summary>
    internal static string Segment(RibbonItem container)
    {
        var id = container.Id;
        var inner = !string.IsNullOrEmpty(id) ? id : container.Text ?? container.Name ?? container.Id;
        return "/" + container.GetType().Name + "(" + CensusText(inner) + ")";
    }

    private static string CensusText(string? value) =>
        (value ?? "").Replace("\t", " ").Replace("\r", " ").Replace("\n", " ").Trim();

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
