// rst-c's own (not vendored): a scripted IHealthService and realistic sample data for the Health
// window's view-model and screenshot tests (spec #16, WU2).

using RST.Core.Health;
using RST.Core.Profiles;
using RST.Core.Telemetry;
using RST.Core.Telemetry.Rollups;
using RST.UI.Health;

namespace RST.RuntimeTests.Health;

/// <summary>Scripted answers, and a record of every call.</summary>
public sealed class FakeHealthService : IHealthService
{
    public HealthSnapshot? Snapshot { get; set; }

    /// <summary>What Scan returns; <see cref="ScanError"/> throws instead.</summary>
    public HealthSnapshot? ScanResult { get; set; }

    public Exception? ScanError { get; set; }

    public List<CleanupTarget> Targets { get; set; } = [];

    public CleanResult CleanResult { get; set; } = new();

    public Exception? CleanError { get; set; }

    public Func<ActivityData>? Activity { get; set; }

    public Exception? ActivityError { get; set; }

    public TelemetryToggleResult? ToggleResult { get; set; }

    public Exception? ToggleError { get; set; }

    public int ScanCalls { get; private set; }

    public List<IReadOnlyCollection<string>> CleanCalls { get; } = [];

    /// <summary>How many times the window read the activity.</summary>
    public int ActivityCalls { get; private set; }

    public List<bool> ToggleCalls { get; } = [];

    /// <summary>Work started by another (closed) window is running.</summary>
    public bool IsWorkRunning { get; set; }

    public event EventHandler? WorkFinished;

