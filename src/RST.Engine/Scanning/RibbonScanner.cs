// RibbonScanner.cs — walk the live Revit ribbon to enumerate add-in commands.
//
// Revit assigns each add-in pushbutton a CommandId of the form
//   CustomCtrl_%TabName%PanelName%ButtonName
// which the Loader feeds back to RevitCommandId.LookupCommandId() to post.
//
// The walk also surfaces (Tab, Panel) for each button, so profiles can
// reference a command by source location when the same DLL ships multiple
// buttons with similar names.
//
// Built-in tabs are skipped — built-in commands come from
// BuiltinCommandScanner, which has authoritative IDs.

using System.Collections.Generic;
using Autodesk.Windows;
using RST.Core.Scanning;

namespace RST.Engine.Scanning;

internal static class RibbonScanner
{
    public static IEnumerable<ScannedCommand> Enumerate()
    {
        var ribbon = ComponentManager.Ribbon;
        if (ribbon is null) yield break;

        foreach (var tab in ribbon.Tabs)
        {
            if (tab is null) continue;
            if (BuiltinTabs.Contains(tab.Title)) continue;

            foreach (var panel in tab.Panels)
            {
                var source = panel?.Source;
                if (source is null) continue;

                foreach (var item in source.Items)
                {
                    foreach (var cmd in EnumerateItem(item, tab.Title, source.Title))
                        yield return cmd;
                }
            }
        }
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
