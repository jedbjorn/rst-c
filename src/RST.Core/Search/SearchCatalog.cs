// rst-c's own (not vendored): Search-It's own catalogue (spec #33, "Search Catalogue").
//
// The Builder's catalogue (CatalogMerge, then CommandCatalog's filters) is made for profile toolbars,
// which are built for project work: it leaves out every family-editor tab and the mode-only ids by
// name. Search-It lists the tools of the file that is open, so it keeps both, from the same ribbon
// scan and the same seed:
//   - the family-editor tabs' hits (ModeRestrictedTabs.ContainsId) are kept, as places of their own;
//   - ModeRestrictedCommandIds is not applied: postability decides how a tool runs, not whether it is
//     listed (spec #35: an id Revit will not let add-ins post arrives marked, RibbonHit.Postable false,
//     and runs through the ribbon's own command path; Revit's live check and the census then decide
//     whether it is listed);
//   - the administrator's ban list applies, as it does to the Builder's catalogue.
// Everything else follows CatalogMerge line for line (seed order, first hit per home, the label rule
// of decision #67), so the places outside the family-editor tabs are the Builder's, in its order.
// CatalogMerge itself is not touched: the Builder's catalogue is the same list as before.
//
// Which of these places is listed is decided when the window opens (SearchShowing).
//
// Spec #35 adds two things. The ids Revit won't let add-ins post come in too, from the hits the scan kept for
// Search-It only (RibbonHit.Postable false): the tool is marked not postable and a click runs it through the
// ribbon's own command path (decision #92). And each place keeps every copy of the tool the scan met on that
// (tab, panel), with its census path: Revit keeps hidden copies of some containers, and the census marks the exact
// path that is not drawn, so a place is "not drawn" only when every copy of it is.

using RST.Core.Scanning;

namespace RST.Core.Search;

/// <summary>One ribbon place of a tool in the search catalogue.</summary>
/// <param name="Handle">The index of the scan hit this place came from: the host's key to its live ribbon objects.</param>
/// <param name="TabId">The ribbon tab's id; titles repeat between project and family tabs ("Create", "Modify").</param>
/// <param name="Label">The name the ribbon shows for the tool here (decision #67).</param>
public sealed record SearchCatalogPlace(int Handle, string? TabId, string Tab, string Panel, string Label)
{
    /// <summary>
    /// Every copy of the tool the scan met at this (tab, panel), first (<see cref="Handle"/>) first, each with its census
    /// path (spec #35). Not part of the place's equality: two places are the same place whatever copies they hold.
    /// </summary>
    public IReadOnlyList<SearchPlaceCopy> Copies { get; init; } = Array.Empty<SearchPlaceCopy>();

    /// <summary><see cref="Copies"/>, or this place's own handle with no path when none were recorded.</summary>
    public IReadOnlyList<SearchPlaceCopy> CopiesOrSelf => Copies.Count > 0 ? Copies : [new SearchPlaceCopy(Handle, null)];

    public bool Equals(SearchCatalogPlace? other) =>
        other is not null && Handle == other.Handle && TabId == other.TabId && Tab == other.Tab && Panel == other.Panel && Label == other.Label;

    public override int GetHashCode() => HashCode.Combine(Handle, TabId, Tab, Panel, Label);

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

/// <summary>One copy of a tool at a place: the scan hit's index and its census path (null when not recorded).</summary>
public readonly record struct SearchPlaceCopy(int Handle, string? Path);

/// <summary>One tool of the search catalogue: a command id and every ribbon place it has, in scan order.</summary>
/// <param name="Postable">Revit lets add-ins post the id (decision #63). False only for the ids spec #35 lets in.</param>
public sealed record SearchCatalogTool(string Id, IReadOnlyList<SearchCatalogPlace> Places, bool Postable = true);

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
        var copies = new Dictionary<SearchCatalogPlace, List<SearchPlaceCopy>>(ReferenceEqualityComparer.Instance);
        var notPostable = new HashSet<string>(StringComparer.Ordinal);
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
            if (!hit.Postable) notPostable.Add(hit.Id);
            if (!places.TryGetValue(hit.Id, out var homes)) places.Add(hit.Id, homes = []);
            // One place per (tab, panel), by title as in the Builder's merge. A family-editor tab is told
            // from the project tab of the same title ("Create", "Modify") by its id.
            var home = homes.FirstOrDefault(p => p.Tab == hit.SourceTab && p.Panel == hit.SourcePanel
                && p.OnFamilyEditorTab == familyTab && (!familyTab || p.TabId == hit.TabId));
            if (home is not null)
            {
                copies[home].Add(new SearchPlaceCopy(i, hit.Path));
                continue;
            }
            var label = RibbonWalkRules.DisplayNameFor(hit.Text, hit.Name, hit.Id,
                command.Origin == CommandOrigin.Native, command.EnumName);
            var place = new SearchCatalogPlace(i, hit.TabId, hit.SourceTab!, hit.SourcePanel!, label);
            homes.Add(place);
            copies[place] = [new SearchPlaceCopy(i, hit.Path)];
        }

        var result = new List<SearchCatalogTool>();
        foreach (var id in byId.Keys)
        {
            if (!places.TryGetValue(id, out var homes)) continue;
            if (isBanned is not null && isBanned(id)) continue;
            var withCopies = homes.ConvertAll(p => p with { Copies = copies[p].AsReadOnly() });
            result.Add(new SearchCatalogTool(id, withCopies.AsReadOnly(), Postable: !notPostable.Contains(id)));
        }
        return result;
    }
}
