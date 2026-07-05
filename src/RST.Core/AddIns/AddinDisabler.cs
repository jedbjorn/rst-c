// AddinDisabler.cs — rename .addin ↔ .addin.RSTdisabled to suppress or
// restore Revit add-ins on the next launch.
//
// Ports /home/jedi/RST/app/addin_scanner.py:
//   - disable_non_required_addins(required, revit_version, protected)
//   - restore_all_addins(revit_version)
//
// Policy:
//   - Disable: rename .addin → .addin.RSTdisabled for any manifest NOT
//     in the required-addins list. Skip manifests in read-only search
//     paths — the Revit install dir (policy: shipped add-ins are not
//     ours to rename) and any path the running token can't write to
//     (probed live at scan time; see DirectoryWritability).
//   - Restore: walk every search path, rename every .addin.RSTdisabled
//     back to .addin. No-op if none found.
//
// Disable preview is computed by DisablePreview.cs (separate type so
// the bridge can render the UI confirm modal without committing to
// any rename).
//
// Naming convention is shared with upstream pyRevit RST so .addin
// files disabled by the Python version remain restorable here, and
// vice versa.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RST.Core.Profiles;
using RST.Core.Scanning;

namespace RST.Core.AddIns;

public sealed record DisableResult(
    int DisabledCount,
    int SkippedReadOnly,
    int SkippedAlreadyDisabled,
    int Failed,
    IReadOnlyList<string> DisabledFiles,
    IReadOnlyList<string> FailedFiles);

public sealed record RestoreResult(
    int RestoredCount,
    int Failed,
    IReadOnlyList<string> RestoredFiles,
    IReadOnlyList<string> FailedFiles);

public static class AddinDisabler
{
    public const string DisabledSuffix = ".RSTdisabled";

    /// <summary>
    /// Rename every non-required .addin file under
    /// <paramref name="revitVersion"/>'s search paths to
    /// <c>.addin.RSTdisabled</c>. Manifests already in the required
    /// list are kept; manifests in read-only search paths
    /// (Revit install) are kept.
    /// </summary>
    /// <summary>
    /// RST's own manifest file name as shipped by the installer.
    /// </summary>
    internal const string RstManifestFileName = "RST.addin";

    /// <summary>
    /// RST's canonical ClientId (RST.addin — never changes across
    /// upgrades; Revit identifies the add-in by it).
    /// </summary>
    internal const string RstClientId = "4f8ef7a0-0001-4000-8000-525354000001";

