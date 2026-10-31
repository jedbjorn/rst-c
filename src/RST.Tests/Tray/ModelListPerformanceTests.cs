// ModelListPerformanceTests.cs — spec #25 AC 9 (logic) and the list's read path (F19 #25 WU1).
// AC 9 is inferred from synthetic timing (CI is a guard, not a lab): with a 500-model index,
// index load + filter ≤ 150 ms on a user's PC, guarded on CI at 2× (300 ms) on the best of
// three fresh loads, each from the file into a new instance after a JIT warm-up on a separate
// small index (all three logged); changing a chip ≤ 100 ms.
// Also: an index the real fold wrote lists with the same active time the model detail reads.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using FluentAssertions;
using RST.Core.Health;
using RST.Core.Telemetry;
using RST.Core.Telemetry.Rollups;
using RST.Core.Tray;
using RST.Tests.Telemetry.Rollups;
using Xunit;
using Xunit.Abstractions;

namespace RST.Tests.Tray;

public sealed class ModelListPerformanceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 31);
    private readonly RollupStoreFixture _f = new(Now);

    /// <summary>Spec #25 AC 9: the user-PC target for index load + filter is 150 ms; the CI guard
    /// is 2× that, 300 ms, on the best of three fresh loads (a shared CI runner is a guard, not
    /// a lab; on it the best of three measured about 151 ms).</summary>
    private const double CiLoadGuardMs = 300;

    /// <summary>Spec #25 AC 9: changing a chip, ≤ 100 ms, unchanged on CI.</summary>
    private const double ChipChangeMs = 100;
    private readonly ITestOutputHelper _out;

    public ModelListPerformanceTests(ITestOutputHelper output) => _out = output;

    public void Dispose() => _f.Dispose();

    /// <summary>A full index row: 14 recent days, 53 recent weeks, three majors and subversions,
    /// 20 local paths and titles seen — the ~4 KB per model spec #24 sizes the index at.</summary>
    private static ModelIndexRow SyntheticRow(int i, Random rng)
    {
        var name = $"Project {i:D3} - Building {(char)('A' + i % 26)}";
        var central = $@"\\fileserver{i % 7}\projects\{i:D3}\{name}.rvt";
        var row = new ModelIndexRow
        {
            ModelId = new ModelKey(ActivityMatchKinds.CentralPath, central.ToUpperInvariant()).ModelId,
            KeyKind = ActivityMatchKinds.CentralPath,
            Keys = new RollupKeys { CentralPath = central, CreationGuid = Guid.NewGuid().ToString() },
            Title = name,
            LocalPath = $@"C:\Users\someone\Documents\{name}_someone.rvt",
            CentralPath = central,
            IsWorkshared = true,
            IsCloud = false,
            Health = new HealthInputs(300_000_000L + rng.Next(), rng.Next(0, 3000), rng.Next(5, 400), rng.Next(5, 400)),
            LastStats = new ModelStatsPoint(Now.AddDays(-rng.Next(0, 30)), rng.Next(0, 3000), 300_000_000L + rng.Next()),
        };
        row.KeysSeen.CentralPaths.Add(central);
        row.KeysSeen.CreationGuids.Add(row.Keys.CreationGuid!);
        for (var k = 0; k < KeysSeen.MaxPathsAndTitles; k++)
        {
            row.KeysSeen.LocalPaths.Add($@"C:\Users\someone\Documents\Revit Local Files\{name}_someone_{k}.rvt");
            row.KeysSeen.Titles.Add($"{name} rev {k}");
        }

        // Spread last-active over two years so every chip has a different visible set.
        var lastDaysAgo = rng.Next(0, 730);
        var last = Today.AddDays(-lastDaysAgo);
        var first = last.AddDays(-rng.Next(0, 900));
        row.FirstActiveDay = first;
        row.LastActiveDay = last;
        var majors = new[] { "2025", "2026", "2027" };
        foreach (var (m, k) in majors.Select((m, k) => (m, k)))
        {
            row.RevitVersions[m] = last.AddDays(-k * rng.Next(0, 60));
            row.RevitSubversions[m + "." + rng.Next(0, 5)] = last.AddDays(-k * rng.Next(0, 60));
        }
        ActivityTotals T(DateOnly d) => new()
        {
            Date = d,
            ActiveMs = rng.Next(0, 6 * 3_600_000),
            OpenMs = rng.Next(1, 9 * 3_600_000),
            Opens = rng.Next(0, 5),
            OpenSumMs = rng.Next(0, 600_000),
            Syncs = rng.Next(0, 20),
            SyncSumMs = rng.Next(0, 3_000_000),
        };
        for (var ago = ModelIndexRow.RecentDays - 1; ago >= 0; ago--)
            if (Today.AddDays(-ago) <= last) row.RecentDaysTotals.Add(T(Today.AddDays(-ago)));
        var monday = ActivityWeeks.WeekStart(Today);
        for (var w = ModelIndexRow.RecentWeeks - 1; w >= 0; w--)
            if (monday.AddDays(-7 * w) <= last) row.RecentWeeksTotals.Add(T(monday.AddDays(-7 * w)));
        row.Totals = new ActivityTotals
        {
            ActiveMs = rng.Next(), OpenMs = rng.Next(), Opens = rng.Next(0, 5000), OpenSumMs = rng.Next(),
            Syncs = rng.Next(0, 20000), SyncSumMs = rng.Next(),
        };
        return row;
    }

    private static long WriteIndex(string rollupsDir, int models)
    {
        var rng = new Random(500);
        var index = new RollupIndexFile();
        for (var i = 0; i < models; i++) index.Models.Add(SyntheticRow(i, rng));
        new RollupStore(rollupsDir).SaveIndex(index, Now);
        return new FileInfo(Path.Combine(rollupsDir, "index.json")).Length;
    }

    private ActivityReadOptions Options(string rollupsDir) => new()
    {
        OutboxDir = _f.Outbox,
        RollupsDir = rollupsDir,
        Zone = TimeZoneInfo.Utc,
        NowUtc = Now,
    };

    [Fact]
    public void With_500_models_index_load_and_filter_stay_within_the_CI_guard_and_a_chip_change_takes_at_most_100_ms()
    {
        // Warm-up on a small, separate index (JIT for the reader, the parse and the query).
        var warmDir = Path.Combine(_f.Root, "warm");
        WriteIndex(warmDir, 20);
        ModelListIndex.Load(Options(warmDir)).Query(ModelListQuery.Default, Today);

        var bytes = WriteIndex(_f.Rollups, 500);
        _out.WriteLine($"index.json: {bytes:N0} bytes for 500 models");

        // AC 9 (spec #25): the best of three fresh loads, each from the file into a new instance
        // after the small-index warm-up, against the CI guard; all three are logged.
        var sw = new Stopwatch();
        var loads = new List<double>();
        ModelListIndex index = ModelListIndex.Empty;
        ModelListResult first = null!;
        for (var i = 0; i < 3; i++)
        {
            sw.Restart();
            index = ModelListIndex.Load(Options(_f.Rollups));
            first = index.Query(ModelListQuery.Default, Today);
            sw.Stop();
            loads.Add(sw.Elapsed.TotalMilliseconds);
        }
        _out.WriteLine($"load + filter, three fresh loads: {string.Join(", ", loads.Select(l => l.ToString("F1") + " ms"))}; best {loads.Min():F1} ms ({first.Cards.Count} cards on Recent)");
        index.Count.Should().Be(500);
        first.Cards.Should().NotBeEmpty();
        loads.Min().Should().BeLessThanOrEqualTo(CiLoadGuardMs);

        var queries = new List<ModelListQuery>();
        foreach (var chip in Enum.GetValues<ModelTimeChip>())
            foreach (var by in Enum.GetValues<ModelGroupBy>())
            {
                queries.Add(new ModelListQuery { Chip = chip, GroupBy = by });
                queries.Add(new ModelListQuery { Chip = chip, GroupBy = by, Versions = new[] { "2026", "2027" } });
            }
        queries.Add(new ModelListQuery { Search = "building c" });
        queries.Add(new ModelListQuery { Search = "rev 7" });

        var slowest = 0.0;
        foreach (var q in queries)
        {
            sw.Restart();
            var r = index.Query(q, Today);
            sw.Stop();
            slowest = Math.Max(slowest, sw.Elapsed.TotalMilliseconds);
            r.IndexedModels.Should().Be(500);
        }
        _out.WriteLine($"slowest of {queries.Count} chip / filter / group / search changes: {slowest:F2} ms");
        index.Query(new ModelListQuery { Chip = ModelTimeChip.OneYear }, Today).Cards.Count
            .Should().BeGreaterThan(first.Cards.Count, "the chips select different sets");
        slowest.Should().BeLessThanOrEqualTo(ChipChangeMs);
    }

    [Fact]
    public void An_index_the_fold_wrote_lists_with_the_detail_s_active_time()
    {
        var doc = SessionBuilder.Central("Tower");
        var other = SessionBuilder.Local("Annex");
        DateTimeOffset D(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0, TimeSpan.Zero);
        new SessionBuilder().Start(D(29, 8, 55)).Open(D(29, 9), doc, 15)
            .Pulses(D(29, 10), D(29, 10, 30), doc).Sync(D(29, 12), doc, 45)
            .Close(D(29, 17), doc).End(D(29, 17, 1)).Write(_f.Outbox, D(29, 18));
        new SessionBuilder().Start(D(30, 8, 55), "2027", "2027.1").Open(D(30, 9), other, 20)
            .Pulses(D(30, 10), D(30, 10, 12), other)
            .Close(D(30, 11), other).End(D(30, 11, 1)).Write(_f.Outbox, D(30, 12));
        _f.PassUntilQuiet();

        var r = ModelListIndex.Load(_f.ReadOptions()).Query(ModelListQuery.Default, Now, TimeZoneInfo.Utc);

        r.Groups.Select(g => g.Key).Should().Equal("2027", "2026");
        var tower = r.Cards.Single(c => c.Title == "Tower");
        var detail = ModelActivityReader.ReadModel(tower.ModelId, _f.ReadOptions());
        var (first, last) = ModelList.Window(ModelTimeChip.Recent, Today);
        tower.ActiveSeconds.Should().Be(detail.ActiveSecondsIn(first, last)).And.BePositive();
        tower.LatestVersion.Should().Be("2026.4");
        tower.Marker.Should().Be(ModelMarker.Workshared);
        tower.ShowFileName.Should().BeTrue();
        tower.Totals.Opens.Should().Be(1);
        tower.Totals.Syncs.Should().Be(1);
        r.Aggregates.Syncs.Should().Be(1);
        r.Aggregates.AverageSyncSeconds.Should().Be(45);
        r.Cards.Single(c => c.Title == "Annex").Marker.Should().Be(ModelMarker.None);
    }

    [Fact]
    public void Index_rows_with_missing_or_reversed_activity_dates_are_skipped_and_logged()
    {
        var rng = new Random(7);
        ModelIndexRow Row(int i, string title)
        {
            var r = SyntheticRow(i, rng);
            r.Title = title;
            r.KeysSeen.Titles.Clear();
            r.Totals.ActiveMs = 9_000_000_000_000_000; // implausible, unbounded without dates
            return r;
        }
        var good = Row(1, "Good");
        good.FirstActiveDay = Today.AddDays(-1);
        good.LastActiveDay = Today;
        var noFirst = Row(2, "No first day");
        noFirst.FirstActiveDay = null;
        var noLast = Row(3, "No last day");
        noLast.LastActiveDay = null;
        var noDates = Row(4, "No dates");
        noDates.FirstActiveDay = noDates.LastActiveDay = null;
        var reversed = Row(5, "Reversed");
        reversed.FirstActiveDay = Today;
        reversed.LastActiveDay = Today.AddDays(-30);
        var idle = new ModelIndexRow // stats only: never open, so no active days and no totals
        {
            ModelId = new ModelKey(ActivityMatchKinds.Creation, "IDLE").ModelId,
            KeyKind = ActivityMatchKinds.Creation,
            Title = "Idle",
        };
        var index = new RollupIndexFile();
        index.Models.AddRange(new[] { good, noFirst, noLast, noDates, reversed, idle });
        new RollupStore(_f.Rollups).SaveIndex(index, Now);
        var logs = new List<string>();

        var list = ModelListIndex.Load(new ActivityReadOptions
        {
            OutboxDir = _f.Outbox, RollupsDir = _f.Rollups, Zone = TimeZoneInfo.Utc, NowUtc = Now, Log = logs.Add,
        });

        list.Count.Should().Be(2, "the good row and the idle row stay");
        // Every synthetic row's paths contain "Project"; only the good one is left to find.
        list.Query(new ModelListQuery { Search = "project" }, Today).Cards.Select(c => c.Title).Should().Equal("Good");
        var goodCard = list.Query(new ModelListQuery { Search = "good" }, Today).Cards.Single();
        goodCard.ActiveSeconds.Should().Be(2 * 24 * 3600, "capped at 24 h for each of its 2 active days");
        list.Query(new ModelListQuery { Search = "idle" }, Today).Cards.Single().ActiveSeconds.Should().Be(0);
        logs.Where(l => l.StartsWith("models list: skipped index row", StringComparison.Ordinal)).Should().HaveCount(4);
        logs.Should().Contain(l => l.Contains(noFirst.ModelId) && l.Contains("without a first or last active day"));
        logs.Should().Contain(l => l.Contains(noLast.ModelId) && l.Contains("without a first or last active day"));
        logs.Should().Contain(l => l.Contains(noDates.ModelId) && l.Contains("without a first or last active day"));
        logs.Should().Contain(l => l.Contains(reversed.ModelId) && l.Contains("last active day before first active day"));
        ModelListIndex.Load(Options(_f.Rollups)).Count.Should().Be(2, "logging is optional");
    }

    [Fact]
    public void A_missing_or_unreadable_index_is_an_empty_list_not_a_failure()
    {
        ModelListIndex.Load(Options(Path.Combine(_f.Root, "nowhere"))).Query(ModelListQuery.Default, Today)
            .EmptyState.Should().Be(ModelListEmptyState.NoModels);

        Directory.CreateDirectory(_f.Rollups);
        File.WriteAllText(Path.Combine(_f.Rollups, "index.json"), "{ not json");
        ModelListIndex.Load(Options(_f.Rollups)).Count.Should().Be(0);
    }
}
