// RollupReviewRound3Tests.cs — regression tests for the PR #177 round-3
// review: R9 a Save As during a close keeps the close's doc_closing clip
// (recovery, session end, and a Save As that re-keys the doc); R13 a ledger
// row with its "models" property omitted is unknown membership, so a backup
// restore still re-folds that session.

using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using FluentAssertions;
using RST.Core.Telemetry;
using RST.Core.Telemetry.Rollups;
using Xunit;

namespace RST.Tests.Telemetry.Rollups;

public sealed class RollupReviewRound3Tests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    private readonly RollupStoreFixture _f = new(Now);

    public void Dispose() => _f.Dispose();

    private static DateTimeOffset D(int day, int hour, int minute = 0, int second = 0) =>
        new(2026, 10, day, hour, minute, second, TimeSpan.Zero);

    private static DocumentIdentity Moved(DocumentIdentity id, string localPath, string? centralPath = null) => new()
    {
        CreationGuid = id.CreationGuid,
        CentralPath = centralPath ?? id.CentralPath,
        LocalPath = localPath,
        Title = id.Title,
        IsWorkshared = id.IsWorkshared,
        IsCloud = false,
    };

    /// <summary>Edits 09:50–10:00, a pulse and doc_closing at 10:00, then a Save As at 10:00:10.</summary>
    private static SessionBuilder CloseThenSaveAs(DocumentIdentity doc, DocumentIdentity savedAs) =>
        new SessionBuilder().Start(D(29, 8)).Open(D(29, 9), doc, 0)
            .Pulses(D(29, 9, 50), D(29, 10), doc)
            .Pulse(D(29, 10), doc)
            .ClosingOnly(D(29, 10), doc)
            .SaveAs(D(29, 10, 0, 10), savedAs, doc.LocalPath!);

    [Fact]
    public void R9_a_save_as_during_a_close_keeps_the_clip_through_crash_recovery()
    {
        var doc = SessionBuilder.Local("A");
        CloseThenSaveAs(doc, Moved(doc, @"C:\models\A-copy.rvt")).Heartbeat(D(29, 10, 2)).Write(_f.Outbox, D(29, 10, 2));
        RecoveryScanner.Scan(_f.Outbox).Should().ContainSingle();
        _f.Pass().Folded.Should().Be(1);

        _f.Rollup(doc)!.Days.Single().ActiveMs.Should().Be(10 * 60_000);
    }

    [Fact]
    public void R9_a_save_as_during_a_close_keeps_the_clip_at_session_end()
    {
        var doc = SessionBuilder.Local("A");
        CloseThenSaveAs(doc, Moved(doc, @"C:\models\A-copy.rvt")).End(D(29, 10, 5)).Write(_f.Outbox);
        _f.Pass();

        _f.Rollup(doc)!.Days.Single().ActiveMs.Should().Be(10 * 60_000);
    }

    [Fact]
    public void R9_a_save_as_that_rekeys_the_doc_during_a_close_keeps_the_clip()
    {
        var doc = SessionBuilder.Central("Tower");
        var newCentral = Moved(doc, @"C:\Users\me\Documents\Tower2_me.rvt", @"\\server\projects\Tower2.rvt");
        CloseThenSaveAs(doc, newCentral).Pulse(D(29, 10, 1), newCentral).End(D(29, 10, 5)).Write(_f.Outbox);
        _f.Pass();

        _f.Rollup(doc)!.Days.Single().ActiveMs.Should().Be(10 * 60_000, "the old identity stops at doc_closing");
        _f.Rollup(newCentral)?.Days.Sum(d => d.ActiveMs).Should().Be(0, "the new identity inherits the close under way");
    }

    [Fact]
    public void R13_an_omitted_models_property_is_unknown_and_a_backup_restore_re_folds_that_session()
    {
        var doc = SessionBuilder.Local("A");
        static SessionBuilder OneMinute(DocumentIdentity d, int day) =>
            new SessionBuilder().Start(D(day, 8)).Open(D(day, 9), d, 0).Pulse(D(day, 10), d)
                .Close(D(day, 11), d).End(D(day, 11, 1));
        OneMinute(doc, 28).Write(_f.Outbox, D(28, 12));
        var second = OneMinute(doc, 29).Write(_f.Outbox, D(29, 12));
        _f.Pass(); // two writes: the backup holds only the 28th

        var ledgerPath = Path.Combine(_f.Rollups, "ledger.json");
        var json = JsonNode.Parse(File.ReadAllText(ledgerPath))!;
        json["files"]!.AsArray().Select(n => n!.AsObject())
            .Single(r => (string?)r["name"] == Path.GetFileName(second)).Remove("models");
        File.WriteAllText(ledgerPath, json.ToJsonString());
        File.Delete(Path.Combine(_f.Rollups, "ledger.bak.json"));
        File.WriteAllText(_f.ModelPath(doc), "{ corrupt");

        _f.PassUntilQuiet();

        _f.Rollup(doc)!.Days.Sum(d => d.ActiveMs).Should().Be(2 * 60_000);
        ModelActivityReader.ReadCurrentModel(doc, _f.ReadOptions()).Days.Sum(d => d.ActiveMs).Should().Be(2 * 60_000);
        _f.Ledger().Files.Single(r => r.Name == Path.GetFileName(second)).Models.Should().ContainSingle();
    }
}
