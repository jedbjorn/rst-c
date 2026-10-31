// RollupReviewRegressionTests.cs — regression tests for the PR #177 review
// (round 1), one per finding, all through the public fold and read APIs:
// R1/R2 cleanup never deletes unfolded data (a changed sealed file, a file
// changed during a pass), R3–R5 the shared read path (backup fingerprints,
// learned keys, Revit = tray), R6/R7 recovery (structural rollup corruption,
// a bad index row), R8 capped index totals, R9 active time clipped at
// doc_closing, R10 RunPass never throws, R11 grey days survive persistence,
// R12 event windows age while idle.

using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using FluentAssertions;
using RST.Core.Health;
using RST.Core.Telemetry;
using RST.Core.Telemetry.Rollups;
using Xunit;

namespace RST.Tests.Telemetry.Rollups;

public sealed class RollupReviewRegressionTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    private readonly RollupStoreFixture _f = new(Now);

    public void Dispose() => _f.Dispose();

    private static DateTimeOffset D(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0, TimeSpan.Zero);

    /// <summary>A session on <paramref name="day"/> with <paramref name="editMinutes"/> of edits from 10:00.</summary>
    private static SessionBuilder Edits(DocumentIdentity doc, int day, int editMinutes, string? guid = null) =>
        new SessionBuilder(guid).Start(D(day, 8)).Open(D(day, 9), doc, 0)
            .Pulses(D(day, 10), D(day, 10).AddMinutes(editMinutes), doc)
            .Close(D(day, 12), doc).End(D(day, 12, 1));

    private ActivityReadOptions Read => _f.ReadOptions();

    private static long ActiveMs(ModelActivity a) => a.Days.Sum(d => d.ActiveMs);

    // ---- R1 ---------------------------------------------------------------

    [Fact]
    public void R1_a_file_that_changes_after_it_was_sealed_replaces_its_share_and_is_never_deleted_with_the_old_one()
    {
        var doc = SessionBuilder.Local("A");
        var first = Edits(doc, 20, 10);
        var path = first.Write(_f.Outbox, D(20, 13));
        _f.Pass();
        _f.Pass(); // sealed
        _f.Rollup(doc)!.Days.Sum(d => d.ActiveMs).Should().Be(10 * 60_000);

        // The same session's file now holds 20 minutes.
        Edits(doc, 20, 20, first.SessionGuid).Write(_f.Outbox, D(20, 14));
        // Newer files so the changed one is outside the newest-3 buffer.
        foreach (var day in new[] { 25, 26, 27 }) Edits(doc, day, 1).Write(_f.Outbox, D(day, 13));
        _f.Pass().Folded.Should().Be(4);
        _f.Rollup(doc)!.Days.Single(d => d.Date.Day == 20).ActiveMs.Should().Be(20 * 60_000);

        _f.PassUntilQuiet();
        File.Exists(path).Should().BeFalse("it is deleted only once the new share is sealed in both copies");
        _f.Rollup(doc)!.Days.Single(d => d.Date.Day == 20).ActiveMs.Should().Be(20 * 60_000);
        ActiveMs(ModelActivityReader.ReadCurrentModel(doc, Read)).Should().Be(23 * 60_000);
    }

    // ---- R2 ---------------------------------------------------------------

    [Fact]
    public void R2_a_file_that_changes_during_the_pass_is_not_deleted()
    {
        var doc = SessionBuilder.Local("A");
        var first = Edits(doc, 20, 10);
        var path = first.Write(_f.Outbox, D(20, 13));
        foreach (var day in new[] { 25, 26, 27 }) Edits(doc, day, 1).Write(_f.Outbox, D(day, 13));
        _f.Pass();
        _f.Pass();

        // Right before cleanup takes it, the file grows to 20 minutes.
        var changed = false;
        _f.FaultHook = stage =>
        {
            if (stage != "delete:" + Path.GetFileName(path) || changed) return;
            changed = true;
            Edits(doc, 20, 20, first.SessionGuid).Write(_f.Outbox, D(20, 14));
        };
        _f.Pass().Deleted.Should().Be(0);
        changed.Should().BeTrue();
        File.Exists(path).Should().BeTrue();

        _f.FaultHook = null;
        _f.PassUntilQuiet();
        _f.Rollup(doc)!.Days.Single(d => d.Date.Day == 20).ActiveMs.Should().Be(20 * 60_000);
    }

    // ---- R3 ---------------------------------------------------------------

    [Fact]
    public void R3_a_backup_holding_an_older_version_of_a_file_is_not_trusted_over_the_raw_file()
    {
        var doc = SessionBuilder.Local("A");
        var first = Edits(doc, 29, 10);
        first.Write(_f.Outbox, D(29, 13));
        _f.Pass();
        Edits(doc, 29, 20, first.SessionGuid).Write(_f.Outbox, D(29, 14));
        _f.Pass(); // the backup now holds the 10-minute share
        File.WriteAllText(_f.ModelPath(doc), "{ corrupt");

        ActiveMs(ModelActivityReader.ReadCurrentModel(doc, Read)).Should().Be(20 * 60_000);
    }

    // ---- R4 ---------------------------------------------------------------

    [Fact]
    public void R4_a_key_learned_after_the_model_was_keyed_still_finds_its_rollup()
    {
        var withCreation = SessionBuilder.Central("Tower");
        var withoutCreation = new DocumentIdentity
        {
            CentralPath = withCreation.CentralPath, LocalPath = withCreation.LocalPath, Title = "Tower", IsWorkshared = true, IsCloud = false,
        };
        Edits(withoutCreation, 28, 10).Write(_f.Outbox, D(28, 13));
        Edits(withCreation, 29, 5).Write(_f.Outbox, D(29, 13));
        _f.Pass();
        _f.Index().Should().ContainSingle();

        var onlyCreation = new DocumentIdentity { CreationGuid = withCreation.CreationGuid };
        ActiveMs(ModelActivityReader.ReadCurrentModel(onlyCreation, Read)).Should().Be(15 * 60_000);
        var sibling = new DocumentIdentity { CreationGuid = withCreation.CreationGuid, CentralPath = @"\\server\other.rvt" };
        ModelActivityReader.ReadCurrentModel(sibling, Read).Days.Should().BeEmpty();
    }

    // ---- R5 ---------------------------------------------------------------

    [Fact]
    public void R5_the_tray_detail_and_Revit_read_the_same_totals()
    {
        var doc = SessionBuilder.Central("Tower");
        var gapped = new DocumentIdentity { CreationGuid = doc.CreationGuid, LocalPath = doc.LocalPath, Title = "Tower" };
        Edits(doc, 28, 10).Write(_f.Outbox, D(28, 13));
        Edits(gapped, 29, 10).Write(_f.Outbox, D(29, 13));
        _f.Pass();
        var rows = _f.Index();
        rows.Should().HaveCount(2, "the gapped session keyed its own rollup");

        var revit = ModelActivityReader.ReadCurrentModel(doc, Read);
        ActiveMs(revit).Should().Be(20 * 60_000);
        foreach (var row in rows)
        {
            var tray = ModelActivityReader.ReadModel(row.ModelId, Read);
            ActiveMs(tray).Should().Be(ActiveMs(revit), row.KeyKind);
            tray.ModelIds.Should().Equal(revit.ModelIds);
        }
    }

    // ---- R6 ---------------------------------------------------------------

    [Fact]
    public void R6_a_structurally_broken_rollup_is_recovered_from_its_backup()
    {
        var doc = SessionBuilder.Local("A");
        Edits(doc, 28, 10).Write(_f.Outbox, D(28, 13));
        Edits(doc, 29, 10).Write(_f.Outbox, D(29, 13));
        _f.Pass();
        var json = JsonNode.Parse(File.ReadAllText(_f.ModelPath(doc)))!;
        json["days"] = null;
        File.WriteAllText(_f.ModelPath(doc), json.ToJsonString());

        ActiveMs(ModelActivityReader.ReadCurrentModel(doc, Read)).Should().Be(20 * 60_000);

        _f.Pass().NothingChanged.Should().BeFalse();
        File.Exists(Path.ChangeExtension(_f.ModelPath(doc), null) + ".corrupt.json").Should().BeTrue();
        _f.Rollup(doc)!.Days.Sum(d => d.ActiveMs).Should().Be(20 * 60_000);
    }

    // ---- R7 ---------------------------------------------------------------

    [Fact]
    public void R7_a_bad_index_row_is_skipped_and_the_rest_still_read()
    {
        Edits(SessionBuilder.Local("A"), 28, 10).Write(_f.Outbox, D(28, 13));
        Edits(SessionBuilder.Local("B"), 29, 10).Write(_f.Outbox, D(29, 13));
        _f.Pass();
        var indexPath = Path.Combine(_f.Rollups, "index.json");
        var json = JsonNode.Parse(File.ReadAllText(indexPath))!;
        json["models"]!.AsArray().Add(null);
        json["models"]!.AsArray().Add(new JsonObject { ["model_id"] = "../../escape" });
        File.WriteAllText(indexPath, json.ToJsonString());

        _f.Index().Select(r => r.Title).Should().BeEquivalentTo("A", "B");
        _f.Pass().Skipped.Should().BeFalse();
        _f.Index().Should().HaveCount(2);
    }

    // ---- R8 ---------------------------------------------------------------

    [Fact]
    public void R8_index_totals_are_capped_like_the_reader()
    {
        var doc = SessionBuilder.Local("A");
        foreach (var _ in Enumerable.Range(0, 2))
            new SessionBuilder().Start(D(29, 5)).Open(D(29, 6), doc, 0)
                .Pulses(D(29, 6), D(29, 22), doc).Close(D(29, 22), doc).End(D(29, 22, 1))
                .Write(_f.Outbox, D(29, 23));
        _f.Pass();

        var reader = ModelActivityReader.ReadCurrentModel(doc, Read);
        ActiveMs(reader).Should().Be(RollupDay.MaxDayMs);
        var row = _f.Index().Single();
        row.Totals.ActiveMs.Should().Be(ActiveMs(reader));
        row.Totals.OpenMs.Should().Be(reader.Days.Sum(d => d.OpenMs));
        row.RecentDaysTotals.Single().ActiveMs.Should().Be(RollupDay.MaxDayMs);

        _f.PassUntilQuiet(); // sealing must not move the totals
        _f.Index().Single().Totals.ActiveMs.Should().Be(RollupDay.MaxDayMs);
    }

    // ---- R9 ---------------------------------------------------------------

    [Fact]
    public void R9_active_time_stops_at_doc_closing_not_doc_closed()
    {
        var doc = SessionBuilder.Local("A");
        new SessionBuilder().Start(D(29, 8)).Open(D(29, 9), doc, 0)
            .Pulses(D(29, 9, 50), D(29, 10), doc) // [09:50, 10:00)
            .Pulse(D(29, 10), doc)
            .Close(D(29, 10), doc, D(29, 10, 3))
            .End(D(29, 10, 4)).Write(_f.Outbox);
        _f.Pass();

        var day = _f.Rollup(doc)!.Days.Single();
        day.ActiveMs.Should().Be(10 * 60_000);
        day.OpenMs.Should().Be(63 * 60_000, "open time still runs to doc_closed");
    }

    [Fact]
    public void R9_a_cancelled_close_leaves_the_model_open_and_its_edits_count()
    {
        var doc = SessionBuilder.Local("A");
        new SessionBuilder().Start(D(29, 8)).Open(D(29, 9), doc, 0)
            .Pulse(D(29, 9, 30), doc)
            .Close(D(29, 10), doc, D(29, 10, 0).AddSeconds(5), status: "Cancelled")
            .Pulses(D(29, 10, 1), D(29, 10, 5), doc) // [10:01, 10:05)
            .Close(D(29, 11), doc, D(29, 11, 2))
            .End(D(29, 11, 3)).Write(_f.Outbox);
        _f.Pass();

        var day = _f.Rollup(doc)!.Days.Single();
        day.ActiveMs.Should().Be(5 * 60_000);
        day.OpenMs.Should().Be(122 * 60_000);
    }

    // ---- R10 --------------------------------------------------------------

    [Fact]
    public void R10_a_failed_index_write_during_sealing_never_escapes_RunPass_and_stops_cleanup()
    {
        var doc = SessionBuilder.Local("A");
        var files = Enumerable.Range(20, 5).Select(day => Edits(doc, day, 2).Write(_f.Outbox, D(day, 13))).ToArray();
        _f.Pass();
        var blocker = Path.Combine(_f.Rollups, "index.json.tmp");
        Directory.CreateDirectory(blocker); // the index can no longer be replaced

        var act = () => _f.Pass();
        var result = act.Should().NotThrow().Subject;
        result.Failed.Should().BeGreaterThan(0);
        result.Deleted.Should().Be(0);
        act.Should().NotThrow().Which.Deleted.Should().Be(0, "no cleanup after a failed persist");
        files.Should().OnlyContain(p => File.Exists(p));

        Directory.Delete(blocker);
        _f.PassUntilQuiet();
        _f.Rollup(doc)!.Days.Sum(d => d.ActiveMs).Should().Be(5 * 2 * 60_000);
        files.Take(2).Should().OnlyContain(p => !File.Exists(p));
    }

    // ---- R11 --------------------------------------------------------------

    [Fact]
    public void R11_a_used_day_with_no_health_inputs_keeps_its_grey_dot_after_folding()
    {
        var doc = SessionBuilder.Local("A");
        new SessionBuilder().Start(D(29, 8)).OpenedOnly(D(29, 9), doc).Pulse(D(29, 10), doc)
            .Close(D(29, 11), doc).End(D(29, 11, 1)).Write(_f.Outbox);
        var (first, last) = ModelActivity.Range(7, new DateOnly(2026, 10, 31));

        var before = ModelActivityReader.ReadCurrentModel(doc, Read).HealthTrend(first, last, weekly: false);
        _f.Pass();
        var after = ModelActivityReader.ReadCurrentModel(doc, Read).HealthTrend(first, last, weekly: false);

        before.Should().ContainSingle().Which.Health.Colour.Should().Be(ModelHealthColour.Grey);
        after.Should().ContainSingle().Which.Health.Colour.Should().Be(ModelHealthColour.Grey);
        ModelActivityReader.ReadCurrentModel(doc, Read).HealthTrend(first, last, weekly: true)
            .Should().ContainSingle().Which.Health.Colour.Should().Be(ModelHealthColour.Grey);
    }

    // ---- R12 --------------------------------------------------------------

    [Fact]
    public void R12_event_windows_age_while_the_model_is_idle_in_the_reader_and_the_store()
    {
        var doc = SessionBuilder.Local("A");
        var s = new SessionBuilder().Start(D(1, 6));
        for (var i = 0; i < 15; i++) s.Open(D(1, 7).AddMinutes(i * 30), doc, 10 + i).Close(D(1, 7).AddMinutes(i * 30 + 20), doc);
        s.End(D(1, 16)).Write(_f.Outbox);
        _f.Pass();
        ModelActivityReader.ReadCurrentModel(doc, Read).Opens.Should().HaveCount(15);

        _f.Now = Now.AddDays(180);
        var later = ModelActivityReader.ReadCurrentModel(doc, _f.ReadOptions());
        later.Opens.Should().HaveCount(10);
        later.Opens.Select(p => p.Seconds).Should().Equal(Enumerable.Range(15, 10).Select(i => (double)i));

        _f.Pass().NothingChanged.Should().BeFalse("the stored window has expired points to drop");
        _f.Rollup(doc)!.Opens.Should().HaveCount(10);
        _f.Rollup(doc)!.Days.Single().Opens.Should().Be(15, "daily rows keep every open");
    }
}
