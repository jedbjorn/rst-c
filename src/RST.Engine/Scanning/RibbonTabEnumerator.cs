// RibbonTabEnumerator.cs — list distinct non-contextual ribbon tab titles
// for the RSTify "Hide These Tabs" picker in the Loader UI.
//
// Mirrors upstream pyRevit RST/RST.tab/Loader.panel/ProfileLoader.pushbutton/
// script.py:60-77 — same Autodesk.Windows ribbon walk, same skip rules:
//   - blank Title
//   - tab.IsContextualTab (Modify | Walls etc.)
//   - RstManagedTabs (the RST tab + the active profile's tab)
//   - ModeRestrictedTabs (Family Editor, In-Place Mass/Model, Zone)
//   - duplicates
//
// Returned in insertion order so the picker mirrors what the user sees on
// the ribbon. Cheap — re-walked on every call (~30-60 tabs); no caching
// needed because the picker only opens via the modal Loader.

using System.Collections.Generic;
using Autodesk.Windows;
using RST.Core.Scanning;
using RST.Engine.Ribbon;
using Serilog;

namespace RST.Engine.Scanning;

internal static class RibbonTabEnumerator
{
    public static IReadOnlyList<string> Enumerate()
    {
        var ribbon = ComponentManager.Ribbon;
        if (ribbon is null)
        {
            Log.Warning("RibbonTabEnumerator: ComponentManager.Ribbon is null — returning empty list");
            return System.Array.Empty<string>();
        }

        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        var ordered = new List<string>();
        var skippedContextual = 0;
        var skippedRst = 0;
        var skippedRestricted = 0;

        foreach (var tab in ribbon.Tabs)
        {
            if (tab is null) continue;
            var title = tab.Title;
            if (string.IsNullOrEmpty(title)) continue;
            if (tab.IsContextualTab) { skippedContextual++; continue; }
            if (RstManagedTabs.Contains(title)) { skippedRst++; continue; }
            if (ModeRestrictedTabs.Contains(title)) { skippedRestricted++; continue; }
            if (!seen.Add(title!)) continue;
            ordered.Add(title!);
        }

        Log.Information("RibbonTabEnumerator: {Returned} tabs (skipped: contextual={Ctx}, rst={Rst}, restricted={R})",
                        ordered.Count, skippedContextual, skippedRst, skippedRestricted);
        return ordered;
    }
}
