// rst-c's own (not vendored): which search-catalogue places are listed when Search-It opens
// (spec #33, "Showing Rule"; decision #88).
//
// Search-It lists a tool when one of its ribbon places is on a panel Revit is showing now. The rule
// is pure: the host reads the live ribbon (a tab's and a panel's IsVisible, an add-in button's
// CanExecute) and the active document's kind, and hands them over as plain values.
//
//   A place shows when its panel is visible, and
//     - on a family-editor tab: that tab is visible;
//     - on any other tab, in a project: nothing more. A project tab the user has hidden in Revit's
//       options (Structure, Systems …) still counts: its tools run, and Search-It exists for the tool
//       the user cannot find;
//     - on any other tab, in a family: that tab is visible.
//   An add-in button that says it cannot run now (CanExecute false) does not show.
//
// Revit's own buttons do not report whether they are greyed, so a Revit tool on a showing panel is
// listed even when the current view greys it (spec #33, Out of Scope).

using RST.Core.Logging;

namespace RST.Core.Search;

/// <summary>What the ribbon says about one place when Search-It opens.</summary>
/// <param name="TabVisible">RibbonTab.IsVisible.</param>
/// <param name="PanelVisible">RibbonPanel.IsVisible.</param>
/// <param name="CanExecute">An add-in button's CommandHandler.CanExecute; null when the place has no handler to ask.</param>
public readonly record struct PlaceState(bool TabVisible, bool PanelVisible, bool? CanExecute = null)
{
    /// <summary>True when a place in this state is showing (the table in the file header).</summary>
    /// <param name="onFamilyEditorTab">The place is on a family-editor tab (<see cref="SearchCatalogPlace.OnFamilyEditorTab"/>).</param>
    /// <param name="isFamily">The active document is a family; a project template is a project.</param>
    public bool Shows(bool onFamilyEditorTab, bool isFamily) =>
        PanelVisible
        && (TabVisible || (!onFamilyEditorTab && !isFamily))
        && CanExecute != false;
}

/// <summary>The tools Search-It lists for the open file.</summary>
/// <param name="IsFamily">The list is a family's: the window names it ("12 of 240 family tools").</param>
public sealed record SearchList(IReadOnlyList<SearchTool> Tools, bool IsFamily = false)
{
    /// <summary>Catalogue tools with no showing place (not counted in <see cref="Tools"/>).</summary>
    public int Hidden { get; init; }

    /// <summary>Showing tools that run nowhere in this file, by the live gate and the census (spec #35): not listed.</summary>
    public int LeftOut { get; init; }

    /// <summary>The census applied to this list; null when none was (no key, no index, or it could not be read).</summary>
    public SearchCensus? Census { get; init; }

    /// <summary>Listed tools badged Must Run In.</summary>
    public int MustRunInCount => Tools.Count(t => t.Mark == SearchToolMark.MustRunIn);

    /// <summary>Listed tools badged NOT IN GUI.</summary>
    public int NotInGuiCount => Tools.Count(t => t.Mark == SearchToolMark.NotInGui);
}

public static class SearchShowing
{
    /// <summary>
    /// The tools with at least one showing place, in catalogue order, each with its showing places only:
    /// the first is the tool's name and place, the rest are its "+N more places".
    /// </summary>
    /// <param name="stateOf">Reads one place's state from the live ribbon (by <see cref="SearchCatalogPlace.Handle"/>).</param>
    public static SearchList List(IReadOnlyList<SearchCatalogTool> catalog, bool isFamily, Func<SearchCatalogPlace, PlaceState> stateOf)
    {
        var tools = new List<SearchTool>(catalog.Count);
        var hidden = 0;
        foreach (var tool in catalog)
        {
            var showing = new List<(SearchCatalogPlace Place, bool TabVisible)>(tool.Places.Count);
            foreach (var place in tool.Places)
                if (Showing(place, isFamily, stateOf) is { } shown) showing.Add(shown);
            if (showing.Count == 0)
            {
                hidden++;
                continue;
            }
            // Spec #35 (review): the fallback keeps how a click runs the tool, so a non-postable tool still goes
            // through route A, on an item that showed.
            var run = showing.Find(s => s.TabVisible) is { Place: { } p } ? p : showing[0].Place;
            tools.Add(new SearchTool(tool.Id, showing.Select(s => s.Place.ToSearchPlace()))
            {
                Postable = tool.Postable,
                RunHandle = run.Handle,
                GateAtClick = showing.Exists(s => s.TabVisible) || !tool.Postable,
            });
        }
        return new SearchList(tools, isFamily) { Hidden = hidden };
    }

