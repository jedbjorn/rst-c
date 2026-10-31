// rst-c's own (not vendored): Health outside Revit, for the tray's Health → Device page (spec #25 Device;
// decisions #53, #56, #59). The same snapshot, junk targets and cleaner as the add-in's HealthService, with three
// differences:
//   - the snapshot is %AppData%\RST\tray_health_scan.json, so neither the tray nor Revit overwrites the other's
//     Revit section;
//   - no Revit is running, so the scan records the installed Revit years and their builds (RevitInstalls) instead
//     of a running Revit's version, username, acceleration, model and warnings;
//   - Clean Junk Files lists each year-specific target once per Revit year found ("Journals — 2026"), and a row
//     cleans only that year's folder. The rows' ids are checked against the active profile's enabled targets
//     again before anything is deleted, as in the add-in, and a year only narrows a target's own paths.
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

public sealed class DeviceHealthService : IHealthService
{
    /// <summary>%AppData%\RST\tray_health_scan.json, the tray's snapshot (Revit's is health_scan.json).</summary>
    public static string DefaultSnapshotPath => Path.Combine(AppDataPaths.Root, "tray_health_scan.json");

    private readonly string _snapshotPath;
    private readonly Func<IReadOnlyList<RevitInstallInfo>> _installedRevit;
    private readonly Func<IReadOnlyList<CleanupTarget>> _activeTargets;
    private readonly CleanupFolders? _folders;

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
    /// target once per Revit year found ("Journals — 2026", id "&lt;id&gt;@2026", the path with that year's folder).
    /// A year-specific target with no year folder on this PC is listed once, as in the add-in.
    /// </summary>
    public IReadOnlyList<CleanupTarget> GetCleanupTargets()
    {
        var targets = Enabled(_activeTargets()).ToList();
        var rows = new List<CleanupTarget>();
        foreach (var t in targets)
        {
            var years = YearsOf(t);
            if (years.Count == 0)
            {
                rows.Add(t);
                continue;
            }
            var name = string.IsNullOrEmpty(t.Name) ? UiStrings.Health_Unnamed : t.Name;
            foreach (var year in years)
            {
                rows.Add(new CleanupTarget
                {
                    Id = CleanupYears.PickId(Key(t), year),
                    Name = UiStrings.Health_JunkTargetYear(name, year.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    Path = CleanupYears.DisplayPath(t.Path, year),
                    Kind = t.Kind,
                    Enabled = true,
                });
            }
        }
        Log.Information("Device health: cleanup rows {Rows} from {Targets} targets", rows.Count, targets.Count);
        return rows;
    }

    public CleanResult Clean(IReadOnlyCollection<string> targetIds)
    {
        using var work = HealthWork.Begin();
        Log.Information("Device health: clean {Ids}", string.Join(",", targetIds));
        return HealthCleaner.Run(SelectPicks(_activeTargets(), targetIds), _folders);
    }

    /// <summary>
    /// The picks a clean may run: for each id, the enabled target of <paramref name="allowed"/> whose id (or name,
    /// when it has none) it is, case-insensitively, for every year; or, for "&lt;id&gt;@&lt;year&gt;", that year-specific
    /// target for that year only. An id that names no allowed target is ignored, so the dialog can never add a path,
    /// and a year only narrows the target's own resolved paths. Each pick counts under the id it was asked by.
    /// </summary>
    public static IReadOnlyList<CleanupPick> SelectPicks(IEnumerable<CleanupTarget> allowed, IEnumerable<string> targetIds)
    {
        var enabled = Enabled(allowed).ToList();
        var picks = new List<CleanupPick>();
        foreach (var id in targetIds.Where(i => !string.IsNullOrEmpty(i)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (enabled.FirstOrDefault(t => string.Equals(Key(t), id, StringComparison.OrdinalIgnoreCase)) is { } whole)
                picks.Add(new CleanupPick(whole, null, id));
            else if (CleanupYears.TryParsePickId(id, out var key, out var year)
                     && enabled.FirstOrDefault(t => CleanupYears.IsYearSpecific(t.Path)
                                                    && string.Equals(Key(t), key, StringComparison.OrdinalIgnoreCase)) is { } perYear)
                picks.Add(new CleanupPick(perYear, year, id));
            else
                Log.Warning("Device health: ignoring cleanup id {Id}: no such enabled target", id);
        }
        return picks;
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
