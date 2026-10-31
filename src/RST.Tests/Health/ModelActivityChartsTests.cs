// ModelActivityChartsTests.cs — the Activity tab's ranges and period-aware
// series (spec #24, Revit Health and Model Health; reused by spec #25's model
// detail): 7D–3M per day and per event, 6M and 1Y per ISO week (active time
// summed, opens and syncs averaged), the Health trend's dots (per day used,
// per week's last day used, Grey for no inputs), its layout (coloured dots at
// their score's height, no numeric axis), the active-time format, and AC 6:
// pre-existing outbox history read through ReadCurrentModel at 6M equals the
// sum of those files' active time.

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using RST.Core.Health;
using RST.Core.Telemetry;
using RST.Core.Telemetry.Rollups;
using RST.Tests.Telemetry.Rollups;
using Xunit;

namespace RST.Tests.Health;

public sealed class ModelActivityChartsTests
{
    /// <summary>A Saturday; its ISO week starts Monday 2026-10-26.</summary>
    private static readonly DateOnly Today = new(2026, 10, 31);

    private static DateOnly Day(int month, int day) => new(2026, month, day);

    private static RollupDay Row(DateOnly date, double activeMinutes, int opens = 0, double openSum = 0, double openMax = 0,
        int syncs = 0, double syncSum = 0, double syncMax = 0, HealthInputs? health = null) => new()
    {
        Date = date,
        ActiveMs = (long)(activeMinutes * 60_000),
        OpenMs = (long)(Math.Max(activeMinutes, 1) * 60_000),
        Opens = opens,
        OpenSumMs = (long)(openSum * 1000),
        OpenMaxMs = (long)(openMax * 1000),
        Syncs = syncs,
        SyncSumMs = (long)(syncSum * 1000),
        SyncMaxMs = (long)(syncMax * 1000),
        Health = health ?? HealthInputs.None,
    };

    private static ModelActivity Model(params RollupDay[] days) => new()
    {
        MatchedKeyKind = ActivityMatchKinds.CentralPath,
        Days = days.OrderBy(d => d.Date).ToList(),
        Zone = TimeZoneInfo.Utc,
    };

    // ---- ranges -----------------------------------------------------------

    [Fact]
    public void Pills_are_Revits_four_and_the_trays_five()
    {
        ActivityRanges.Pills.Select(p => p.Label).Should().Equal("7D", "1M", "3M", "6M");
        ActivityRanges.PillsWithYear.Select(p => p.Label).Should().Equal("7D", "1M", "3M", "6M", "1Y");
        ActivityRanges.PillsWithYear.Select(p => p.Days).Should().Equal(7, 30, 90, 180, 365);
        ActivityRanges.Default.Should().Be(7);
        ActivityRanges.PillsWithYear.Select(p => p.IsWeekly).Should().Equal(false, false, false, true, true);
        ActivityRanges.SixMonths.Weeks.Should().Be(26);
        ActivityRanges.OneYear.Weeks.Should().Be(52);
    }

    [Theory]
    [InlineData(1, 7)]
    [InlineData(7, 7)]
    [InlineData(8, 30)]
    [InlineData(90, 90)]
    [InlineData(180, 180)]
    [InlineData(181, 365)]
    [InlineData(9999, 365)]
    public void For_picks_the_pill_at_or_above(int days, int expected) =>
        ActivityRanges.For(days).Days.Should().Be(expected);

    [Fact]
    public void Daily_ranges_are_the_last_n_days_weekly_ranges_whole_iso_weeks()
    {
        ActivityRanges.SevenDays.Window(Today).Should().Be((Day(10, 25), Today));
        ActivityRanges.OneMonth.Window(Today).Should().Be((Day(10, 2), Today));
        ActivityRanges.ThreeMonths.Window(Today).Should().Be((Day(8, 3), Today));

        var (first6, last6) = ActivityRanges.SixMonths.Window(Today);
        first6.Should().Be(Day(5, 4));
        first6.DayOfWeek.Should().Be(DayOfWeek.Monday);
        last6.Should().Be(Today);
        (ActivityWeeks.WeekStart(Today).DayNumber - first6.DayNumber).Should().Be(25 * 7, "26 weeks including the current one");

        var (first1, _) = ActivityRanges.OneYear.Window(Today);
        first1.DayOfWeek.Should().Be(DayOfWeek.Monday);
        (ActivityWeeks.WeekStart(Today).DayNumber - first1.DayNumber).Should().Be(51 * 7);
    }

