// rst-c's own (not vendored): the Health window's services, extracted from the retired
// HealthBridge (v2.2.0's host object behind the HTML page health_viewer.html) with the JSON layer removed
// (spec #16, WU2). Same reads, writes and rules: the snapshot at %AppData%\RST\health_scan.json,
// the active profile's enabled cleanup targets (or the defaults), the ids re-checked here before
// anything is deleted, the activity read and the consent toggle. From spec #24 the activity read is
// the current model's rollups plus unfolded and live outbox files, folded in memory
// (ModelActivityReader.ReadCurrentModel); it writes nothing.

using System.IO;
using RST.Core.Configuration;
using RST.Core.Health;
using RST.Core.Profiles;
using RST.Core.Telemetry;
using RST.Core.Telemetry.Rollups;
using RST.UI.Resources;
using RST.Core.Logging;

namespace RST.UI.Health;

/// <summary>The outcome of a collection toggle: whether it saved, and the state now on disk.</summary>
public sealed record TelemetryToggleResult(bool Ok, bool? Enabled, string? Error);

/// <summary>What the Health window reads and does. Scan and Clean are slow and run off the UI thread.</summary>
public interface IHealthService
{
    /// <summary>The last saved snapshot, or null when there is none (or it does not parse).</summary>
    HealthSnapshot? LoadSnapshot();

    /// <summary>Scans the machine (WMI), saves the snapshot and returns it. Throws on failure. Any thread.</summary>
    HealthSnapshot RunScan();

    /// <summary>The cleanup targets the user may clean: the active profile's enabled ones, or the defaults.</summary>
    IReadOnlyList<CleanupTarget> GetCleanupTargets();

    /// <summary>
    /// Cleans the targets named by <paramref name="targetIds"/> (a target's id, or its name when it has
    /// none). The ids are re-checked against the active profile's enabled targets, so only the curated
    /// list can be cleaned. Counts are keyed by target id. Any thread.
    /// </summary>
    CleanResult Clean(IReadOnlyCollection<string> targetIds);

    /// <summary>
    /// The current model's activity, every day it has (the tab slices the range), plus the footer
    /// status; <see cref="ActivityData.Model"/> is null with no model open. Reads, never writes.
    /// Throws on failure.
    /// </summary>
    ActivityData GetActivity();

    /// <summary>Saves the collection choice and reconciles this process's collector.</summary>
    TelemetryToggleResult SetTelemetryEnabled(bool enabled);

    /// <summary>A scan or clean is running in this process, possibly started by a Health window that has since closed.</summary>
    bool IsWorkRunning { get; }

    /// <summary>Raised (on the worker thread) when the running scan or clean ends.</summary>
    event EventHandler? WorkFinished;
}

/// <summary>
/// The process-wide "a scan or clean is running" flag. A Health window can close while its scan or
/// clean finishes in the background; a window reopened meanwhile must not start a second one (two
/// writers of health_scan.json, or two cleans over the same targets). One piece of work at a time.
/// </summary>
public static class HealthWork
{
    private static int _running;

    public static bool IsRunning => Volatile.Read(ref _running) != 0;

    /// <summary>Raised on the worker thread when the work ends.</summary>
    public static event EventHandler? Finished;

    /// <summary>Marks work as running until the result is disposed. Throws when work is already running.</summary>
    public static IDisposable Begin()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new InvalidOperationException(UiStrings.Health_WorkRunning);
        return new End();
    }

    private sealed class End : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            Volatile.Write(ref _running, 0);
            Finished?.Invoke(null, EventArgs.Empty);
        }
    }
}

/// <summary>The production <see cref="IHealthService"/>.</summary>
public sealed class HealthService : IHealthService
{
    /// <summary>%AppData%\RST\health_scan.json, the snapshot file.</summary>
    public static string DefaultSnapshotPath => Path.Combine(AppDataPaths.Root, "health_scan.json");

