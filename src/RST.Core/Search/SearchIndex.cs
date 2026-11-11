// rst-c's own (not vendored): what Search-It searches (v3.4).
//
// Scope rule: Search-It offers exactly the tools the Profile Builder's catalogue offers. Both start
// from the same scanned catalogue (RstApplication.GetOrBuildCatalog → CommandCatalog.Build, which has
// already left out what RST knows will not run: ids Revit won't let add-ins post (PostCommandRule),
// mode-only tabs and ids (ModeRestrictedTabs, ModeRestrictedCommandIds) and the admin's BanList), and
// both shape it through the same function, CatalogCommand.FromScanned. The Builder shows one row per
// ribbon placement; Search-It folds those rows to one per command id. There is no second list here:
// a tool is searchable if and only if the Builder lists it.

using RST.Core.Builder;
using RST.Core.Scanning;

namespace RST.Core.Search;

public static class SearchIndex
{
    /// <summary>
    /// One <see cref="SearchTool"/> per command id in the Builder's catalogue
    /// (<see cref="CatalogCommand.FromScanned(IEnumerable{ScannedCommand})"/>), in catalogue order. The
    /// first placement gives the name and the location; the others are counted.
    /// </summary>
    public static IReadOnlyList<SearchTool> Build(IEnumerable<ScannedCommand>? catalog) =>
        FromCatalog(CatalogCommand.FromScanned(catalog));

    /// <summary>Folds the Builder's catalogue rows (one per placement) to one tool per command id.</summary>
    public static IReadOnlyList<SearchTool> FromCatalog(IEnumerable<CatalogCommand>? rows)
    {
        var order = new List<string>();
        var first = new Dictionary<string, CatalogCommand>(StringComparer.Ordinal);
        var places = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in rows ?? [])
        {
            if (row is null || string.IsNullOrEmpty(row.Id)) continue;
            if (first.TryAdd(row.Id, row))
            {
                order.Add(row.Id);
                places[row.Id] = 1;
            }
            else
            {
                places[row.Id]++;
            }
        }

        var tools = new List<SearchTool>(order.Count);
        foreach (var id in order)
        {
            var row = first[id];
            // A command promoted from a host tab ("Add-Ins") is grouped by its panel in the Builder;
            // "where it lives" is the literal ribbon tab and panel.
            tools.Add(new SearchTool(id, row.Label, row.HostTab ?? row.SourceTab, row.LiteralPanel ?? row.SourcePanel, places[id] - 1));
        }
        return tools;
    }
}
