// PulseThrottleTests.cs — the ≤1/doc/min cap behind doc_changed_pulse
// (Telemetry v1 spec, Build Plan step 2): per-key windows, window
// anchoring to the last EMITTED pulse, and clock-driven expiry.

using System;
using FluentAssertions;
using RST.Core.Telemetry;
using Xunit;

namespace RST.Tests.Telemetry;

public sealed class PulseThrottleTests
{
    private DateTimeOffset _now = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);

    private PulseThrottle NewThrottle(TimeSpan? interval = null) =>
        new(interval, () => _now);

    [Fact]
    public void First_pulse_for_a_key_emits()
    {
        NewThrottle().TryAcquire("doc-a").Should().BeTrue();
    }

    [Fact]
    public void Second_pulse_inside_the_interval_is_suppressed()
    {
        var throttle = NewThrottle();
        throttle.TryAcquire("doc-a");

        _now += TimeSpan.FromSeconds(59);
        throttle.TryAcquire("doc-a").Should().BeFalse();
    }

    [Fact]
    public void Pulse_after_the_interval_emits_again()
    {
        var throttle = NewThrottle();
        throttle.TryAcquire("doc-a");

        _now += TimeSpan.FromSeconds(60);
        throttle.TryAcquire("doc-a").Should().BeTrue();
    }

    [Fact]
    public void Suppressed_attempts_do_not_push_the_window()
    {
        // A steady transaction stream (one every 10 s) must still emit
        // once per minute — a throttle that re-anchors on every ATTEMPT
        // would go silent forever after the first pulse.
        var throttle = NewThrottle();
        var emitted = 0;
        for (var tick = 0; tick < 36; tick++) // 6 minutes of activity
        {
            if (throttle.TryAcquire("doc-a")) emitted++;
            _now += TimeSpan.FromSeconds(10);
        }
        emitted.Should().Be(6);
    }

    [Fact]
    public void Keys_throttle_independently()
    {
        var throttle = NewThrottle();
        throttle.TryAcquire("doc-a").Should().BeTrue();
        throttle.TryAcquire("doc-b").Should().BeTrue("another document has its own window");
        throttle.TryAcquire("doc-a").Should().BeFalse();
    }

    [Fact]
    public void CanAcquire_peeks_without_recording()
    {
        // The hot-path peek (SC-030): a peek must never start a window,
        // and must mirror what TryAcquire would answer.
        var throttle = NewThrottle();
        throttle.CanAcquire("doc-a").Should().BeTrue();
        throttle.CanAcquire("doc-a").Should().BeTrue("a peek must not record an acquisition");
        throttle.TryAcquire("doc-a").Should().BeTrue();

        _now += TimeSpan.FromSeconds(59);
        throttle.CanAcquire("doc-a").Should().BeFalse("the peek mirrors TryAcquire inside the window");

        _now += TimeSpan.FromSeconds(1);
        throttle.CanAcquire("doc-a").Should().BeTrue();
        throttle.TryAcquire("doc-a").Should().BeTrue();
    }

    [Fact]
    public void Custom_interval_is_honored()
    {
        var throttle = NewThrottle(TimeSpan.FromSeconds(5));
        throttle.TryAcquire("doc-a");

        _now += TimeSpan.FromSeconds(4);
        throttle.TryAcquire("doc-a").Should().BeFalse();

        _now += TimeSpan.FromSeconds(1);
        throttle.TryAcquire("doc-a").Should().BeTrue();
    }
}
