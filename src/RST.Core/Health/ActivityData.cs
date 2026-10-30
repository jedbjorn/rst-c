// rst-c's own (not vendored): the Activity tab's data, as the retired HealthBridge.GetActivity
// shaped it for health_viewer.html, plus activityStatValues and the range pills from that page
// (spec #16, WU2).

using RST.Core.Telemetry;

namespace RST.Core.Health;

/// <summary>The Activity tab's range pills: 7D (the default), 1M, 3M, 6M.</summary>
public static class ActivityRanges
{
    public const int Default = 7;

    /// <summary>The pills, in order: their range in days and their label.</summary>
    public static IReadOnlyList<(int Days, string Label)> Pills { get; } = [(7, "7D"), (30, "1M"), (90, "3M"), (180, "6M")];

    /// <summary>A requested range clamped to the nearest allowed one at or above it (7, 30, 90, 180), as the bridge did.</summary>
    public static int Clamp(int requestedDays) => requestedDays switch
    {
        <= 7 => 7,
        <= 30 => 30,
        <= 90 => 90,
        _ => 180,
    };
}

/// <summary>The collection footer's status: the consent state and what the outbox holds.</summary>
public sealed record TelemetryStatus(bool Enabled, int OutboxFileCount, long OutboxSizeBytes, DateTimeOffset? OldestEventTs);

/// <summary>The current model's activity over the chosen range.</summary>
public sealed class ActivityFile
{
    /// <summary>The identity key the outbox was matched by (<see cref="ActivityMatchKinds"/>); null when the model has no usable key.</summary>
    public string? MatchedKeys { get; init; }

    public bool? IsWorkshared { get; init; }

    public bool? IsCloud { get; init; }

    /// <summary>Point-in-time, captured at window open (range-independent).</summary>
    public int? WarningsCount { get; init; }

    /// <summary>Point-in-time, captured at window open (range-independent).</summary>
    public double? FileSizeMb { get; init; }

    public IReadOnlyList<DayOpenHoursPoint> PerDayOpenHours { get; init; } = [];

    public IReadOnlyList<DurationPoint> OpenEvents { get; init; } = [];

    public IReadOnlyList<DurationPoint> SyncEvents { get; init; } = [];
}

/// <summary>One Activity load: the live session, the current file (null when no model is open) and the footer status.</summary>
public sealed class ActivityData
{
    public int RangeDays { get; init; } = ActivityRanges.Default;

    /// <summary>The telemetry session's start; the Session Timer ticks from it. Null: "—".</summary>
    public DateTimeOffset? SessionStartUtc { get; init; }

    /// <summary>When the current file was opened in this session; the "file open" sub-line ticks from it.</summary>
    public DateTimeOffset? CurrentFileOpenUtc { get; init; }

    public ActivityFile? File { get; init; }

    public TelemetryStatus Status { get; init; } = new(false, 0, 0, null);
}

/// <summary>The five data-driven stat cards (the Session Timer ticks on its own).</summary>
public sealed record ActivityStats(
    double TotalHours,
    double? LongestSyncSeconds,
    double? LongestOpenSeconds,
    int? WarningsCount,
    double? FileSizeBytes)
{
    /// <summary>
    /// <c>activityStatValues</c>: Session Total and the two Longest cards come from the same
    /// series the charts plot (so a range switch recomputes them); Warning Count and File Size
    /// are the model's point-in-time capture. Null when no model is open (every card "—").
    /// </summary>
    public static ActivityStats? From(ActivityFile? file)
    {
        if (file is null) return null;
        return new ActivityStats(
            file.PerDayOpenHours.Sum(p => p.Hours),
            Max(file.SyncEvents),
            Max(file.OpenEvents),
            file.WarningsCount,
            file.FileSizeMb is { } mb ? mb * 1024 * 1024 : null);
    }

    private static double? Max(IReadOnlyList<DurationPoint> events)
    {
        double? max = null;
        foreach (var e in events)
            if (max is null || e.Seconds > max) max = e.Seconds;
        return max;
    }
}
