// rst-c's own (not vendored): the Activity tab's data and range pills (spec #16, WU2), rebuilt on
// the per-model rollups for spec #24 (Revit Health): the range a pill stands for, what one
// Activity load returns, and the period-aware series the four charts plot, shared with the tray's
// model detail (spec #25, which adds 1Y). Pure; the labels are the caller's.

using RST.Core.Telemetry;
using RST.Core.Telemetry.Rollups;

namespace RST.Core.Health;

/// <summary>
/// A range pill: its length in days, its label and how the charts plot it. 7D, 1M and 3M plot per
/// day (Active time) and per event (Opening time, Sync history); 6M and 1Y plot per ISO week
/// (spec #24, Revit Health; spec #25, Model Detail).
/// </summary>
public sealed record ActivityRange(int Days, string Label)
{
    /// <summary>The longest range that still plots per day and per event.</summary>
    public const int LongestDailyDays = 90;

    /// <summary>6M and 1Y: the charts plot ISO weeks.</summary>
    public bool IsWeekly => Days > LongestDailyDays;

    /// <summary>The ISO weeks a weekly range spans, the current one included (26 for 6M, 52 for 1Y); 0 for a daily range.</summary>
    public int Weeks => IsWeekly ? (int)Math.Round(Days / 7.0, MidpointRounding.AwayFromZero) : 0;

    /// <summary>
    /// The calendar days the range covers, ending <paramref name="today"/>. A daily range is the last
    /// <see cref="Days"/> days; a weekly range is the last <see cref="Weeks"/> whole ISO weeks
    /// including the current one (the tray list's 6M and 1Y chips use the same buckets, spec #25),
    /// so no week is plotted half-counted.
    /// </summary>
    public (DateOnly First, DateOnly Last) Window(DateOnly today) => IsWeekly
        ? (ActivityWeeks.WeekStart(today).AddDays(-7 * (Weeks - 1)), today)
        : (today.AddDays(-(Math.Max(1, Days) - 1)), today);
}

/// <summary>The range pills: Revit's 7D (the default), 1M, 3M, 6M; the tray's model detail adds 1Y.</summary>
public static class ActivityRanges
{
    public const int Default = 7;

    public static ActivityRange SevenDays { get; } = new(7, "7D");
    public static ActivityRange OneMonth { get; } = new(30, "1M");
    public static ActivityRange ThreeMonths { get; } = new(90, "3M");
    public static ActivityRange SixMonths { get; } = new(180, "6M");
    public static ActivityRange OneYear { get; } = new(365, "1Y");

    /// <summary>Revit's Activity tab: 7D, 1M, 3M, 6M.</summary>
    public static IReadOnlyList<ActivityRange> Pills { get; } = [SevenDays, OneMonth, ThreeMonths, SixMonths];

    /// <summary>The tray's model detail: 7D, 1M, 3M, 6M, 1Y.</summary>
    public static IReadOnlyList<ActivityRange> PillsWithYear { get; } = [SevenDays, OneMonth, ThreeMonths, SixMonths, OneYear];

    /// <summary>The pill of <paramref name="days"/>, or the shortest pill at or above it (1Y beyond 6M).</summary>
    public static ActivityRange For(int days) =>
        PillsWithYear.FirstOrDefault(r => r.Days >= days) ?? OneYear;
}

/// <summary>The collection footer's status: the consent state and what the outbox holds now.</summary>
public sealed record TelemetryStatus(bool Enabled, int OutboxFileCount, long OutboxSizeBytes);

/// <summary>
/// One Activity load (spec #24, Revit Health, "Data path"): the current model's activity as
/// <see cref="ModelActivityReader.ReadCurrentModel"/> returns it (range-independent; the tab slices
/// it), what Revit says about the model right now, and the footer status.
/// </summary>
public sealed class ActivityData
{
    /// <summary>The current model's activity; null when no model is open (every card "—", "No model open").</summary>
    public ModelActivity? Model { get; init; }

    public bool? IsWorkshared { get; init; }

    public bool? IsCloud { get; init; }

    /// <summary>The model's warnings, read by Revit when the window opened (range-independent).</summary>
    public int? WarningsCount { get; init; }

    /// <summary>The model's size on disk, read when the window opened (range-independent).</summary>
    public double? FileSizeBytes { get; init; }

