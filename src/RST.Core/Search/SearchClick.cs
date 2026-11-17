// rst-c's own (not vendored): where a click on a runnable card runs the tool (Feature #21, spec #35, "Running a Tool",
// steps 4-5; decision #127).
//
// The place is chosen at the click, from fresh visibility, not when the list was made: a fallback list or a tab that
// showed or hid since then must not decide it. Of the tool's run places (drawn copies first), the first whose tab and
// panel show now is asked through the guarded gate (SearchGate) and, when it may run, that same place is run (route A)
// or the id posted. False, a throw, or nothing showing gives the amber message and the window stays open, with one
// exception (spec #33 AC12): when nothing shows and every project place is on a tab the user hid, a postable tool is
// posted without asking. Family-editor places are not counted there: Revit hides them in a project, not the user;
// and in a family's list nothing counts as user-hidden.

namespace RST.Core.Search;

/// <summary>What a click does.</summary>
public enum ClickRoute
{
    /// <summary>Run on <see cref="ClickDecision.Handle"/>: route A, or PostCommand for a postable id.</summary>
    Run,
    /// <summary>Post the id without asking: only on user-hidden project tabs (spec #33 AC12).</summary>
    PostWithoutAsking,
    /// <summary>The amber message; the window stays open.</summary>
    Refuse,
}

/// <param name="Handle">The place asked and run, for <see cref="ClickRoute.Run"/>.</param>
/// <param name="Answer">What the gate said (NotShowing when nothing was showing).</param>
/// <param name="Error">The throw, when the gate or a read threw.</param>
public readonly record struct ClickDecision(ClickRoute Route, int? Handle, GateAnswer Answer, Exception? Error = null);

public static class SearchClick
{
    /// <summary>
    /// Decides a click (file header). <paramref name="visibilityNow"/> reads a place's tab and panel now;
    /// <paramref name="ask"/> is the guarded gate (it reads them again itself). Never throws.
    /// </summary>
    public static ClickDecision Decide(SearchTool tool, Func<int, (bool Tab, bool Panel)> visibilityNow,
        Func<int, (GateAnswer Answer, Exception? Error)> ask)
    {
        if (!tool.CanRun || tool.RunPlaces.Count == 0) return new ClickDecision(ClickRoute.Refuse, null, GateAnswer.NotShowing);
        var everyProjectPlaceUserHidden = !tool.InFamily;
        var projectPlaces = 0;
        foreach (var place in tool.RunPlaces)
        {
            (bool Tab, bool Panel) now;
            try
            {
                now = visibilityNow(place.Handle);
            }
            catch (Exception)
            {
                everyProjectPlaceUserHidden = false;
                continue;
            }
            if (now.Tab && now.Panel)
            {
                var (answer, error) = ask(place.Handle);
                return SearchGate.AllowsClick(answer)
                    ? new ClickDecision(ClickRoute.Run, place.Handle, answer, error)
                    : new ClickDecision(ClickRoute.Refuse, place.Handle, answer, error);
            }
            if (place.OnFamilyEditorTab) continue;
            projectPlaces++;
            if (now.Tab) everyProjectPlaceUserHidden = false;
        }
        return tool.Postable && everyProjectPlaceUserHidden && projectPlaces > 0
            ? new ClickDecision(ClickRoute.PostWithoutAsking, null, GateAnswer.NotShowing)
            : new ClickDecision(ClickRoute.Refuse, null, GateAnswer.NotShowing);
    }

    /// <summary>
    /// The run places of a tool: every copy of every catalogue place, drawn ones first (<paramref name="drawn"/>: the
    /// census's view of one copy; null treats every copy as drawn), then the rest, each group in catalogue order.
    /// </summary>
    public static IReadOnlyList<SearchRunPlace> PlacesOf(SearchCatalogTool tool, Func<SearchCatalogPlace, SearchPlaceCopy, bool>? drawn = null)
    {
        var all = new List<SearchRunPlace>();
        foreach (var place in tool.Places)
            foreach (var copy in place.CopiesOrSelf)
                all.Add(new SearchRunPlace(copy.Handle, drawn?.Invoke(place, copy) ?? true, place.OnFamilyEditorTab));
        return all.Where(p => p.Drawn).Concat(all.Where(p => !p.Drawn)).ToList();
    }
}
