// rst-c's own (not vendored): Health outside Revit, for the tray's Health → Device page (spec #25 Device;
// decisions #53, #56, #59). The same snapshot, junk targets and cleaner as the add-in's HealthService, with three
// differences:
//   - the snapshot is %AppData%\RST\tray_health_scan.json, so neither the tray nor Revit overwrites the other's
//     Revit section;
//   - no Revit is running, so the scan records the installed Revit years and their builds (RevitInstalls) instead
//     of a running Revit's version, username, acceleration, model and warnings;
//   - Clean Junk Files lists each year-specific target once per Revit year found ("Journals — 2026"), and a row
//     cleans only that year's folder. A row is a structured (target, year) pair (CleanupRow), handed to the
//     dialog as an opaque handle; before anything is deleted it is checked against the active profile's enabled
//     targets again, as in the add-in, and a year only narrows that target's own paths.
// No Revit is needed and nothing is shared with the add-in (decision #29). Activity is the Models page's (WU3).

using System.Diagnostics;
using System.IO;
using RST.Core.Activation;
using RST.Core.Configuration;
using RST.Core.Health;
using RST.Core.Logging;
using RST.Core.Profiles;
using RST.UI.Resources;

namespace RST.UI.Health;

/// <summary>
/// One row of the tray's Junk dialog: the target at <paramref name="TargetIndex"/> in the profile's list, with the
/// id (or name) <paramref name="TargetKey"/> and path <paramref name="TargetPath"/> it had when listed, for every Revit
/// year (<paramref name="RevitYear"/> null) or one.
/// </summary>
public sealed record CleanupRow(int TargetIndex, string TargetKey, string TargetPath, int? RevitYear);

public sealed class DeviceHealthService : IHealthService
{
    /// <summary>%AppData%\RST\tray_health_scan.json, the tray's snapshot (Revit's is health_scan.json).</summary>
    public static string DefaultSnapshotPath => Path.Combine(AppDataPaths.Root, "tray_health_scan.json");

    private readonly string _snapshotPath;
    private readonly Func<IReadOnlyList<RevitInstallInfo>> _installedRevit;
    private readonly Func<IReadOnlyList<CleanupTarget>> _activeTargets;
    private readonly CleanupFolders? _folders;

    /// <summary>The last listing's rows by handle (replaced whole on each listing; read by a clean on another thread).</summary>
    private volatile IReadOnlyDictionary<string, CleanupRow> _rows = new Dictionary<string, CleanupRow>();

    /// <param name="snapshotPath">The snapshot file; null for <see cref="DefaultSnapshotPath"/>.</param>
    /// <param name="installedRevit">The installed Revit years and builds; null reads this PC (<see cref="ReadInstalledRevit"/>).</param>
    /// <param name="activeTargets">The cleanup targets; null: the active profile's, or the presets (as the add-in).</param>
    /// <param name="folders">The user folders the target paths' tokens stand for; null: this user's. Tests pass their own.</param>
    public DeviceHealthService(string? snapshotPath = null,
                               Func<IReadOnlyList<RevitInstallInfo>>? installedRevit = null,
                               Func<IReadOnlyList<CleanupTarget>>? activeTargets = null,
                               CleanupFolders? folders = null)
    {
        _snapshotPath = snapshotPath ?? DefaultSnapshotPath;
        _installedRevit = installedRevit ?? ReadInstalledRevit;
        _activeTargets = activeTargets ?? HealthService.ResolveActiveTargets;
        _folders = folders;
    }

    public HealthSnapshot? LoadSnapshot()
    {
        var snap = HealthScanner.Load(_snapshotPath);
        if (snap is null) Log.Information("Device health: no snapshot at {Path}", _snapshotPath);
        return snap;
    }

    public bool IsWorkRunning => HealthWork.IsRunning;

    public event EventHandler? WorkFinished
    {
        add => HealthWork.Finished += value;
        remove => HealthWork.Finished -= value;
    }

