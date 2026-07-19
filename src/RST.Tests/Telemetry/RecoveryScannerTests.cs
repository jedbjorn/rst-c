// RecoveryScannerTests.cs — orphan detection, synthetic close/end
// synthesis, partial-line tolerance, lock-skip, and closed-file
// idempotence for next-startup crash recovery (Telemetry v1 spec,
// Build Plan step 1).

using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using RST.Core.Telemetry;
using Xunit;
using static RST.Tests.Telemetry.OutboxTestData;

namespace RST.Tests.Telemetry;

public sealed class RecoveryScannerTests : IDisposable
{
    private static readonly DateTimeOffset T0 =
        new(2026, 7, 18, 9, 0, 0, TimeSpan.Zero);

    private readonly string _root;

    public RecoveryScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "RST-RecoveryTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private static TelemetryEvent DocOpened(string sessionGuid, long seq, DateTimeOffset ts, string creationGuid)
    {
        var e = Event(sessionGuid, seq, TelemetryEventTypes.DocOpened, ts);
        new DocumentIdentity { CreationGuid = creationGuid, Title = creationGuid + ".rvt" }.WriteTo(e);
        return e;
    }

    [Fact]
    public void Orphan_gets_synthetic_doc_closed_and_session_end()
    {
        var s = Guid.NewGuid().ToString();
        var lastHeartbeat = T0.AddMinutes(10);
        var path = WriteSessionFile(_root, s,
            Event(s, 1, TelemetryEventTypes.SessionStart, T0),
            DocOpened(s, 2, T0.AddMinutes(1), "doc-a"),
            Event(s, 3, TelemetryEventTypes.Heartbeat, lastHeartbeat));

        var recovered = RecoveryScanner.Scan(_root);

        recovered.Should().Equal(path);
        var events = OutboxFiles.ReadEvents(path);
        events.Select(e => e.EventType).Should().Equal(
            "session_start", "doc_opened", "heartbeat", "doc_closed", "session_end");

        var close = events[3];
        close.Synthetic.Should().BeTrue();
        close.Source.Should().Be("recovery");
        close.Seq.Should().Be(4, "seq continues from the last parsed value");
        close.Ts.Should().Be(lastHeartbeat, "recovery truncates to the last observed event, never overcounts");
        close.SessionGuid.Should().Be(s);
        close.GetString(TelemetryFields.CreationGuid).Should().Be("doc-a",
            "the synthetic close must carry join keys — no doc_closing exists to correlate through");

        var end = events[4];
        end.EventType.Should().Be("session_end");
        end.Synthetic.Should().BeTrue();
        end.Seq.Should().Be(5);
        end.Ts.Should().Be(lastHeartbeat);
    }

    [Fact]
    public void Closed_file_is_left_untouched()
    {
        var s = Guid.NewGuid().ToString();
        var path = WriteSessionFile(_root, s,
            Event(s, 1, TelemetryEventTypes.SessionStart, T0),
            Event(s, 2, TelemetryEventTypes.SessionEnd, T0.AddHours(1)));
        var before = File.ReadAllText(path);

        RecoveryScanner.Scan(_root).Should().BeEmpty();

        File.ReadAllText(path).Should().Be(before);
    }

    [Fact]
    public void Locked_file_is_skipped_as_live()
    {
        var s = Guid.NewGuid().ToString();
        var path = WriteSessionFile(_root, s,
            Event(s, 1, TelemetryEventTypes.SessionStart, T0));

        // Hold the file the way a live writer does.
        using (new FileStream(path, FileMode.Append, FileAccess.Write, OutboxFiles.LiveFileShare))
        {
            RecoveryScanner.Scan(_root).Should().BeEmpty();
        }

        OutboxFiles.ReadEvents(path).Should().ContainSingle(
            "a concurrent live session must never be closed out from under itself");
    }

