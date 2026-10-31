// RollupReviewRound2Tests.cs — regression tests for the PR #177 delta review
// (round 2), through the public fold and read APIs: R2 a file replaced after
// cleanup verified it is never deleted; R13 corrupt ledger membership never
// authorizes a delete, while a genuinely empty session still cleans up; R5 a
// sibling central sharing a creation GUID is rejected in every folded and
// unfolded combination; R9 a crash during a close clips active time at the
// doc_closing recovery never saw closed.

using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using FluentAssertions;
using RST.Core.Telemetry;
using RST.Core.Telemetry.Rollups;
using Xunit;

namespace RST.Tests.Telemetry.Rollups;

public sealed class RollupReviewRound2Tests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    private readonly RollupStoreFixture _f = new(Now);

    public void Dispose() => _f.Dispose();

    private static DateTimeOffset D(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0, TimeSpan.Zero);

    private static SessionBuilder Edits(DocumentIdentity doc, int day, int editMinutes, string? guid = null) =>
        new SessionBuilder(guid).Start(D(day, 8)).Open(D(day, 9), doc, 0)
            .Pulses(D(day, 10), D(day, 10).AddMinutes(editMinutes), doc)
            .Close(D(day, 12), doc).End(D(day, 12, 1));

    private static long ActiveMs(ModelActivity a) => a.Days.Sum(d => d.ActiveMs);

    private long ActiveOf(DocumentIdentity doc) => ActiveMs(ModelActivityReader.ReadCurrentModel(doc, _f.ReadOptions()));

    // ---- R2 ---------------------------------------------------------------

    /// <summary>Takes files through the real taker, but replaces the path with
    /// <see cref="Replacement"/> after cleanup has verified the held file and just before it deletes.</summary>
    private sealed class ReplacingTaker : IFileTaker
    {
        public Func<string, byte[]?> Replacement { get; init; } = _ => null;
        public int Replaced { get; private set; }

        public ITakenFile? TryTake(string path) =>
            OutboxFileTaker.Default.TryTake(path) is { } inner ? new Taken(this, path, inner) : null;

        private sealed class Taken : ITakenFile
        {
            private readonly ReplacingTaker _owner;
            private readonly string _path;
            private readonly ITakenFile _inner;

            public Taken(ReplacingTaker owner, string path, ITakenFile inner) { _owner = owner; _path = path; _inner = inner; }
            public long Length => _inner.Length;
            public DateTimeOffset LastWriteUtc => _inner.LastWriteUtc;
            public byte[] ReadAll() => _inner.ReadAll();
            public void Dispose() => _inner.Dispose();

            public bool Delete()
            {
                if (_owner.Replacement(_path) is { } bytes)
                {
                    var temp = _path + ".incoming";
                    File.WriteAllBytes(temp, bytes);
                    File.SetLastWriteTimeUtc(temp, DateTime.UtcNow);
                    File.Move(temp, _path, overwrite: true); // the path now names another file
                    _owner.Replaced++;
                }
                return _inner.Delete();
            }
        }
    }

    [Fact]
    public void R2_a_file_replaced_after_verification_is_not_deleted_and_its_new_data_folds()
    {
        var doc = SessionBuilder.Local("A");
        var first = Edits(doc, 20, 10);
        var path = first.Write(_f.Outbox, D(20, 13));
        foreach (var day in new[] { 25, 26, 27 }) Edits(doc, day, 1).Write(_f.Outbox, D(day, 13));
        _f.Pass();
        _f.Pass();

        // The 20-minute version of the same session, as the bytes Revit would have written.
        var scratch = Path.Combine(_f.Root, "scratch");
        var newer = File.ReadAllBytes(Edits(doc, 20, 20, first.SessionGuid).Write(scratch, D(20, 14)));
        var taker = new ReplacingTaker { Replacement = p => p == path ? newer : null };
        _f.FileTaker = taker;

        _f.Pass().Deleted.Should().Be(0);
        taker.Replaced.Should().Be(1);
        File.Exists(path).Should().BeTrue();
        File.ReadAllBytes(path).Should().Equal(newer, "the replacement is never the file deleted");

        _f.FileTaker = null;
        _f.PassUntilQuiet();
        _f.Rollup(doc)!.Days.Single(d => d.Date.Day == 20).ActiveMs.Should().Be(20 * 60_000);
    }

    // ---- R13 --------------------------------------------------------------

    private void EditLedgerRow(string fileName, Action<JsonObject> edit)
    {
        var ledgerPath = Path.Combine(_f.Rollups, "ledger.json");
        var json = JsonNode.Parse(File.ReadAllText(ledgerPath))!;
        var row = json["files"]!.AsArray().Select(n => n!.AsObject()).Single(r => (string?)r["name"] == fileName);
        edit(row);
        File.WriteAllText(ledgerPath, json.ToJsonString());
        File.Delete(Path.Combine(_f.Rollups, "ledger.bak.json"));
    }

    [Fact]
    public void R13_corrupt_ledger_membership_never_authorizes_a_delete()
    {
        var docs = Enumerable.Range(0, 4).Select(i => SessionBuilder.Local("M" + i)).ToArray();
        var paths = docs.Select((d, i) => Edits(d, 20 + i, 5).Write(_f.Outbox, D(20 + i, 13))).ToArray();
        _f.Pass();
        EditLedgerRow(Path.GetFileName(paths[0]), row => row["models"] = null);

        _f.Pass();
        File.Exists(paths[0]).Should().BeTrue("unknown membership re-folds; it never reads as 'touched nothing'");
        _f.Ledger().Files.Single(r => r.Name == Path.GetFileName(paths[0])).Models.Should().ContainSingle();

        _f.PassUntilQuiet();
        File.Exists(paths[0]).Should().BeFalse("once re-folded and sealed in both copies it cleans up normally");
        ActiveOf(docs[0]).Should().Be(5 * 60_000);
        var store = new RollupStore(_f.Rollups);
        store.ReadCopy(ModelKey.From(docs[0])!.Value.ModelId, backup: true, out _).Should().NotBeNull();
    }

    [Fact]
    public void R13_an_empty_membership_the_content_contradicts_is_never_deleted_on_its_word()
    {
        var docs = Enumerable.Range(0, 4).Select(i => SessionBuilder.Local("M" + i)).ToArray();
        var paths = docs.Select((d, i) => Edits(d, 20 + i, 5).Write(_f.Outbox, D(20 + i, 13))).ToArray();
        _f.Pass();
        // A ledger claiming the oldest file touched no model, already sealed everywhere.
        EditLedgerRow(Path.GetFileName(paths[0]), row => row["models"] = new JsonArray());

        _f.Pass().Deleted.Should().Be(0);
        File.Exists(paths[0]).Should().BeTrue("re-folding the held content finds a model the ledger does not list");

        _f.PassUntilQuiet();
        ActiveOf(docs[0]).Should().Be(5 * 60_000);
    }

    [Fact]
    public void R13_a_genuinely_empty_session_is_sealed_vacuously_and_cleaned_up()
    {
        var empty = new SessionBuilder().Start(D(19, 8)).Heartbeat(D(19, 8, 2)).End(D(19, 8, 3));
        var emptyPath = empty.Write(_f.Outbox, D(19, 9));
        var doc = SessionBuilder.Local("A");
        foreach (var day in new[] { 25, 26, 27 }) Edits(doc, day, 1).Write(_f.Outbox, D(day, 13));

        _f.Pass();
        _f.Ledger().Files.Single(r => r.Name == Path.GetFileName(emptyPath)).Models.Should().BeEmpty();
        _f.PassUntilQuiet();

        File.Exists(emptyPath).Should().BeFalse("a session that touched no model has nothing to lose");
        _f.OutboxNames().Should().HaveCount(3);
    }

    // ---- R5 ---------------------------------------------------------------

    [Fact]
    public void R5_a_sibling_central_sharing_a_creation_guid_is_rejected_folded_or_not()
    {
        var a = SessionBuilder.Central("A");
        var b = new DocumentIdentity
        {
            CreationGuid = a.CreationGuid, CentralPath = @"\\server\projects\B.rvt", LocalPath = @"C:\b.rvt", Title = "B",
            IsWorkshared = true, IsCloud = false,
        };
        var gappedA = new DocumentIdentity { CreationGuid = a.CreationGuid, LocalPath = a.LocalPath, Title = "A" };
        Edits(gappedA, 27, 10).Write(_f.Outbox, D(27, 13)); // a creation-only rollup holding A's 10 minutes
        _f.Pass();

        // B unfolded.
        Edits(b, 28, 20).Write(_f.Outbox, D(28, 13));
        ActiveOf(a).Should().Be(10 * 60_000);

        // B folded.
        _f.Pass();
        ActiveOf(a).Should().Be(10 * 60_000);

        // A's own session unfolded, then folded.
        Edits(a, 29, 5).Write(_f.Outbox, D(29, 13));
        ActiveOf(a).Should().Be(15 * 60_000);
        _f.PassUntilQuiet();
        ActiveOf(a).Should().Be(15 * 60_000);

        // The tray detail of A's own rollup agrees.
        var aRow = _f.Index().Single(r => r.ModelId == ModelKey.From(a)!.Value.ModelId);
        ActiveMs(ModelActivityReader.ReadModel(aRow.ModelId, _f.ReadOptions())).Should().Be(15 * 60_000);
    }

    // ---- R9 ---------------------------------------------------------------

    [Fact]
    public void R9_a_crash_during_a_close_clips_active_time_at_its_doc_closing()
    {
        var doc = SessionBuilder.Local("A");
        new SessionBuilder().Start(D(29, 8)).Open(D(29, 9), doc, 0)
            .Pulses(D(29, 9, 50), D(29, 10), doc) // [09:50, 10:00)
            .Pulse(D(29, 10), doc)
            .ClosingOnly(D(29, 10), doc)          // Revit died while closing
            .Heartbeat(D(29, 10, 2))
            .Write(_f.Outbox, D(29, 10, 2));
        RecoveryScanner.Scan(_f.Outbox).Should().ContainSingle();
        _f.Pass().Folded.Should().Be(1);

        var day = _f.Rollup(doc)!.Days.Single();
        day.ActiveMs.Should().Be(10 * 60_000);
        day.OpenMs.Should().Be(62 * 60_000, "open time runs to recovery's close");
    }

    [Fact]
    public void R9_a_cancelled_close_before_a_crash_does_not_clip()
    {
        var doc = SessionBuilder.Local("A");
        new SessionBuilder().Start(D(29, 8)).Open(D(29, 9), doc, 0)
            .Close(D(29, 9, 30), doc, D(29, 9, 30).AddSeconds(5), status: "Cancelled")
            .Pulses(D(29, 10), D(29, 10, 5), doc)  // [10:00, 10:05)
            .Heartbeat(D(29, 10, 10))
            .Write(_f.Outbox, D(29, 10, 10));
        RecoveryScanner.Scan(_f.Outbox);
        _f.Pass();

        _f.Rollup(doc)!.Days.Single().ActiveMs.Should().Be(5 * 60_000);
    }

    [Fact]
    public void R9_a_close_under_way_at_session_end_clips_too()
    {
        var doc = SessionBuilder.Local("A");
        new SessionBuilder().Start(D(29, 8)).Open(D(29, 9), doc, 0)
            .Pulse(D(29, 9, 59), doc)
            .ClosingOnly(D(29, 9, 59).AddSeconds(30), doc)
            .End(D(29, 10, 5)).Write(_f.Outbox);
        _f.Pass();
        _f.Rollup(doc)!.Days.Single().ActiveMs.Should().Be(30_000);
    }
}
