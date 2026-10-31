// ActivityWeeksTests.cs — spec #24 AC 4: weekly values and the event window.
// ISO-week sums and averages match hand-computed fixtures (Monday start, the
// year-boundary week); events older than 90 days drop except the last 10
// opens and the last 10 syncs.

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using RST.Core.Health;
using RST.Core.Telemetry;
using RST.Core.Telemetry.Rollups;
using Xunit;

namespace RST.Tests.Telemetry.Rollups;

public sealed class ActivityWeeksTests
{
    private static RollupDay Day(int y, int m, int d, long activeS, long openS, int opens = 0, long openSumS = 0,
        long openMaxS = 0, int syncs = 0, long syncSumS = 0, long syncMaxS = 0, HealthInputs? health = null) => new()
    {
        Date = new DateOnly(y, m, d),
        ActiveMs = activeS * 1000,
        OpenMs = openS * 1000,
        Opens = opens,
        OpenSumMs = openSumS * 1000,
        OpenMaxMs = openMaxS * 1000,
        Syncs = syncs,
        SyncSumMs = syncSumS * 1000,
        SyncMaxMs = syncMaxS * 1000,
        Health = health,
    };

    [Fact]
    public void Weeks_start_on_Monday()
    {
        ActivityWeeks.WeekStart(new DateOnly(2026, 10, 26)).Should().Be(new DateOnly(2026, 10, 26)); // Monday
        ActivityWeeks.WeekStart(new DateOnly(2026, 11, 1)).Should().Be(new DateOnly(2026, 10, 26));  // Sunday
        ActivityWeeks.WeekStart(new DateOnly(2026, 10, 25)).Should().Be(new DateOnly(2026, 10, 19));  // Sunday before
    }

    [Fact]
    public void Weekly_sums_averages_and_maxima_match_hand_computed_values()
    {
        var late = new HealthInputs(1, 1, 200, null);
        var days = new[]
        {
            // Week of Mon 2026-10-19: Tue and Sun.
            Day(2026, 10, 20, activeS: 600, openS: 3600, opens: 2, openSumS: 40, openMaxS: 30, syncs: 3, syncSumS: 90, syncMaxS: 50),
            Day(2026, 10, 25, activeS: 300, openS: 1800, opens: 1, openSumS: 20, openMaxS: 20, syncs: 1, syncSumS: 10, syncMaxS: 10,
                health: late),
            // Week of Mon 2026-10-26: Monday only, no syncs.
            Day(2026, 10, 26, activeS: 100, openS: 200, opens: 4, openSumS: 100, openMaxS: 60),
        };

        var weeks = ActivityWeeks.Derive(days.AsEnumerable().Reverse());

        weeks.Should().HaveCount(2);
        var w1 = weeks[0];
        w1.WeekStart.Should().Be(new DateOnly(2026, 10, 19));
        (w1.IsoYear, w1.IsoWeek).Should().Be((2026, 43));
        w1.ActiveSeconds.Should().Be(900);
        w1.OpenSeconds.Should().Be(5400);
        w1.Opens.Should().Be(3);
        w1.AverageOpenSeconds.Should().BeApproximately(60.0 / 3, 1e-9);
        w1.OpenSecondsMax.Should().Be(30);
        w1.Syncs.Should().Be(4);
        w1.AverageSyncSeconds.Should().Be(25);
        w1.SyncSecondsMax.Should().Be(50);
        w1.LastDayUsed.Should().Be(new DateOnly(2026, 10, 25));
        w1.Health.Should().Be(late);

        var w2 = weeks[1];
        (w2.IsoYear, w2.IsoWeek).Should().Be((2026, 44));
        w2.AverageOpenSeconds.Should().Be(25);
        w2.AverageSyncSeconds.Should().BeNull("no syncs that week");
    }

