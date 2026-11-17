// rst-c's own (not vendored): the one rule every live CanExecute / Execute call on a ribbon item goes through
// (Feature #21, spec #35; review of PR #231).
//
// Asking Revit's ribbon handler for an item on a tab that is not showing crashed Revit (a project-only item on a
// hidden tab while a family was active: WU1c, WU1d). Search-It's list asks only for places on visible tabs, but a click
// comes later: the family may have closed, or the tab hidden, between the list and the click, or before a queued
// route-A run. So the item's live tab and panel are read again immediately before every call, and an item that is not
// showing is never asked. The host (RST.Engine's SearchItCatalog) supplies the reads; this decides.

namespace RST.Core.Search;

/// <summary>What asking the live gate gave.</summary>
public enum GateAnswer
{
    /// <summary>The handler says it can run.</summary>
    Runs,
    /// <summary>The handler says it can't (greyed), or the item is disabled.</summary>
    Greyed,
    /// <summary>There is no handler to ask.</summary>
    NothingToAsk,
    /// <summary>The item's tab or panel is not showing now (or there is no item): it was not asked.</summary>
    NotShowing,
    /// <summary>The visibility read or the handler threw.</summary>
    Threw,
}

public static class SearchGate
{
    /// <summary>
    /// Reads the tab's and the panel's live visibility and, only when both show, asks <paramref name="canExecute"/>
    /// (null: nothing to ask). Never throws: a throw from any read is <see cref="GateAnswer.Threw"/>, with the exception.
    /// </summary>
    public static GateAnswer Ask(Func<bool> tabVisible, Func<bool> panelVisible, Func<bool?> canExecute, out Exception? error)
    {
        error = null;
        try
        {
            if (!tabVisible() || !panelVisible()) return GateAnswer.NotShowing;
            return canExecute() switch
            {
                true => GateAnswer.Runs,
                false => GateAnswer.Greyed,
                null => GateAnswer.NothingToAsk,
            };
        }
        catch (Exception ex)
        {
            error = ex;
            return GateAnswer.Threw;
        }
    }

    /// <summary>
    /// A click on a runnable card, before the window closes: true when it may run. Runs and NothingToAsk run (decision
    /// #92: list, and start, what can't be told); Greyed, NotShowing and Threw give the amber message.
    /// </summary>
    public static bool AllowsClick(GateAnswer answer) => answer is GateAnswer.Runs or GateAnswer.NothingToAsk;

    /// <summary>
    /// Just before route A calls Execute: only Runs. Execute is never called on an item whose handler said nothing.
    /// </summary>
    public static bool AllowsExecute(GateAnswer answer) => answer == GateAnswer.Runs;
}
