// rst-c's own (not vendored): weekly values derived from daily rows when
// read (spec #24, Rollup Store, "Weekly values"; decision #52). ISO weeks
// starting Monday, in the local zone the daily rows were bucketed in.
// Active and open time sum; open and sync time plot as the weekly average
// (sum ÷ count — a median cannot be rebuilt from daily rows); the Longest
// cards use the maximum; the Health chart uses the week's last day used.

using System.Globalization;
using RST.Core.Health;

namespace RST.Core.Telemetry.Rollups;

/// <summary>One ISO week of one model.</summary>
public sealed class ActivityWeek
{
    /// <summary>The Monday the week starts on.</summary>
    public DateOnly WeekStart { get; init; }
    public int IsoYear { get; init; }
    public int IsoWeek { get; init; }
    public long ActiveMs { get; internal set; }
    public long OpenMs { get; internal set; }
    public int Opens { get; internal set; }
    public long OpenSumMs { get; internal set; }
    public long OpenMaxMs { get; internal set; }
    public int Syncs { get; internal set; }
    public long SyncSumMs { get; internal set; }
    public long SyncMaxMs { get; internal set; }

    /// <summary>The week's last day with any data; its health inputs are the week's.</summary>
    public DateOnly LastDayUsed { get; internal set; }

    public HealthInputs? Health { get; internal set; }

    public double ActiveSeconds => ActiveMs / 1000.0;
    public double OpenSeconds => OpenMs / 1000.0;
    public double OpenSecondsMax => OpenMaxMs / 1000.0;
    public double SyncSecondsMax => SyncMaxMs / 1000.0;

    /// <summary>Sum of open durations ÷ opens; null with no timed opens.</summary>
    public double? AverageOpenSeconds => Opens > 0 ? OpenSumMs / 1000.0 / Opens : null;

    /// <summary>Sum of sync durations ÷ syncs; null with no syncs.</summary>
    public double? AverageSyncSeconds => Syncs > 0 ? SyncSumMs / 1000.0 / Syncs : null;
}

public static class ActivityWeeks
{
    /// <summary>The Monday of <paramref name="day"/>'s ISO week.</summary>
    public static DateOnly WeekStart(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    /// <summary>Weeks with data, oldest first, from daily rows (any order).</summary>
    public static IReadOnlyList<ActivityWeek> Derive(IEnumerable<RollupDay> days)
    {
        var weeks = new SortedDictionary<DateOnly, ActivityWeek>();
        foreach (var d in days.OrderBy(d => d.Date))
        {
            var ws = WeekStart(d.Date);
            if (!weeks.TryGetValue(ws, out var w))
            {
                var dt = ws.ToDateTime(TimeOnly.MinValue);
                weeks[ws] = w = new ActivityWeek
                {
                    WeekStart = ws,
                    IsoYear = ISOWeek.GetYear(dt),
                    IsoWeek = ISOWeek.GetWeekOfYear(dt),
                };
            }
            w.ActiveMs += d.ActiveMs;
            w.OpenMs += d.OpenMs;
            w.Opens += d.Opens;
            w.OpenSumMs += d.OpenSumMs;
            w.OpenMaxMs = Math.Max(w.OpenMaxMs, d.OpenMaxMs);
            w.Syncs += d.Syncs;
            w.SyncSumMs += d.SyncSumMs;
            w.SyncMaxMs = Math.Max(w.SyncMaxMs, d.SyncMaxMs);
            w.LastDayUsed = d.Date;
            if (d.Health is not null) w.Health = d.Health;
        }
        return weeks.Values.ToList();
    }
}
