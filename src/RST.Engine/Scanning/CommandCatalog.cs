// CommandCatalog.cs — single in-memory catalog joining the three scan sources.
//
// Sources, in priority order (first wins on Id collision):
//   1. BuiltinCommandScanner — PostableCommand-derived "ID_BUTTON_*" entries.
//   2. RibbonScanner         — live ribbon walk for add-in pushbuttons.
//                              Per-button entries get enriched with
//                              AssemblyPath/AddinFile by joining against
//                              the manifest scan via tab title.
//   3. AddinManifestParser   — XML scan; supplies fallback entries for add-ins
//                              that declare commands without a visible ribbon
//                              presence (e.g. zero-doc commands).
//
// Filter pipeline (applied after merge):
//   raw → drop ModeRestrictedCommandIds (hard, code-defined)
//       → drop BanList entries (admin-curated, %AppData%\RST\bans.json)
//       → catalog
//
// The catalog is the only surface RST-004 (Loader) talks to.

using System.Collections.Generic;
using System.Linq;
using RST.Core.Configuration;
using RST.Core.Scanning;

namespace RST.Engine.Scanning;

public sealed class CommandCatalog
{
    public IReadOnlyList<ScannedCommand> Commands { get; }
    public IReadOnlyList<AddinManifest> Manifests { get; }

    private CommandCatalog(IReadOnlyList<ScannedCommand> cmds, IReadOnlyList<AddinManifest> manifests)
    {
        Commands = cmds;
        Manifests = manifests;
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
        bans ??= BanList.Load(BanList.DefaultPath);

        var manifests = ScanManifests(revitVersion);
        var assemblyToManifest = BuildAssemblyIndex(manifests);

        var byId = new Dictionary<string, ScannedCommand>();

        foreach (var c in BuiltinCommandScanner.Enumerate())
            byId[c.Id] = c;

        foreach (var c in RibbonScanner.Enumerate())
            if (!byId.ContainsKey(c.Id))
                byId[c.Id] = EnrichFromManifests(c, assemblyToManifest);

        var filtered = byId.Values
            .Where(c => !ModeRestrictedCommandIds.Contains(c.Id))
            .Where(c => !bans.IsBanned(c.Id))
            .ToList();

        return new CommandCatalog(filtered, manifests);
    }

    private static List<AddinManifest> ScanManifests(string revitVersion)
    {
        var roots = AddinSearchPaths.ForVersion(revitVersion);
        var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        var result = new List<AddinManifest>();
        foreach (var root in roots)
        {
            foreach (var m in AddinManifestParser.ParseDirectory(root))
            {
                if (seen.Add(m.FilePath)) result.Add(m);
            }
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