    /// <summary>Ends the other window's work.</summary>
    public void FinishWork()
    {
        IsWorkRunning = false;
        WorkFinished?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>When set, LoadSnapshot blocks until it is set (at most 30 s): a slow disk, for the first-open checks (SC-011).</summary>
    public ManualResetEventSlim? LoadGate { get; set; }

    /// <summary>How many times the window read the snapshot, and on which thread the last read ran.</summary>
    public int LoadCalls => Volatile.Read(ref _loadCalls);

    public int? LoadThreadId { get; private set; }

    /// <summary>Runs on the reading thread as the read starts (before <see cref="LoadGate"/>): what the test sees at that moment.</summary>
    public Action? BeforeLoad { get; set; }

    private int _loadCalls;

    public HealthSnapshot? LoadSnapshot()
    {
        LoadThreadId = Environment.CurrentManagedThreadId;
        Interlocked.Increment(ref _loadCalls);
        BeforeLoad?.Invoke();
        LoadGate?.Wait(TimeSpan.FromSeconds(30));
        return Snapshot;
    }

    public HealthSnapshot RunScan()
    {
        ScanCalls++;
        if (ScanError is not null) throw ScanError;
        return ScanResult ?? HealthSamples.Snapshot();
    }

    public IReadOnlyList<CleanupTarget> GetCleanupTargets() => Targets;

    public CleanResult Clean(IReadOnlyCollection<string> targetIds)
    {
        CleanCalls.Add(targetIds);
        if (CleanError is not null) throw CleanError;
        return CleanResult;
    }

    public ActivityData GetActivity()
    {
        ActivityCalls++;
        if (ActivityError is not null) throw ActivityError;
        return Activity?.Invoke() ?? HealthSamples.Activity(DateTimeOffset.UtcNow);
    }

    public TelemetryToggleResult SetTelemetryEnabled(bool enabled)
    {
        ToggleCalls.Add(enabled);
        if (ToggleError is not null) throw ToggleError;
        return ToggleResult ?? new TelemetryToggleResult(true, enabled, null);
    }
}

/// <summary>Realistic sample snapshots, cleanup targets and activity.</summary>
public static class HealthSamples
{
    /// <summary>A workstation with mixed severities: RAM danger (88 %), CPU soft warning (64 %), disk warning (42 GB free), model warning (1.7 GB).</summary>
    public static HealthSnapshot Snapshot() => new()
    {
        CaptureTimestamp = "2026-10-30T14:05:12.0000000Z",
        Identity = new Identity { WindowsUsername = "j.doe", DeviceName = "WS-ARCH-042" },
        Ram = new Ram { TotalMB = 32768, AvailableMB = 3932, UsedMB = 28836, UsedPercent = 88 },
        Cpu = new Cpu { Name = "Intel(R) Core(TM) i9-13900K", PhysicalCores = 24, LogicalCores = 32, UsedPercent = 64 },
        Gpus =
        [
            new Gpu { Name = "Intel(R) UHD Graphics 770", DriverVersion = "31.0.101.4502", VramTotalMB = 1024 },
            new Gpu { Name = "NVIDIA RTX A4000", DriverVersion = "31.0.15.3742", VramTotalMB = 16384 },
        ],
        Gpu = new Gpu { Name = "Intel(R) UHD Graphics 770", DriverVersion = "31.0.101.4502", VramTotalMB = 1024 },
        Disk = new Disk
        {
            TotalGB = 953.3, AvailableGB = 42.6, UsedGB = 910.7, UsedPercent = 95.5,
            Type = "SSD", BusType = "NVMe", FriendlyName = "Samsung SSD 980 PRO 1TB",
        },
        Display = new Display { MonitorCount = 2, PrimaryResolution = "2560x1440" },
        Network = new Network { AdapterName = "Intel(R) Ethernet Connection I219-LM", Type = "Ethernet", SpeedMbps = 1000 },
        Os = new OsInfo { Name = "Windows 11 Pro", Release = "23H2", Build = "22631.4317", Version = "10.0.22631" },
        Revit = new RevitInfo
        {
            Version = "2026", Build = "26.1.0.34", Username = "jdoe",
            HardwareAcceleration = true,
            Model = new RevitModel { Name = "Tower-A_Central.rvt", Path = @"\\fs01\projects\2410 Tower A\Tower-A_Central.rvt", SizeMB = 1740.5 },
            WarningsCount = 612,
            WarningsBySeverity = new Dictionary<string, int> { ["Warning"] = 598, ["Error"] = 14 },
        },
        Cleanup = new CleanupSummary
        {
            Targets =
            [
                new CleanupTargetSummary
                {
                    Id = "revit-journals", Name = "Revit journals", Kind = CleanupTarget.KindDirectory,
                    Path = @"%LocalAppData%\Autodesk\Revit\Autodesk Revit 2026\Journals",
                    ResolvedPaths = [@"C:\Users\j.doe\AppData\Local\Autodesk\Revit\Autodesk Revit 2026\Journals"],
                    FileCount = 1284, SizeBytes = 642L * 1024 * 1024,
                },
                new CleanupTargetSummary
                {
                    Id = "collab-cache", Name = "Collaboration cache", Kind = CleanupTarget.KindDirectory,
                    Path = @"%LocalAppData%\Autodesk\Revit\Autodesk Revit 2026\CollaborationCache",
                    ResolvedPaths = [@"C:\Users\j.doe\AppData\Local\Autodesk\Revit\Autodesk Revit 2026\CollaborationCache", @"D:\RevitCache"],
                    FileCount = 9312, SizeBytes = (long)(5.2 * 1024 * 1024 * 1024),
                },
                new CleanupTargetSummary
                {
                    Id = "recent-files", Name = "Recent files list", Kind = CleanupTarget.KindIniRecentFiles,
                    Path = @"%AppData%\Autodesk\Revit\Autodesk Revit 2026\Revit.ini",
                    ResolvedPaths = [@"C:\Users\j.doe\AppData\Roaming\Autodesk\Revit\Autodesk Revit 2026\Revit.ini"],
                    FileCount = 18,
                },
            ],
            TotalFileCount = 10614,
            TotalSizeBytes = 642L * 1024 * 1024 + (long)(5.2 * 1024 * 1024 * 1024),
        },
    };

