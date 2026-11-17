// rst-c's own (not vendored): which showing tools Search-It lists, and with which label (Feature #21, spec #35,
// "Runnable Only"; decisions #119, #120).
//
// Spec #33's showing rule still decides the candidates: a tool with a place on a panel Revit shows now
// (SearchShowing, PlaceState.Shows). Spec #35 then asks, for each candidate, whether it runs here:
//
//   Runs here    Revit's live CanExecute is true at a showing place, and the census does not mark the id
//                not-runnable in this context.
//
//   | Result               | When                                                         | In Search-It                   |
//   |----------------------|--------------------------------------------------------------|--------------------------------|
//   | Runnable             | Runs here, at a drawn place                                  | listed; a click runs it        |
//   | NOT IN GUI           | Runs here, and every place where it runs is census not-drawn | listed, amber badge; runs      |
//   | Must Run In          | Does not run here; the runs-in list has it in another view   | listed, dimmed, badge; never   |
//   |                      | of this file                                                 | runs from Search-It            |
//   | Runs nowhere here    | Does not run here, and no other view of this file runs it    | not listed                     |
//
// The live gate (the host's CanExecute) is asked only for places on a visible tab: asking it for an item on a hidden
// family tab crashed Revit (WU1d). A place on a project tab the user hid is not asked. For Revit's own tools the
// answer is the id's, not the place's: the ribbon's global handler answers per command (WU1c: "the same id in several
// places gave the same answer"), so it is asked once, at the tool's first showing place on a visible tab, and that
// answer holds for the tool's places on hidden project tabs too. A Revit tool with no showing place on a visible tab is
// not asked and counts as runnable (spec #33, AC12: Structural Column with the Structure tab hidden). An add-in's
// button answers for itself (its own handler AND IsEnabled), place by place, as spec #33 did.
//
// No census (no context key for this view, no index for this Revit year): nothing is removed, badged or labelled on
// the census's account; the live gate still applies (spec #35, "No key, or no index for the year").
//
// Pure: the host hands over the ribbon's state and the census as plain values and callbacks.

using RST.Core.Census;
using RST.Core.Logging;
using RST.Core.Scanning;

namespace RST.Core.Search;

/// <summary>The census, as one open of Search-It uses it: the context key of the open file and view, and that year's lists.</summary>
public sealed record SearchCensus(CensusContextKey Key, CensusIndex Index, CensusRunsIn RunsIn, CensusContexts Contexts)
{
    /// <summary>
    /// The views of this file, other than the current one (the key's, after any fallback), where the census saw
    /// <paramref name="id"/> start: named and ordered as the Must Run In badge shows them. Empty when there are none.
    /// </summary>
    public IReadOnlyList<string> MustRunIn(string id)
    {
        var where = RunsIn.ContextsOf(id);
        if (where.Count == 0) return Array.Empty<string>();
        var views = new List<string>();
        foreach (var row in Contexts.SameFile(Key))
        {
            if (row.Context == Key.Key) continue;
            if (where.Contains(row.Context)) views.Add(row.ViewType);
        }
        return views.Count == 0 ? Array.Empty<string>() : SearchViews.Ordered(views);
    }

    /// <summary>
    /// The census for the open file and view, or null when there is none to apply: the year has no index, or the file
    /// and view have no context key.
    /// </summary>
    public static SearchCensus? For(int year, CensusDocument document) =>
        For(CensusIndex.For(year), CensusRunsIn.For(year), CensusContexts.For(year), document);

    /// <inheritdoc cref="For(int, CensusDocument)"/>
    public static SearchCensus? For(CensusIndex index, CensusRunsIn runsIn, CensusContexts contexts, CensusDocument document)
    {
        if (index.IsEmpty || runsIn.IsEmpty || contexts.IsEmpty) return null;
        var key = contexts.Resolve(document);
        return key is null ? null : new SearchCensus(key, index, runsIn, contexts);
    }
}

public static class SearchRunnable
{
    /// <summary>
    /// The tools Search-It lists for the open file (spec #35's table, file header), in catalogue order.
    /// </summary>
    /// <param name="visibility">A place's tab and panel visibility (its <see cref="PlaceState.CanExecute"/> is not read).</param>
    /// <param name="gate">
    /// The live CanExecute of a place's ribbon item: Revit's global handler for Revit's items, the add-in's own handler
    /// AND IsEnabled for an add-in's. Null when there is nothing to ask. Called only for places on a visible tab.
    /// </param>
    /// <param name="census">The census for the open file and view; null applies none.</param>
    public static SearchList List(IReadOnlyList<SearchCatalogTool> catalog, bool isFamily,
        Func<SearchCatalogPlace, PlaceState> visibility, Func<SearchCatalogPlace, bool?> gate, SearchCensus? census)
    {
        var tools = new List<SearchTool>(catalog.Count);
        var hidden = 0;
        var leftOut = 0;
        foreach (var tool in catalog)
        {
            var showing = new List<(SearchCatalogPlace Place, bool TabVisible)>(tool.Places.Count);
            foreach (var place in tool.Places)
            {
                var state = visibility(place) with { CanExecute = null };
                if (state.Shows(place.OnFamilyEditorTab, isFamily)) showing.Add((place, state.TabVisible));
            }
            if (showing.Count == 0)
            {
                hidden++;
                continue;
            }
            var judged = Judge(tool, showing, gate, census);
            if (judged is null) leftOut++;
            else tools.Add(judged);
        }
        return new SearchList(tools, isFamily) { Hidden = hidden, LeftOut = leftOut, Census = census };
    }

