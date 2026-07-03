// AddinDirectoryScanner.cs — find every .addin / .addin.RSTdisabled
// file Revit could load for the running version, and parse each into an
// AddinManifest.
//
// Ports /home/jedi/RST/app/addin_scanner.py:
//   - get_addins_dirs(revit_version)
//   - _find_all_addin_files(search_dirs)
//
// Search order (mirrors upstream + Revit's documented load order):
//   1. %AppData%\Autodesk\Revit\Addins\<ver>\
//   2. %ProgramData%\Autodesk\Revit\Addins\<ver>\
//   3. %AppData%\Autodesk\ApplicationPlugins\
//   4. %ProgramData%\Autodesk\ApplicationPlugins\
//   5. %ProgramFiles%\Autodesk\Revit <ver>\           (read-only)
//
// Recursive walks (App Store bundles nest .addin files inside
// `*.bundle/Contents/<lang>/`). Manifests on case-insensitive Windows
// paths are deduplicated by canonical full path so the same bundle
// doesn't surface twice when it sits in two scanned roots.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RST.Core.Scanning;

namespace RST.Core.AddIns;

public sealed record AddinSearchPath(string Path, AddinPathKind Kind, bool ReadOnly);

public enum AddinPathKind
{
    UserAddins,         // %AppData%\Autodesk\Revit\Addins\<ver>
    MachineAddins,      // %ProgramData%\Autodesk\Revit\Addins\<ver>
    UserApplicationPlugins,
    MachineApplicationPlugins,
    RevitInstall,       // %ProgramFiles%\Autodesk\Revit <ver>\ — protected
}

public static class AddinDirectoryScanner
{
    /// <summary>
    /// Resolve every search path that could contain .addin files for
    /// <paramref name="revitVersion"/>. Caller can use the kind/readonly
    /// flags to drive the disable-skip protection.
    ///
    /// ReadOnly is decided by a live write probe as the RUNNING process
    /// token — not by path kind. ProgramData is writable to an elevated
    /// console session but not to interactive (non-elevated) Revit, and
    /// classifying it as writable made the confirm modal promise disables
    /// that every rename then failed (flag #15). The Revit install dir is
    /// the one policy exception: always ReadOnly, never even probed —
    /// shipped add-ins are not ours to rename regardless of token.
    /// </summary>
    public static IReadOnlyList<AddinSearchPath> GetSearchPaths(string revitVersion)
    {
        var programData = Environment.GetEnvironmentVariable("PROGRAMDATA")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var programFiles = Environment.GetEnvironmentVariable("PROGRAMFILES")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return BuildSearchPaths(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            programData,
            programFiles,
            revitVersion,
            DirectoryWritability.CanWrite);
    }

    /// <summary>
    /// Test seam — <see cref="GetSearchPaths(string)"/> minus the
    /// environment lookups and the real filesystem probe.
    /// </summary>
    internal static IReadOnlyList<AddinSearchPath> BuildSearchPaths(
        string? appData,
        string? programData,
        string? programFiles,
        string revitVersion,
        Func<string, bool> canWrite)
    {
        var ver = (revitVersion ?? "").Trim();
        var roots = new List<AddinSearchPath>();

        void AddIfExists(string path, AddinPathKind kind)
        {
            if (Directory.Exists(path))
                roots.Add(new AddinSearchPath(path, kind, ReadOnly: !canWrite(path)));
        }

        if (!string.IsNullOrEmpty(appData))
        {
            AddIfExists(Path.Combine(appData, "Autodesk", "Revit", "Addins", ver), AddinPathKind.UserAddins);
            AddIfExists(Path.Combine(appData, "Autodesk", "ApplicationPlugins"), AddinPathKind.UserApplicationPlugins);
        }

        if (!string.IsNullOrEmpty(programData))
        {
            AddIfExists(Path.Combine(programData, "Autodesk", "Revit", "Addins", ver), AddinPathKind.MachineAddins);
            AddIfExists(Path.Combine(programData, "Autodesk", "ApplicationPlugins"), AddinPathKind.MachineApplicationPlugins);
        }

        // Revit install dir — policy-protected. We never rename anything
        // here (and never probe it — no writes in the install tree); the
        // disable step skips ReadOnly=true paths.
        if (!string.IsNullOrEmpty(programFiles) && !string.IsNullOrEmpty(ver))
        {
            var revitInstall = Path.Combine(programFiles, "Autodesk", $"Revit {ver}");
            if (Directory.Exists(revitInstall))
                roots.Add(new AddinSearchPath(revitInstall, AddinPathKind.RevitInstall, ReadOnly: true));
        }

        return roots;
    }

    /// <summary>
    /// Walk every search path for <paramref name="revitVersion"/> and
    /// return parsed manifests. Manifests at the same canonical path
    /// are emitted once (dedup by full-path, OrdinalIgnoreCase). Skipped
    /// files (parse errors, IO errors) are reported via
    /// <paramref name="onSkip"/>.
    /// </summary>
    public static IReadOnlyList<AddinManifest> Scan(
        string revitVersion,
        Action<string, Exception>? onSkip = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<AddinManifest>();
        foreach (var path in GetSearchPaths(revitVersion))
        {
            foreach (var manifest in AddinManifestParser.ParseDirectory(path.Path, onSkip))
            {
                var key = NormalizePath(manifest.FilePath);
                if (seen.Add(key)) result.Add(manifest);
            }
        }
        return result;
    }

    /// <summary>
    /// Same as <see cref="Scan"/> but tags each manifest with the
    /// search-path it was discovered under so callers can apply
    /// path-kind policies (e.g. skip RevitInstall on disable).
    /// </summary>
    public static IReadOnlyList<(AddinManifest Manifest, AddinSearchPath Source)> ScanWithSource(
        string revitVersion,
        Action<string, Exception>? onSkip = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(AddinManifest, AddinSearchPath)>();
        foreach (var path in GetSearchPaths(revitVersion))
        {
            foreach (var manifest in AddinManifestParser.ParseDirectory(path.Path, onSkip))
            {
                var key = NormalizePath(manifest.FilePath);
                if (seen.Add(key)) result.Add((manifest, path));
            }
        }
        return result;
    }

    private static string NormalizePath(string p)
    {
        try { return Path.GetFullPath(p); }
        catch { return p; }
    }
}