    /// <summary>A quiet machine: everything OK, acceleration off (the Revit dot warns).</summary>
    public static HealthSnapshot Healthy()
    {
        var s = Snapshot();
        s.Ram.UsedPercent = 41;
        s.Cpu.UsedPercent = 12;
        s.Disk.AvailableGB = 412.4;
        s.Revit.HardwareAcceleration = false;
        s.Revit.WarningsCount = 37;
        s.Revit.Model.SizeMB = 212;
        s.Cleanup = null;
        return s;
    }

    public static List<CleanupTarget> Targets() =>
    [
        new() { Id = "revit-journals", Name = "Revit journals", Path = @"%LocalAppData%\Autodesk\Revit\Autodesk Revit 2026\Journals", Enabled = true },
        new() { Id = "collab-cache", Name = "Collaboration cache", Path = @"%LocalAppData%\Autodesk\Revit\Autodesk Revit 2026\CollaborationCache", Enabled = true },
        new() { Id = "recent-files", Name = "Recent files list", Path = @"%AppData%\Autodesk\Revit\Autodesk Revit 2026\Revit.ini", Kind = CleanupTarget.KindIniRecentFiles, Enabled = true },
    ];

    // Health inputs that land in each band (open and sync medians only: score 100, 59, 30, 0).
    private static readonly HealthInputs Green = new(null, null, 20, 20);
    private static readonly HealthInputs Yellow = new(null, null, 140, 140);
    private static readonly HealthInputs Amber = new(null, null, 220, 220);
    private static readonly HealthInputs Red = new(null, null, 300, 300);

    /// <summary>
    /// Six months of a workshared model's history, the way ModelActivityReader returns it: daily rows
    /// back to 189 days ago (every third day before the last two weeks, most days within them),
    /// timed opens and syncs for the last 90 days, health improving from red to green over the
    /// months with every colour and a grey day in the last week, and a live session (47 min active
    /// since it was opened 1 h 03 min ago). Deterministic for a given <paramref name="now"/>.
    /// </summary>
    public static ActivityData Activity(DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        HealthInputs[] lastWeek = [Green, Yellow, HealthInputs.None, Green, Amber, Red, Yellow]; // today, yesterday, … (3 days ago is a gap)
        var days = new List<RollupDay>();
        var opens = new List<DurationPoint>();
        var syncs = new List<DurationPoint>();
        for (var back = 189; back >= 0; back--)
        {
            if (back > 14 ? back % 3 != 0 : back % 7 == 3) continue; // sparser before the last two weeks; a gap in them
            var date = today.AddDays(-back);
            var midnight = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var dayOpens = Enumerable.Range(0, 1 + back % 2).Select(i => 35.0 + (back * 13 + i * 29) % 70).ToList();
            var daySyncs = Enumerable.Range(0, 2 + back % 3).Select(i => 9.5 + (back * 7 + i * 17) % 55).ToList();
            var activeMs = back == 0 ? (47 * 60 + 10) * 1000L : (25 + back * 37 % 190) * 60_000L;
            days.Add(new RollupDay
            {
                Date = date,
                ActiveMs = activeMs,
                OpenMs = activeMs + 95 * 60_000L,
                Opens = dayOpens.Count,
                OpenSumMs = (long)(dayOpens.Sum() * 1000),
                OpenMaxMs = (long)(dayOpens.Max() * 1000),
                Syncs = daySyncs.Count,
                SyncSumMs = (long)(daySyncs.Sum() * 1000),
                SyncMaxMs = (long)(daySyncs.Max() * 1000),
                RevitVersions = ["2026"],
                RevitSubversions = ["2026.4"],
                Health = back < lastWeek.Length ? lastWeek[back] : back > 120 ? Red : back > 70 ? Amber : back > 30 ? Yellow : Green,
            });
            if (back > 90) continue; // the event window
            for (var i = 0; i < dayOpens.Count; i++) opens.Add(new DurationPoint(midnight.AddHours(8 + i * 4), dayOpens[i]));
            for (var i = 0; i < daySyncs.Count; i++) syncs.Add(new DurationPoint(midnight.AddHours(10 + i * 2), daySyncs[i]));
        }
        return new ActivityData
        {
            Model = new ModelActivity
            {
                MatchedKeyKind = ActivityMatchKinds.CentralPath,
                ModelIds = ["5f0c1a7e9b3d4c2a8e6f1b0d3c5a7e9f"],
                Days = days,
                Opens = opens,
                Syncs = syncs,
                LiveOpenSinceUtc = now.AddHours(-1).AddMinutes(-3).AddSeconds(-41),
                SessionActiveSeconds = 47 * 60 + 10,
                Zone = TimeZoneInfo.Utc,
            },
            IsWorkshared = true,
            IsCloud = false,
            WarningsCount = 612,
            FileSizeBytes = 1740.5 * 1024 * 1024,
            Status = new TelemetryStatus(true, 23, 1_482_113),
        };
    }