    public HealthSnapshot RunScan()
    {
        using var work = HealthWork.Begin();
        Log.Information("Device health: scan started");
        try
        {
            // As in the add-in: the Junk Files rows measure the targets the user would actually clean.
            var targets = Enabled(_activeTargets()).ToArray();
            var snap = HealthScanner.Capture(cleanupTargets: targets);
            snap.RevitInstalls = InstalledRevit();
            HealthScanner.Save(snap, _snapshotPath);
            Log.Information("Device health: Revit {Years}", string.Join(", ", snap.RevitInstalls.Select(r => r.Year + " " + r.Build)));
            return snap;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Device health: scan failed");
            throw;
        }
    }

    /// <summary>
    /// The Junk dialog's rows: the active profile's enabled targets (or the presets), in order, each year-specific
    /// target once per Revit year found ("Journals — 2026", the path with that year's folder). A year-specific target
    /// with no year folder on this PC is listed once, as in the add-in.
    /// <para>
    /// Each row's identity is a structured <see cref="CleanupRow"/> (which target, which year), kept here and handed
    /// out as an opaque handle (the row's <c>Id</c>), never a string built from the profile's ids: a profile's id can
    /// be anything, so a "&lt;id&gt;@&lt;year&gt;" string could name another target (PR #176 review F1). <see cref="Clean"/>
    /// accepts only the handles of the last listing.
    /// </para>
    /// </summary>
    public IReadOnlyList<CleanupTarget> GetCleanupTargets()
    {
        var targets = _activeTargets() ?? [];
        var rows = new List<CleanupTarget>();
        var issued = new Dictionary<string, CleanupRow>(StringComparer.Ordinal);
        for (var index = 0; index < targets.Count; index++)
        {
            var t = targets[index];
            if (t is null || !t.Enabled) continue;
            var years = YearsOf(t);
            if (years.Count == 0)
            {
                rows.Add(Row(issued, new CleanupRow(index, Key(t), t.Path, null), t.Name, t.Path, t.Kind));
                continue;
            }
            var name = string.IsNullOrEmpty(t.Name) ? UiStrings.Health_Unnamed : t.Name;
            foreach (var year in years)
            {
                rows.Add(Row(issued, new CleanupRow(index, Key(t), t.Path, year),
                    UiStrings.Health_JunkTargetYear(name, year.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    CleanupYears.DisplayPath(t.Path, year), t.Kind));
            }
        }
        _rows = issued;
        Log.Information("Device health: {Rows} cleanup rows from {Targets} targets", rows.Count, targets.Count);
        return rows;
    }

    public CleanResult Clean(IReadOnlyCollection<string> targetIds)
    {
        using var work = HealthWork.Begin();
        Log.Information("Device health: clean {Rows}", string.Join(", ", targetIds.Select(id => _rows.TryGetValue(id, out var r) ? r.ToString() : id)));
        return HealthCleaner.Run(SelectPicks(_rows, _activeTargets() ?? [], targetIds), _folders);
    }

    /// <summary>
    /// The picks a clean may run. Each <paramref name="rowIds"/> entry must be a handle of <paramref name="rows"/> (the
    /// dialog's last listing); its <see cref="CleanupRow"/> must still name the same enabled target in
    /// <paramref name="current"/> (the active profile now: same place, id and path), and a year only on a year-specific
    /// target. Anything else (an unknown handle, a profile changed since the listing, a target switched off) is
    /// ignored and logged, so a ticked row cleans exactly the target and year it showed and never another one. The
    /// year only narrows that target's own resolved paths. Each pick counts under its row's handle.
    /// </summary>
    public static IReadOnlyList<CleanupPick> SelectPicks(IReadOnlyDictionary<string, CleanupRow> rows,
                                                         IReadOnlyList<CleanupTarget> current, IEnumerable<string> rowIds)
    {
        var picks = new List<CleanupPick>();
        foreach (var id in rowIds.Where(i => !string.IsNullOrEmpty(i)).Distinct(StringComparer.Ordinal))
        {
            if (!rows.TryGetValue(id, out var row))
            {
                Log.Warning("Device health: ignoring cleanup row {Id}: not one the dialog listed", id);
                continue;
            }
            var target = row.TargetIndex >= 0 && row.TargetIndex < current.Count ? current[row.TargetIndex] : null;
            if (target is null || !target.Enabled
                || !string.Equals(Key(target), row.TargetKey, StringComparison.Ordinal)
                || !string.Equals(target.Path, row.TargetPath, StringComparison.Ordinal)
                || (row.RevitYear is not null && !CleanupYears.IsYearSpecific(target.Path)))
            {
                Log.Warning("Device health: ignoring cleanup row {Row}: the profile's targets changed since it was listed", row);
                continue;
            }
            picks.Add(new CleanupPick(target, row.RevitYear, id));
        }
        return picks;
    }

    /// <summary>Hands out the next row's handle for <paramref name="row"/> and returns the dialog's row.</summary>
    private static CleanupTarget Row(Dictionary<string, CleanupRow> issued, CleanupRow row, string name, string path, string kind)
    {
        // An opaque, per-listing handle: it is only ever looked up in this listing, never parsed or matched to a profile id.
        var handle = "row-" + issued.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        issued[handle] = row;
        return new CleanupTarget { Id = handle, Name = name, Path = path, Kind = kind, Enabled = true };
    }

    /// <summary>The supported Revit years installed on this PC (decision #21: 2025 and later), each with Revit.exe's version.</summary>
    public static IReadOnlyList<RevitInstallInfo> ReadInstalledRevit()
    {
        IReadOnlyList<RevitYear> years;
        try
        {
            years = RevitInstallations.Detect(AddinRoots.FromEnvironment());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Device health: reading the installed Revit years failed");
            return [];
        }
        return years.Select(y => new RevitInstallInfo
        {
            Year = y.Year,
            InstallDir = y.InstallDir ?? "",
            Build = string.IsNullOrEmpty(y.InstallDir) ? "" : ReadBuild(Path.Combine(y.InstallDir, "Revit.exe")),
        }).ToList();
    }

    /// <summary>
    /// Revit.exe's file version as Revit itself reports its build (<c>Application.VersionBuild</c>, for example
    /// 26.0.4.409); "" when it cannot be read.
    /// </summary>
    internal static string ReadBuild(string revitExe)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(revitExe);
            if (info.FileMajorPart > 0)
                return string.Join(".", info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
            return info.FileVersion?.Trim() ?? "";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Device health: reading the version of {Exe} failed", revitExe);
            return "";
        }
    }