    [Fact]
    public void Partial_trailing_line_gets_a_defensive_newline_before_synthesis()
    {
        var s = Guid.NewGuid().ToString();
        var path = WriteSessionFile(_root, s,
            Event(s, 1, TelemetryEventTypes.SessionStart, T0));
        File.AppendAllText(path, "{\"event_id\":\"truncated-mid-wr"); // no newline

        RecoveryScanner.Scan(_root).Should().Equal(path);

        var events = OutboxFiles.ReadEvents(path);
        events.Select(e => e.EventType).Should().Equal(
            new[] { "session_start", "session_end" },
            "the partial line is skipped by readers and the synthetics parse cleanly after it");
        events[^1].Synthetic.Should().BeTrue();
    }

    [Fact]
    public void Properly_closed_doc_gets_no_synthetic_close()
    {
        var s = Guid.NewGuid().ToString();
        var closing = Event(s, 3, TelemetryEventTypes.DocClosing, T0.AddMinutes(5));
        new DocumentIdentity { CreationGuid = "doc-a" }.WriteKeysTo(closing);
        closing.SetField(TelemetryFields.ClosingId, "17");
        var closed = Event(s, 4, TelemetryEventTypes.DocClosed, T0.AddMinutes(5));
        closed.SetField(TelemetryFields.ClosingId, "17");
        closed.SetField(TelemetryFields.Status, "Succeeded");

        var path = WriteSessionFile(_root, s,
            Event(s, 1, TelemetryEventTypes.SessionStart, T0),
            DocOpened(s, 2, T0.AddMinutes(1), "doc-a"),
            closing,
            closed);

        RecoveryScanner.Scan(_root).Should().Equal(path);

        var events = OutboxFiles.ReadEvents(path);
        events.Count(e => e.EventType == TelemetryEventTypes.DocClosed).Should().Be(1,
            "the doc closed for real — only the session_end is synthesized");
        events[^1].EventType.Should().Be("session_end");
    }

    [Fact]
    public void Central_path_only_close_correlates_when_creation_guid_is_null()
    {
        // SC-035 failure 1: a file-share central whose creation_guid
        // capture failed (an allowed identity gap) still closes through
        // its keys-only doc_closing — the central path is the join key.
        // Without it the doc reads as still open and recovery would
        // synthesize a close for a doc that closed for real.
        var s = Guid.NewGuid().ToString();
        var identity = new DocumentIdentity
        {
            CentralPath = "\\\\server\\projects\\Tower_Central.rvt",
            LocalPath = "C:\\models\\tower.rvt",
            Title = "tower.rvt",
            IsWorkshared = true,
            IsCloud = false,
        };
        var opened = Event(s, 2, TelemetryEventTypes.DocOpened, T0.AddMinutes(1));
        identity.WriteTo(opened);
        var closing = Event(s, 3, TelemetryEventTypes.DocClosing, T0.AddMinutes(5));
        identity.WriteKeysTo(closing);
        closing.SetField(TelemetryFields.ClosingId, "17");
        var closed = Event(s, 4, TelemetryEventTypes.DocClosed, T0.AddMinutes(5));
        closed.SetField(TelemetryFields.ClosingId, "17");
        closed.SetField(TelemetryFields.Status, "Succeeded");

        var path = WriteSessionFile(_root, s,
            Event(s, 1, TelemetryEventTypes.SessionStart, T0),
            opened, closing, closed);

        RecoveryScanner.Scan(_root).Should().Equal(path);

        OutboxFiles.ReadEvents(path).Where(e => e.Synthetic == true).Should().ContainSingle(
                "the doc closed for real — its keys-only close correlates on central path alone, "
                + "so the session_end is the sole synthetic")
            .Which.EventType.Should().Be(TelemetryEventTypes.SessionEnd);
    }

