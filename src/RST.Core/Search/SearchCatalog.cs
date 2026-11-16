// rst-c's own (not vendored): Search-It's own catalogue (spec #33, "Search Catalogue").
//
// The Builder's catalogue (CatalogMerge, then CommandCatalog's filters) is made for profile toolbars,
// which are built for project work: it leaves out every family-editor tab and the mode-only ids by
// name. Search-It lists the tools of the file that is open, so it keeps both, from the same ribbon
// scan and the same seed:
//   - the family-editor tabs' hits (ModeRestrictedTabs.ContainsId) are kept, as places of their own;
//   - ModeRestrictedCommandIds is not applied: the scan has already left out every id Revit will not
//     let add-ins post (decision #63), and that decides. Load into Project stays; Family Status and
//     the in-place Finish/Cancel ids are not PostableCommand members and never arrive here;
//   - the administrator's ban list applies, as it does to the Builder's catalogue.
// Everything else follows CatalogMerge line for line (seed order, first hit per home, the label rule
// of decision #67), so the places outside the family-editor tabs are the Builder's, in its order.
// CatalogMerge itself is not touched: the Builder's catalogue is the same list as before.
//
// Which of these places is listed is decided when the window opens (SearchShowing).

using RST.Core.Scanning;

namespace RST.Core.Search;

/// <summary>One ribbon place of a tool in the search catalogue.</summary>
/// <param name="Handle">The index of the scan hit this place came from: the host's key to its live ribbon objects.</param>
/// <param name="TabId">The ribbon tab's id; titles repeat between project and family tabs ("Create", "Modify").</param>
/// <param name="Label">The name the ribbon shows for the tool here (decision #67).</param>
public sealed record SearchCatalogPlace(int Handle, string? TabId, string Tab, string Panel, string Label)
{
    /// <summary>The tab Revit draws the Family Editor panel on: no user ever sees a tab of that name.</summary>
    public const string FamilyEditorPanelTabId = "FamilyEditorTab";

    /// <summary>True for a place on a family-editor tab, which shows only while that tab does.</summary>
    public bool OnFamilyEditorTab => ModeRestrictedTabs.ContainsId(TabId);

    /// <summary>
    /// The place as a result shows it. The Family Editor panel sits at the end of whichever family tab
    /// is active, so it is shown by its panel alone ("Family Editor").
    /// </summary>
    public SearchPlace ToSearchPlace() =>
        new(Label, TabId == FamilyEditorPanelTabId ? null : Tab, Panel);
}

/// <summary>One tool of the search catalogue: a command id and every ribbon place it has, in scan order.</summary>
public sealed record SearchCatalogTool(string Id, IReadOnlyList<SearchCatalogPlace> Places);

public static class SearchCatalog
{
    /// <summary>
    /// The search catalogue for one scan. <paramref name="seed"/> and <paramref name="hits"/> are the
    /// ones <see cref="CatalogMerge.Merge"/> takes (hits with panel items before launchers); a place's
    /// <see cref="SearchCatalogPlace.Handle"/> is its hit's index in <paramref name="hits"/>.
    /// </summary>
    public static IReadOnlyList<SearchCatalogTool> Build(IEnumerable<ScannedCommand> seed, IReadOnlyList<RibbonHit> hits,
        Func<string, bool>? isBanned = null, int? revitYear = null)
    {
        var byId = new Dictionary<string, ScannedCommand>(StringComparer.Ordinal);
        foreach (var command in seed) byId[command.Id] = command;
        var places = new Dictionary<string, List<SearchCatalogPlace>>(StringComparer.Ordinal);
        for (var i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            if (string.IsNullOrEmpty(hit.SourceTab) || string.IsNullOrEmpty(hit.SourcePanel)) continue;
            var familyTab = ModeRestrictedTabs.ContainsId(hit.TabId);
            // A mode tab known by its title alone is not a family-editor tab: it stays out, as in the Builder's merge.
            if (!familyTab && ModeRestrictedTabs.Contains(hit.SourceTab)) continue;
            if (!byId.TryGetValue(hit.Id, out var command))
            {
                command = new ScannedCommand(hit.Id, hit.Id,
                    OriginClassifier.ClassifyRibbonButton(hit.SourceTab, hit.Id, revitYear), null, null, null, null);
                byId.Add(hit.Id, command);
            }
            if (!places.TryGetValue(hit.Id, out var homes)) places.Add(hit.Id, homes = []);
            // One place per (tab, panel), by title as in the Builder's merge. A family-editor tab is told
            // from the project tab of the same title ("Create", "Modify") by its id.
            if (homes.Any(p => p.Tab == hit.SourceTab && p.Panel == hit.SourcePanel
                    && p.OnFamilyEditorTab == familyTab && (!familyTab || p.TabId == hit.TabId))) continue;
            var label = RibbonWalkRules.DisplayNameFor(hit.Text, hit.Name, hit.Id,
                command.Origin == CommandOrigin.Native, command.EnumName);
            homes.Add(new SearchCatalogPlace(i, hit.TabId, hit.SourceTab!, hit.SourcePanel!, label));
        }

        var result = new List<SearchCatalogTool>();
        foreach (var id in byId.Keys)
        {
            if (!places.TryGetValue(id, out var homes)) continue;
            if (isBanned is not null && isBanned(id)) continue;
            result.Add(new SearchCatalogTool(id, homes.AsReadOnly()));
        }
        return result;
    }
}
