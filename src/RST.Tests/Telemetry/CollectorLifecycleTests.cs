// CollectorLifecycleTests.cs — the collector's Revit-free lifecycle:
// consent-flip marker framing under one gate (SC-028), startup rollback
// on post-start failure (SC-029), session_start-once ordering, the
// closed-file contract at shutdown, and heartbeat safety. Interleaving
// tests are deterministic: a blocked emitter holds the gate (via a
// ManualResetEvent inside its populate callback, which TryEmit runs
// under the gate) while the racing transition is asserted to wait.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using RST.Core.Telemetry;
using Xunit;

namespace RST.Tests.Telemetry;

public sealed class CollectorLifecycleTests : IDisposable
{
    private const string InstallId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string SessionGuid = "cccccccc-cccc-cccc-cccc-cccccccccccc";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly string _root;
    private readonly TelemetrySession _session = new(SessionGuid);
    private readonly OutboxWriter _writer;

    public CollectorLifecycleTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "RST-LifecycleTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _writer = new OutboxWriter(_root, InstallId, SessionGuid);
        // Tests emit without always calling RunStartup — start the
        // writer thread here (Start is idempotent, so RunStartup's own
        // call stays a no-op).
        _writer.Start();
    }

    public void Dispose()
    {
        // Teardown owns the writer stop: an assertion failure before an
        // in-body Shutdown must not leak the writer thread or its open
        // stream into the temp-dir delete (idempotent when a test's own
        // Shutdown already joined it).
        _writer.CompleteAndJoin(TimeSpan.FromSeconds(5));
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private CollectorLifecycle NewLifecycle(
        bool enabled,
        Action<TelemetryEvent>? enrichSessionStart = null,
        Action<TelemetryEvent>? enrichHeartbeat = null,
        Action<string>? log = null) =>
        new(_session, _writer, enabled,
            enrichSessionStart ?? (_ => { }),
            enrichHeartbeat ?? (e => e.SetField(TelemetryFields.OpenDocCount, 0)),
            log ?? (_ => { }));

    private IReadOnlyList<string> WrittenEventTypes() =>
        OutboxFiles.ReadEvents(_writer.SessionFilePath).Select(e => e.EventType).ToList();

    // ---- ordering & the closed-file contract ----------------------------

    [Fact]
    public void First_emission_writes_session_start_first_and_shutdown_appends_session_end()
    {
        var lifecycle = NewLifecycle(enabled: true);
        lifecycle.TryEmit(TelemetryEventTypes.DocOpening).Should().BeTrue();
        lifecycle.Shutdown();

        WrittenEventTypes().Should().Equal("session_start", "doc_opening", "session_end");
        OutboxFiles.ReadEvents(_writer.SessionFilePath).Select(e => e.Seq)
            .Should().BeInAscendingOrder("file order must match seq order");
    }

    [Fact]
    public void Disabled_session_writes_no_file_at_all()
    {
        var lifecycle = NewLifecycle(enabled: false);
        lifecycle.RunStartup(() => { }, () => { });
        lifecycle.TryEmit(TelemetryEventTypes.DocOpening).Should().BeFalse();
        lifecycle.EmitHeartbeat();
        lifecycle.Shutdown();

        Directory.GetFiles(_root).Should().BeEmpty(
            "a session that never collects must not create an outbox file");
    }

    [Fact]
    public void Session_end_still_written_when_collection_was_disabled_before_shutdown()
    {
        var lifecycle = NewLifecycle(enabled: true);
        lifecycle.TryEmit(TelemetryEventTypes.DocOpening).Should().BeTrue();
        lifecycle.SetEnabled(false);
        lifecycle.Shutdown();

        WrittenEventTypes().Should().Equal(
            "session_start", "doc_opening", "collection_disabled", "session_end");
    }

    [Fact]
    public void Disable_with_nothing_written_writes_no_lone_marker()
    {
        var lifecycle = NewLifecycle(enabled: true);
        lifecycle.SetEnabled(false);
        lifecycle.Shutdown();
        Directory.GetFiles(_root).Should().BeEmpty(
            "a lone collection_disabled could never satisfy the closed-file contract");
    }

    [Fact]
    public void Late_enable_opens_the_file_with_session_start_then_enabled_marker()
    {
        var lifecycle = NewLifecycle(enabled: false);
        lifecycle.SetEnabled(true);
        lifecycle.TryEmit(TelemetryEventTypes.DocOpening).Should().BeTrue();
        lifecycle.Shutdown();

        WrittenEventTypes().Should().Equal(
            "session_start", "collection_enabled", "doc_opening", "session_end");
    }

    [Fact]
    public void Throttle_refusal_inside_the_gate_enqueues_nothing()
    {
        var lifecycle = NewLifecycle(enabled: true);
        lifecycle.TryEmit(TelemetryEventTypes.DocOpening, admit: () => false).Should().BeFalse();
        lifecycle.Shutdown();
        // session_start was ensured before the throttle refused — the
        // closed-file contract then demands session_end.
        WrittenEventTypes().Should().Equal("session_start", "session_end");
    }

    [Fact]
    public void Gate_refusal_never_reaches_admit_so_no_throttle_window_burns()
    {
        // SC-030's split leaves the throttle COMMIT inside the gate for
        // exactly this: a disabled or shut-down gate refuses before the
        // caller's throttle state can be touched.
        var admitCalls = 0;
        var lifecycle = NewLifecycle(enabled: false);
        lifecycle.TryEmit(TelemetryEventTypes.DocOpening,
            admit: () => { admitCalls++; return true; }).Should().BeFalse();
        lifecycle.Shutdown();
        lifecycle.TryEmit(TelemetryEventTypes.DocOpening,
            admit: () => { admitCalls++; return true; }).Should().BeFalse();
        admitCalls.Should().Be(0, "a gate refusal must not commit throttle state");
    }

    [Fact]
    public void Shutdown_is_idempotent_and_gates_all_later_emission()
    {
        var lifecycle = NewLifecycle(enabled: true);
        lifecycle.TryEmit(TelemetryEventTypes.DocOpening).Should().BeTrue();
        lifecycle.Shutdown();
        lifecycle.Shutdown();
        lifecycle.TryEmit(TelemetryEventTypes.DocOpening).Should().BeFalse();
        lifecycle.SetEnabled(false);
        lifecycle.EmitHeartbeat();

        WrittenEventTypes().Should().Equal("session_start", "doc_opening", "session_end")
            .And.HaveElementAt(2, "session_end");
    }

    // ---- SC-028: consent flips serialize with concurrent emitters -------

    [Fact]
    public async Task Disable_waits_for_an_inflight_emission_and_no_event_lands_after_the_marker()
    {
        var lifecycle = NewLifecycle(enabled: true);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var emitter = Task.Run(() => lifecycle.TryEmit(TelemetryEventTypes.Heartbeat, populate: _ =>
        {
            entered.Set();
            release.Wait(Patience);
        }));
        entered.Wait(Patience).Should().BeTrue();

        var disable = Task.Run(() => lifecycle.SetEnabled(false));
        (await Task.WhenAny(disable, Task.Delay(150))).Should().NotBeSameAs(disable,
            "a consent flip must serialize behind the in-flight emission");

        release.Set();
        (await emitter.WaitAsync(Patience)).Should().BeTrue();
        await disable.WaitAsync(Patience);

        // Post-flip emitters are refused — nothing can land after the marker.
        lifecycle.EmitHeartbeat();
        lifecycle.TryEmit(TelemetryEventTypes.DocOpening).Should().BeFalse();
        lifecycle.Shutdown();

        WrittenEventTypes().Should().Equal(
            "session_start", "heartbeat", "collection_disabled", "session_end");
    }

    [Fact]
    public async Task Emitter_racing_an_enable_lands_only_after_both_markers()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var lifecycle = NewLifecycle(enabled: false, enrichSessionStart: _ =>
        {
            entered.Set();
            release.Wait(Patience);
        });

        var enable = Task.Run(() => lifecycle.SetEnabled(true));
        entered.Wait(Patience).Should().BeTrue();

        var emitter = Task.Run(() => lifecycle.TryEmit(TelemetryEventTypes.DocOpening));
        (await Task.WhenAny(emitter, Task.Delay(150))).Should().NotBeSameAs(emitter,
            "an emission must serialize behind the in-flight enable transition");

        release.Set();
        await enable.WaitAsync(Patience);
        (await emitter.WaitAsync(Patience)).Should().BeTrue();
        lifecycle.Shutdown();

        WrittenEventTypes().Should().Equal(
            "session_start", "collection_enabled", "doc_opening", "session_end");
    }

    [Fact]
    public async Task Shutdown_waits_for_an_inflight_emission_and_session_end_is_last()
    {
        var lifecycle = NewLifecycle(enabled: true);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var emitter = Task.Run(() => lifecycle.TryEmit(TelemetryEventTypes.DocOpening, populate: _ =>
        {
            entered.Set();
            release.Wait(Patience);
        }));
        entered.Wait(Patience).Should().BeTrue();

        var shutdown = Task.Run(() => lifecycle.Shutdown());
        (await Task.WhenAny(shutdown, Task.Delay(150))).Should().NotBeSameAs(shutdown,
            "shutdown must serialize behind the in-flight emission");

        release.Set();
        (await emitter.WaitAsync(Patience)).Should().BeTrue();
        await shutdown.WaitAsync(Patience);

        WrittenEventTypes().Should().Equal("session_start", "doc_opening", "session_end");
    }

    // ---- SC-029: startup rollback ---------------------------------------

    [Fact]
    public void Subscribe_failure_rolls_back_and_rethrows()
    {
        var unsubscribed = false;
        var lifecycle = NewLifecycle(enabled: true);

        var act = () => lifecycle.RunStartup(
            subscribe: () => throw new InvalidOperationException("subscribe boom"),
            unsubscribe: () => unsubscribed = true);

        act.Should().Throw<InvalidOperationException>().WithMessage("subscribe boom");
        unsubscribed.Should().BeTrue("a partial subscription must be detached");
        lifecycle.HeartbeatRunning.Should().BeFalse();
        lifecycle.TryEmit(TelemetryEventTypes.DocOpening).Should().BeFalse("the gate must be closed after rollback");
        Directory.GetFiles(_root).Should().BeEmpty();
    }

    [Fact]
    public void Maintenance_failure_after_subscribe_rolls_back_and_rethrows()
    {
        var unsubscribed = false;
        var lifecycle = NewLifecycle(enabled: true);

        var act = () => lifecycle.RunStartup(
            subscribe: () => { },
            unsubscribe: () => unsubscribed = true,
            startMaintenance: () => throw new InvalidOperationException("maintenance boom"));

        act.Should().Throw<InvalidOperationException>().WithMessage("maintenance boom");
        unsubscribed.Should().BeTrue();
        lifecycle.HeartbeatRunning.Should().BeFalse("the already-started heartbeat must stop");
        lifecycle.TryEmit(TelemetryEventTypes.DocOpening).Should().BeFalse();
    }

    [Fact]
    public void Rollback_failure_does_not_mask_the_startup_failure()
    {
        var lifecycle = NewLifecycle(enabled: true);
        var act = () => lifecycle.RunStartup(
            subscribe: () => throw new InvalidOperationException("original"),
            unsubscribe: () => throw new IOException("rollback boom"));
        act.Should().Throw<InvalidOperationException>().WithMessage("original");
    }

    [Fact]
    public void Successful_startup_starts_the_heartbeat_only_when_enabled()
    {
        var enabledLifecycle = NewLifecycle(enabled: true);
        enabledLifecycle.RunStartup(() => { }, () => { });
        enabledLifecycle.HeartbeatRunning.Should().BeTrue();
        enabledLifecycle.Shutdown();
        enabledLifecycle.HeartbeatRunning.Should().BeFalse();

        var disabledLifecycle = NewLifecycle(enabled: false);
        disabledLifecycle.RunStartup(() => { }, () => { });
        disabledLifecycle.HeartbeatRunning.Should().BeFalse(
            "a disabled session must not run a heartbeat");
    }

    [Fact]
    public void Shutdown_detaches_subscriptions()
    {
        var unsubscribed = false;
        var lifecycle = NewLifecycle(enabled: true);
        lifecycle.RunStartup(() => { }, () => unsubscribed = true);
        lifecycle.Shutdown();
        unsubscribed.Should().BeTrue();
    }

    // ---- heartbeat safety -----------------------------------------------

    [Fact]
    public void Heartbeat_emits_with_enrichment_between_start_and_end()
    {
        var lifecycle = NewLifecycle(enabled: true,
            enrichHeartbeat: e => e.SetField(TelemetryFields.OpenDocCount, 3));
        lifecycle.EmitHeartbeat();
        lifecycle.Shutdown();

        var events = OutboxFiles.ReadEvents(_writer.SessionFilePath);
        events.Select(e => e.EventType).Should().Equal("session_start", "heartbeat", "session_end");
        events[1].GetInt64(TelemetryFields.OpenDocCount).Should().Be(3);
    }

    [Fact]
    public void Heartbeat_failure_never_throws_and_logs_once()
    {
        var logs = new List<string>();
        var lifecycle = NewLifecycle(enabled: true,
            enrichHeartbeat: _ => throw new InvalidOperationException("heartbeat boom"),
            log: logs.Add);

        var act = () => { lifecycle.EmitHeartbeat(); lifecycle.EmitHeartbeat(); };
        act.Should().NotThrow("an unhandled timer-thread exception would kill Revit");
        logs.Should().ContainSingle().Which.Should().Contain("heartbeat boom");
        lifecycle.Shutdown();
    }

    [Fact]
    public void Session_start_written_flag_tracks_first_emission()
    {
        var lifecycle = NewLifecycle(enabled: true);
        lifecycle.SessionStartWritten.Should().BeFalse();
        lifecycle.EmitSessionStartIfEnabled();
        lifecycle.SessionStartWritten.Should().BeTrue();
        lifecycle.Shutdown();
        WrittenEventTypes().Should().Equal("session_start", "session_end");
    }

    // ---- live-session anchor (Activity tab wiring, step 5) --------------

    [Fact]
    public void Session_start_utc_is_null_until_written_then_matches_the_event_ts()
    {
        var lifecycle = NewLifecycle(enabled: true);
        lifecycle.SessionStartUtc.Should().BeNull("no session_start has been written yet");

        lifecycle.TryEmit(TelemetryEventTypes.DocOpening).Should().BeTrue();
        var afterFirst = lifecycle.SessionStartUtc;
        afterFirst.Should().NotBeNull();

        lifecycle.TryEmit(TelemetryEventTypes.DocOpened).Should().BeTrue();
        lifecycle.SessionStartUtc.Should().Be(afterFirst, "the anchor is session_start's ts, not the latest event's");
        lifecycle.Shutdown();

        var sessionStart = OutboxFiles.ReadEvents(_writer.SessionFilePath)
            .Single(e => e.EventType == TelemetryEventTypes.SessionStart);
        // The wire format carries milliseconds; the in-memory anchor
        // keeps full ticks — same instant to within the serialization.
        lifecycle.SessionStartUtc.Should().BeCloseTo(sessionStart.Ts, TimeSpan.FromMilliseconds(1),
            "the Activity tab ticks from session_start.ts — the property must be that event's ts");
    }

    [Fact]
    public void Disabled_session_never_exposes_a_session_start_utc()
    {
        var lifecycle = NewLifecycle(enabled: false);
        lifecycle.TryEmit(TelemetryEventTypes.DocOpening).Should().BeFalse();
        lifecycle.Shutdown();
        lifecycle.SessionStartUtc.Should().BeNull("a session that never collected has no live-session anchor");
    }

    [Fact]
    public void Mid_session_enable_sets_the_session_start_utc()
    {
        var lifecycle = NewLifecycle(enabled: false);
        lifecycle.SessionStartUtc.Should().BeNull();
        lifecycle.SetEnabled(true);
        lifecycle.SessionStartUtc.Should().NotBeNull("enable emits session_start at enable time (unit 2, call #4)");
        lifecycle.Shutdown();

        var sessionStart = OutboxFiles.ReadEvents(_writer.SessionFilePath)
            .Single(e => e.EventType == TelemetryEventTypes.SessionStart);
        lifecycle.SessionStartUtc.Should().BeCloseTo(sessionStart.Ts, TimeSpan.FromMilliseconds(1));
    }
}