    [Fact]
    public void The_week_across_new_year_is_one_ISO_week()
    {
        var weeks = ActivityWeeks.Derive(new[]
        {
            Day(2026, 12, 28, 60, 60),
            Day(2027, 1, 3, 60, 60),
            Day(2027, 1, 4, 60, 60),
        });
        weeks.Should().HaveCount(2);
        (weeks[0].WeekStart, weeks[0].IsoYear, weeks[0].IsoWeek).Should().Be((new DateOnly(2026, 12, 28), 2026, 53));
        weeks[0].ActiveSeconds.Should().Be(120);
        (weeks[1].IsoYear, weeks[1].IsoWeek).Should().Be((2027, 1));
    }

    [Fact]
    public void Events_older_than_90_days_drop_except_the_last_10_opens_and_10_syncs()
    {
        var now = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        var r = new ModelRollup
        {
            // 15 opens 200–186 days ago: only the newest 10 survive.
            Opens = Enumerable.Range(0, 15).Select(i => new DurationPoint(now.AddDays(-200 + i), 10 + i)).ToList(),
            // 30 syncs: 20 old (120–101 days ago) and 10 recent; the window alone keeps the recent ones,
            // which are also the newest 10 — so all 20 old ones drop.
            Syncs = Enumerable.Range(0, 20).Select(i => new DurationPoint(now.AddDays(-120 + i), 5))
                .Concat(Enumerable.Range(0, 10).Select(i => new DurationPoint(now.AddDays(-30 + i), 7)))
                .ToList(),
        };

        RollupEditor.TrimEvents(r, now);

        r.Opens.Should().HaveCount(10);
        r.Opens.Select(p => p.Seconds).Should().Equal(Enumerable.Range(5, 10).Select(i => 10.0 + i));
        r.OpensTrimmedThrough.Should().Be(now.AddDays(-196));
        r.Syncs.Should().HaveCount(10);
        r.Syncs.Should().OnlyContain(p => p.Seconds == 7);
        r.SyncsTrimmedThrough.Should().Be(now.AddDays(-101));

        // Within 90 days everything stays, beyond the 10.
        var recent = new ModelRollup
        {
            Opens = Enumerable.Range(0, 40).Select(i => new DurationPoint(now.AddDays(-89).AddHours(i), 1)).ToList(),
        };
        RollupEditor.TrimEvents(recent, now);
        recent.Opens.Should().HaveCount(40);
        recent.OpensTrimmedThrough.Should().BeNull();
    }

    [Fact]
    public void Health_medians_use_the_last_10_and_keep_the_stored_value_once_the_window_no_longer_reaches()
    {
        var zone = TimeZoneInfo.Utc;
        var start = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        var r = new ModelRollup();
        var days = new List<RollupDay>();
        for (var i = 0; i < 12; i++)
        {
            r.Opens.Add(new DurationPoint(start.AddDays(i), i + 1)); // 1, 2, … 12 seconds
            days.Add(new RollupDay { Date = DateOnly.FromDateTime(start.AddDays(i).UtcDateTime), OpenMs = 1000, Opens = 1 });
        }
        r.Days = days;
        RollupEditor.RecomputeHealth(r, days[0].Date, zone);
        r.Days[0].Health!.OpenMedianSeconds.Should().Be(1);
        r.Days[3].Health!.OpenMedianSeconds.Should().Be(2.5);
        r.Days[11].Health!.OpenMedianSeconds.Should().Be(7.5, "median of 3..12");

        // Once early events are trimmed, early days keep what they had.
        r.Opens.RemoveRange(0, 5);
        r.OpensTrimmedThrough = start.AddDays(4);
        r.Days[3].Health = r.Days[3].Health! with { OpenMedianSeconds = 99 };
        RollupEditor.RecomputeHealth(r, days[0].Date, zone);
        r.Days[3].Health!.OpenMedianSeconds.Should().Be(99);
        r.Days[11].Health!.OpenMedianSeconds.Should().Be(7.5);
    }
}