    /// <summary>The VM's case: a fresh cloud model with a few minutes a day, a few opens and sub-second syncs.</summary>
    public static ActivityData SmallActivity(DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        double[] minutes = [1, 6, 0, 3, 5, 1, 4];
        double[] opens = [4.2, 6.8, 3.1];
        double[] syncs = [0.31, 0.12, 0.44, 0.27];
        return new ActivityData
        {
            Model = new ModelActivity
            {
                MatchedKeyKind = ActivityMatchKinds.Cloud,
                Days = minutes.Select((m, i) => new RollupDay
                {
                    Date = today.AddDays(i - 6),
                    ActiveMs = (long)(m * 60_000),
                    OpenMs = (long)((m + 3) * 60_000),
                    Health = i < 3 ? new HealthInputs(null, null, 5, null) : new HealthInputs(null, null, 5, 0.3),
                }).ToList(),
                Opens = opens.Select((s, i) => new DurationPoint(now.AddDays(i - 3), s)).ToList(),
                Syncs = syncs.Select((s, i) => new DurationPoint(now.AddDays(i - 4).AddHours(2), s)).ToList(),
                LiveOpenSinceUtc = now.AddMinutes(-6).AddSeconds(-58),
                SessionActiveSeconds = 4 * 60,
                Zone = TimeZoneInfo.Utc,
            },
            IsWorkshared = true,
            IsCloud = true,
            WarningsCount = 0,
            FileSizeBytes = 6.5 * 1024 * 1024,
            Status = new TelemetryStatus(true, 2, 18_432),
        };
    }

    /// <summary>A local model opened once today and not yet edited: no active time, a single open (one dot),
    /// one health dot, sync not applicable; collection off, so no live session.</summary>
    public static ActivityData SparseActivity(DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        return new ActivityData
        {
            Model = new ModelActivity
            {
                MatchedKeyKind = ActivityMatchKinds.Creation,
                Days = [new RollupDay { Date = today, OpenMs = 40 * 60_000, Opens = 1, OpenSumMs = 6400, OpenMaxMs = 6400,
                    Health = new HealthInputs(null, null, 6.4, null) }],
                Opens = [new DurationPoint(now.AddHours(-3), 6.4)],
                Zone = TimeZoneInfo.Utc,
            },
            IsWorkshared = false,
            IsCloud = false,
            WarningsCount = 3,
            FileSizeBytes = 18.25 * 1024 * 1024,
            Status = new TelemetryStatus(false, 0, 0),
        };
    }

    /// <summary>No model open in Revit.</summary>
    public static ActivityData NoModel() => new() { Status = new TelemetryStatus(true, 23, 1_482_113) };

    /// <summary>A cleanup result: journals fully deleted, two cache files locked.</summary>
    public static CleanResult CleanResult()
    {
        var r = new CleanResult();
        r.Deleted["revit-journals"] = 1284;
        r.Skipped["revit-journals"] = 0;
        r.Deleted["collab-cache"] = 9310;
        r.Skipped["collab-cache"] = 2;
        r.Deleted["recent-files"] = 18;
        r.Skipped["recent-files"] = 0;
        return r;
    }
}
