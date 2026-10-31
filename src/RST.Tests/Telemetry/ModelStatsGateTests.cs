// ModelStatsGateTests.cs — when model_stats is captured (spec #24
// Capture Additions, AC 9): open/save/sync requests coalesce into one
// capture per model at the next Idling, at most once per model per
// 5 minutes; a request inside the window queues no Idling work. Also
// pins the wire names the fold (WU1) and a fork's shipper read.

using System;
using FluentAssertions;
using RST.Core.Telemetry;
using Xunit;

namespace RST.Tests.Telemetry;

public sealed class ModelStatsGateTests
{
    private DateTimeOffset _now = new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);

    private ModelStatsGate NewGate() => new(clock: () => _now);

    /// <summary>What the collector's Idling handler does for one key.</summary>
    private static bool Emit(ModelStatsGate gate, string key) =>
        gate.CanEmit(key) && gate.TryAcquire(key);

    [Fact]
    public void Default_window_is_five_minutes()
    {
        ModelStatsGate.DefaultInterval.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void A_fresh_model_is_requested_and_pending_until_taken()
    {
        var gate = NewGate();
        gate.HasPending.Should().BeFalse();

        gate.Request("doc-a").Should().BeTrue();
        gate.HasPending.Should().BeTrue();

        gate.TakePending().Should().Equal("doc-a");
        gate.HasPending.Should().BeFalse("taking empties the set");
        gate.TakePending().Should().BeEmpty();
    }

    [Fact]
    public void Open_then_save_before_idling_is_one_capture()
    {
        var gate = NewGate();
        gate.Request("doc-a").Should().BeTrue("the open");
        gate.Request("doc-a").Should().BeFalse("the save coalesces into the pending capture");

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
        var gate = NewGate();
        gate.Request("doc-a");
        gate.TakePending();
        Emit(gate, "doc-a").Should().BeTrue();

        _now += TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59);
        gate.Request("doc-a").Should().BeFalse("a save 4:59 after the last record cannot emit");
        gate.HasPending.Should().BeFalse("no Idling work is queued for it");
    }

    [Fact]
    public void A_request_after_the_window_captures_again()
    {
        var gate = NewGate();
        gate.Request("doc-a");
        gate.TakePending();
        Emit(gate, "doc-a");

        _now += TimeSpan.FromMinutes(5);
        gate.Request("doc-a").Should().BeTrue();
        gate.TakePending();
        Emit(gate, "doc-a").Should().BeTrue();
    }

    [Fact]
    public void The_window_is_per_model()
    {
        var gate = NewGate();
        gate.Request("doc-a");
        gate.TakePending();
        Emit(gate, "doc-a");

        _now += TimeSpan.FromMinutes(1);
        gate.Request("doc-b").Should().BeTrue("another model's window does not apply");
    }

    [Fact]
    public void A_capture_that_never_emitted_does_not_open_a_window()
    {
        // The model closed before Idling (no document found), or collection
        // was switched off: nothing was acquired, so the next save captures.
        var gate = NewGate();
        gate.Request("doc-a");
        gate.TakePending();

        _now += TimeSpan.FromSeconds(30);
        gate.Request("doc-a").Should().BeTrue();
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
