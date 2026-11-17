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
        // Each tab's and panel's visibility is read once per open, not once per place (many places share a panel).
        // The guarded gate (AskLive) still reads them afresh before every CanExecute.
        var tabs = new Dictionary<RibbonTab, bool>(ReferenceEqualityComparer.Instance);
        var panels = new Dictionary<RibbonPanel, bool>(ReferenceEqualityComparer.Instance);
        return SearchRunnable.ListOrFallBack(Tools, isFamily,
            place =>
            {
                var handle = Handles[place.Handle];
                if (!tabs.TryGetValue(handle.Tab, out var tab)) tabs[handle.Tab] = tab = handle.Tab.IsVisible;
                if (!panels.TryGetValue(handle.Panel, out var panel)) panels[handle.Panel] = panel = handle.Panel.IsVisible;
                return new PlaceState(tab, panel);
            },
            place => GateWhileListing(place.Handle, ref handlerThrowLogged),
            census);
    }

    /// <summary>A place's tab and panel visibility, read now (a click's choice of place, decision #127). Throws on a stale handle.</summary>
    public (bool Tab, bool Panel) VisibilityNow(int handle)
    {
        var place = Handles[handle];
        return (place.Tab.IsVisible, place.Panel.IsVisible);
    }

    /// <summary>
    /// A click on a runnable card (spec #35, Running a Tool, steps 4-5; decision #127): the place chosen now from fresh
    /// visibility and asked through <see cref="AskLive"/>.
    /// </summary>
    public ClickDecision DecideClick(SearchTool tool) =>
        SearchClick.Decide(tool, VisibilityNow, h => (AskLive(h, out var error), error));

    /// <summary>The ribbon item behind a handle, or null when there is none (a stale handle).</summary>
    public RibbonItem? ItemAt(int? handle) =>
        handle is { } h && h >= 0 && h < Handles.Count ? Handles[h].Item : null;

    /// <summary>
    /// The one way Search-It asks Revit's live gate about a ribbon item (<see cref="SearchGate"/>): the item's live tab
    /// and panel are read first, and an item that is not showing is never asked (asking a hidden family tab's item
    /// crashed Revit: WU1c, WU1d). An add-in's button answers with IsEnabled AND its own handler, as the census asked;
    /// Revit's items with the ribbon's global handler (<see cref="NativeGate"/>). Never throws.
    /// </summary>
    public GateAnswer AskLive(int? handle, out Exception? error)
    {
        if (handle is not { } h || h < 0 || h >= Handles.Count)
        {
            error = null;
            return GateAnswer.NotShowing;
        }
        var place = Handles[h];
        return SearchGate.Ask(() => place.Tab.IsVisible, () => place.Panel.IsVisible, () => CanExecute(place.Item), out error);
    }

    /// <summary>
    /// The gate while listing. An add-in's handler that throws counts as "cannot run" (spec #33, Debug once per open);
    /// Revit's handler that throws is passed on, and SearchRunnable counts that tool runnable with one Warning (the
    /// Planner's ruling on AC4).
    /// </summary>
    private bool? GateWhileListing(int handle, ref bool handlerThrowLogged)
    {
        switch (AskLive(handle, out var error))
        {
            case GateAnswer.Runs: return true;
            case GateAnswer.Greyed: return false;
            case GateAnswer.NothingToAsk: return null;
            case GateAnswer.NotShowing: return false;
            default:
                var item = Handles[handle].Item;
                if (item is RibbonCommandItem button && PostCommandRule.IsExternalCommandId(button.Id))
                {
                    if (!handlerThrowLogged)
                    {
                        handlerThrowLogged = true;
                        Log.Debug(error, "Search-It: CanExecute threw for {CommandId}; a handler that throws counts as not runnable (logged once per open)", button.Id);
                    }
                    return false;
                }
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error!).Throw();
                return null; // not reached
        }
    }

    private bool? CanExecute(RibbonItem item)
    {
        if (item is RibbonCommandItem button && PostCommandRule.IsExternalCommandId(button.Id))
        {
            // As the census asked (src/RST.Census/Ribbon.cs, Gate): IsEnabled first, then the add-in's own handler.
            if (!button.IsEnabled) return false;
            return button.CommandHandler is { } handler ? handler.CanExecute(button) : null;
        }
        return NativeGate(item);
    }

    /// <summary>
    /// Route A (decision #92): asks the live gate again through <see cref="AskLive"/> and, only when Revit says the item
    /// runs, calls the ribbon's command path on it at once. Returns what the gate said; a throw from Execute is passed on.
    /// </summary>
    public GateAnswer ExecuteLive(int? handle, Action<RibbonItem> execute, out Exception? error)
    {
        var answer = AskLive(handle, out error);
        if (SearchGate.AllowsExecute(answer)) execute(Handles[handle!.Value].Item);
        return answer;
    }

    /// <summary>Revit's ribbon handler: <c>ComponentManager.CommandHandler.CanExecute(item)</c>; null when there is no handler.</summary>
    internal static bool? GlobalCanExecute(RibbonItem item) =>
        ComponentManager.CommandHandler is { } handler ? handler.CanExecute(item) : null;
}