    private List<RevitInstallInfo> InstalledRevit()
    {
        try
        {
            return _installedRevit().Where(r => r is not null).OrderBy(r => r.Year).ToList();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Device health: reading the installed Revit years failed");
            return [];
        }
    }

    /// <summary>The Revit years a year-specific preset target has a folder for, oldest first; none for any other target.</summary>
    private List<int> YearsOf(CleanupTarget t)
    {
        // Only a preset path is expanded (the cleaner and the size preview refuse any other), so a profile's path
        // never makes the tray list folders it would not clean.
        if (!CleanupYears.IsYearSpecific(t.Path) || !CleanupDefaults.IsPresetPath(t.Path)) return [];
        return CleanupPathResolver.ResolveEach(t.Path, _folders)
            .Select(p => p.RevitYear)
            .OfType<int>()
            .Distinct()
            .Order()
            .ToList();
    }

    private static IEnumerable<CleanupTarget> Enabled(IEnumerable<CleanupTarget>? targets) =>
        (targets ?? []).Where(t => t is not null && t.Enabled);

    private static string Key(CleanupTarget t) => string.IsNullOrEmpty(t.Id) ? t.Name ?? "" : t.Id;

    // The Models page (spec #25 WU3) owns activity; the Device page never asks for it.
    public ActivityData GetActivity(int rangeDays) =>
        throw new NotSupportedException("The tray's Device page has no Activity tab.");

    public TelemetryToggleResult SetTelemetryEnabled(bool enabled) =>
        new(false, null, "The tray's Device page has no Collection switch.");
}
