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
// Match policy is three-tier:
//   1. addinFile name (exact, case-insensitive)
//   2. AddinId GUID (exact, case-insensitive)
//   3. fuzzy fallback — live ribbon tab title that contains the
//      requirement's tab name (or vice-versa), or a manifest
//      displayName / file-stem that does the same. Mirrors the
//      JS-side isAddinLoaded heuristic in profile_loader.html so
//      the picker's "Loaded" badge and the QA modal agree about
//      whether an addin counts as installed.
//
// Without tier 3 the QA over-reports Missing whenever the actual
// installed .addin filename differs from what the curated registry
// recorded (e.g. registry says "Lumion.addin" but the user's
// install ships "LumionLiveSync.addin").

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
    /// <param name="loadedRibbonTabs">Optional list of live ribbon
    /// tab titles. When passed, a tab name match is treated as
    /// authoritative "loaded" even if no manifest fuzzy-matches —
    /// mirrors how the Loader picker decides "Loaded".</param>
    public static IReadOnlyList<RequiredAddinQaResult> Classify(
        string revitVersion,
        IReadOnlyList<RequiredAddin>? required,
        IReadOnlyList<string>? loadedRibbonTabs = null) =>
        Classify(AddinDirectoryScanner.ScanWithSource(revitVersion), required, loadedRibbonTabs);

    /// <summary>Test seam — accepts an already-built scan.</summary>
    internal static IReadOnlyList<RequiredAddinQaResult> Classify(
        IEnumerable<(AddinManifest Manifest, AddinSearchPath Source)> scan,
        IReadOnlyList<RequiredAddin>? required,
        IReadOnlyList<string>? loadedRibbonTabs = null)
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

            // Tier 3: live ribbon tab match. If the addin owns a tab
            // by the requirement's name, it's loaded in the running
            // session — even when its .addin filename / displayName
            // don't fuzzy-match (uncommon in practice but possible
            // for addins whose tab title is set programmatically).
            if (match is null && loadedRibbonTabs is not null
                && !string.IsNullOrWhiteSpace(req.TabName)
                && TabIsOnRibbon(req.TabName, loadedRibbonTabs))
            {
                results.Add(new RequiredAddinQaResult(req, RequiredAddinStatus.InstalledActive, null));
                continue;
            }

            var status = match is null
                ? RequiredAddinStatus.NotInstalled
                : (match.IsDisabled
                    ? RequiredAddinStatus.InstalledDisabled
                    : RequiredAddinStatus.InstalledActive);
            results.Add(new RequiredAddinQaResult(req, status, match));
        }
        return results;
    }

    /// <summary>
    /// The set of manifest FileNames (canonical, case-insensitive) that satisfy
    /// any entry in <paramref name="required"/>, using the SAME three-tier match
    /// as <see cref="Classify"/>. AddinDisabler consumes this so "required"
    /// means the same thing at disable time, restore time, and QA time: a
    /// profile's own dependency that matches only by the fuzzy tier is never
    /// disabled, and is restored when it was disabled.
    /// </summary>
    public static HashSet<string> RequiredManifestFileNames(
        IReadOnlyList<AddinManifest> manifests,
        IReadOnlyList<RequiredAddin>? required)
    {
        var set = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        if (manifests is null || required is null) return set;
        foreach (var req in required)
        {
            if (req is null) continue;
            var m = FindMatch(manifests, req);
            if (m is not null) set.Add(m.FileName);
        }
        return set;
    }

    private static bool TabIsOnRibbon(string tabName, IReadOnlyList<string> ribbon)
    {
        var t = tabName.Trim();
        if (t.Length == 0) return false;
        for (var i = 0; i < ribbon.Count; i++)
        {
            var r = ribbon[i];
            if (string.IsNullOrEmpty(r)) continue;
            if (r.IndexOf(t, System.StringComparison.OrdinalIgnoreCase) >= 0
                || t.IndexOf(r, System.StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
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
        // Tier 3 — fuzzy substring match against manifest displayName /
        // file-stem and entry Name. Mirrors the JS-side isAddinLoaded
        // heuristic in profile_loader.html so the picker's Loaded badge
        // and the QA modal agree. Triggered when the registry's
        // "addinFile" hint doesn't match the user's actual filename
        // (e.g. registry "Lumion.addin" vs installed "LumionLiveSync.addin").
        if (!string.IsNullOrWhiteSpace(req.TabName))
        {
            var tab = req.TabName.Trim();
            for (var i = 0; i < manifests.Count; i++)
            {
                if (FuzzyMatchesTab(manifests[i], tab))
                    return manifests[i];
            }
        }
        return null;
    }

    private static bool FuzzyMatchesTab(AddinManifest manifest, string tab)
    {
        var stem = StripAddinSuffix(manifest.FileName);
        if (Contains(stem, tab) || Contains(tab, stem)) return true;
        foreach (var entry in manifest.Entries)
        {
            if (!string.IsNullOrWhiteSpace(entry.Name)
                && (Contains(entry.Name!, tab) || Contains(tab, entry.Name!)))
                return true;
        }
        return false;
    }

    private static string StripAddinSuffix(string fileName)
    {
        const string addin = ".addin";
        if (fileName.EndsWith(addin, System.StringComparison.OrdinalIgnoreCase))
            return fileName.Substring(0, fileName.Length - addin.Length);
        return fileName;
    }

    private static bool Contains(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return false;
        return haystack.IndexOf(needle, System.StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
