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
    /// </summary>
    public static IReadOnlyList<AddinSearchPath> GetSearchPaths(string revitVersion)
    {
        var ver = (revitVersion ?? "").Trim();
        var roots = new List<AddinSearchPath>();

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
        {
            var userAddins = Path.Combine(appData, "Autodesk", "Revit", "Addins", ver);
            if (Directory.Exists(userAddins))
                roots.Add(new AddinSearchPath(userAddins, AddinPathKind.UserAddins, ReadOnly: false));

            var userPlugins = Path.Combine(appData, "Autodesk", "ApplicationPlugins");
            if (Directory.Exists(userPlugins))
                roots.Add(new AddinSearchPath(userPlugins, AddinPathKind.UserApplicationPlugins, ReadOnly: false));
        }

        var programData = Environment.GetEnvironmentVariable("PROGRAMDATA")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrEmpty(programData))
        {
            var machineAddins = Path.Combine(programData, "Autodesk", "Revit", "Addins", ver);
            if (Directory.Exists(machineAddins))
                roots.Add(new AddinSearchPath(machineAddins, AddinPathKind.MachineAddins, ReadOnly: false));

            var machinePlugins = Path.Combine(programData, "Autodesk", "ApplicationPlugins");
            if (Directory.Exists(machinePlugins))
                roots.Add(new AddinSearchPath(machinePlugins, AddinPathKind.MachineApplicationPlugins, ReadOnly: false));
        }

        // Revit install dir — read-only / protected. We never rename
        // anything here; the disable step skips ReadOnly=true paths.
        var programFiles = Environment.GetEnvironmentVariable("PROGRAMFILES")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
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
