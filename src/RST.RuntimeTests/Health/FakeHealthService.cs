// rst-c's own (not vendored): a scripted IHealthService and realistic sample data for the Health
// window's view-model and screenshot tests (spec #16, WU2).

using RST.Core.Health;
using RST.Core.Profiles;
using RST.Core.Telemetry;
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

    public Func<int, ActivityData>? Activity { get; set; }

    public Exception? ActivityError { get; set; }

    public TelemetryToggleResult? ToggleResult { get; set; }

    public Exception? ToggleError { get; set; }

    public int ScanCalls { get; private set; }

    public List<IReadOnlyCollection<string>> CleanCalls { get; } = [];

    public List<int> ActivityCalls { get; } = [];

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

    public HealthSnapshot? LoadSnapshot() => Snapshot;

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

    public ActivityData GetActivity(int rangeDays)
    {
        ActivityCalls.Add(rangeDays);
        if (ActivityError is not null) throw ActivityError;
        return Activity?.Invoke(rangeDays) ?? HealthSamples.Activity(DateTimeOffset.UtcNow);
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

    /// <summary>Two weeks of history for a workshared model: curve charts in every card.</summary>
    public static ActivityData Activity(DateTimeOffset now, int rangeDays = 7)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        double[] hours = [3.2, 0, 5.5, 6.1, 2.4, 0, 4.8, 7.2, 1.1, 0, 3.9, 5.0, 6.6, 2.2];
        var days = Math.Min(rangeDays, hours.Length);
        var perDay = Enumerable.Range(0, days)
            .Select(i => new DayOpenHoursPoint(today.AddDays(i - days + 1), hours[hours.Length - days + i]))
            .ToList();
        double[] opens = [48, 52.5, 61, 39, 44, 95, 50];
        double[] syncs = [12, 18, 9.5, 41, 22, 15, 64, 19, 11];
        return new ActivityData
        {
            RangeDays = rangeDays,
            SessionStartUtc = now.AddHours(-2).AddMinutes(-14).AddSeconds(-9),
            CurrentFileOpenUtc = now.AddHours(-1).AddMinutes(-3).AddSeconds(-41),
            File = new ActivityFile
            {
                MatchedKeys = ActivityMatchKinds.Central,
                IsWorkshared = true,
                IsCloud = false,
                WarningsCount = 612,
                FileSizeMb = 1740.5,
                PerDayOpenHours = perDay,
                OpenEvents = opens.Select((s, i) => new DurationPoint(now.AddDays(i - opens.Length).AddHours(9), s)).ToList(),
                SyncEvents = syncs.Select((s, i) => new DurationPoint(now.AddDays((i - syncs.Length) * 0.7).AddHours(11), s)).ToList(),
            },
            Status = new TelemetryStatus(true, 23, 1_482_113, now.AddDays(-41)),
        };
    }

    /// <summary>A new, non-workshared model: one open (a single dot), no time recorded, sync not applicable; collection off.</summary>
    public static ActivityData SparseActivity(DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        return new ActivityData
        {
            RangeDays = 7,
            SessionStartUtc = null,
            CurrentFileOpenUtc = null,
            File = new ActivityFile
            {
                MatchedKeys = null,
                IsWorkshared = false,
                IsCloud = false,
                WarningsCount = 3,
                FileSizeMb = 18.25,
                PerDayOpenHours = Enumerable.Range(0, 7).Select(i => new DayOpenHoursPoint(today.AddDays(i - 6), 0)).ToList(),
                OpenEvents = [new DurationPoint(now.AddHours(-3), 6.4)],
                SyncEvents = [],
            },
            Status = new TelemetryStatus(false, 0, 0, null),
        };
    }

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