    // ---- Active time ------------------------------------------------------

    [Fact]
    public void Active_time_daily_has_every_day_of_the_range_zeros_included()
    {
        var m = Model(Row(Day(10, 26), 30), Row(Day(10, 29), 12), Row(Day(10, 1), 99));

        var points = ModelActivityCharts.ActiveTime(m, ActivityRanges.SevenDays, Today);

        points.Select(p => p.Date).Should().Equal(Enumerable.Range(0, 7).Select(i => Day(10, 25 + i)));
        points.Select(p => p.Value).Should().Equal(0, 1800, 0, 0, 720, 0, 0);
    }

    [Fact]
    public void Active_time_weekly_sums_each_iso_week_and_keeps_empty_weeks()
    {
        var m = Model(
            Row(Day(5, 3), 500),              // the Sunday before the 6M window: out
            Row(Day(5, 4), 10), Row(Day(5, 10), 20), // week of 05-04
            Row(Day(10, 26), 30), Row(Day(10, 31), 5)); // the current week

        var points = ModelActivityCharts.ActiveTime(m, ActivityRanges.SixMonths, Today);

        points.Should().HaveCount(26);
        points.Select(p => p.Date).Should().OnlyContain(d => d.DayOfWeek == DayOfWeek.Monday);
        points[0].Should().Be(new ActivityPeriodValue(Day(5, 4), 30 * 60));
        points[^1].Should().Be(new ActivityPeriodValue(Day(10, 26), 35 * 60));
        points.Skip(1).Take(24).Should().OnlyContain(p => p.Value == 0);
        points.Sum(p => p.Value).Should().Be(ModelActivityCharts.Totals(m, ActivityRanges.SixMonths, Today).ActiveSeconds,
            "the weekly chart and the Activity (Total) card agree");
    }

    // ---- Opening time and Sync history ------------------------------------

    [Fact]
    public void Opens_and_syncs_are_per_event_up_to_3M()
    {
        var t = new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
        var m = new ModelActivity
        {
            Days = [Row(Day(10, 30), 10)],
            Opens = [new DurationPoint(t.AddDays(-100), 99), new DurationPoint(t.AddDays(-3), 40), new DurationPoint(t, 50)],
            Syncs = [new DurationPoint(t.AddDays(-8), 11), new DurationPoint(t.AddHours(1), 22)],
            Zone = TimeZoneInfo.Utc,
        };

        ModelActivityCharts.Opens(m, ActivityRanges.SevenDays, Today).Select(p => p.Seconds).Should().Equal(40, 50);
        ModelActivityCharts.Opens(m, ActivityRanges.ThreeMonths, Today).Select(p => p.Seconds).Should().Equal(40, 50);
        ModelActivityCharts.Syncs(m, ActivityRanges.SevenDays, Today).Select(p => p.Seconds).Should().Equal(22);
        ModelActivityCharts.Syncs(m, ActivityRanges.OneMonth, Today).Select(p => p.Seconds).Should().Equal(11, 22);
    }

    [Fact]
    public void Opens_and_syncs_at_6M_are_the_weekly_average_weeks_with_events_only()
    {
        var m = Model(
            Row(Day(10, 26), 10, opens: 2, openSum: 60, openMax: 40, syncs: 3, syncSum: 90, syncMax: 50),
            Row(Day(10, 28), 10, opens: 1, openSum: 90, openMax: 90),
            Row(Day(9, 1), 10, syncs: 1, syncSum: 12, syncMax: 12),
            Row(Day(8, 4), 10));

        var opens = ModelActivityCharts.WeeklyOpens(m, ActivityRanges.SixMonths, Today);
        var syncs = ModelActivityCharts.WeeklySyncs(m, ActivityRanges.SixMonths, Today);

        opens.Should().Equal(new ActivityPeriodValue(Day(10, 26), 50, 3)); // (60 + 90) / 3
        syncs.Should().Equal(new ActivityPeriodValue(Day(8, 31), 12, 1), new ActivityPeriodValue(Day(10, 26), 30, 3));

        var totals = ModelActivityCharts.Totals(m, ActivityRanges.SixMonths, Today);
        totals.LongestOpenSeconds.Should().Be(90, "the Longest cards use the maximum");
        totals.LongestSyncSeconds.Should().Be(50);
        totals.ActiveSeconds.Should().Be(40 * 60);
    }

