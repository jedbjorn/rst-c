// ViewActivationGateTests.cs — view_activated gating (Telemetry v1
// spec): emit only when the ACTIVE DOCUMENT changes, never on view
// switches within a document, capped at 1/min/doc so rapid A↔B
// flipping can't flood the outbox.

using System;
using FluentAssertions;
using RST.Core.Telemetry;
using Xunit;

namespace RST.Tests.Telemetry;

public sealed class ViewActivationGateTests
{
    private DateTimeOffset _now = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);

    private ViewActivationGate NewGate() => new(clock: () => _now);

    [Fact]
    public void First_activation_of_the_session_emits()
    {
        NewGate().ShouldEmit("doc-a").Should().BeTrue();
    }

    [Fact]
    public void View_switches_within_the_same_document_never_emit()
    {
        var gate = NewGate();
        gate.ShouldEmit("doc-a");

        // Plenty of time passes — but the active document never changed,
        // so a plain per-key throttle would wrongly emit here.
        _now += TimeSpan.FromMinutes(10);
        gate.ShouldEmit("doc-a").Should().BeFalse();
    }

    [Fact]
    public void Switching_documents_emits_for_the_newly_active_one()
    {
        var gate = NewGate();
        gate.ShouldEmit("doc-a").Should().BeTrue();
        gate.ShouldEmit("doc-b").Should().BeTrue();
    }

    [Fact]
    public void Rapid_flipping_is_capped_per_document()
    {
        var gate = NewGate();
        gate.ShouldEmit("doc-a").Should().BeTrue();
        _now += TimeSpan.FromSeconds(10);
        gate.ShouldEmit("doc-b").Should().BeTrue();
        _now += TimeSpan.FromSeconds(10);
        gate.ShouldEmit("doc-a").Should().BeFalse("doc-a emitted 20 s ago");
        _now += TimeSpan.FromSeconds(10);
        gate.ShouldEmit("doc-b").Should().BeFalse("doc-b emitted 20 s ago");
    }

    [Fact]
    public void Suppressed_switch_still_counts_as_the_active_document()
    {
        // A→B(suppressed)→A: the gate must know B became active, or the
        // return to A would be read as "no change" and go silent.
        var gate = NewGate();
        gate.ShouldEmit("doc-a").Should().BeTrue();
        _now += TimeSpan.FromSeconds(10);
        gate.ShouldEmit("doc-b").Should().BeTrue();
        _now += TimeSpan.FromSeconds(10);
        gate.ShouldEmit("doc-a").Should().BeFalse("inside doc-a's window");

        _now += TimeSpan.FromSeconds(45); // t=65 s: doc-a's minute is over, doc-b's is not
        gate.ShouldEmit("doc-b").Should().BeFalse("doc-b emitted 55 s ago — inside its window");
        gate.ShouldEmit("doc-a").Should().BeTrue("changed back after doc-a's window");
    }

    [Fact]
    public void Reactivation_after_the_window_emits()
    {
        var gate = NewGate();
        gate.ShouldEmit("doc-a").Should().BeTrue();
        gate.ShouldEmit("doc-b").Should().BeTrue();

        _now += TimeSpan.FromMinutes(1);
        gate.ShouldEmit("doc-a").Should().BeTrue();
    }
}
