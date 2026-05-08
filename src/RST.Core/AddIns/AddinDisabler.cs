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
//     paths (Revit install dir) — renaming there would fail and is
//     also a bad idea for shipped add-ins.
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
    public static DisableResult DisableNonRequired(
        string revitVersion,
        IReadOnlyList<RequiredAddin> required,
        Action<string, Exception>? onError = null)
    {
        var requiredFiles = BuildRequiredFileSet(required);
        var requiredIds = BuildRequiredIdSet(required);

        int disabled = 0, skipReadOnly = 0, skipAlready = 0, failed = 0;
        var disabledFiles = new List<string>();
        var failedFiles = new List<string>();

        foreach (var (manifest, source) in AddinDirectoryScanner.ScanWithSource(revitVersion))
        {
            if (manifest.IsDisabled) { skipAlready++; continue; }
            if (source.ReadOnly)     { skipReadOnly++; continue; }

            if (IsRequired(manifest, requiredFiles, requiredIds)) continue;

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
        Action<string, Exception>? onError = null)
    {
        int restored = 0, failed = 0;
        var restoredFiles = new List<string>();
        var failedFiles = new List<string>();

        foreach (var (manifest, source) in AddinDirectoryScanner.ScanWithSource(revitVersion))
        {
            if (!manifest.IsDisabled) continue;
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
