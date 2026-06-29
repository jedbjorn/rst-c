// CleanupDefaults.cs — the 5 OOB cleanup targets that seed every new
// profile. Mirror the pre-RST-031 hardcoded behavior of HealthCleaner so
// users moving from a legacy profile see the same set of options on
// first run.
//
// Stable IDs are assigned per OOB entry so a user's edits to the *list*
// (rename, disable, delete) survive across saves cleanly — and so the
// Health viewer's per-target result counts can correlate with the
// rendered checkbox even after names drift.

using System;
using System.Collections.Generic;
using System.Linq;

namespace RST.Core.Profiles;

public static class CleanupDefaults
{
    public const string IdTemp        = "rst.cleanup.temp";
    public const string IdPacCache    = "rst.cleanup.pacCache";
    public const string IdJournals    = "rst.cleanup.journals";
    public const string IdCollabCache = "rst.cleanup.collabCache";
    public const string IdRecentFiles = "rst.cleanup.recentFiles";
    public const string IdRstLogs     = "rst.cleanup.rstLogs";

    /// <summary>
    /// Build a fresh list of the OOB targets. Called by the Builder when
    /// creating a new profile and by HealthBridge.GetCleanupTargets as a
    /// fallback when a loaded profile has null CleanupTargets.
    /// </summary>
    public static List<CleanupTarget> Build() => new()
    {
        new CleanupTarget
        {
            Id = IdTemp,
            Name = "Temp",
            Path = @"%LocalAppData%\Temp",
            Kind = CleanupTarget.KindDirectory,
            Enabled = true,
        },
        new CleanupTarget
        {
            Id = IdPacCache,
            Name = "PacCache",
            Path = @"%LocalAppData%\Autodesk\Revit\PacCache",
            Kind = CleanupTarget.KindDirectory,
            Enabled = true,
        },
        new CleanupTarget
        {
            Id = IdJournals,
            Name = "Journals",
            Path = @"%LocalAppData%\Autodesk\Revit\{revit-version}\Journals",
            Kind = CleanupTarget.KindDirectory,
            Enabled = true,
        },
        new CleanupTarget
        {
            Id = IdCollabCache,
            Name = "Collaboration Cache",
            Path = @"%LocalAppData%\Autodesk\Revit\{revit-version}\CollaborationCache",
            Kind = CleanupTarget.KindDirectory,
            Enabled = true,
        },
        new CleanupTarget
        {
            Id = IdRecentFiles,
            Name = "Recent File List",
            Path = @"%AppData%\Autodesk\Revit\{revit-version}\Revit.ini",
            Kind = CleanupTarget.KindIniRecentFiles,
            Enabled = true,
        },
        // RST's own session logs. The boot-time prune in
        // RstApplication.PruneOldSessionLogs already caps these at 5
        // files; this entry is the user-visible hard exit when that
        // automation fails (locked files, IO errors, etc.) — they can
        // see "RST Logs · 8 files · 14 MB" in the Health snapshot and
        // sweep manually instead of grepping AppData by hand.
        new CleanupTarget
        {
            Id = IdRstLogs,
            Name = "RST Logs",
            Path = @"%AppData%\RST\logs",
            Kind = CleanupTarget.KindDirectory,
            Enabled = true,
        },
    };

    // The set of permitted cleanup paths, derived from the OOB list so it
    // stays in sync. Cleanup is locked to these — custom paths are no longer
    // authorable in the Builder, and an imported/hand-edited profile carrying
    // a non-preset path must never drive a recursive delete.
    private static readonly HashSet<string> PresetPaths =
        new(Build().Select(t => t.Path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="path"/> is one of the built-in preset cleanup
    /// paths. The Health cleaner and its size-preview both refuse any target
    /// whose path is not a preset, so a profile cannot point cleanup at an
    /// arbitrary directory (C:\, a UNC share, the user's documents, …).
    /// </summary>
    public static bool IsPresetPath(string? path)
        => !string.IsNullOrWhiteSpace(path) && PresetPaths.Contains(path!.Trim());
}
