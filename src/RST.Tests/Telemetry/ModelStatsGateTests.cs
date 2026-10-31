// ModelStatsGateTests.cs — when model_stats is captured (spec #24
// Capture Additions, AC 9): open/save/sync requests coalesce into one
// capture per model at the next Idling, at most once per model per
// 5 minutes; a request inside the window queues no Idling work. A model
// that closes before Idling still gets its capture, in either Revit
// event order (review WU2-01), and the window runs on a monotonic clock,
// so wall-clock changes neither stretch nor shorten it (review WU2-02).
// Also pins the wire names the fold (WU1) and a fork's shipper read.

using System;
using FluentAssertions;
using RST.Core.Telemetry;
using Xunit;

namespace RST.Tests.Telemetry;

public sealed class ModelStatsGateTests
{
    // The gate's only time input: monotonic elapsed time.
    private TimeSpan _mono = TimeSpan.FromHours(3);

    private ModelStatsGate NewGate() => new(monotonicNow: () => _mono);

    /// <summary>What the collector's capture does for one key.</summary>
    private static bool Emit(ModelStatsGate gate, string key) =>
        gate.CanEmit(key) && gate.TryAcquire(key);

    /// <summary>An open captured at Idling, leaving doc-a inside its window.</summary>
    private static ModelStatsGate Opened(ModelStatsGate gate, string key = "doc-a")
    {
        gate.Request(key).Should().Be(ModelStatsDecision.Defer);
        gate.TakePending();
        Emit(gate, key).Should().BeTrue();
        return gate;
    }

    [Fact]
    public void Default_window_is_five_minutes()
    {
        ModelStatsGate.DefaultInterval.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void A_fresh_model_is_deferred_and_pending_until_taken()
    {
        var gate = NewGate();
        gate.HasPending.Should().BeFalse();

        gate.Request("doc-a").Should().Be(ModelStatsDecision.Defer);
        gate.HasPending.Should().BeTrue();

        gate.TakePending().Should().Equal("doc-a");
        gate.HasPending.Should().BeFalse("taking empties the set");
        gate.TakePending().Should().BeEmpty();
    }

    [Fact]
    public void Open_then_save_before_idling_is_one_capture()
    {
        var gate = NewGate();
        gate.Request("doc-a").Should().Be(ModelStatsDecision.Defer, "the open");
        gate.Request("doc-a").Should().Be(ModelStatsDecision.Skip, "the save coalesces into the pending capture");

        gate.TakePending().Should().ContainSingle().Which.Should().Be("doc-a");
    }

    [Fact]
    public void Each_model_is_pending_independently()
    {
        var gate = NewGate();
        gate.Request("doc-a");
        gate.Request("doc-b");

        gate.TakePending().Should().BeEquivalentTo(new[] { "doc-a", "doc-b" });
    }

    [Fact]
    public void A_request_inside_the_window_is_dropped_on_arrival()
    {
        var gate = Opened(NewGate());

        _mono += TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59);
        gate.Request("doc-a").Should().Be(ModelStatsDecision.Skip, "a save 4:59 after the last record cannot emit");
        gate.HasPending.Should().BeFalse("no Idling work is queued for it");
    }

    [Fact]
    public void A_request_after_the_window_captures_again()
    {
        var gate = Opened(NewGate());

        _mono += TimeSpan.FromMinutes(5);
        gate.Request("doc-a").Should().Be(ModelStatsDecision.Defer);
        gate.TakePending();
        Emit(gate, "doc-a").Should().BeTrue();
    }

    [Fact]
    public void The_window_is_per_model()
    {
        var gate = Opened(NewGate());

        _mono += TimeSpan.FromMinutes(1);
        gate.Request("doc-b").Should().Be(ModelStatsDecision.Defer, "another model's window does not apply");
    }

    [Fact]
    public void A_capture_that_never_emitted_does_not_open_a_window()
    {
        // Collection was switched off before Idling: nothing was acquired,
        // so the next save captures.
        var gate = NewGate();
        gate.Request("doc-a");
        gate.TakePending();

        _mono += TimeSpan.FromSeconds(30);
        gate.Request("doc-a").Should().Be(ModelStatsDecision.Defer);
    }

    [Fact]
    public void Two_emissions_inside_one_window_admit_only_the_first()
    {
        // The lifecycle's admit callback is the authoritative commit.
        var gate = NewGate();
        gate.TryAcquire("doc-a").Should().BeTrue();
        gate.CanEmit("doc-a").Should().BeFalse();
        gate.TryAcquire("doc-a").Should().BeFalse();
    }

    // ---- closing before Idling (review WU2-01) ---------------------------

    [Fact]
    public void Save_then_close_before_idling_captures_at_the_close()
    {
        // Revit order A: the close prompt's save fires before DocumentClosing.
        var gate = Opened(NewGate());
        _mono += TimeSpan.FromMinutes(6);

        gate.Request("doc-a").Should().Be(ModelStatsDecision.Defer, "the save, deferred as usual");
        gate.Closing("doc-a").Should().BeTrue("the pending capture runs while the Document is still valid");
        Emit(gate, "doc-a").Should().BeTrue();

        gate.HasPending.Should().BeFalse("the close took the request; Idling has nothing left to drop");
        gate.TakePending().Should().BeEmpty();
    }

