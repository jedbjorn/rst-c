// OutboxWriterTests.cs — append/flush semantics, live-file sharing,
// bounded-queue drop-oldest, shutdown drain, and never-throw degradation
// for the session outbox writer (Telemetry v1 spec, Build Plan step 1).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using RST.Core.Telemetry;
using Xunit;

namespace RST.Tests.Telemetry;

public sealed class OutboxWriterTests : IDisposable
{
    private const string InstallId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string SessionGuid = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

    private readonly string _root;
    private readonly TelemetrySession _session = new(SessionGuid);

    public OutboxWriterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "RST-OutboxTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private OutboxWriter NewWriter(int capacity = OutboxWriter.DefaultQueueCapacity, Action<string>? log = null) =>
        new(_root, InstallId, SessionGuid, capacity, log);

    [Fact]
    public void Writes_one_line_per_event_to_the_session_named_file()
    {
        using (var writer = NewWriter())
        {
            writer.Start();
            writer.Enqueue(_session.Create(TelemetryEventTypes.SessionStart));
            writer.Enqueue(_session.Create(TelemetryEventTypes.Heartbeat));
            writer.Enqueue(_session.Create(TelemetryEventTypes.SessionEnd));
            writer.CompleteAndJoin(TimeSpan.FromSeconds(10)).Should().BeTrue();
        }

        var expected = Path.Combine(_root, $"{InstallId}_{SessionGuid}.jsonl");
        File.Exists(expected).Should().BeTrue();

        var events = OutboxFiles.ReadEvents(expected);
        events.Select(e => e.EventType).Should().Equal(
            "session_start", "heartbeat", "session_end");
        events.Select(e => e.Seq).Should().Equal(1, 2, 3);
        File.ReadAllText(expected).Should().EndWith("\n", "every record is a complete line");
    }

    [Fact]
    public void No_events_means_no_file()
    {
        using (var writer = NewWriter())
        {
            writer.Start();
            writer.CompleteAndJoin(TimeSpan.FromSeconds(10)).Should().BeTrue();
        }
        Directory.GetFiles(_root).Should().BeEmpty(
            "a disabled or event-less session must not create an outbox file");
    }

    [Fact]
    public void Live_file_is_readable_and_probes_as_locked()
    {
        using var writer = NewWriter();
        writer.Start();
        writer.Enqueue(_session.Create(TelemetryEventTypes.SessionStart));

        // Wait until the writer thread has created + flushed the file.
        WaitUntil(() => File.Exists(writer.SessionFilePath)
                        && OutboxFiles.ReadEvents(writer.SessionFilePath).Count == 1);

        // Dashboard / future shipper can read while the session is live…
        OutboxFiles.ReadEvents(writer.SessionFilePath).Should().ContainSingle()
            .Which.EventType.Should().Be("session_start");

        // …but recovery/prune's write-access probe sees it as locked.
        OutboxFiles.IsLocked(writer.SessionFilePath).Should().BeTrue();

        writer.CompleteAndJoin(TimeSpan.FromSeconds(10)).Should().BeTrue();
        OutboxFiles.IsLocked(writer.SessionFilePath).Should().BeFalse(
            "the handle must be released after shutdown drain");
    }

    [Fact]
    public void Full_queue_drops_oldest_and_warns_exactly_once()
    {
        var logs = new List<string>();
        using var writer = NewWriter(capacity: 3, log: logs.Add);

        // Not started yet — the queue fills deterministically.
        for (var i = 0; i < 6; i++)
            writer.Enqueue(_session.Create(TelemetryEventTypes.DocChangedPulse));

        writer.Start();
        writer.CompleteAndJoin(TimeSpan.FromSeconds(10)).Should().BeTrue();

        var events = OutboxFiles.ReadEvents(writer.SessionFilePath);
        events.Should().HaveCount(3, "capacity is 3 and the oldest drop first");
        events.Select(e => e.Seq).Should().Equal(4, 5, 6);
        logs.Count(m => m.Contains("queue full")).Should().Be(1,
            "degraded-mode warning logs once per session, not per drop");
    }

    [Fact]
    public void Io_failure_degrades_to_off_without_throwing_and_logs_once()
    {
        // An outbox dir path that collides with an existing FILE makes
        // CreateDirectory fail — the writer must swallow it, log once,
        // and go dead for the session.
        var fileAsDir = Path.Combine(_root, "not-a-dir");
        File.WriteAllText(fileAsDir, "x");

        var logs = new List<string>();
        using var writer = new OutboxWriter(fileAsDir, InstallId, SessionGuid, log: logs.Add);
        writer.Start();
        writer.Enqueue(_session.Create(TelemetryEventTypes.SessionStart));

        WaitUntil(() => writer.IsDegraded);

        writer.Enqueue(_session.Create(TelemetryEventTypes.Heartbeat));
        writer.CompleteAndJoin(TimeSpan.FromSeconds(10)).Should().BeTrue();

        writer.IsDegraded.Should().BeTrue();
        logs.Count(m => m.Contains("telemetry write failed")).Should().Be(1);
    }

    [Fact]
    public void Overflow_warning_is_never_invoked_synchronously_by_Enqueue()
    {
        var logThreads = new List<int>();
        using var writer = NewWriter(capacity: 2, log: _ => logThreads.Add(Environment.CurrentManagedThreadId));

        // Writer not started: every overflow happens on this thread, and
        // the warning must NOT — Enqueue runs on Revit's event thread and
        // may never perform or block on logging IO.
        for (var i = 0; i < 5; i++)
            writer.Enqueue(_session.Create(TelemetryEventTypes.DocChangedPulse));
        logThreads.Should().BeEmpty("Enqueue must only flag the warning, never emit it");

        writer.Start();
        writer.CompleteAndJoin(TimeSpan.FromSeconds(10)).Should().BeTrue();

        logThreads.Should().ContainSingle("the writer thread emits the one-shot warning")
            .Which.Should().NotBe(Environment.CurrentManagedThreadId);
    }

    [Fact]
    public void Enqueue_after_complete_is_a_silent_no_op()
    {
        var writer = NewWriter();
        writer.Start();
        writer.Enqueue(_session.Create(TelemetryEventTypes.SessionStart));
        writer.CompleteAndJoin(TimeSpan.FromSeconds(10)).Should().BeTrue();

        writer.Enqueue(_session.Create(TelemetryEventTypes.Heartbeat));

        OutboxFiles.ReadEvents(writer.SessionFilePath).Should().ContainSingle();
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("condition not met within 10s");
            System.Threading.Thread.Sleep(10);
        }
    }
}
