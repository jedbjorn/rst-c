// rst-c's own (not vendored): the Builder's catalogue folded to one tool per command (v3.4).
//
// Until spec #33 this was the list Search-It showed: exactly the tools the Profile Builder's catalogue
// offers (RstApplication.GetOrBuildCatalog → CommandCatalog.Build, shaped through
// CatalogCommand.FromScanned), one tool per command id with every placement and its label.
//
// Search-It now lists from its own catalogue (SearchCatalog), cut down to what the ribbon is showing
// (SearchShowing). This fold stays as the reference those are checked against: in a project the new
// list must be this one, apart from the exceptions the spec names (SearchCatalogTests, AC3).

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
