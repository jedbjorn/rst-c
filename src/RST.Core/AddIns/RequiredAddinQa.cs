// RequiredAddinQa.cs — classify the required-addin list of a profile
// against the running machine's installed manifests.
//
// Three outcomes per RequiredAddin:
//   - InstalledActive    — manifest present, not disabled
//   - InstalledDisabled  — present as .addin.RSTdisabled (was disabled
//                          by RST or pyRevit's earlier version)
//   - NotInstalled       — no matching manifest on disk
//
// The Loader's Load Profile flow consumes this to:
//   1. Auto-restore any InstalledDisabled entries (rename .RSTdisabled
//      → .addin) so the next Revit launch loads them. Forces
//      restart_needed=true since DLLs already in this session don't
//      hot-reload.
//   2. Surface NotInstalled entries with their baked-in download URL
//      so the user knows what to grab.
//
// Match policy mirrors RequiredAddinMatcher: addinFile name first,
// AddinId GUID second. Both are authoritative — if either matches a
// scanned manifest, the requirement is satisfied.

using System.Collections.Generic;
using RST.Core.Profiles;
using RST.Core.Scanning;

namespace RST.Core.AddIns;

public enum RequiredAddinStatus
{
    InstalledActive,
    InstalledDisabled,
    NotInstalled,
}

public sealed record RequiredAddinQaResult(
    RequiredAddin Required,
    RequiredAddinStatus Status,
    AddinManifest? MatchedManifest);

public static class RequiredAddinQa
{
    /// <summary>
    /// Classify each entry of <paramref name="required"/> against the
    /// scanned manifests for <paramref name="revitVersion"/>. Returns
    /// one result per input, in input order.
    /// </summary>
    public static IReadOnlyList<RequiredAddinQaResult> Classify(
        string revitVersion,
        IReadOnlyList<RequiredAddin>? required) =>
        Classify(AddinDirectoryScanner.ScanWithSource(revitVersion), required);

    /// <summary>Test seam — accepts an already-built scan.</summary>
    internal static IReadOnlyList<RequiredAddinQaResult> Classify(
        IEnumerable<(AddinManifest Manifest, AddinSearchPath Source)> scan,
        IReadOnlyList<RequiredAddin>? required)
    {
        var results = new List<RequiredAddinQaResult>();
        if (required is null || required.Count == 0) return results;

        // Materialise once — we walk the scan multiple times.
        var manifests = new List<AddinManifest>();
        foreach (var (m, _s) in scan) manifests.Add(m);

        foreach (var req in required)
        {
            if (req is null) continue;
            var match = FindMatch(manifests, req);
            var status = match is null
                ? RequiredAddinStatus.NotInstalled
                : (match.IsDisabled
                    ? RequiredAddinStatus.InstalledDisabled
                    : RequiredAddinStatus.InstalledActive);
            results.Add(new RequiredAddinQaResult(req, status, match));
        }
        return results;
    }

    private static AddinManifest? FindMatch(IReadOnlyList<AddinManifest> manifests, RequiredAddin req)
    {
        // Tier 1 — addinFile name (canonical, suffix already stripped
        // by AddinManifestParser, so an enabled "Kinship.addin" and a
        // disabled "Kinship.addin.RSTdisabled" both report FileName =
        // "Kinship.addin").
        if (!string.IsNullOrWhiteSpace(req.AddinFile))
        {
            for (var i = 0; i < manifests.Count; i++)
            {
                if (string.Equals(manifests[i].FileName, req.AddinFile,
                                  System.StringComparison.OrdinalIgnoreCase))
                    return manifests[i];
            }
        }
        // Tier 2 — AddinId GUID. Manifests can carry multiple <AddIn>
        // entries; any match counts.
        if (!string.IsNullOrWhiteSpace(req.AddinId))
        {
            for (var i = 0; i < manifests.Count; i++)
            {
                foreach (var entry in manifests[i].Entries)
                {
                    if (!string.IsNullOrWhiteSpace(entry.AddinId)
                        && string.Equals(entry.AddinId, req.AddinId,
                                         System.StringComparison.OrdinalIgnoreCase))
                        return manifests[i];
                }
            }
        }
        return null;
    }
}
