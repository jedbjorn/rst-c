// SearchItCatalog.cs — Search-It's catalogue with the live ribbon behind it (spec #33).
//
// CommandCatalog.Build makes it in the same ribbon scan as the Builder's catalogue, and RstApplication
// caches it beside that one. Each place of a tool carries a handle: the index of the scan hit it came
// from, which is also the index of the tab, panel and item RibbonScanner kept for that hit. Revit keeps
// those objects for the whole session (Work Unit 1: the same 1,920 items in every context), so when
// Search-It opens the showing rule (RST.Core.Search.SearchShowing) reads their state here and the
// ribbon is not walked again.
//
// What is read per place:
//   - RibbonTab.IsVisible and RibbonPanel.IsVisible;
//   - for an add-in's button only (PostCommandRule.IsExternalCommandId), its command handler's
//     CanExecute, which follows the greyed state on screen. Revit's own buttons are not asked: most
//     have no handler, and the few that do (a UIFramework visibility binding) do not report whether
//     the tool can run, so asking would drop Revit tools the spec lists (Out of Scope: greyed Revit
//     tools stay listed).

using System;
using System.Collections.Generic;
using Autodesk.Windows;
using RST.Core.Logging;
using RST.Core.Scanning;
using RST.Core.Search;

namespace RST.Engine.Scanning;

/// <param name="Tools">The search catalogue (<see cref="SearchCatalog.Build"/>).</param>
/// <param name="Handles">The scan's live objects; <see cref="SearchCatalogPlace.Handle"/> indexes them.</param>
internal sealed record SearchItCatalog(IReadOnlyList<SearchCatalogTool> Tools, IReadOnlyList<RibbonPlaceHandle> Handles)
{
    public static readonly SearchItCatalog Empty = new(Array.Empty<SearchCatalogTool>(), Array.Empty<RibbonPlaceHandle>());

    /// <summary>
    /// The tools the ribbon is showing now, for a family or a project. When the ribbon's state cannot
    /// be read the unfiltered list for that kind comes back instead, with one Warning
    /// (<see cref="SearchShowing.ListOrFallBack"/>, <see cref="SearchShowing.Unfiltered"/>).
    /// </summary>
    public SearchList ListShowing(bool isFamily)
    {
        var handlerThrowLogged = false;
        return SearchShowing.ListOrFallBack(Tools, isFamily, place =>
        {
            var handle = Handles[place.Handle];
            return new PlaceState(handle.Tab.IsVisible, handle.Panel.IsVisible, CanExecute(handle.Item, ref handlerThrowLogged));
        });
    }

    /// <summary>
    /// What an add-in button's command handler says now; null when there is none to ask (every Revit
    /// button, and an add-in button without a handler). A handler that throws counts as "cannot run";
    /// the first one in an open is logged at Debug.
    /// </summary>
    private static bool? CanExecute(RibbonItem item, ref bool throwLogged)
    {
        if (item is not RibbonCommandItem button || !PostCommandRule.IsExternalCommandId(button.Id)) return null;
        try
        {
            return button.CommandHandler?.CanExecute(button);
        }
        catch (Exception ex)
        {
            if (!throwLogged)
            {
                throwLogged = true;
                Log.Debug(ex, "Search-It: CanExecute threw for {CommandId}; a handler that throws counts as not runnable (logged once per open)", button.Id);
            }
            return false;
        }
    }
}