    [Fact]
    public void Totals_with_nothing_in_range_are_zero_and_null()
    {
        var totals = ModelActivityCharts.Totals(ModelActivity.Empty, ActivityRanges.SevenDays, Today);
        totals.Should().Be(new ActivityCardTotals(0, null, null));
    }

    // ---- Health trend -------------------------------------------------------

    private static HealthInputs Healthy => new(100L * 1024 * 1024, 10, 20, 20);   // green
    private static HealthInputs Slow => new(null, null, 300, 30);                  // yellow (50)
    private static HealthInputs Heavy => new(4096L * 1024 * 1024, 8192 * 2, 300, 300); // red

    [Fact]
    public void Health_trend_is_one_dot_per_day_used_grey_when_no_inputs()
    {
        var m = Model(
            Row(Day(10, 27), 10, health: Healthy),
            Row(Day(10, 28), 10),                     // a day used with no inputs: Grey
            Row(Day(10, 30), 10, health: Heavy));

        var trend = ModelActivityCharts.HealthTrend(m, ActivityRanges.SevenDays, Today);

        trend.Select(p => p.Date).Should().Equal(Day(10, 27), Day(10, 28), Day(10, 30));
        trend.Select(p => p.Health.Colour).Should().Equal(ModelHealthColour.Green, ModelHealthColour.Grey, ModelHealthColour.Red);
        trend[1].Health.Score.Should().BeNull();
    }

    [Fact]
    public void Health_trend_at_6M_is_one_dot_per_week_at_its_last_day_used()
    {
        var m = Model(
            Row(Day(10, 19), 10, health: Heavy), Row(Day(10, 22), 10, health: Slow),
            Row(Day(10, 26), 10, health: Slow), Row(Day(10, 29), 10, health: Healthy));

        var trend = ModelActivityCharts.HealthTrend(m, ActivityRanges.SixMonths, Today);

        trend.Select(p => p.Date).Should().Equal(Day(10, 22), Day(10, 29));
        trend.Select(p => p.Health.Colour).Should().Equal(ModelHealthColour.Yellow, ModelHealthColour.Green);
    }

    [Fact]
    public void Health_trend_layout_places_dots_at_their_score_and_draws_no_numbers()
    {
        HealthTrendDot Dot(double x, ModelHealthColour c, double? score) =>
            new(x, score, ModelHealth.ToSeverity(c), "2026-10-30 — " + c);
        var dots = new[]
        {
            Dot(0, ModelHealthColour.Green, 100),
            Dot(1, ModelHealthColour.Yellow, 60),
            Dot(2, ModelHealthColour.Grey, null),
            Dot(3, ModelHealthColour.Red, 0),
        };

        var layout = ActivityChartLayout.BuildHealthTrend(dots, x => "d" + x);

        layout.Mode.Should().Be(ChartMode.Dots);
        layout.Curve.Should().BeNull("dots only: the run of dots shows the trend");
        layout.GridLines.Should().HaveCount(3, "the band edges 75 / 50 / 25");
        layout.GridLines.Should().OnlyContain(g => g.Label == "", "no numeric axis");
        layout.Dots.Select(d => d.Tone).Should().Equal(HealthSeverity.Ok, HealthSeverity.WarnSoft, HealthSeverity.None, HealthSeverity.Danger);
        layout.Dots.Select(d => d.Label).Should().Equal(dots.Select(d => d.Label));
        layout.Dots[0].Y.Should().BeLessThan(layout.Dots[1].Y, "a higher score sits higher");
        layout.Dots[1].Y.Should().BeLessThan(layout.Dots[3].Y);
        layout.Dots[2].Y.Should().BeApproximately(layout.GridLines[1].Y, 0.001, "Grey sits mid-height, on the 50 line");
        layout.Dots.Should().OnlyContain(d => d.Y > ActivityChartLayout.PadTop && d.Y < ActivityChartLayout.PadTop + ActivityChartLayout.InnerHeight,
            "no dot is cut at 0 or 100");
        layout.Ticks.Select(t => t.Label).Should().Equal("d0", "d1", "d2", "d3");
    }