    [Fact]
    public void Save_as_between_file_share_centrals_re_identifies_the_open_doc()
    {
        // SC-035 failure 2: Save As from one file-share central to
        // another keeps the creation GUID. The doc is open under the NEW
        // identity from the save — a crash must synthesize its close
        // with the post-save central path, not the pre-save one, and one
        // close only: the identity moved, it didn't fork.
        var s = Guid.NewGuid().ToString();
        var before = new DocumentIdentity
        {
            CreationGuid = "doc-a",
            CentralPath = "\\\\server\\projects\\Tower_Central.rvt",
            LocalPath = "C:\\models\\tower.rvt",
            IsWorkshared = true,
            IsCloud = false,
        };
        var after = new DocumentIdentity
        {
            CreationGuid = "doc-a",
            CentralPath = "\\\\server\\projects\\Tower_B_Central.rvt",
            LocalPath = "C:\\models\\tower-b.rvt",
            IsWorkshared = true,
            IsCloud = false,
        };
        var opened = Event(s, 2, TelemetryEventTypes.DocOpened, T0.AddMinutes(1));
        before.WriteTo(opened);
        var savedAs = Event(s, 3, TelemetryEventTypes.DocSavedAs, T0.AddMinutes(30));
        after.WriteTo(savedAs);
        savedAs.SetField(TelemetryFields.PreviousLocalPath, "C:\\models\\tower.rvt");

        var path = WriteSessionFile(_root, s,
            Event(s, 1, TelemetryEventTypes.SessionStart, T0),
            opened, savedAs);

        RecoveryScanner.Scan(_root).Should().Equal(path);

        OutboxFiles.ReadEvents(path)
            .Where(e => e.Synthetic == true && e.EventType == TelemetryEventTypes.DocClosed)
            .Should().ContainSingle("one doc is open — the identity moved, it didn't fork")
            .Which.GetString(TelemetryFields.CentralPath).Should().Be(
                "\\\\server\\projects\\Tower_B_Central.rvt",
                "the synthetic close must carry the post-save identity");
    }

    [Fact]
    public void Save_as_with_absent_path_join_retires_the_old_identity_via_creation_lineage()
    {
        // SC-035 review fix: doc_opened's LocalPath read failed (an
        // allowed identity gap) and doc_saved_as carries no
        // previous_local_path — the path join is absent. The unchanged
        // creation GUID is what says "same doc": without the lineage
        // fallback the pre-save entry survives the real post-save close
        // and recovery forges a doc_closed under the old central path.
        var s = Guid.NewGuid().ToString();
        var before = new DocumentIdentity
        {
            CreationGuid = "doc-a",
            CentralPath = "\\\\server\\projects\\Tower_Central.rvt",
            IsWorkshared = true,
            IsCloud = false,
        };
        var after = new DocumentIdentity
        {
            CreationGuid = "doc-a",
            CentralPath = "\\\\server\\projects\\Tower_B_Central.rvt",
            LocalPath = "C:\\models\\tower-b.rvt",
            IsWorkshared = true,
            IsCloud = false,
        };
        var opened = Event(s, 2, TelemetryEventTypes.DocOpened, T0.AddMinutes(1));
        before.WriteTo(opened);
        var savedAs = Event(s, 3, TelemetryEventTypes.DocSavedAs, T0.AddMinutes(30));
        after.WriteTo(savedAs); // no previous_local_path — the read gap under test
        var closing = Event(s, 4, TelemetryEventTypes.DocClosing, T0.AddMinutes(45));
        after.WriteKeysTo(closing);
        closing.SetField(TelemetryFields.ClosingId, "17");
        var closed = Event(s, 5, TelemetryEventTypes.DocClosed, T0.AddMinutes(45));
        closed.SetField(TelemetryFields.ClosingId, "17");
        closed.SetField(TelemetryFields.Status, "Succeeded");

        var path = WriteSessionFile(_root, s,
            Event(s, 1, TelemetryEventTypes.SessionStart, T0),
            opened, savedAs, closing, closed);

        RecoveryScanner.Scan(_root).Should().Equal(path);

        OutboxFiles.ReadEvents(path).Where(e => e.Synthetic == true).Should().ContainSingle(
                "the doc closed for real under its post-save identity — a synthetic doc_closed "
                + "means the pre-save entry survived the save-as")
            .Which.EventType.Should().Be(TelemetryEventTypes.SessionEnd);
    }

