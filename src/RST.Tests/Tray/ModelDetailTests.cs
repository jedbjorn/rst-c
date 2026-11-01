// ModelDetailTests.cs — the tray's model detail (spec #25 Model Detail; F19 #25 WU4): the pill a list chip opens on,
// the Revit versions a model was opened in, the "as of" day, a model whose history is gone, and AC 4 / AC 5 at unit
// level: the tray's read (ReadModel, by id, no Revit and no live session) and Revit's (ReadCurrentModel, by the open
// document) give the same cards and chart series for every range, folded or not.

using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using RST.Core.Health;
using RST.Core.Telemetry;
using RST.Core.Telemetry.Rollups;
using RST.Core.Tray;
using RST.Tests.Telemetry.Rollups;
using Xunit;

namespace RST.Tests.Tray;

public sealed class ModelDetailTests : IDisposable
{
    // Evening, after the day's session closed (17:00); no stored event sits exactly on the 90-day edge.
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 20, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 31);
    private readonly RollupStoreFixture _f = new(Now);

    public void Dispose() => _f.Dispose();

    // ── the range the detail opens on ───────────────────────────────────

    [Theory]
    [InlineData(ModelTimeChip.Recent, "1M")]
    [InlineData(ModelTimeChip.ThreeMonths, "3M")]
    [InlineData(ModelTimeChip.SixMonths, "6M")]
    [InlineData(ModelTimeChip.OneYear, "1Y")]
    public void The_detail_opens_on_the_pill_nearest_the_lists_chip(ModelTimeChip chip, string pill)
    {
        var range = ModelDetail.RangeFor(ModelList.DetailRangeFor(ModelListQuery.Default with { Chip = chip }));
        range.Label.Should().Be(pill);
        ModelDetail.Ranges.Should().Contain(range);
    }

    [Fact]
    public void While_searching_the_detail_opens_on_1Y()
    {
        var range = ModelDetail.RangeFor(ModelList.DetailRangeFor(ModelListQuery.Default with { Search = "tower", Chip = ModelTimeChip.Recent }));
        range.Should().Be(ActivityRanges.OneYear);
        range.IsWeekly.Should().BeTrue();
    }

    [Fact]
    public void The_pills_are_7D_1M_3M_6M_1Y()
    {
        ModelDetail.Ranges.Select(r => r.Label).Should().Equal("7D", "1M", "3M", "6M", "1Y");
        ModelDetail.Ranges.Select(r => r.IsWeekly).Should().Equal(false, false, false, true, true);
    }

    // ── header facts ────────────────────────────────────────────────────

    [Fact]
    public void Versions_opened_list_each_minor_newest_first_and_a_major_with_no_minor_alone()
    {
        RollupDay Day(string[] majors, string[] minors) => new() { RevitVersions = [.. majors], RevitSubversions = [.. minors] };
        var days = new[]
        {
            Day(["2025"], []),                       // before spec #24: no minor recorded
            Day(["2026"], ["2026.4"]),
            Day(["2026"], ["2026.10", "2026.4"]),    // numeric order: 2026.10 after 2026.4
            Day(["2027"], ["2027.1"]),
            Day(["2026"], []),                       // 2026 has minors: not listed alone
            Day([" "], [""]),
        };

        ModelDetail.VersionsOpened(days).Should().Equal("2027.1", "2026.10", "2026.4", "2025");
        ModelDetail.VersionsOpened(Array.Empty<RollupDay>()).Should().BeEmpty();
    }

    [Fact]
    public void Stats_are_as_of_their_local_day_and_health_prefers_the_read()
    {
        var model = new ModelActivity
        {
            LastStats = new ModelStatsPoint(new DateTimeOffset(2026, 10, 28, 23, 30, 0, TimeSpan.Zero), 612, 1_000),
            CurrentHealth = new HealthInputs(null, null, 320, null), // Red on its own
        };
        ModelDetail.StatsAsOf(model, TimeZoneInfo.Utc).Should().Be(new DateOnly(2026, 10, 28));
        var plus2 = TimeZoneInfo.CreateCustomTimeZone("plus2", TimeSpan.FromHours(2), "plus2", "plus2");
        ModelDetail.StatsAsOf(model, plus2).Should().Be(new DateOnly(2026, 10, 29));
        ModelDetail.StatsAsOf(ModelActivity.Empty, TimeZoneInfo.Utc).Should().BeNull();

        ModelDetail.Health(model, ModelHealthColour.Green).Should().Be(ModelHealthColour.Red);
        ModelDetail.Health(ModelActivity.Empty, ModelHealthColour.Amber).Should().Be(ModelHealthColour.Amber);
    }

    // ── the shared read path (AC 4, AC 5 at unit level) ────────────────

    private static SessionBuilder Workday(DocumentIdentity doc, DateOnly day, int editMinutes, int seed, string version = "2026", string minor = "2026.4")
    {
        var at = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        return new SessionBuilder().Start(at.AddHours(8).AddMinutes(55), version, minor)
            .Open(at.AddHours(9), doc, 10 + seed % 40)
            .Pulses(at.AddHours(10), at.AddHours(10).AddMinutes(editMinutes), doc)
            .Sync(at.AddHours(12), doc, 5 + seed % 70)
            .Stats(at.AddHours(12).AddMinutes(5), doc, 100 + seed, 50_000_000 + seed * 1000L)
            .Sync(at.AddHours(15), doc, 3 + seed % 20)
            .Close(at.AddHours(17), doc).End(at.AddHours(17).AddMinutes(1));
    }

    /// <summary>A year of work on one central model, in two Revit versions, every few days.</summary>
    private DocumentIdentity AYearOfWork()
    {
        var doc = SessionBuilder.Central("Riverside Tower");
        var n = 0;
        for (var back = 360; back >= 0; back -= back > 30 ? 9 : 2)
        {
            var day = Today.AddDays(-back);
            var newer = back < 60;
            Workday(doc, day, 15 + n * 7 % 90, n++, newer ? "2027" : "2026", newer ? "2027.1" : "2026.4")
                .Write(_f.Outbox, new DateTimeOffset(day.ToDateTime(new TimeOnly(18, 0)), TimeSpan.Zero));
        }
        return doc;
    }

    private static void SameNumbers(ModelActivity tray, ModelActivity revit)
    {
        foreach (var range in ModelDetail.Ranges)
        {
            ModelActivityCharts.Totals(tray, range, Today).Should().Be(ModelActivityCharts.Totals(revit, range, Today), range.Label);
            ModelActivityCharts.ActiveTime(tray, range, Today).Should().Equal(ModelActivityCharts.ActiveTime(revit, range, Today), range.Label);
            ModelActivityCharts.Opens(tray, range, Today).Should().Equal(ModelActivityCharts.Opens(revit, range, Today), range.Label);
            ModelActivityCharts.Syncs(tray, range, Today).Should().Equal(ModelActivityCharts.Syncs(revit, range, Today), range.Label);
            ModelActivityCharts.WeeklyOpens(tray, range, Today).Should().Equal(ModelActivityCharts.WeeklyOpens(revit, range, Today), range.Label);
            ModelActivityCharts.WeeklySyncs(tray, range, Today).Should().Equal(ModelActivityCharts.WeeklySyncs(revit, range, Today), range.Label);
            ModelActivityCharts.HealthTrend(tray, range, Today).Select(p => (p.Date, p.Health.Colour, p.Health.Score))
                .Should().Equal(ModelActivityCharts.HealthTrend(revit, range, Today).Select(p => (p.Date, p.Health.Colour, p.Health.Score)), range.Label);
        }
        tray.LastStats.Should().Be(revit.LastStats);
        ModelDetail.VersionsOpened(tray.Days).Should().Equal(ModelDetail.VersionsOpened(revit.Days));
    }

    [Fact]
    public void The_tray_reads_the_same_cards_and_chart_series_as_Revit_for_every_range_folded_or_not()
    {
        var doc = AYearOfWork();
        var id = ModelKey.From(doc)!.Value.ModelId;

        // The list only offers indexed models, so the tray reads after a fold: all folded, then a fold that stopped
        // part-way (a later file still raw), then a new session the worker has not seen yet.
        _f.Pass();
        SameNumbers(ModelActivityReader.ReadModel(id, _f.ReadOptions()), ModelActivityReader.ReadCurrentModel(doc, _f.ReadOptions()));
        _f.PassUntilQuiet(20);
        var tray = ModelActivityReader.ReadModel(id, _f.ReadOptions());
        SameNumbers(tray, ModelActivityReader.ReadCurrentModel(doc, _f.ReadOptions()));
        Workday(doc, Today, 40, 99, "2027", "2027.1").Write(_f.Outbox, Now);
        var later = ModelActivityReader.ReadModel(id, _f.ReadOptions());
        SameNumbers(later, ModelActivityReader.ReadCurrentModel(doc, _f.ReadOptions()));

        // The data really spans the ranges: weekly points at 6M and 1Y, events within 90 days, both versions.
        ModelActivityCharts.WeeklyOpens(tray, ActivityRanges.OneYear, Today).Count.Should().BeGreaterThan(30);
        ModelActivityCharts.Opens(tray, ActivityRanges.ThreeMonths, Today).Should().NotBeEmpty();
        ModelActivityCharts.Totals(later, ActivityRanges.SevenDays, Today).ActiveSeconds
            .Should().BeGreaterThan(ModelActivityCharts.Totals(tray, ActivityRanges.SevenDays, Today).ActiveSeconds);
        ModelDetail.VersionsOpened(tray.Days).Should().Equal("2027.1", "2026.4");
        later.LastStats!.WarningsCount.Should().Be(199);
        ModelDetail.IsGone(tray).Should().BeFalse();
    }

    [Fact]
    public void The_tray_read_needs_no_Revit_and_no_live_session()
    {
        var doc = AYearOfWork();
        _f.PassUntilQuiet(20);

        var tray = ModelActivityReader.ReadModel(ModelKey.From(doc)!.Value.ModelId, _f.ReadOptions(liveSessionGuid: null));

        tray.HasData.Should().BeTrue();
        tray.SessionActiveSeconds.Should().BeNull();
        tray.LiveOpenSinceUtc.Should().BeNull();
        tray.Identity.IsWorkshared.Should().BeTrue();
        tray.Identity.CentralPath.Should().Be(doc.CentralPath);
    }

    [Fact]
    public void A_model_whose_history_is_gone_reads_as_gone_not_as_an_error()
    {
        var doc = SessionBuilder.Local("Old");
        Workday(doc, Today.AddDays(-3), 20, 1).Write(_f.Outbox, Now.AddDays(-3));
        _f.PassUntilQuiet(20);
        var id = ModelKey.From(doc)!.Value.ModelId;
        ModelDetail.IsGone(ModelActivityReader.ReadModel(id, _f.ReadOptions())).Should().BeFalse();

        // Its rollup, backup, index and raw files deleted (a cleanup, a reset): nothing left to read.
        foreach (var f in Directory.GetFiles(_f.Root, "*", SearchOption.AllDirectories)) File.Delete(f);
        ModelDetail.IsGone(ModelActivityReader.ReadModel(id, _f.ReadOptions())).Should().BeTrue();
        ModelDetail.IsGone(ModelActivityReader.ReadModel("no-such-model", _f.ReadOptions())).Should().BeTrue();
    }

    [Fact]
    public void A_corrupt_rollup_with_its_raw_files_still_there_still_shows_its_history()
    {
        var doc = SessionBuilder.Local("Kept");
        Workday(doc, Today.AddDays(-2), 25, 2).Write(_f.Outbox, Now.AddDays(-2));
        _f.PassUntilQuiet(20);
        var id = ModelKey.From(doc)!.Value.ModelId;
        File.WriteAllText(_f.ModelPath(doc), "corrupt");
        if (File.Exists(_f.BakPath(doc))) File.WriteAllText(_f.BakPath(doc), "corrupt");

        var tray = ModelActivityReader.ReadModel(id, _f.ReadOptions());

        ModelDetail.IsGone(tray).Should().BeFalse();
        tray.ActiveSecondsIn(Today.AddDays(-6), Today).Should().Be(25 * 60);
    }
}