    /// <summary>
    /// The place as it shows now, or null when it does not (decision #125). A place is one (tab, panel) by title, and
    /// Revit can keep two live panels of the same title on a tab (Create › Datum in a family: Level in a fold panel on
    /// one, directly on the other; each family kind shows one of them). The place shows when any copy shows; what
    /// comes back keeps only the showing copies, its <see cref="SearchCatalogPlace.Handle"/> the first of them, so the
    /// gate, the not-drawn check and route A all use an item Revit is showing.
    /// </summary>
    internal static (SearchCatalogPlace Place, bool TabVisible)? Showing(SearchCatalogPlace place, bool isFamily,
        Func<SearchCatalogPlace, PlaceState> stateOf)
    {
        var copies = place.CopiesOrSelf;
        List<SearchPlaceCopy>? shown = null;
        var tabVisible = false;
        foreach (var copy in copies)
        {
            var at = copy.Handle == place.Handle ? place : place with { Handle = copy.Handle };
            var state = stateOf(at);
            if (!state.Shows(place.OnFamilyEditorTab, isFamily)) continue;
            if (shown is null) tabVisible = state.TabVisible;
            (shown ??= new List<SearchPlaceCopy>(copies.Count)).Add(copy);
        }
        if (shown is null) return null;
        if (shown.Count == copies.Count && shown[0].Handle == place.Handle) return (place, tabVisible);
        return (place with { Handle = shown[0].Handle, Copies = shown }, tabVisible);
    }

    /// <summary>
    /// <see cref="List"/>, and when the ribbon's state cannot be read, <see cref="Unfiltered"/> with one
    /// Warning: a failure there never stops Search-It from opening.
    /// </summary>
    public static SearchList ListOrFallBack(IReadOnlyList<SearchCatalogTool> catalog, bool isFamily, Func<SearchCatalogPlace, PlaceState> stateOf)
    {
        try
        {
            return List(catalog, isFamily, stateOf);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Search-It: the ribbon's state could not be read (list={List}); listing the unfiltered tools",
                isFamily ? "family" : "project");
            return Unfiltered(catalog, isFamily);
        }
    }

    /// <summary>
    /// The list when the ribbon's state is unknown (Planner's ruling on spec #33, Sprint 1 message #22):
    ///   - in a project: every tool with a place outside the family-editor tabs, with all of those
    ///     places. That is the list Search-It showed before it followed the open file;
    ///   - in a family: the whole search catalogue, family-editor tabs included. The family's tools
    ///     must be there, and telling which project tools to leave out is the reading that just
    ///     failed, so they stay (decision #90: list a tool when we cannot tell whether it runs).
    /// Neither is named a family's: the family one mixes project and family tools, so "Project tools
    /// are not listed while a family is open" would be false.
    /// </summary>
    public static SearchList Unfiltered(IReadOnlyList<SearchCatalogTool> catalog, bool isFamily)
    {
        var tools = new List<SearchTool>(catalog.Count);
        foreach (var tool in catalog)
        {
            var kept = tool.Places.Where(p => isFamily || !p.OnFamilyEditorTab).ToList();
            if (kept.Count == 0) continue;
            // The ribbon's state is unknown: a postable tool is posted as before spec #35; a click on any other asks
            // the live gate, which re-reads its item's tab and panel first (SearchGate).
            tools.Add(new SearchTool(tool.Id, kept.Select(p => p.ToSearchPlace()))
            {
                Postable = tool.Postable,
                RunHandle = kept[0].Handle,
                GateAtClick = !tool.Postable,
            });
        }
        return new SearchList(tools);
    }
}