    [Fact]
    public void Health_trend_layout_with_no_dots_is_empty_and_one_dot_is_centred()
    {
        ActivityChartLayout.BuildHealthTrend([], x => "").Mode.Should().Be(ChartMode.Empty);
        var one = ActivityChartLayout.BuildHealthTrend([new HealthTrendDot(5, 80, HealthSeverity.Ok, "x")], x => "");
        one.Dots.Single().X.Should().Be(ActivityChartLayout.PadLeft + ActivityChartLayout.InnerWidth / 2);
    }

    // ---- formatting ---------------------------------------------------------

    [Theory]
    [InlineData(0, "0m")]
    [InlineData(29, "0m")]
    [InlineData(60, "1m")]
    [InlineData(11 * 60 + 20, "11m")]
    [InlineData(3600 + 5 * 60, "1h 05m")]
    [InlineData(26 * 3600, "26h 00m")]
    [InlineData(-5, "0m")]
    public void Active_time_reads_in_whole_minutes(double seconds, string expected) =>
        HealthFormat.Active(seconds).Should().Be(expected);

    [Fact]
    public void Active_time_null_is_a_dash() => HealthFormat.Active(null).Should().Be("—");
}

/// <summary>AC 6 (the Revit half): pre-existing v3.x outbox history read the way Revit's Activity tab reads it.</summary>
public sealed class ExistingHistoryCarriesOverTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    private readonly RollupStoreFixture _f = new(Now);

    public void Dispose() => _f.Dispose();

    private static DateTimeOffset D(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    /// <summary>A v3.x session (no revit_subversion, no model_stats): open, edit for <paramref name="minutes"/>, close.</summary>
    private static SessionBuilder OldSession(DocumentIdentity doc, int month, int day, int minutes) =>
        new SessionBuilder().Start(D(month, day, 8, 55), subversion: null).Open(D(month, day, 9), doc, 20)
            .Pulses(D(month, day, 10), D(month, day, 10).AddMinutes(minutes), doc)
            .Sync(D(month, day, 12), doc, 30)
            .Close(D(month, day, 17), doc).End(D(month, day, 17, 1));

    [Fact]
    public void Activity_Total_at_6M_equals_the_sum_of_active_time_across_the_pre_existing_files()
    {
        var doc = SessionBuilder.Central("Tower");
        var other = SessionBuilder.Central("Annex");
        (int Month, int Day, int Minutes)[] files =
            [(5, 4, 25), (6, 15, 40), (7, 1, 7), (8, 20, 15), (10, 29, 30), (10, 30, 12)];
        foreach (var (m, d, min) in files) OldSession(doc, m, d, min).Write(_f.Outbox, D(m, d, 18));
        OldSession(doc, 5, 3, 60).Write(_f.Outbox, D(5, 3, 18));   // the day before the 6M window
        OldSession(other, 10, 30, 45).Write(_f.Outbox, D(10, 30, 19)); // another model
        var expectedInRange = files.Sum(f => f.Minutes) * 60.0;

        var before = Read(doc);
        _f.Pass().Folded.Should().Be(files.Length + 2, "the tray's first pass folds every pre-existing file");
        var afterFold = Read(doc);
        for (var i = 0; i < 3; i++) _f.Pass(); // seal, carry the seal to .bak.json, clean up
        var afterCleanup = Read(doc);

        _f.OutboxNames().Should().HaveCount(3, "cleanup kept only the newest 3 raw files; the rest lives in the rollups");
        foreach (var a in new[] { before, afterFold, afterCleanup })
        {
            ModelActivityCharts.Totals(a, ActivityRanges.SixMonths, Today(a)).ActiveSeconds.Should().Be(expectedInRange);
            ModelActivityCharts.ActiveTime(a, ActivityRanges.SixMonths, Today(a)).Sum(p => p.Value).Should().Be(expectedInRange);
            a.Days.Sum(d => d.ActiveSeconds).Should().Be(expectedInRange + 60 * 60, "all time also holds the 05-03 file");
            a.OldestDay.Should().Be(new DateOnly(2026, 5, 3), "the footer's \"since\" is the model's oldest day");
        }
    }

    private ModelActivity Read(DocumentIdentity doc) => ModelActivityReader.ReadCurrentModel(doc, _f.ReadOptions());

    private static DateOnly Today(ModelActivity a) => ActiveTime.LocalDay(Now, a.Zone);
}
