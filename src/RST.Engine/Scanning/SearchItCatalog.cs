// SearchItCatalog.cs — Search-It's catalogue with the live ribbon behind it (spec #33).
//
// CommandCatalog.Build makes it in the same ribbon scan as the Builder's catalogue, and RstApplication
// caches it beside that one. Each place of a tool carries a handle: the index of the scan hit it came
// from, which is also the index of the tab, panel and item RibbonScanner kept for that hit. Revit keeps
// those objects for the whole session (Work Unit 1: the same 1,920 items in every context), so when
// Search-It opens the showing rule (RST.Core.Search.SearchShowing) reads their state here and the
// ribbon is not walked again.
//
// What is read per place (spec #35, "Live gate"):
//   - RibbonTab.IsVisible and RibbonPanel.IsVisible;
//   - for a place on a visible tab only, the live gate: for an add-in's button (PostCommandRule.IsExternalCommandId)
//     its own command handler's CanExecute AND the item's IsEnabled, which the drawn greyed state follows; for
//     Revit's own items the ribbon's global handler, Autodesk.Windows.ComponentManager.CommandHandler.CanExecute(item)
//     (WU1c: it matches the drawn state in every context and both years). Never for an item on a hidden tab: asking
//     it for one on a hidden family tab crashed Revit (WU1c, WU1d). RST.Core.Search.SearchRunnable decides which
//     places are asked, and asks a Revit tool once.
//   - Revit's own buttons' own CommandHandler (a UIFramework visibility binding on a few of them) is never asked:
//     it does not say whether the tool can run.

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
    /// The ribbon's global gate for Revit's own items; null when there is none to ask. Tests replace it (outside Revit
    /// there is no Revit handler behind <see cref="ComponentManager.CommandHandler"/>).
    /// </summary>
    public Func<RibbonItem, bool?> NativeGate { get; init; } = GlobalCanExecute;

    /// <summary>
    /// The tools to list now, for a family or a project, judged by the live gate and, when <paramref name="census"/>
    /// gives one, by the census for the open file and view (spec #35). Failures fall back as
    /// <see cref="SearchRunnable.ListOrFallBack"/> says, each with one Warning: Search-It always opens.
    /// </summary>
    public SearchList ListShowing(bool isFamily, Func<SearchCensus?>? census = null)
    {
        var handlerThrowLogged = false;
        var gateThrowLogged = false;
        return SearchRunnable.ListOrFallBack(Tools, isFamily,
            place =>
            {
                var handle = Handles[place.Handle];
                return new PlaceState(handle.Tab.IsVisible, handle.Panel.IsVisible);
            },
            place => Gate(Handles[place.Handle].Item, ref handlerThrowLogged, ref gateThrowLogged),
            census);
    }

    /// <summary>The ribbon item behind a handle, or null when there is none (a stale handle).</summary>
    public RibbonItem? ItemAt(int? handle) =>
        handle is { } h && h >= 0 && h < Handles.Count ? Handles[h].Item : null;

    /// <summary>
    /// The live gate of one item (file header). An add-in's handler that throws counts as "cannot run"; Revit's global
    /// handler that throws counts as "can't tell" (null: listed, decision #92). The first throw of each kind in an open
    /// is logged at Debug.
    /// </summary>
    private bool? Gate(RibbonItem item, ref bool handlerThrowLogged, ref bool gateThrowLogged)
    {
        if (item is RibbonCommandItem button && PostCommandRule.IsExternalCommandId(button.Id))
        {
            try
            {
                // As the census asked (src/RST.Census/Ribbon.cs, Gate): IsEnabled first, then the add-in's own handler.
                if (!button.IsEnabled) return false;
                return button.CommandHandler is { } handler ? handler.CanExecute(button) : null;
            }
            catch (Exception ex)
            {
                if (!handlerThrowLogged)
                {
                    handlerThrowLogged = true;
                    Log.Debug(ex, "Search-It: CanExecute threw for {CommandId}; a handler that throws counts as not runnable (logged once per open)", button.Id);
                }
                return false;
            }
        }
        try
        {
            return NativeGate(item);
        }
        catch (Exception ex)
        {
            if (!gateThrowLogged)
            {
                gateThrowLogged = true;
                Log.Debug(ex, "Search-It: Revit's CanExecute threw for {CommandId}; counted as runnable (logged once per open)", item.Id);
            }
            return null;
        }
    }

    /// <summary>Revit's ribbon handler: <c>ComponentManager.CommandHandler.CanExecute(item)</c>; null when there is no handler.</summary>
    internal static bool? GlobalCanExecute(RibbonItem item) =>
        ComponentManager.CommandHandler is { } handler ? handler.CanExecute(item) : null;
}