    [Fact]
    public void Save_after_closing_has_started_captures_at_once()
    {
        // Revit order B: DocumentClosing fires, then the save (or a
        // Synchronize and Close sync) runs before DocumentClosed.
        var gate = Opened(NewGate());
        _mono += TimeSpan.FromMinutes(6);

        gate.Closing("doc-a").Should().BeFalse("nothing was pending at the close");
        gate.Request("doc-a").Should().Be(ModelStatsDecision.CaptureNow,
            "the model will be gone before Idling");
        gate.HasPending.Should().BeFalse("a close-time capture is never deferred");
        Emit(gate, "doc-a").Should().BeTrue();
    }

    [Fact]
    public void Close_with_nothing_pending_captures_nothing()
    {
        var gate = NewGate();
        gate.Closing("doc-a").Should().BeFalse();
        gate.HasPending.Should().BeFalse();
    }

    [Fact]
    public void Save_on_close_inside_the_window_is_still_limited_to_one_per_five_minutes()
    {
        var gate = Opened(NewGate());
        _mono += TimeSpan.FromMinutes(2);

        gate.Request("doc-a").Should().Be(ModelStatsDecision.Skip);
        gate.Closing("doc-a").Should().BeFalse();
        gate.Request("doc-a").Should().Be(ModelStatsDecision.Skip, "closing does not lift the window");
    }

    [Fact]
    public void A_cancelled_close_defers_again()
    {
        var gate = NewGate();
        gate.Closing("doc-a");
        gate.ClosingEnded("doc-a"); // DocumentClosed, status Cancelled

        gate.Request("doc-a").Should().Be(ModelStatsDecision.Defer, "the model is open again");
    }

    [Fact]
    public void Closing_one_model_leaves_others_deferred()
    {
        var gate = NewGate();
        gate.Request("doc-a");
        gate.Request("doc-b");

        gate.Closing("doc-a").Should().BeTrue();
        gate.TakePending().Should().Equal("doc-b");
        gate.Request("doc-c").Should().Be(ModelStatsDecision.Defer);
    }

    // ---- monotonic window (review WU2-02) --------------------------------

    [Fact]
    public void The_constructor_takes_no_wall_clock()
    {
        // The only time source is monotonic: a wall-clock change cannot
        // reach the window at all. Pin the signature so a DateTimeOffset
        // clock is not reintroduced.
        var ctor = typeof(ModelStatsGate).GetConstructors();
        ctor.Should().ContainSingle();
        ctor[0].GetParameters().Should().NotContain(p =>
            p.ParameterType == typeof(Func<DateTimeOffset>) || p.ParameterType == typeof(Func<DateTime>));
    }

    [Fact]
    public void A_wall_clock_set_back_an_hour_does_not_stretch_the_window()
    {
        // Emit at 10:00 wall time, then the clock is corrected to 09:00.
        // Five real minutes later (monotonic +5 min, wall 09:05) the model
        // captures again — not at 10:05 wall time, 65 minutes later.
        var gate = Opened(NewGate());

        _mono += TimeSpan.FromMinutes(5);
        gate.Request("doc-a").Should().Be(ModelStatsDecision.Defer);
    }

    [Fact]
    public void A_wall_clock_set_forward_an_hour_does_not_shorten_the_window()
    {
        // Emit, then the clock jumps an hour ahead; 30 real seconds later
        // the window is still closed.
        var gate = Opened(NewGate());

        _mono += TimeSpan.FromSeconds(30);
        gate.Request("doc-a").Should().Be(ModelStatsDecision.Skip);
    }

    [Fact]
    public void The_default_source_advances_with_real_time()
    {
        var gate = new ModelStatsGate(interval: TimeSpan.FromMilliseconds(250));
        gate.TryAcquire("doc-a").Should().BeTrue();
        gate.CanEmit("doc-a").Should().BeFalse();

        System.Threading.Thread.Sleep(400);
        gate.CanEmit("doc-a").Should().BeTrue();
    }

    // ---- wire shape ------------------------------------------------------

    [Fact]
    public void Wire_names_are_the_spec_names()
    {
        TelemetryEventTypes.ModelStats.Should().Be("model_stats");
        TelemetryFields.WarningsCount.Should().Be("warnings_count");
        TelemetryFields.FileSizeBytes.Should().Be("file_size_bytes");
        TelemetryFields.RevitSubversion.Should().Be("revit_subversion");
    }

    [Fact]
    public void A_model_stats_record_round_trips_with_a_null_file_size()
    {
        var e = new TelemetrySession().Create(TelemetryEventTypes.ModelStats);
        e.SetField(TelemetryFields.CreationGuid, "c-1");
        e.SetField(TelemetryFields.WarningsCount, 42);
        e.SetField(TelemetryFields.FileSizeBytes, null);

        var back = TelemetryJson.TryParseLine(TelemetryJson.SerializeLine(e))!;

        back.EventType.Should().Be("model_stats");
        back.GetInt64(TelemetryFields.WarningsCount).Should().Be(42);
        back.GetInt64(TelemetryFields.FileSizeBytes).Should().BeNull();
        back.Payload.Should().ContainKey(TelemetryFields.FileSizeBytes,
            "null is written, so readers can tell 'not readable' from an older record");
    }
}