    private static SearchTool? Judge(SearchCatalogTool tool, List<(SearchCatalogPlace Place, bool TabVisible)> showing,
        Func<SearchCatalogPlace, bool?> gate, SearchCensus? census)
    {
        // The live gate (file header): per id for Revit's tools, per place for an add-in's buttons.
        var runsAt = new bool[showing.Count];
        if (PostCommandRule.IsExternalCommandId(tool.Id))
        {
            for (var i = 0; i < showing.Count; i++)
                runsAt[i] = !showing[i].TabVisible || gate(showing[i].Place) != false;
        }
        else
        {
            var asked = showing.FindIndex(s => s.TabVisible);
            var answer = asked < 0 ? null : gate(showing[asked].Place);
            Array.Fill(runsAt, answer != false);
        }

        var key = census?.Key.Key;
        var notRunnable = census is not null
            && census.Index.Mark(key, tool.Id, default).Kind == CensusMarkKind.NotRunnable;

        var runs = new List<SearchCatalogPlace>(showing.Count);
        if (!notRunnable)
            for (var i = 0; i < showing.Count; i++)
                if (runsAt[i]) runs.Add(showing[i].Place);

        if (runs.Count > 0)
        {
            var drawn = census is null ? runs : runs.Where(p => !NotDrawn(census, tool.Id, p)).ToList();
            var mark = drawn.Count > 0 ? SearchToolMark.None : SearchToolMark.NotInGui;
            var places = drawn.Count > 0 ? drawn : runs;
            return Make(tool, places, census, mark, Array.Empty<string>());
        }

        var views = census?.MustRunIn(tool.Id) ?? Array.Empty<string>();
        if (views.Count == 0) return null;
        var all = showing.ConvertAll(s => s.Place);
        var drawnShowing = all.Where(p => !NotDrawn(census!, tool.Id, p)).ToList();
        return Make(tool, drawnShowing.Count > 0 ? drawnShowing : all, census, SearchToolMark.MustRunIn, views);
    }

    /// <summary>True when the census marks every copy of the place not drawn in this context.</summary>
    private static bool NotDrawn(SearchCensus census, string id, SearchCatalogPlace place)
    {
        foreach (var copy in place.CopiesOrSelf)
            if (census.Index.Mark(census.Key.Key, id, new CensusPlace(place.TabId, place.Panel, copy.Path)).Kind != CensusMarkKind.NotDrawn)
                return false;
        return true;
    }

    /// <summary>The copy a click runs through route A: the place's first copy that is drawn, else its first.</summary>
    private static int RunCopy(SearchCensus? census, string id, SearchCatalogPlace place)
    {
        var copies = place.CopiesOrSelf;
        if (census is null) return copies[0].Handle;
        foreach (var copy in copies)
            if (census.Index.Mark(census.Key.Key, id, new CensusPlace(place.TabId, place.Panel, copy.Path)).Kind != CensusMarkKind.NotDrawn)
                return copy.Handle;
        return copies[0].Handle;
    }

    private static SearchTool Make(SearchCatalogTool tool, List<SearchCatalogPlace> places, SearchCensus? census,
        SearchToolMark mark, IReadOnlyList<string> mustRunIn) =>
        new(tool.Id, places.Select(p => p.ToSearchPlace()))
        {
            Mark = mark,
            MustRunIn = mustRunIn,
            Postable = tool.Postable,
            RunHandle = mark == SearchToolMark.MustRunIn ? null : RunCopy(census, tool.Id, places[0]),
        };

    /// <summary>
    /// <see cref="List"/>, made safe: a failure never stops Search-It (spec #35, AC4).
    /// <list type="bullet">
    /// <item>The census can't be read (the key or an index throws): the tools are judged by the live gate alone, unlabelled.</item>
    /// <item>The judging throws (the gate, or the census mid-way): the showing tools are listed unlabelled (spec #33's
    /// rule, nothing asked); if the ribbon's state can't be read either, <see cref="SearchShowing.Unfiltered"/>.</item>
    /// </list>
    /// Each failure is logged once, at Warning.
    /// </summary>
    public static SearchList ListOrFallBack(IReadOnlyList<SearchCatalogTool> catalog, bool isFamily,
        Func<SearchCatalogPlace, PlaceState> visibility, Func<SearchCatalogPlace, bool?> gate, Func<SearchCensus?>? census)
    {
        SearchCensus? applied = null;
        try
        {
            applied = census?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Search-It: the census could not be read (list={List}); listing by Revit's live check alone, unlabelled",
                isFamily ? "family" : "project");
        }
        try
        {
            return List(catalog, isFamily, visibility, gate, applied);
        }
        catch (Exception ex)
        {
            SearchList fallback;
            string what;
            try
            {
                fallback = SearchShowing.List(catalog, isFamily, p => visibility(p) with { CanExecute = null });
                what = "the showing tools, unlabelled";
            }
            catch (Exception)
            {
                fallback = SearchShowing.Unfiltered(catalog, isFamily);
                what = "the unfiltered tools";
            }
            Log.Warning(ex, "Search-It: the tools could not be judged (list={List}); listing {What}", isFamily ? "family" : "project", what);
            return fallback;
        }
    }
}
