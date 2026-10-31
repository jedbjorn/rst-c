// ModelActivityReaderTests.cs — the shared read path (spec #24, Revit Health
// "Data path"; spec #25 detail): rollup + unfolded closed files + the live
// session, folded in memory with the fold's code, writing nothing. The same
// numbers whether the tray has folded or not, no double counting of a
// changed or pending file, Activity (Session), the matching ladder over index
// rows, and range slicing.

using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using RST.Core.Health;
using RST.Core.Telemetry;
using RST.Core.Telemetry.Rollups;
using Xunit;

namespace RST.Tests.Telemetry.Rollups;

public sealed class ModelActivityReaderTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    private readonly RollupStoreFixture _f = new(Now);

    public void Dispose() => _f.Dispose();

    private static DateTimeOffset D(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0, TimeSpan.Zero);

    private static SessionBuilder Workday(DocumentIdentity doc, int day, int editMinutes = 30) =>
        new SessionBuilder().Start(D(day, 8, 55)).Open(D(day, 9), doc, 15)
            .Pulses(D(day, 10), D(day, 10).AddMinutes(editMinutes), doc)
            .Sync(D(day, 12), doc, 45)
            .Close(D(day, 17), doc).End(D(day, 17, 1));

    private ModelActivity Read(DocumentIdentity doc, string? live = null) =>
        ModelActivityReader.ReadCurrentModel(doc, _f.ReadOptions(live));

    private static (DateOnly, long, long, int, int)[] Shape(ModelActivity a) =>
        a.Days.Select(d => (d.Date, d.ActiveMs, d.OpenMs, d.Opens, d.Syncs)).ToArray();

    [Fact]
    public void Folded_unfolded_and_partly_folded_stores_read_the_same_numbers()
    {
        var doc = SessionBuilder.Central("Tower");
        Workday(doc, 27).Write(_f.Outbox, D(27, 18));
        Workday(doc, 28, 20).Write(_f.Outbox, D(28, 18));

        var neverFolded = Read(doc);
        _f.Pass();
        var folded = Read(doc);
        Workday(doc, 29, 10).Write(_f.Outbox, D(29, 18)); // not folded yet
        var partly = Read(doc);
        _f.PassUntilQuiet();
        var allFolded = Read(doc);

        Shape(neverFolded).Should().Equal(Shape(folded));
        neverFolded.Days.Sum(d => d.ActiveMs).Should().Be(50 * 60_000);
        Shape(partly).Should().Equal(Shape(allFolded));
        allFolded.Days.Sum(d => d.ActiveMs).Should().Be(60 * 60_000);
        allFolded.Opens.Should().HaveCount(3);
        allFolded.Syncs.Should().HaveCount(3);
        allFolded.MatchedKeyKind.Should().Be(ActivityMatchKinds.CentralPath);
        allFolded.ModelIds.Should().Equal(ModelKey.From(doc)!.Value.ModelId);
    }

    [Fact]
    public void Reading_writes_nothing()
    {
        var doc = SessionBuilder.Local("A");
        Workday(doc, 27).Write(_f.Outbox, D(27, 18));
        _f.Pass();
        Workday(doc, 28).Write(_f.Outbox, D(28, 18));
        File.WriteAllText(_f.ModelPath(doc), "corrupt"); // the reader falls back, never repairs
        var before = _f.Snapshot(_f.Root);

        var a = Read(doc);

        a.Days.Sum(d => d.ActiveMs).Should().Be(60 * 60_000, "the backup plus both raw files");
        var after = _f.Snapshot(_f.Root);
        after.Keys.Should().BeEquivalentTo(before.Keys);
        foreach (var k in after.Keys) after[k].Should().Equal(before[k], k);
    }

    [Fact]
    public void A_file_changed_after_folding_replaces_its_pending_share_rather_than_adding()
    {
        var doc = SessionBuilder.Local("A");
        var s = Workday(doc, 29, 10);
        s.Write(_f.Outbox, D(29, 18));
        _f.Pass();
        // The same session's file, rewritten with more edits.
        var grown = new SessionBuilder(s.SessionGuid).Start(D(29, 8, 55)).Open(D(29, 9), doc, 15)
            .Pulses(D(29, 10), D(29, 10, 25), doc).Sync(D(29, 12), doc, 45).Close(D(29, 17), doc).End(D(29, 17, 1));
        grown.Write(_f.Outbox, D(29, 19));

        var a = Read(doc);

        a.Days.Single().ActiveMs.Should().Be(25 * 60_000);
        a.Days.Single().Opens.Should().Be(1);
        a.Opens.Should().ContainSingle();
    }

    [Fact]
    public void The_live_session_extends_to_now_and_gives_activity_session()
    {
        var doc = SessionBuilder.Central("Tower");
        Workday(doc, 30).Write(_f.Outbox, D(30, 18));
        _f.Pass();
        var live = new SessionBuilder().Start(D(31, 8)).Open(D(31, 9), doc, 30)
            .Pulses(D(31, 10), D(31, 10, 5), doc)   // 5 min
            .Pulses(D(31, 10, 20), D(31, 10, 26), doc) // 6 min after a 15-min idle
            .Heartbeat(D(31, 11, 58));
        live.Write(_f.Outbox);

        var a = Read(doc, live.SessionGuid);

        a.SessionActiveSeconds.Should().Be(11 * 60);
        a.LiveOpenSinceUtc.Should().Be(D(31, 9, 0).AddSeconds(30));
        var today = a.Days.Single(d => d.Date == new DateOnly(2026, 10, 31));
        today.OpenMs.Should().Be((long)(Now - D(31, 9).AddSeconds(30)).TotalMilliseconds, "open to now");
        a.ActiveSecondsIn(new DateOnly(2026, 10, 25), new DateOnly(2026, 10, 31)).Should().Be(41 * 60);

        // Without the live guid (the tray), the unclosed file is capped at its last event.
        var tray = ModelActivityReader.ReadModel(ModelKey.From(doc)!.Value.ModelId, _f.ReadOptions());
        tray.SessionActiveSeconds.Should().BeNull();
        tray.Days.Single(d => d.Date.Day == 31).OpenMs.Should().Be((long)(D(31, 11, 58) - D(31, 9).AddSeconds(30)).TotalMilliseconds);
        tray.ActiveSecondsIn(new DateOnly(2026, 10, 25), new DateOnly(2026, 10, 31)).Should().Be(41 * 60);
    }

    [Fact]
    public void A_model_not_open_in_the_live_session_has_no_session_value()
    {
        var doc = SessionBuilder.Local("A");
        var other = SessionBuilder.Local("B");
        var live = new SessionBuilder().Start(D(31, 8)).Open(D(31, 9), other).Pulse(D(31, 10), other);
        live.Write(_f.Outbox);
        Read(doc, live.SessionGuid).SessionActiveSeconds.Should().BeNull();
        Read(other, live.SessionGuid).SessionActiveSeconds.Should().Be(60);
    }

    [Fact]
    public void No_model_open_or_no_usable_key_reads_empty()
    {
        ModelActivityReader.ReadCurrentModel(null, _f.ReadOptions()).Should().BeSameAs(ModelActivity.Empty);
        ModelActivityReader.ReadCurrentModel(new DocumentIdentity { Title = "Untitled" }, _f.ReadOptions())
            .HasData.Should().BeFalse();
    }

    [Fact]
    public void A_gapped_current_identity_still_finds_the_models_rollup_by_the_ladder()
    {
        var doc = SessionBuilder.Central("Tower");
        Workday(doc, 30).Write(_f.Outbox, D(30, 18));
        _f.PassUntilQuiet();
        // This time Revit could not read the central path: only the creation GUID is known.
        var gapped = new DocumentIdentity { CreationGuid = doc.CreationGuid };
        var sibling = new DocumentIdentity { CreationGuid = doc.CreationGuid, CentralPath = @"\\server\other\Tower2.rvt" };

        Read(gapped).Days.Sum(d => d.ActiveMs).Should().Be(30 * 60_000);
        Read(sibling).Days.Should().BeEmpty("another central of the same lineage is another model");
    }

    [Fact]
    public void Range_helpers_slice_days_weeks_events_longest_and_the_health_trend()
    {
        var doc = SessionBuilder.Central("Tower");
        Workday(doc, 19).Stats(D(19, 9, 1), doc, 10, 100L * 1024 * 1024).Write(_f.Outbox, D(19, 18));
        Workday(doc, 27).Write(_f.Outbox, D(27, 18));
        Workday(doc, 29).Write(_f.Outbox, D(29, 18));
        _f.Pass();
        var a = Read(doc);
        var (first, last) = ModelActivity.Range(7, new DateOnly(2026, 10, 31));

        first.Should().Be(new DateOnly(2026, 10, 25));
        a.PerDay(first, last).Should().HaveCount(7);
        a.PerDay(first, last).Count(d => d.ActiveMs > 0).Should().Be(2);
        a.OpensIn(first, last).Should().HaveCount(2);
        a.SyncsIn(first, last).Should().HaveCount(2);
        a.LongestSyncSecondsIn(first, last).Should().Be(45);
        a.LongestOpenSecondsIn(first, last).Should().Be(15);
        a.OldestDay.Should().Be(new DateOnly(2026, 10, 19));
        a.LastStats!.WarningsCount.Should().Be(10);

        var weeks = a.WeeksIn(new DateOnly(2026, 10, 1), last);
        weeks.Select(w => w.WeekStart.Day).Should().Equal(19, 26);
        weeks[1].ActiveSeconds.Should().Be(60 * 60);

        var daily = a.HealthTrend(new DateOnly(2026, 10, 1), last, weekly: false);
        daily.Select(p => p.Date.Day).Should().Equal(19, 27, 29);
        daily.Should().OnlyContain(p => p.Health.Colour == ModelHealthColour.Green);
        a.HealthTrend(new DateOnly(2026, 10, 1), last, weekly: true).Select(p => p.Date.Day).Should().Equal(19, 29);
        a.CurrentHealth.Should().Be(new HealthInputs(100L * 1024 * 1024, 10, 15, 45));
    }

    [Fact]
    public void The_index_reads_alone_and_rebuilds_from_model_files_when_unreadable()
    {
        Workday(SessionBuilder.Local("A"), 30).Write(_f.Outbox, D(30, 18));
        Workday(SessionBuilder.Local("B"), 30).Write(_f.Outbox, D(30, 19));
        _f.Pass();
        _f.Pass(); // a second write gives index.json a backup
        _f.Index().Should().HaveCount(2);

        File.WriteAllText(Path.Combine(_f.Rollups, "index.json"), "nope");
        _f.Index().Should().HaveCount(2, "from index.bak.json");
        File.WriteAllText(Path.Combine(_f.Rollups, "index.bak.json"), "nope");
        _f.Index().Select(r => r.Title).Should().BeEquivalentTo("A", "B");
    }
}