    [Fact]
    public void Save_as_lineage_fallback_declines_when_two_open_docs_share_the_creation_guid()
    {
        // Two Save-As siblings of one lineage are open with no local
        // paths on record when a path-less doc_saved_as arrives —
        // retiring either candidate would be a guess. Both stay open, so
        // a crash closes all three identities: a conservative extra
        // close beats silently dropping a real one.
        var s = Guid.NewGuid().ToString();
        var siblingA = new DocumentIdentity
        {
            CreationGuid = "doc-a",
            CentralPath = "\\\\server\\projects\\Tower_Central.rvt",
        };
        var siblingB = new DocumentIdentity
        {
            CreationGuid = "doc-a",
            CentralPath = "\\\\server\\projects\\Tower_Copy_Central.rvt",
        };
        var moved = new DocumentIdentity
        {
            CreationGuid = "doc-a",
            CentralPath = "\\\\server\\projects\\Tower_B_Central.rvt",
            LocalPath = "C:\\models\\tower-b.rvt",
        };
        var openedA = Event(s, 2, TelemetryEventTypes.DocOpened, T0.AddMinutes(1));
        siblingA.WriteTo(openedA);
        var openedB = Event(s, 3, TelemetryEventTypes.DocOpened, T0.AddMinutes(2));
        siblingB.WriteTo(openedB);
        var savedAs = Event(s, 4, TelemetryEventTypes.DocSavedAs, T0.AddMinutes(30));
        moved.WriteTo(savedAs); // no previous_local_path — ambiguous lineage

        var path = WriteSessionFile(_root, s,
            Event(s, 1, TelemetryEventTypes.SessionStart, T0),
            openedA, openedB, savedAs);

        RecoveryScanner.Scan(_root).Should().Equal(path);

        OutboxFiles.ReadEvents(path)
            .Where(e => e.Synthetic == true && e.EventType == TelemetryEventTypes.DocClosed)
            .Select(e => e.GetString(TelemetryFields.CentralPath))
            .Should().BeEquivalentTo(
                new[]
                {
                    "\\\\server\\projects\\Tower_Central.rvt",
                    "\\\\server\\projects\\Tower_Copy_Central.rvt",
                    "\\\\server\\projects\\Tower_B_Central.rvt",
                },
                "ambiguous lineage retires nothing — two closes here mean the fallback guessed a sibling");
    }

    private string WriteFileWithClosedStatus(string sessionGuid, string? status)
    {
        var closing = Event(sessionGuid, 3, TelemetryEventTypes.DocClosing, T0.AddMinutes(5));
        new DocumentIdentity { CreationGuid = "doc-a" }.WriteKeysTo(closing);
        closing.SetField(TelemetryFields.ClosingId, "17");
        var closed = Event(sessionGuid, 4, TelemetryEventTypes.DocClosed, T0.AddMinutes(5));
        closed.SetField(TelemetryFields.ClosingId, "17");
        if (status is not null) closed.SetField(TelemetryFields.Status, status);

        return WriteSessionFile(_root, sessionGuid,
            Event(sessionGuid, 1, TelemetryEventTypes.SessionStart, T0),
            DocOpened(sessionGuid, 2, T0.AddMinutes(1), "doc-a"),
            closing,
            closed);
    }

    [Theory]
    [InlineData("Cancelled")]
    [InlineData("Failed")]
    public void Cancelled_or_failed_close_leaves_the_doc_open_for_recovery(string status)
    {
        var s = Guid.NewGuid().ToString();
        var path = WriteFileWithClosedStatus(s, status);

        RecoveryScanner.Scan(_root).Should().Equal(path);

        var synthetic = OutboxFiles.ReadEvents(path)
            .Where(e => e.Synthetic == true && e.EventType == TelemetryEventTypes.DocClosed)
            .ToList();
        synthetic.Should().ContainSingle($"a {status} close never actually closed the doc")
            .Which.GetString(TelemetryFields.CreationGuid).Should().Be("doc-a");
    }

