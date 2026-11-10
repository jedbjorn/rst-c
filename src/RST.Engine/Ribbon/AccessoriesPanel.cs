// AccessoriesPanel.cs — gather the Designs/OS sister apps' buttons into one "RST Accessories" panel
// (decision #79).
//
// Gantt-It, Splash-It and Pick-It each create their own panel on Revit's Add-Ins tab at OnStartup. Once every
// add-in has loaded (ApplicationInitialized), RST moves those buttons into one panel placed right after the RST
// tools panel, and hides each panel it emptied. The buttons are the same AdWindows items, only re-parented: their
// ids, and so their commands, saved profile slots and the apps' own references to them, are unchanged. The sister
// apps know nothing of this; without RST they keep their own panels. What counts as a sister button is
// RST.Core.Ribbon.AccessoryPanels.
//
// Nothing here throws: a ribbon that will not be rearranged is left as the apps built it.
//
// Thread model: mutates ComponentManager.Ribbon, so the Revit UI thread only (ApplicationInitialized).

using System;
using System.Collections.Generic;
using System.Linq;
using RST.Core.Logging;
using RST.Core.Ribbon;
using AwComponentManager = Autodesk.Windows.ComponentManager;
using AwRibbonItem = Autodesk.Windows.RibbonItem;
using AwRibbonPanel = Autodesk.Windows.RibbonPanel;
using AwRibbonPanelSource = Autodesk.Windows.RibbonPanelSource;
using AwRibbonRowPanel = Autodesk.Windows.RibbonRowPanel;
using AwRibbonTab = Autodesk.Windows.RibbonTab;

namespace RST.Engine.Ribbon;

internal static class AccessoriesPanel
{
    /// <summary>Gathers the sister apps' buttons on the tab that holds the panel titled <paramref name="anchorPanelTitle"/>.</summary>
    public static void GatherBeside(string anchorPanelTitle)
    {
        try
        {
            var tab = AwComponentManager.Ribbon?.Tabs.FirstOrDefault(t => t is not null && IndexOfPanel(t, anchorPanelTitle) >= 0);
            if (tab is null)
            {
                Log.Debug("AccessoriesPanel: no tab holds the {Panel} panel; sister apps keep their own panels", anchorPanelTitle);
                return;
            }
            var moved = Gather(tab, anchorPanelTitle);
            if (moved > 0) Log.Information("AccessoriesPanel: gathered {Count} sister-app button(s) into {Panel}", moved, AccessoryPanels.PanelTitle);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "AccessoriesPanel: could not gather the sister apps' buttons; the ribbon is left as it was");
        }
    }

    /// <summary>
    /// Moves every sister-app button on <paramref name="tab"/> into the accessories panel, creating it after the
    /// anchor panel on first use, and hides each sister panel left empty. Returns how many items moved (0: no
    /// sister app is on the tab, and no panel is created). Safe to run again.
    /// </summary>
    internal static int Gather(AwRibbonTab tab, string anchorPanelTitle)
    {
        var picks = new List<(AwRibbonPanel Panel, AwRibbonItem Item)>();
        foreach (var title in AccessoryPanels.SisterPanelTitles)
            foreach (var panel in tab.Panels)
            {
                if (panel?.Source is not { } source || source.Id == AccessoryPanels.PanelId) continue;
                if (!string.Equals(source.Title, title, StringComparison.Ordinal)) continue;
                picks.AddRange(source.Items.Where(item => IsSisterItem(title, item)).Select(item => (panel, item)));
            }
        if (picks.Count == 0) return 0;

        var target = tab.Panels.FirstOrDefault(p => p?.Source?.Id == AccessoryPanels.PanelId)?.Source;
        if (target is null)
        {
            target = new AwRibbonPanelSource { Title = AccessoryPanels.PanelTitle, Name = AccessoryPanels.PanelTitle, Id = AccessoryPanels.PanelId };
            tab.Panels.Insert(IndexOfPanel(tab, anchorPanelTitle) + 1, new AwRibbonPanel { Source = target });
        }

        var moved = 0;
        foreach (var (panel, item) in picks)
        {
            var source = panel.Source;
            var index = source.Items.IndexOf(item);
            source.Items.RemoveAt(index);
            try
            {
                target.Items.Add(item);
                moved++;
            }
            catch (Exception ex)
            {
                // Never lose a button: it goes back where its app put it.
                source.Items.Insert(index, item);
                Log.Warning(ex, "AccessoriesPanel: {Item} stays on {Panel}", item.Id, source.Title);
            }
        }
        foreach (var panel in picks.Select(p => p.Panel).Distinct())
            if (panel.Source.Items.Count == 0) panel.IsVisible = false;
        return moved;
    }

    /// <summary>A sister button, or a stack (row panel) holding one.</summary>
    private static bool IsSisterItem(string panelTitle, AwRibbonItem? item) => item switch
    {
        null => false,
        AwRibbonRowPanel row => row.Items.Any(child => IsSisterItem(panelTitle, child)),
        _ => AccessoryPanels.IsSisterButton(panelTitle, item.Id),
    };

    private static int IndexOfPanel(AwRibbonTab tab, string panelTitle)
    {
        for (var i = 0; i < tab.Panels.Count; i++)
            if (string.Equals(tab.Panels[i]?.Source?.Title, panelTitle, StringComparison.Ordinal)) return i;
        return -1;
    }
}
