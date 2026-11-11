// rst-c's own (not vendored): what Search-It searches (v3.4).
//
// Scope rule: Search-It offers exactly the tools the Profile Builder's catalogue offers. Both start
// from the same scanned catalogue (RstApplication.GetOrBuildCatalog → CommandCatalog.Build, which has
// already left out what RST knows will not run: ids Revit won't let add-ins post (PostCommandRule),
// mode-only tabs and ids (ModeRestrictedTabs, ModeRestrictedCommandIds) and the admin's BanList), and
// both shape it through the same function, CatalogCommand.FromScanned. The Builder shows one row per
// ribbon placement; Search-It folds those rows to one tool per command id that keeps every placement
// and its label. There is no second list here: a tool is searchable if and only if the Builder lists
// it, under every name the Builder lists it by.

using RST.Core.Builder;
using RST.Core.Scanning;

namespace RST.Core.Search;

public static class SearchIndex
{
    /// <summary>
    /// One <see cref="SearchTool"/> per command id in the Builder's catalogue
    /// (<see cref="CatalogCommand.FromScanned(IEnumerable{ScannedCommand})"/>), in catalogue order, each
    /// with all its placements.
    /// </summary>
    public static IReadOnlyList<SearchTool> Build(IEnumerable<ScannedCommand>? catalog) =>
        FromCatalog(CatalogCommand.FromScanned(catalog));

    /// <summary>Folds the Builder's catalogue rows (one per placement) to one tool per command id.</summary>
    public static IReadOnlyList<SearchTool> FromCatalog(IEnumerable<CatalogCommand>? rows)
    {
        var order = new List<string>();
        var places = new Dictionary<string, List<SearchPlace>>(StringComparer.Ordinal);
        foreach (var row in rows ?? [])
        {
            if (row is null || string.IsNullOrEmpty(row.Id)) continue;
            if (!places.TryGetValue(row.Id, out var list))
            {
                places.Add(row.Id, list = []);
                order.Add(row.Id);
            }
            // A command promoted from a host tab ("Add-Ins") is grouped by its panel in the Builder;
            // "where it lives" is the literal ribbon tab and panel.
            list.Add(new SearchPlace(row.Label, row.HostTab ?? row.SourceTab, row.LiteralPanel ?? row.SourcePanel));
        }
        return order.ConvertAll(id => new SearchTool(id, places[id]));
    }
}