    public TelemetryStatus Status { get; init; } = new(false, 0, 0);
}

/// <summary>One point of a per-day or per-week chart: the day (a week's Monday) and its value.</summary>
public sealed record ActivityPeriodValue(DateOnly Date, double Value, int Count = 0);

/// <summary>
/// The four charts' series for one model and range (spec #24, Revit Health and Model Health), the
/// same in Revit and in the tray. Values are seconds; a caller plotting hours divides.
/// </summary>
public static class ModelActivityCharts
{
    /// <summary>
    /// "Active time — this file": one point per calendar day (7D–3M) or per ISO week, its Monday
    /// (6M, 1Y), every day or week of the window included, zeros too, so a gap reads as a gap.
    /// </summary>
    public static IReadOnlyList<ActivityPeriodValue> ActiveTime(ModelActivity model, ActivityRange range, DateOnly today)
    {
        var (first, last) = range.Window(today);
        if (!range.IsWeekly)
            return model.PerDay(first, last).Select(d => new ActivityPeriodValue(d.Date, d.ActiveSeconds)).ToList();

        var weeks = model.WeeksIn(first, last).ToDictionary(w => w.WeekStart);
        var list = new List<ActivityPeriodValue>();
        for (var ws = ActivityWeeks.WeekStart(first); ws <= last; ws = ws.AddDays(7))
            list.Add(new ActivityPeriodValue(ws, weeks.TryGetValue(ws, out var w) ? w.ActiveSeconds : 0));
        return list;
    }

    /// <summary>"Opening time — this file" at 6M and 1Y: each week's average open (sum ÷ count), weeks with opens only.</summary>
    public static IReadOnlyList<ActivityPeriodValue> WeeklyOpens(ModelActivity model, ActivityRange range, DateOnly today)
    {
        var (first, last) = range.Window(today);
        return model.WeeksIn(first, last)
            .Where(w => w.AverageOpenSeconds is not null)
            .Select(w => new ActivityPeriodValue(w.WeekStart, w.AverageOpenSeconds!.Value, w.Opens))
            .ToList();
    }

    /// <summary>"Sync history — this file" at 6M and 1Y: each week's average sync, weeks with syncs only.</summary>
    public static IReadOnlyList<ActivityPeriodValue> WeeklySyncs(ModelActivity model, ActivityRange range, DateOnly today)
    {
        var (first, last) = range.Window(today);
        return model.WeeksIn(first, last)
            .Where(w => w.AverageSyncSeconds is not null)
            .Select(w => new ActivityPeriodValue(w.WeekStart, w.AverageSyncSeconds!.Value, w.Syncs))
            .ToList();
    }

    /// <summary>Each timed open in the window (the 7D–3M Opening time chart).</summary>
    public static IReadOnlyList<DurationPoint> Opens(ModelActivity model, ActivityRange range, DateOnly today)
    {
        var (first, last) = range.Window(today);
        return model.OpensIn(first, last);
    }

    /// <summary>Each sync in the window, at its start (the 7D–3M Sync history chart).</summary>
    public static IReadOnlyList<DurationPoint> Syncs(ModelActivity model, ActivityRange range, DateOnly today)
    {
        var (first, last) = range.Window(today);
        return model.SyncsIn(first, last);
    }

    /// <summary>"Health — this file": one dot per day used (7D–3M) or per week, at its last day used (6M, 1Y).</summary>
    public static IReadOnlyList<HealthTrendPoint> HealthTrend(ModelActivity model, ActivityRange range, DateOnly today)
    {
        var (first, last) = range.Window(today);
        return model.HealthTrend(first, last, range.IsWeekly);
    }

    /// <summary>The cards over the window: Activity (Total), Longest Sync and Longest Open Time (seconds; null with none).</summary>
    public static ActivityCardTotals Totals(ModelActivity model, ActivityRange range, DateOnly today)
    {
        var (first, last) = range.Window(today);
        return new ActivityCardTotals(
            model.ActiveSecondsIn(first, last),
            model.LongestSyncSecondsIn(first, last),
            model.LongestOpenSecondsIn(first, last));
    }
}

/// <summary>The range-dependent card values: Activity (Total), Longest Sync, Longest Open Time, in seconds.</summary>
public sealed record ActivityCardTotals(double ActiveSeconds, double? LongestSyncSeconds, double? LongestOpenSeconds);
