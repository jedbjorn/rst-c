// CommandCatalog.cs — single in-memory catalog joining the scan sources.
//
// Core's CatalogMerge joins the PostableCommand seed with every ribbon placement, names each
// placement and drops headless ids. This layer supplies Revit, manifests, bans and logging.
//
// Spec #33: the same scan also makes Search-It's catalogue (Core's SearchCatalog), which keeps the
// family-editor tabs and the mode-only ids Revit lets add-ins post. Spec #35: it also keeps the ids Revit
// won't let add-ins post (Search-It runs them through the ribbon's own command path); the Builder never
// sees them. The Builder's catalogue is merged
// from the scan's hits outside the family-editor tabs (RibbonScan.BuilderHits): the input it always
// had, so the list it always was.

using System.Collections.Generic;
using System.Linq;
using RST.Core.Configuration;
using RST.Core.Scanning;
using RST.Core.Logging;
using RST.Core.Search;

namespace RST.Engine.Scanning;

public sealed class CommandCatalog
{
    public IReadOnlyList<ScannedCommand> Commands { get; }
    public IReadOnlyList<AddinManifest> Manifests { get; }
    /// <summary>Search-It's catalogue, from the same scan as <see cref="Commands"/> (spec #33).</summary>
    internal SearchItCatalog Search { get; }

    private CommandCatalog(IReadOnlyList<ScannedCommand> cmds, IReadOnlyList<AddinManifest> manifests, SearchItCatalog search)
    {
        Commands = cmds;
        Manifests = manifests;
        Search = search;
    }

    /// <summary>
    /// Build the catalog from the live Revit session. Must be called from the
    /// Revit UI thread (typically during OnStartup or first ribbon use).
    /// </summary>
    /// <param name="revitVersion">Revit major version (e.g. "2026").</param>
    /// <param name="bans">Admin-curated denylist. When null, loads from the
    /// default per-user path (<see cref="BanList.DefaultPath"/>); pass an
    /// explicit instance for testing or to bypass the disk read.</param>
    public static CommandCatalog Build(string revitVersion, BanList? bans = null)
    {
        Log.Debug("CommandCatalog.Build start: revit={Version}, banListProvided={Provided}",
                  revitVersion, bans is not null);

        if (bans is null)
        {
            var banPath = BanList.DefaultPath;
            var banExists = System.IO.File.Exists(banPath);
            bans = BanList.Load(banPath);
            Log.Information("BanList: path={Path}, exists={Exists}, banned={Count}",
                            banPath, banExists, bans.Count);
        }
        else
        {
            Log.Debug("BanList: explicit instance, banned={Count}", bans.Count);
        }

        var searchRoots = AddinSearchPaths.ForVersion(revitVersion);
        Log.Information("AddinSearchPaths: revit={Version}, roots={Roots}",
                        revitVersion, searchRoots);

        var manifests = ScanManifests(revitVersion);
        Log.Information("ManifestScan: {Count} manifests parsed across {RootCount} roots",
                        manifests.Count, searchRoots.Count);

        var assemblyToManifest = BuildAssemblyIndex(manifests);
        Log.Debug("AssemblyIndex: {Count} unique assembly paths", assemblyToManifest.Count);

        var seed = BuiltinCommandScanner.Enumerate().ToList();
        var postableIds = new HashSet<string>(seed.Select(c => c.Id), System.StringComparer.Ordinal);
        Log.Information("BuiltinCommandScanner: {Count} commands", seed.Count);

        int? revitYear = int.TryParse(revitVersion, out var year) ? year : null;
        var scan = RibbonScanner.Scan(postableIds, revitYear);
        var hits = scan.BuilderHits;
        var merged = CatalogMerge.Merge(seed, hits, revitYear);
        var headlessDropped = postableIds.Count - merged.Count(c => postableIds.Contains(c.Id));
        Log.Information("RibbonScanner: {Hits} hits → {Commands} commands, {Placements} placements (headless={Headless})",
                        hits.Count, merged.Count, merged.Sum(c => c.Placements?.Count ?? 0), headlessDropped);

        var preFilter = merged.Count;
        var modeDropped = 0;
        var bansDropped = 0;
        var filtered = new List<ScannedCommand>(preFilter);
        foreach (var c in merged)
        {
            if (ModeRestrictedCommandIds.Contains(c.Id)) { modeDropped++; continue; }
            if (bans.IsBanned(c.Id))                      { bansDropped++; continue; }
            filtered.Add(EnrichFromManifests(c, assemblyToManifest));
        }
        Log.Information("Filter pipeline: {Pre} → {Post} (mode-restricted={Mode}, banned={Banned})",
                        preFilter, filtered.Count, modeDropped, bansDropped);

        var searchTools = SearchCatalog.Build(seed, scan.Hits, bans.IsBanned, revitYear);
        Log.Information("Search catalogue: {Hits} hits → {Tools} tools, {Places} places ({FamilyPlaces} on family-editor tabs; {NotPostable} tools Revit won't let add-ins post, run through the ribbon's command path)",
                        scan.Hits.Count, searchTools.Count, searchTools.Sum(t => t.Places.Count),
                        searchTools.Sum(t => t.Places.Count(p => p.OnFamilyEditorTab)), searchTools.Count(t => !t.Postable));

        return new CommandCatalog(filtered, manifests, new SearchItCatalog(searchTools, scan.Handles));
    }

    private static List<AddinManifest> ScanManifests(string revitVersion)
    {
        var roots = AddinSearchPaths.ForVersion(revitVersion);
        var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        var result = new List<AddinManifest>();
        foreach (var root in roots)
        {
            var before = result.Count;
            foreach (var m in AddinManifestParser.ParseDirectory(
                root,
                onSkip: (path, ex) => Log.Warning(ex, "AddinManifestParser: skipped {Path}", path)))
            {
                if (seen.Add(m.FilePath)) result.Add(m);
            }
            Log.Debug("ManifestScan: {Root} → {Added} new manifests", root, result.Count - before);
        }
        return result;
    }

    private static Dictionary<string, AddinManifest> BuildAssemblyIndex(IEnumerable<AddinManifest> manifests)
    {
        var index = new Dictionary<string, AddinManifest>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var m in manifests)
        {
            foreach (var e in m.Entries)
            {
                if (string.IsNullOrEmpty(e.AssemblyPath)) continue;
                var key = NormalizePath(e.AssemblyPath!);
                if (!index.ContainsKey(key)) index[key] = m;
            }
        }
        return index;
    }

    private static ScannedCommand EnrichFromManifests(
        ScannedCommand c, IReadOnlyDictionary<string, AddinManifest> idx)
    {
        // Ribbon-walked commands don't carry an assembly path; enrichment is
        // best-effort via tab name — many add-ins use the assembly basename
        // as the tab title, but not all. RST-004 will fall back to ID-only
        // posting when this fails.
        return c;
    }

    private static string NormalizePath(string path) =>
        path.Replace('/', System.IO.Path.DirectorySeparatorChar)
            .Replace('\\', System.IO.Path.DirectorySeparatorChar)
            .TrimEnd(System.IO.Path.DirectorySeparatorChar)
            .ToLowerInvariant();
}