    [Theory]
    [InlineData(null)]            // status field absent
    [InlineData("SomethingElse")] // unrecognized status
    public void Missing_or_other_close_status_counts_as_closed(string? status)
    {
        var s = Guid.NewGuid().ToString();
        var path = WriteFileWithClosedStatus(s, status);

        RecoveryScanner.Scan(_root).Should().Equal(path);

        var events = OutboxFiles.ReadEvents(path);
        events.Where(e => e.Synthetic == true).Should().ContainSingle(
                "only Cancelled/Failed mean the close did not happen — anything else closed the doc, "
                + "so the session_end is the sole synthetic")
            .Which.EventType.Should().Be(TelemetryEventTypes.SessionEnd);
    }

    [Fact]
    public void Envelope_less_session_end_does_not_count_as_closed()
    {
        // A damaged line that still says event_type:session_end must not
        // satisfy the closed-file contract — recovery treats the file as
        // a zero-parseable-line orphan and closes it for real.
        var s = Guid.NewGuid().ToString();
        var path = Path.Combine(_root, OutboxFiles.SessionFileName(InstallId, s));
        File.WriteAllText(path, "{\"event_type\":\"session_end\"}\n");

        RecoveryScanner.Scan(_root).Should().Equal(path);

        var events = OutboxFiles.ReadEvents(path);
        events.Should().ContainSingle("the forged line stays unparseable; the synthetic close is real");
        events[0].EventType.Should().Be("session_end");
        events[0].Synthetic.Should().BeTrue();
        events[0].SessionGuid.Should().Be(s);
    }

    [Fact]
    public void Session_end_missing_schema_version_or_source_does_not_count_as_closed()
    {
        // Everything else about the envelope is right — but the property
        // initializers must not paper over the missing keys and let the
        // line count as a close.
        var s = Guid.NewGuid().ToString();
        var noSchema =
            "{\"event_id\":\"" + Guid.NewGuid() + "\",\"session_guid\":\"" + s + "\"," +
            "\"seq\":2,\"ts\":\"2026-07-18T09:30:00.000Z\",\"event_type\":\"session_end\"," +
            "\"source\":\"addin\"}";
        var path = WriteSessionFile(_root, s,
            Event(s, 1, TelemetryEventTypes.SessionStart, T0));
        File.AppendAllText(path, noSchema + "\n");

        RecoveryScanner.Scan(_root).Should().Equal(path);

        var events = OutboxFiles.ReadEvents(path);
        events[^1].EventType.Should().Be("session_end");
        events[^1].Synthetic.Should().BeTrue(
            "the schema_version-less line is not a valid close — recovery must close the file for real");
    }

    [Fact]
    public void Unparseable_file_still_reaches_closed_state_via_filename_identity()
    {
        var s = Guid.NewGuid().ToString();
        var path = Path.Combine(_root, OutboxFiles.SessionFileName(InstallId, s));
        File.WriteAllText(path, "garbage that is not json");

        RecoveryScanner.Scan(_root).Should().Equal(path);

        var events = OutboxFiles.ReadEvents(path);
        events.Should().ContainSingle("closed-file contract: every non-live file gets a session_end");
        events[0].EventType.Should().Be("session_end");
        events[0].SessionGuid.Should().Be(s, "session_guid is recovered from the file name");
        events[0].Synthetic.Should().BeTrue();
    }

    [Fact]
    public void Missing_outbox_dir_is_an_empty_scan()
    {
        RecoveryScanner.Scan(Path.Combine(_root, "does-not-exist")).Should().BeEmpty();
    }
}