    private readonly HealthContext _context;
    private readonly Action<bool>? _telemetryToggled;
    private readonly string _snapshotPath;
    private readonly Func<ActivityReadOptions>? _readOptions;

    /// <param name="context">The Revit context captured before the window opened.</param>
    /// <param name="telemetryToggled">
    /// Invoked with the clicked value on every successful persist of the collection toggle, including
    /// when the disk already matched (SC-034: another instance may have written the shared prefs while
    /// this process's collector kept its old state). Null: persist only.
    /// </param>
    /// <param name="snapshotPath">The snapshot file; null for <see cref="DefaultSnapshotPath"/>.</param>
    /// <param name="readOptions">Where and when the activity read looks (tests: folders, clock and zone); null: the user's
    /// outbox and rollups, now, local time. The live session always comes from <paramref name="context"/>.</param>
    public HealthService(HealthContext? context, Action<bool>? telemetryToggled = null, string? snapshotPath = null,
                         Func<ActivityReadOptions>? readOptions = null)
    {
        _context = context ?? HealthContext.Empty;
        _telemetryToggled = telemetryToggled;
        _snapshotPath = snapshotPath ?? DefaultSnapshotPath;
        _readOptions = readOptions;
        Log.Information("HealthService ready: revit={Revit}, model={Model}", _context.RevitVersion, _context.ModelName);
    }

    public HealthSnapshot? LoadSnapshot()
    {
        var snap = HealthScanner.Load(_snapshotPath);
        if (snap is null) Log.Information("Health: no snapshot at {Path}", _snapshotPath);
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
        Log.Information("Health: scan started");
        try
        {
            // The scan measures the targets the user would actually clean (enabled only), for the
            // Hardware section's Junk Files rows.
            var targets = ResolveActiveTargets().Where(t => t.Enabled).ToArray();
            var snap = HealthScanner.Capture(
                revitVersion: NullIfEmpty(_context.RevitVersion),
                revitBuild: NullIfEmpty(_context.RevitBuild),
                revitUsername: NullIfEmpty(_context.RevitUsername),
                modelName: NullIfEmpty(_context.ModelName),
                modelPath: NullIfEmpty(_context.ModelPath),
                modelSizeMb: _context.ModelSizeMb,
                warningsCount: _context.WarningsCount,
                cleanupTargets: targets);
            HealthScanner.Save(snap, _snapshotPath);
            return snap;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Health: scan failed");
            throw;
        }
    }

    public IReadOnlyList<CleanupTarget> GetCleanupTargets()
    {
        var targets = ResolveActiveTargets();
        // Disabled entries stay hidden: admins re-enable them in the Builder.
        var visible = targets.Where(t => t.Enabled).ToArray();
        Log.Information("Health: cleanup targets {Visible}/{Total} (active profile-aware)", visible.Length, targets.Count);
        return visible;
    }

    public CleanResult Clean(IReadOnlyCollection<string> targetIds)
    {
        using var work = HealthWork.Begin();
        Log.Information("Health: clean {Ids}", string.Join(",", targetIds));
        return HealthCleaner.Run(SelectTargets(ResolveActiveTargets(), targetIds));
    }

    /// <summary>
    /// The targets a clean may touch: the enabled ones of <paramref name="allowed"/> (the active
    /// profile's list) whose id (or name, when the id is empty) is in <paramref name="targetIds"/>,
    /// case-insensitively. An id that names no allowed target is ignored, so the window can never
    /// add a path of its own.
    /// </summary>
    public static CleanupTarget[] SelectTargets(IEnumerable<CleanupTarget> allowed, IEnumerable<string> targetIds)
    {
        var idSet = new HashSet<string>(targetIds, StringComparer.OrdinalIgnoreCase);
        return allowed
            .Where(t => t.Enabled && idSet.Contains(string.IsNullOrEmpty(t.Id) ? t.Name : t.Id))
            .ToArray();
    }

