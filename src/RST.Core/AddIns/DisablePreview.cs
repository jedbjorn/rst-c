// DisablePreview.cs — classify scanned manifests into the buckets the
// loader UI's confirm modal renders before committing to a disable.
//
// Mirrors the dict shape upstream LoaderBridge.GetDisablePreview returns:
//   staying      — required, will not be touched.
//   disabling    — non-required and writeable; will be renamed.
//   tryDisable   — non-required but in a read-only path; we'll skip
//                  the rename and surface the user warning.
//   skipped      — already .addin.RSTdisabled; nothing to do.
//
// Keeps the preview pure — no side effects, safe to call repeatedly
// from the loader UI to refresh the modal.

using System;
using System.Collections.Generic;
using RST.Core.Profiles;
using RST.Core.Scanning;

namespace RST.Core.AddIns;

public sealed record DisablePreview(
    IReadOnlyList<AddinPreviewEntry> Staying,
    IReadOnlyList<AddinPreviewEntry> Disabling,
    IReadOnlyList<AddinPreviewEntry> TryDisable,
    IReadOnlyList<AddinPreviewEntry> Skipped);

public sealed record AddinPreviewEntry(
    string FileName,
    string FilePath,
    string? FirstAssemblyPath,
    string? FirstAddinId,
    AddinPathKind SourceKind);

public static class DisablePreviewBuilder
{
    public static DisablePreview Build(
        string revitVersion,
        IReadOnlyList<RequiredAddin> required) =>
        BuildFromScan(AddinDirectoryScanner.ScanWithSource(revitVersion), required);

    /// <summary>
    /// Test seam — accepts a pre-built scan. Public callers go through
    /// <see cref="Build"/> which scans the live filesystem.
    /// </summary>
    internal static DisablePreview BuildFromScan(
        IEnumerable<(AddinManifest Manifest, AddinSearchPath Source)> scan,
        IReadOnlyList<RequiredAddin> required)
    {
        var requiredFiles = AddinDisabler.BuildRequiredFileSet(required);
        var requiredIds = AddinDisabler.BuildRequiredIdSet(required);

        var staying = new List<AddinPreviewEntry>();
        var disabling = new List<AddinPreviewEntry>();
        var tryDisable = new List<AddinPreviewEntry>();
        var skipped = new List<AddinPreviewEntry>();

        foreach (var (manifest, source) in scan)
        {
            var entry = ToPreview(manifest, source);

            if (manifest.IsDisabled) { skipped.Add(entry); continue; }

            // RST itself is never disabled (see AddinDisabler.IsSelf) —
            // show it as staying so preview matches the commit.
            var isRequired = AddinDisabler.IsSelf(manifest)
                || AddinDisabler.IsRequired(manifest, requiredFiles, requiredIds);
            if (isRequired)         { staying.Add(entry); continue; }

            if (source.ReadOnly)    { tryDisable.Add(entry); continue; }
            disabling.Add(entry);
        }

        return new DisablePreview(staying, disabling, tryDisable, skipped);
    }

    private static AddinPreviewEntry ToPreview(AddinManifest m, AddinSearchPath source)
    {
        string? firstAsm = null, firstId = null;
        foreach (var e in m.Entries)
        {
            if (firstAsm is null && !string.IsNullOrWhiteSpace(e.AssemblyPath))
                firstAsm = e.AssemblyPath;
            if (firstId is null && !string.IsNullOrWhiteSpace(e.AddinId))
                firstId = e.AddinId;
            if (firstAsm is not null && firstId is not null) break;
        }
        return new AddinPreviewEntry(m.FileName, m.FilePath, firstAsm, firstId, source.Kind);
    }
}
