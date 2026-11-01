// CatalogMerge.cs — pure seed/ribbon merge (SC-030). Postability is checked by RibbonWalker;
// mode-command and admin bans are applied by CommandCatalog after this merge.

namespace RST.Core.Scanning;

public static class CatalogMerge
{
    /// <summary>
    /// Keeps the first hit per (tab, panel), using the seed's origin and enum name for native
    /// labels. Hits must arrive with panel items before launchers. Seeds without a home are dropped.
    /// </summary>
    public static IReadOnlyList<ScannedCommand> Merge(IEnumerable<ScannedCommand> seed, IEnumerable<RibbonHit> hits, int? revitYear = null)
    {
        // Preserve the old seed behavior if two enum members resolve to the same id.
        var byId = new Dictionary<string, ScannedCommand>(StringComparer.Ordinal);
        foreach (var command in seed) byId[command.Id] = command;
        var placements = new Dictionary<string, List<CommandPlacement>>(StringComparer.Ordinal);
        foreach (var hit in hits)
        {
            if (string.IsNullOrEmpty(hit.SourceTab) || string.IsNullOrEmpty(hit.SourcePanel)) continue;
            if (ModeRestrictedTabs.Contains(hit.SourceTab) || ModeRestrictedTabs.ContainsId(hit.TabId)) continue;
            if (!byId.TryGetValue(hit.Id, out var command))
            {
                command = new ScannedCommand(hit.Id, hit.Id,
                    OriginClassifier.ClassifyRibbonButton(hit.SourceTab, hit.Id, revitYear), null, null, null, null);
                byId.Add(hit.Id, command);
            }
            if (!placements.TryGetValue(hit.Id, out var homes)) placements.Add(hit.Id, homes = []);
            if (homes.Any(p => p.Tab == hit.SourceTab && p.Panel == hit.SourcePanel)) continue;
            var label = RibbonWalkRules.DisplayNameFor(hit.Text, hit.Name, hit.Id,
                command.Origin == CommandOrigin.Native, command.EnumName);
            homes.Add(new CommandPlacement(hit.SourceTab, hit.SourcePanel, label, hit.TabId));
        }

        var result = new List<ScannedCommand>();
        foreach (var command in byId.Values)
        {
            if (!placements.TryGetValue(command.Id, out var homes)) continue;
            var primary = homes[0];
            result.Add(command with
            {
                DisplayName = primary.Label,
                SourceTab = primary.Tab,
                SourcePanel = primary.Panel,
                Placements = homes.AsReadOnly(),
            });
        }
        return result;
    }
}
