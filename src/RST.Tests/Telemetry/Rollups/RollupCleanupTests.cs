// RollupCleanupTests.cs — spec #24 AC 3: cleanup never deletes unfolded data.
// Live, unrecovered, unsealed, single-copy-sealed and newest-3 files survive;
// files sealed in both the rollup and its .bak.json are deleted; a registered
// gate that refuses (or throws) blocks deletion; the age backstop still applies.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using RST.Core.Telemetry;
using RST.Core.Telemetry.Rollups;
using Xunit;

namespace RST.Tests.Telemetry.Rollups;

public sealed class RollupCleanupTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    private readonly RollupStoreFixture _f = new(Now);
    private readonly DocumentIdentity _doc = SessionBuilder.Central("Tower");

    public void Dispose() => _f.Dispose();

    private static DateTimeOffset D(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0, TimeSpan.Zero);

    private string Closed(int day) =>
        new SessionBuilder().Start(D(day, 8)).Open(D(day, 9), _doc)
            .Pulses(D(day, 10), D(day, 10, 10), _doc).Close(D(day, 12), _doc).End(D(day, 12, 1))
            .Write(_f.Outbox, D(day, 13));

    /// <summary>Five closed sessions (the 20th–24th), one live, one crashed and not yet recovered.</summary>
    private (string[] Closed, string Live, string Unrecovered) Outbox()
    {
        var closed = Enumerable.Range(20, 5).Select(Closed).ToArray();
        var live = new SessionBuilder().Start(D(31, 8)).Open(D(31, 9), _doc).Pulse(D(31, 10), _doc).Write(_f.Outbox);
        _f.LiveFiles.Add(live);
        var unrecovered = new SessionBuilder().Start(D(19, 8)).Open(D(19, 9), _doc).Pulse(D(19, 10), _doc)
            .Write(_f.Outbox, D(19, 11));
        return (closed, live, unrecovered);
    }

    private string SealState(string path) =>
        _f.Ledger().Files.Single(r => r.Name == Path.GetFileName(path)).Models.Single().Sealed;

    [Fact]
    public void Files_are_deleted_only_after_the_seal_reaches_both_copies_and_never_the_newest_three()
    {
        var (closed, live, unrecovered) = Outbox();

        // Pass 1 folds: nothing sealed, nothing deleted.
        _f.Pass().Folded.Should().Be(5);
        closed.Should().OnlyContain(p => File.Exists(p));
        SealState(closed[0]).Should().Be("no");

        // Pass 2 seals into the current rollup only: still nothing deleted.
        _f.Pass().Sealed.Should().Be(5);
        closed.Should().OnlyContain(p => File.Exists(p));
        SealState(closed[0]).Should().Be("current");
        _f.Rollup(_doc)!.SealedSessions.Should().HaveCount(5);
        _f.Rollup(_doc)!.Pending.Should().BeEmpty();

        // Pass 3 carries the seal into .bak.json, verifies both copies, deletes the older two.
        _f.Pass().Deleted.Should().Be(2);
        File.Exists(closed[0]).Should().BeFalse();
        File.Exists(closed[1]).Should().BeFalse();
        closed.Skip(2).Should().OnlyContain(p => File.Exists(p), "the 3 newest closed files are a re-fold buffer");
        File.Exists(live).Should().BeTrue();
        File.Exists(unrecovered).Should().BeTrue();

        // The data is all still there, and the store goes quiet.
        _f.Rollup(_doc)!.Days.Sum(d => d.ActiveMs).Should().Be(5 * 10 * 60_000);
        _f.PassUntilQuiet();
        _f.OutboxNames().Should().HaveCount(5);
    }

    [Fact]
    public void A_file_sealed_in_the_current_rollup_but_not_its_backup_is_kept_even_when_the_ledger_says_both()
    {
        var closed = Enumerable.Range(20, 5).Select(Closed).ToArray();
        _f.Pass();
        _f.Pass();
        var id = ModelKey.From(_doc)!.Value.ModelId;
        var store = new RollupStore(_f.Rollups);
        store.ReadCopy(id, backup: false, out _)!.SealedSessions.Should().HaveCount(5);
        store.ReadCopy(id, backup: true, out _)!.SealedSessions.Should().HaveCount(4, "the backup is the write before the last seal");

        // A ledger that claims more than the files show must not delete anything.
        var ledgerPath = Path.Combine(_f.Rollups, "ledger.json");
        File.WriteAllText(ledgerPath, File.ReadAllText(ledgerPath).Replace("\"sealed\":\"current\"", "\"sealed\":\"both\""));
        File.Delete(Path.Combine(_f.Rollups, "ledger.bak.json"));
        // Take the seals out of the backup entirely.
        var bak = _f.BakPath(_doc);
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(bak))!;
        json["sealed_sessions"] = new System.Text.Json.Nodes.JsonArray();
        File.WriteAllText(bak, json.ToJsonString());

        _f.Pass().Deleted.Should().Be(0);
        closed.Should().OnlyContain(p => File.Exists(p));
        SealState(closed[0]).Should().Be("current", "verification found the seal in the current copy only");

        // The next pass carries the seal into the backup; the one after may delete.
        _f.PassUntilQuiet();
        File.Exists(closed[0]).Should().BeFalse();
        File.Exists(closed[1]).Should().BeFalse();
    }

    [Fact]
    public void A_gate_that_refuses_blocks_deletion_and_one_that_allows_does_not()
    {
        var closed = Enumerable.Range(20, 5).Select(Closed).ToArray();
        var refusing = new Gate(_ => false);
        _f.Gates = new IRetentionGate[] { new Gate(_ => true), refusing };
        _f.Pass();
        _f.Pass();
        _f.Pass().Deleted.Should().Be(0);
        closed.Should().OnlyContain(p => File.Exists(p));
        refusing.Asked.Select(c => Path.GetFileName(c.Path)).Should()
            .BeEquivalentTo(closed.Take(2).Select(Path.GetFileName));
        refusing.Asked.First().ModelIds.Should().Equal(ModelKey.From(_doc)!.Value.ModelId);

        _f.Gates = new IRetentionGate[] { new Gate(_ => true) };
        _f.Pass().Deleted.Should().Be(2);
    }

    [Fact]
    public void A_gate_that_throws_refuses()
    {
        var closed = Enumerable.Range(20, 5).Select(Closed).ToArray();
        _f.Gates = new IRetentionGate[] { new Gate(_ => throw new InvalidOperationException("shipper down")) };
        for (var i = 0; i < 4; i++) _f.Pass();
        closed.Should().OnlyContain(p => File.Exists(p));
        _f.Logs.Should().Contain(m => m.Contains("shipper down"));
    }

    [Fact]
    public void The_process_wide_registry_is_the_default_and_upstream_registers_none()
    {
        RetentionGates.Registered.Should().BeEmpty();
        var gate = new Gate(_ => false);
        RetentionGates.Register(gate);
        try { RetentionGates.Registered.Should().Contain(gate); }
        finally { RetentionGates.Unregister(gate); }
        RetentionGates.Registered.Should().BeEmpty();
    }

    [Fact]
    public void A_file_live_at_cleanup_time_is_never_deleted()
    {
        var closed = Enumerable.Range(20, 5).Select(Closed).ToArray();
        _f.Pass();
        _f.Pass();
        _f.LiveFiles.Add(closed[0]); // a reader or writer holds it now
        _f.Pass().Deleted.Should().Be(1);
        File.Exists(closed[0]).Should().BeTrue();
        File.Exists(closed[1]).Should().BeFalse();
    }

    [Fact]
    public void A_raw_file_pruned_by_age_before_sealing_is_sealed_from_its_pending_share()
    {
        var closed = Enumerable.Range(20, 5).Select(Closed).ToArray();
        _f.Pass();
        File.Delete(closed[0]); // the backstop got there first
        _f.Pass();
        var r = _f.Rollup(_doc)!;
        r.Pending.Should().BeEmpty();
        r.Days.Sum(d => d.ActiveMs).Should().Be(5 * 10 * 60_000);
        _f.Ledger().Files.Should().HaveCount(4);
    }

    [Fact]
    public void The_age_backstop_still_deletes_old_closed_files_the_tray_never_folded()
    {
        var old = new SessionBuilder().Start(Now.AddDays(-200)).Open(Now.AddDays(-200).AddMinutes(1), _doc)
            .End(Now.AddDays(-200).AddHours(1)).Write(_f.Outbox);
        var recent = Closed(30);

        var deleted = RetentionPruner.Prune(_f.Outbox, RetentionPruner.DefaultRetentionDays, Now);

        deleted.Should().Equal(old);
        File.Exists(recent).Should().BeTrue();
    }

    private sealed class Gate : IRetentionGate
    {
        private readonly Func<RetentionCandidate, bool> _allow;
        public Gate(Func<RetentionCandidate, bool> allow) => _allow = allow;
        public List<RetentionCandidate> Asked { get; } = new();

        public bool AllowsDelete(RetentionCandidate candidate)
        {
            Asked.Add(candidate);
            return _allow(candidate);
        }
    }
}
