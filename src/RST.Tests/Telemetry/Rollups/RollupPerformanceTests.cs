// RollupPerformanceTests.cs — spec #24 AC 5: stays fast over years. Inferred
// from synthetic benchmarks (CI timing is a guard, not a lab measurement):
// a 10-year daily rollup is ≤ 1 MB, the Health activity read of it is
// ≤ 250 ms, and folding a 10-hour heavy session (a pulse every minute,
// 30 syncs) into it is ≤ 1 s.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using FluentAssertions;
using RST.Core.Health;
using RST.Core.Telemetry;
using RST.Core.Telemetry.Rollups;
using Xunit;
using Xunit.Abstractions;

namespace RST.Tests.Telemetry.Rollups;

public sealed class RollupPerformanceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    private readonly RollupStoreFixture _f = new(Now);
    private readonly ITestOutputHelper _out;
    private readonly DocumentIdentity _doc = SessionBuilder.Central("Hospital");

    public RollupPerformanceTests(ITestOutputHelper output) => _out = output;

    public void Dispose() => _f.Dispose();

    /// <summary>Ten years of daily use, every column filled with realistic magnitudes, and a
    /// full 90-day event window (3 opens and 12 syncs a day).</summary>
    private long WriteTenYearRollup()
    {
        var key = ModelKey.From(_doc)!.Value;
        var r = RollupEditor.Create(key, _doc);
        r.Identity = new RollupIdentity { Title = "Hospital", LocalPath = _doc.LocalPath, CentralPath = _doc.CentralPath, IsWorkshared = true, IsCloud = false, AsOf = Now };
        r.KeysSeen.Add(_doc);
        var today = DateOnly.FromDateTime(Now.UtcDateTime);
        var rng = new Random(42);
        for (var i = 3649; i >= 0; i--)
        {
            var date = today.AddDays(-i);
            r.Days.Add(new RollupDay
            {
                Date = date,
                ActiveMs = 14_400_000 + rng.Next(0, 9_999_999),
                OpenMs = 28_800_000 + rng.Next(0, 9_999_999),
                Opens = 3,
                OpenSumMs = 180_000 + rng.Next(0, 99_999),
                OpenMaxMs = 90_000 + rng.Next(0, 99_999),
                Syncs = 12,
                SyncSumMs = 1_200_000 + rng.Next(0, 999_999),
                SyncMaxMs = 200_000 + rng.Next(0, 99_999),
                RevitVersions = { "2026" },
                RevitSubversions = { "2026.4" },
                Stats = new ModelStatsPoint(new DateTimeOffset(date.ToDateTime(new TimeOnly(9, 1, 7, 123)), TimeSpan.Zero), 1234 + rng.Next(0, 999), 812_345_678 + rng.Next()),
                Health = new HealthInputs(812_345_678, 1234, 61.234, 101.5),
            });
            if (i < 90)
            {
                var day0 = new DateTimeOffset(date.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero);
                for (var k = 0; k < 3; k++) r.Opens.Add(new DurationPoint(day0.AddHours(k * 3), 60 + rng.Next(0, 60000) / 1000.0));
                for (var k = 0; k < 12; k++) r.Syncs.Add(new DurationPoint(day0.AddMinutes(30 + k * 40), 100 + rng.Next(0, 60000) / 1000.0));
            }
        }
        var store = new RollupStore(_f.Rollups);
        var (bytes, ts) = store.SaveModel(r);
        var index = new RollupIndexFile();
        index.Models.Add(RollupEditor.BuildIndexRow(r, today, bytes, ts));
        store.SaveIndex(index, Now);
        return bytes;
    }

    [Fact]
    public void A_ten_year_daily_rollup_is_at_most_1_MB()
    {
        var bytes = WriteTenYearRollup();
        _out.WriteLine($"10-year rollup: {bytes:N0} bytes");
        bytes.Should().BeLessThanOrEqualTo(1_000_000);
        _f.Index().Single().Totals.Opens.Should().Be(3650 * 3);
        new FileInfo(Path.Combine(_f.Rollups, "index.json")).Length.Should().BeLessThan(16_000);
    }

    [Fact]
    public void The_Health_activity_read_of_a_ten_year_rollup_takes_at_most_250_ms()
    {
        WriteTenYearRollup();
        var options = _f.ReadOptions();
        ModelActivityReader.ReadCurrentModel(_doc, options); // warm-up (JIT)

        var sw = Stopwatch.StartNew();
        var a = ModelActivityReader.ReadCurrentModel(_doc, options);
        var (first, last) = ModelActivity.Range(180, DateOnly.FromDateTime(Now.UtcDateTime));
        var weeks = a.WeeksIn(first, last);
        var trend = a.HealthTrend(first, last, weekly: true);
        sw.Stop();

        _out.WriteLine($"read: {sw.ElapsedMilliseconds} ms");
        a.Days.Should().HaveCount(3650);
        weeks.Should().NotBeEmpty();
        trend.Should().NotBeEmpty();
        sw.ElapsedMilliseconds.Should().BeLessThanOrEqualTo(250);
    }

    [Fact]
    public void Folding_a_ten_hour_heavy_session_into_a_ten_year_rollup_takes_at_most_1_s()
    {
        WriteTenYearRollup();
        // Warm up the fold path on a throwaway store.
        using (var warm = new RollupStoreFixture(Now))
        {
            Heavy(SessionBuilder.Local("Warm"), 30).Write(warm.Outbox);
            warm.Pass();
        }

        Heavy(_doc, 31).Write(_f.Outbox);
        var sw = Stopwatch.StartNew();
        var result = _f.Engine.TryBeginPass()!;
        var folded = result.FoldNext();
        result.Dispose();
        sw.Stop();

        _out.WriteLine($"fold: {sw.ElapsedMilliseconds} ms");
        folded.Should().BeTrue();
        result.Folded.Should().Be(1);
        var r = _f.Rollup(_doc)!;
        r.Days.Should().HaveCount(3650, "today's row exists already; the session adds to it");
        r.Pending.Values.Single().Days.Single().ActiveMs.Should().BeGreaterThan(9 * 3_600_000);
        sw.ElapsedMilliseconds.Should().BeLessThanOrEqualTo(1000);
    }

    /// <summary>10 hours open with a pulse every minute and 30 syncs.</summary>
    private static SessionBuilder Heavy(DocumentIdentity doc, int day)
    {
        var start = new DateTimeOffset(2026, 10, day, 1, 0, 0, TimeSpan.Zero);
        var s = new SessionBuilder().Start(start).Open(start.AddMinutes(1), doc, 45);
        for (var m = 2; m < 600; m++)
        {
            s.Pulse(start.AddMinutes(m), doc);
            if ((m - 2) % 20 == 0) s.Sync(start.AddMinutes(m).AddSeconds(5), doc, 50);
        }
        return s.Close(start.AddMinutes(601), doc).End(start.AddMinutes(602));
    }
}
