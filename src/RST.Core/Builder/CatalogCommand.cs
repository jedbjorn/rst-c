// rst-c's own (not vendored): the Builder's command catalogue entry (spec #16, WU4), a port of
// LoaderBridge.GetCatalog's DTO and its host-tab promotion (LB:402-446). Behavior is unchanged:
// a command on a host tab ("Add-Ins", "FormIt", …) is grouped under its panel name, so each
// vendor's panel becomes its own group; the literal tab and panel are kept as HostTab / LiteralPanel.

using RST.Core.Scanning;

namespace RST.Core.Builder;

/// <summary>
/// One command the Builder offers, in get_catalog's shape: <see cref="SourceTab"/> is the catalogue
/// group (the panel name when the command sits on a host tab), <see cref="SourcePanel"/> is null for
/// a promoted command, and <see cref="Origin"/> is the <see cref="CommandOrigin"/> name.
/// </summary>
public sealed record CatalogCommand(
    string Id,
    string DisplayName,
    string Origin,
    string? SourceTab,
    string? SourcePanel,
    string? HostTab = null,
    string? LiteralPanel = null,
    string? AddinFile = null,
    string? AssemblyPath = null)
{
    /// <summary>The name a new slot takes and the tree shows: <c>displayName || id</c>.</summary>
    public string Label => string.IsNullOrEmpty(DisplayName) ? Id : DisplayName;

    /// <summary>The Revit built-in origin, which never makes a required add-in.</summary>
    public const string NativeOrigin = nameof(CommandOrigin.Native);

    /// <summary>The Builder's catalogue: the scanned commands, reshaped with the host-tab promotion (v2.2.0's get_catalog).</summary>
    public static IReadOnlyList<CatalogCommand> FromScanned(IEnumerable<ScannedCommand>? catalog) =>
        FromScanned(catalog, out _);

    /// <inheritdoc cref="FromScanned(IEnumerable{ScannedCommand})"/>
    /// <param name="promoted">How many placements moved from a host tab to their panel's group (for the log).</param>
    public static IReadOnlyList<CatalogCommand> FromScanned(IEnumerable<ScannedCommand>? catalog, out int promoted)
    {
        var count = 0;
        var result = new List<CatalogCommand>();
        foreach (var c in catalog ?? [])
        {
            if (c is null) continue;
            // Older callers can still supply the original single-placement shape.
            var placements = c.Placements ?? [new CommandPlacement(c.SourceTab!, c.SourcePanel!, c.DisplayName, null)];
            foreach (var placement in placements)
            {
                var isHost = HostTabs.Contains(placement.Tab);
                var group = isHost ? placement.Panel ?? placement.Tab : placement.Tab;
                if (isHost && !string.IsNullOrEmpty(placement.Panel)) count++;
                result.Add(new CatalogCommand(
                    c.Id,
                    placement.Label,
                    c.Origin.ToString(),
                    group,
                    isHost ? null : placement.Panel,
                    isHost ? placement.Tab : null,
                    isHost ? placement.Panel : null,
                    c.AddinFile,
                    c.AssemblyPath));
            }
        }
        promoted = count;
        return result;
    }
}