    public ActivityData GetActivity()
    {
        try
        {
            var options = ReadOptions();
            var currentFile = BuildCurrentFileIdentity();
            var model = currentFile is null ? null : ModelActivityReader.ReadCurrentModel(currentFile, options);
            var outbox = OutboxFiles.ReadStatus(options.OutboxDir, ActivityLog);
            var prefs = TelemetryPrefs.Read();
            return new ActivityData
            {
                Model = model,
                IsWorkshared = _context.IsWorkshared,
                IsCloud = _context.IsCloud,
                // The Warning Count and File Size cards: read by Revit when the window opened.
                WarningsCount = _context.WarningsCount,
                FileSizeBytes = _context.ModelSizeMb is { } mb ? mb * 1024 * 1024 : null,
                Status = new TelemetryStatus(prefs.Enabled, outbox.FileCount, outbox.TotalSizeBytes),
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Health: activity failed");
            throw;
        }
    }

    /// <summary>The user's folders, now, in local time (or the test's); this Revit session's file extends to now.</summary>
    private ActivityReadOptions ReadOptions()
    {
        var where = _readOptions?.Invoke();
        return new ActivityReadOptions
        {
            OutboxDir = where?.OutboxDir ?? AppDataPaths.TelemetryOutboxDir,
            RollupsDir = where?.RollupsDir ?? AppDataPaths.TelemetryRollupsDir,
            Zone = where?.Zone ?? TimeZoneInfo.Local,
            NowUtc = where?.NowUtc ?? DateTimeOffset.UtcNow,
            LiveSessionGuid = _context.TelemetrySessionGuid,
            Log = ActivityLog,
        };
    }

    private static void ActivityLog(string message) => Log.Warning("Telemetry activity: {Message}", message);

    public TelemetryToggleResult SetTelemetryEnabled(bool enabled)
    {
        Log.Information("Health: collection {Enabled}", enabled);
        try
        {
            if (!ConsentToggle.Apply(enabled, _telemetryToggled, log: m => Log.Warning("Telemetry toggle: {Message}", m)))
                return new TelemetryToggleResult(false, TelemetryPrefs.Read().Enabled, "failed to save preference");
            return new TelemetryToggleResult(true, enabled, null);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Health: collection toggle failed");
            return new TelemetryToggleResult(false, false, ex.Message);
        }
    }

    /// <summary>The active profile's cleanup targets, or the defaults when no profile is loaded or it predates RST-031.</summary>
    internal static List<CleanupTarget> ResolveActiveTargets()
    {
        try
        {
            var ap = ActiveProfile.Read();
            if (!ap.IsBlank)
            {
                var entry = ProfileStore.Resolve(ap.ProfileName, ap.ProfileId);
                if (entry?.Profile.CleanupTargets is { } targets) return targets;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Health: resolving the active profile's cleanup targets failed; using the defaults");
        }
        return CleanupDefaults.Build();
    }

    /// <summary>
    /// The open document's identity keys for current-model matching; null when no model is open,
    /// which differs from a document whose keys all failed to capture (that still returns an identity,
    /// and the read reports no usable key: "save it first").
    /// </summary>
    private DocumentIdentity? BuildCurrentFileIdentity()
    {
        var hasDoc = !string.IsNullOrEmpty(_context.ModelName) ||
                     !string.IsNullOrEmpty(_context.ModelPath) ||
                     _context.CreationGuid is not null ||
                     _context.CentralGuid is not null ||
                     _context.CentralPath is not null ||
                     _context.CloudModelGuid is not null;
        if (!hasDoc) return null;
        return new DocumentIdentity
        {
            CreationGuid = _context.CreationGuid,
            CloudProjectGuid = _context.CloudProjectGuid,
            CloudModelGuid = _context.CloudModelGuid,
            CentralGuid = _context.CentralGuid,
            CentralPath = _context.CentralPath,
            IsWorkshared = _context.IsWorkshared,
            IsCloud = _context.IsCloud,
        };
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