    /// <summary>
    /// RST's own manifest must never be disabled: renaming it removes
    /// the Loader, the Restore path, and every other in-product way
    /// back — a full self-lockout until someone hand-renames the file.
    /// Matched by file name or by canonical ClientId (covers a renamed
    /// manifest).
    /// </summary>
    internal static bool IsSelf(AddinManifest manifest)
    {
        if (string.Equals(manifest.FileName, RstManifestFileName, StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (var entry in manifest.Entries)
        {
            if (!string.IsNullOrWhiteSpace(entry.AddinId)
                && string.Equals(entry.AddinId!.Trim(), RstClientId, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static DisableResult DisableNonRequired(
        string revitVersion,
        IReadOnlyList<RequiredAddin> required,
        Action<string, Exception>? onError = null)
    {
        // Materialise once and compute "required" via the SAME three-tier
        // matcher the QA classifier uses, so a dependency matched only by the
        // fuzzy tier (registry filename ≠ installed filename) is not disabled.
        var scan = AddinDirectoryScanner.ScanWithSource(revitVersion).ToList();
        var requiredNames = RequiredAddinQa.RequiredManifestFileNames(
            scan.Select(s => s.Manifest).ToList(), required);
        return DisableFiltered(scan, requiredNames, onError);
    }

    /// <summary>
    /// Test seam — accepts a pre-built scan and the resolved required
    /// set. Public callers go through <see cref="DisableNonRequired"/>
    /// which scans the live filesystem.
    /// </summary>
    internal static DisableResult DisableFiltered(
        IEnumerable<(AddinManifest Manifest, AddinSearchPath Source)> scan,
        HashSet<string> requiredNames,
        Action<string, Exception>? onError = null)
    {
        int disabled = 0, skipReadOnly = 0, skipAlready = 0, failed = 0;
        var disabledFiles = new List<string>();
        var failedFiles = new List<string>();

        foreach (var (manifest, source) in scan)
        {
            if (manifest.IsDisabled) { skipAlready++; continue; }
            if (source.ReadOnly)     { skipReadOnly++; continue; }

            if (IsSelf(manifest)) continue;
            if (requiredNames.Contains(manifest.FileName)) continue;

            var dest = manifest.FilePath + DisabledSuffix;
            try
            {
                File.Move(manifest.FilePath, dest);
                disabled++;
                disabledFiles.Add(manifest.FileName);
            }
            catch (Exception ex)
            {
                failed++;
                failedFiles.Add(manifest.FileName);
                onError?.Invoke(manifest.FilePath, ex);
            }
        }

        return new DisableResult(disabled, skipReadOnly, skipAlready, failed, disabledFiles, failedFiles);
    }

    /// <summary>
    /// Rename every .addin.RSTdisabled file under
    /// <paramref name="revitVersion"/>'s search paths back to plain
    /// <c>.addin</c>. Idempotent — running twice does nothing on the
    /// second call.
    /// </summary>
    public static RestoreResult RestoreAll(
        string revitVersion,
        Action<string, Exception>? onError = null) =>
        RestoreFiltered(AddinDirectoryScanner.ScanWithSource(revitVersion),
                        manifest => true,
                        onError);

    /// <summary>
    /// Rename only the .addin.RSTdisabled files that match
    /// <paramref name="required"/> (by file name first, AddinId GUID
    /// second) back to plain <c>.addin</c>. Used by Load Profile QA so
    /// a previously-disabled required addin auto-reactivates without
    /// touching unrelated disabled files. Idempotent.
    /// </summary>
    public static RestoreResult RestoreRequired(
        string revitVersion,
        IReadOnlyList<RequiredAddin> required,
        Action<string, Exception>? onError = null)
    {
        // Same three-tier matcher as DisableNonRequired / QA, so a disabled
        // required add-in matched only by the fuzzy tier is actually restored
        // (the old 2-tier predicate silently left it disabled while the UI
        // reported it restored).
        var scan = AddinDirectoryScanner.ScanWithSource(revitVersion).ToList();
        var requiredNames = RequiredAddinQa.RequiredManifestFileNames(
            scan.Select(s => s.Manifest).ToList(), required);
        return RestoreFiltered(scan, manifest => requiredNames.Contains(manifest.FileName), onError);
    }

    /// <summary>
    /// Test seam — accepts a pre-built scan and a predicate. Public
    /// callers go through <see cref="RestoreAll"/> /
    /// <see cref="RestoreRequired"/> which scan the live filesystem.
    /// </summary>
    internal static RestoreResult RestoreFiltered(
        IEnumerable<(AddinManifest Manifest, AddinSearchPath Source)> scan,
        Func<AddinManifest, bool> shouldRestore,
        Action<string, Exception>? onError = null)
    {
        int restored = 0, failed = 0;
        var restoredFiles = new List<string>();
        var failedFiles = new List<string>();

        foreach (var (manifest, _source) in scan)
        {
            if (!manifest.IsDisabled) continue;
            if (!shouldRestore(manifest)) continue;
            // Restore is non-destructive — even read-only paths that
            // somehow got disabled get put back. (Won't happen via
            // DisableNonRequired but a manual rename / pyRevit version
            // running once might.)
            var dest = manifest.FilePath.Substring(0, manifest.FilePath.Length - DisabledSuffix.Length);
            try
            {
                File.Move(manifest.FilePath, dest);
                restored++;
                restoredFiles.Add(manifest.FileName);
            }
            catch (Exception ex)
            {
                failed++;
                failedFiles.Add(manifest.FileName);
                onError?.Invoke(manifest.FilePath, ex);
            }
        }

        return new RestoreResult(restored, failed, restoredFiles, failedFiles);
    }

    internal static HashSet<string> BuildRequiredFileSet(IEnumerable<RequiredAddin>? required)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (required is null) return set;
        foreach (var r in required)
        {
            if (!string.IsNullOrWhiteSpace(r?.AddinFile))
                set.Add(r!.AddinFile!.Trim());
        }
        return set;
    }

    internal static HashSet<string> BuildRequiredIdSet(IEnumerable<RequiredAddin>? required)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (required is null) return set;
        foreach (var r in required)
        {
            if (!string.IsNullOrWhiteSpace(r?.AddinId))
                set.Add(r!.AddinId!.Trim());
        }
        return set;
    }

    internal static bool IsRequired(
        AddinManifest manifest,
        HashSet<string> requiredFiles,
        HashSet<string> requiredIds)
    {
        if (requiredFiles.Contains(manifest.FileName)) return true;
        foreach (var entry in manifest.Entries)
        {
            if (!string.IsNullOrWhiteSpace(entry.AddinId)
                && requiredIds.Contains(entry.AddinId!))
                return true;
        }
        return false;
    }
}
