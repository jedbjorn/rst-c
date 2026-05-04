// RibbonScanner.cs — walk the live Revit ribbon to enumerate commands.
//
// The Id surfaced by Autodesk.Windows.RibbonButton.Id is treated as opaque:
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

using System.Collections.Generic;
using Autodesk.Windows;
using RST.Core.Scanning;
using RST.Engine.Ribbon;
using Serilog;

namespace RST.Engine.Scanning;

internal static class RibbonScanner
{
    public static IEnumerable<ScannedCommand> Enumerate()
    {
        var ribbon = ComponentManager.Ribbon;
        if (ribbon is null)
        {
            Log.Warning("RibbonScanner: ComponentManager.Ribbon is null — no add-in commands will be enumerated");
            yield break;
        }

        var tabsTotal = 0;
        var tabsWalked = 0;
        var tabsSkippedRestricted = 0;
        var tabsSkippedRst = 0;
        var buttonsEmitted = 0;

        foreach (var tab in ribbon.Tabs)
        {
            if (tab is null) continue;
            tabsTotal++;
            if (ModeRestrictedTabs.Contains(tab.Title)) { tabsSkippedRestricted++; continue; }
            // RST-managed tabs (the RST tab itself + the active profile's tab,
            // if any). RST built them this session, so reading them back
            // would surface our own buttons as profile-buildable commands.
            if (RstManagedTabs.Contains(tab.Title))     { tabsSkippedRst++; continue; }
            tabsWalked++;

            var perTab = 0;
            foreach (var panel in tab.Panels)
            {
                var source = panel?.Source;
                if (source is null) continue;

                foreach (var item in source.Items)
                {
                    foreach (var cmd in EnumerateItem(item, tab.Title, source.Title))
                    {
                        perTab++;
                        buttonsEmitted++;
                        yield return cmd;
                    }
                }
            }
            Log.Debug("RibbonScanner: tab={Tab} → {Count} buttons", tab.Title, perTab);
        }

        Log.Debug("RibbonScanner: tabsTotal={Total}, walked={Walked}, " +
                  "skippedRestricted={Restricted}, skippedRst={Rst}, buttons={Buttons}",
                  tabsTotal, tabsWalked, tabsSkippedRestricted, tabsSkippedRst, buttonsEmitted);
    }

    private static IEnumerable<ScannedCommand> EnumerateItem(
        RibbonItem? item, string tabTitle, string? panelTitle)
    {
        if (item is null) yield break;

        if (item is RibbonSplitButton split)
        {
            foreach (var sub in split.Items)
                foreach (var c in EnumerateItem(sub, tabTitle, panelTitle))
                    yield return c;
            yield break;
        }

        if (item is RibbonRowPanel row)
        {
            foreach (var sub in row.Items)
                foreach (var c in EnumerateItem(sub, tabTitle, panelTitle))
                    yield return c;
            yield break;
        }

        if (item is RibbonButton button)
        {
            var id = button.Id;
            if (string.IsNullOrEmpty(id)) yield break;

            yield return new ScannedCommand(
                Id: id!,
                DisplayName: button.Text ?? id!,
                Origin: CommandOrigin.Unknown,
                AddinFile: null,
                AssemblyPath: null,
                SourceTab: tabTitle,
                SourcePanel: panelTitle);
        }
    }
}
