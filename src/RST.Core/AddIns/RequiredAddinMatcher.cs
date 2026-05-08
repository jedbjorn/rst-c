// RequiredAddinMatcher.cs — match a Profile.RequiredAddins list against
// the .addin manifests on disk.
//
// Two-tier match (mirrors a subset of the upstream
// /home/jedi/RST/app/addin_scanner.py three-tier policy):
//
//   Tier 1 — by addinFile name (case-insensitive, .RSTdisabled stripped).
//   Tier 2 — by AddInId GUID (case-insensitive).
//
// The third upstream tier (resolve via loaded-addin probe + tab name)
// requires a live Revit ribbon walk which is a Phase-2 follow-up.
// Until then, profiles whose RequiredAddins entries lack both
// addinFile and addinId will report Missing — matches upstream when
// the live probe also fails.
//
// Returns one MatchResult per Profile.RequiredAddins entry, preserving
// the input order. UI callers render this directly.

using System;
using System.Collections.Generic;
using System.Linq;
using RST.Core.Profiles;
using RST.Core.Scanning;

namespace RST.Core.AddIns;

public sealed record AddinMatchResult(
    RequiredAddin Required,
    bool Found,
    AddinManifest? Manifest);

public static class RequiredAddinMatcher
{
    /// <summary>
    /// Resolve each <see cref="RequiredAddin"/> to a scanned manifest if
    /// possible. Order of results matches the input order of
    /// <paramref name="required"/>.
    /// </summary>
    public static IReadOnlyList<AddinMatchResult> Match(
        IEnumerable<RequiredAddin> required,
        IEnumerable<AddinManifest> manifests)
    {
        if (required is null) return Array.Empty<AddinMatchResult>();
        var manifestList = (manifests ?? Array.Empty<AddinManifest>()).ToList();

        // Index for tier 1 (by canonical filename) and tier 2 (by AddInId).
        // The same manifest may appear in both indexes — that's fine, we
        // bind to the first hit and return it as the single match.
        var byFile = new Dictionary<string, AddinManifest>(StringComparer.OrdinalIgnoreCase);
        var byId = new Dictionary<string, AddinManifest>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in manifestList)
        {
            // FileName already strips the .RSTdisabled suffix in
            // AddinManifestParser, so it's the canonical name.
            byFile.TryAdd(m.FileName, m);
            foreach (var entry in m.Entries)
            {
                if (!string.IsNullOrWhiteSpace(entry.AddinId))
                    byId.TryAdd(entry.AddinId!, m);
            }
        }

        var results = new List<AddinMatchResult>();
        foreach (var req in required)
        {
            if (req is null) continue;

            AddinManifest? hit = null;

            // Tier 1: match by addinFile name. The profile JSON stores
            // the canonical (.addin) name; manifests on disk may be
            // .addin.RSTdisabled but their FileName is the canonical
            // form, so a direct lookup works either way.
            if (!string.IsNullOrWhiteSpace(req.AddinFile)
                && byFile.TryGetValue(req.AddinFile!.Trim(), out var byFileHit))
            {
                hit = byFileHit;
            }

            // Tier 2: match by AddInId. Only consulted when tier 1 didn't
            // resolve — addinFile is the more precise identifier when
            // both are available.
            if (hit is null
                && !string.IsNullOrWhiteSpace(req.AddinId)
                && byId.TryGetValue(req.AddinId!.Trim(), out var byIdHit))
            {
                hit = byIdHit;
            }

            results.Add(new AddinMatchResult(req, Found: hit is not null, Manifest: hit));
        }
        return results;
    }
}
