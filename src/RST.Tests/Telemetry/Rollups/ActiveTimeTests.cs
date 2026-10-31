// ActiveTimeTests.cs — spec #24 AC 1: active time follows decision #46
// exactly. The pure rule (gap ≤ 10 min filled, > 10 min not, single pulse
// = 1 min, clipping) and the rule on replayed session files (clip at close,
// session_end, collection_disabled and recovery-written closes; local-midnight
// split; a gap never bridges a close).

using System;
using System.Linq;
using FluentAssertions;
using RST.Core.Telemetry;
using RST.Core.Telemetry.Rollups;
using Xunit;

namespace RST.Tests.Telemetry.Rollups;

public sealed class ActiveTimeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset M(double minutes) => T0.AddMinutes(minutes);

    private static double ActiveMinutes((DateTimeOffset, DateTimeOffset)[] intervals, params double[] pulseMinutes) =>
        ActiveTime.TotalMilliseconds(ActiveTime.Spans(intervals, pulseMinutes.Select(M))) / 60000.0;

    private static readonly (DateTimeOffset, DateTimeOffset)[] AllDay = { (M(-60), M(600)) };

    // ---- the pure rule ----------------------------------------------------

    [Fact]
    public void A_single_pulse_counts_one_minute()
    {
        ActiveMinutes(AllDay, 0).Should().Be(1);
    }

    [Fact]
    public void A_gap_of_exactly_ten_minutes_is_filled()
    {
        // [0, 10) by the gap + [10, 11) by the second pulse.
        ActiveMinutes(AllDay, 0, 10).Should().Be(11);
    }

    [Fact]
    public void A_gap_just_over_ten_minutes_is_not_filled()
    {
        ActiveMinutes(AllDay, 0, 10.01).Should().BeApproximately(2, 1e-9);
    }

    [Fact]
    public void A_run_of_pulses_every_minute_is_continuous_and_ends_one_minute_after_the_last()
    {
        ActiveMinutes(AllDay, Enumerable.Range(0, 30).Select(i => (double)i).ToArray()).Should().Be(30);
    }

    [Fact]
    public void Pulses_inside_one_minute_overlap_rather_than_add()
    {
        ActiveMinutes(AllDay, 0, 0.5).Should().Be(1.5);
    }

    [Fact]
    public void Spans_are_clipped_to_the_open_interval_and_pulses_outside_count_for_nothing()
    {
        var interval = new[] { (M(0), M(5.5)) };
        // The trailing minute after 5 is cut at the close; the pulse at 20 is outside.
        ActiveMinutes(interval, 5, 20).Should().Be(0.5);
    }

    [Fact]
    public void A_gap_never_bridges_two_open_intervals()
    {
        // Closed at 4, reopened at 6: the 4-minute gap between pulses 3 and 7 spans a close.
        var intervals = new[] { (M(0), M(4)), (M(6), M(30)) };
        ActiveMinutes(intervals, 3, 7).Should().Be(2);
    }

    [Fact]
    public void Overlapping_intervals_of_one_model_are_unioned()
    {
        var intervals = new[] { (M(0), M(60)), (M(10), M(40)) };
        ActiveMinutes(intervals, 15, 16).Should().Be(2);
    }

    [Fact]
    public void Active_time_splits_at_local_midnight()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Plus2", TimeSpan.FromHours(2), "Plus2", "Plus2");
        // 21:55 UTC = 23:55 local; pulses every minute to 22:05 UTC = 00:05 local.
        var start = new DateTimeOffset(2026, 10, 30, 21, 55, 0, TimeSpan.Zero);
        var spans = ActiveTime.Spans(new[] { (start.AddHours(-1), start.AddHours(1)) },
            Enumerable.Range(0, 10).Select(i => start.AddMinutes(i)));
        var days = ActiveTime.MillisecondsByDay(spans, zone);
        days[new DateOnly(2026, 10, 30)].Should().Be(5 * 60_000);
        days[new DateOnly(2026, 10, 31)].Should().Be(5 * 60_000);
    }

    // ---- on replayed session files ----------------------------------------

    private static ModelContribution Fold(SessionBuilder s, DocumentIdentity id, TimeZoneInfo? zone = null)
    {
        var events = s.ToList();
        var info = SessionInfo.Read(events, null);
        var contributions = ActivityFold.FoldSession(events, info, zone ?? TimeZoneInfo.Utc, DateTimeOffset.MaxValue);
        var key = ModelKey.From(id)!.Value;
        return contributions.Single(c => c.Key == key);
    }

    private static double ActiveMinutesOf(ModelContribution c) => c.Days.Sum(d => d.ActiveMs) / 60000.0;

    [Fact]
    public void Edit_idle_edit_counts_the_two_edit_runs_plus_nothing_for_the_idle_gap()
    {
        // The AC 7 scenario in miniature: edit 5 min, idle 15, edit 5 → 10 min active, not 25.
        var doc = SessionBuilder.Central("Tower");
        var s = new SessionBuilder().Start(M(-5)).Open(M(-1), doc, loadSeconds: 60)
            .Pulses(M(0), M(5), doc).Pulses(M(20), M(25), doc)
            .Close(M(40), doc).End(M(41));
        var c = Fold(s, doc);
        ActiveMinutesOf(c).Should().Be(10);
        c.Days.Single().OpenMs.Should().Be(40 * 60_000);
    }

    [Fact]
    public void A_model_open_but_untouched_contributes_no_active_time()
    {
        var doc = SessionBuilder.Local("Idle");
        var s = new SessionBuilder().Start(M(0)).Open(M(1), doc).Close(M(480), doc).End(M(481));
        var c = Fold(s, doc);
        c.Days.Single().ActiveMs.Should().Be(0);
        c.Days.Single().OpenMs.Should().BeGreaterThan(7 * 3_600_000);
    }

    [Fact]
    public void The_trailing_minute_is_clipped_at_the_close()
    {
        var doc = SessionBuilder.Local("A");
        var s = new SessionBuilder().Start(M(0)).Open(M(0), doc, 0)
            .Pulse(M(10), doc).Close(M(10.25), doc).End(M(11));
        ActiveMinutesOf(Fold(s, doc)).Should().Be(0.25);
    }

    [Fact]
    public void Active_time_is_clipped_at_session_end()
    {
        var doc = SessionBuilder.Local("A");
        var s = new SessionBuilder().Start(M(0)).Open(M(0), doc, 0)
            .Pulse(M(10), doc).End(M(10.5));
        ActiveMinutesOf(Fold(s, doc)).Should().Be(0.5);
    }

    [Fact]
    public void Active_time_is_clipped_at_collection_disabled_and_the_gap_is_never_bridged()
    {
        var doc = SessionBuilder.Local("A");
        // Off at 10.5; back on at 12; the pulse at 13 has no open interval to sit in
        // (nothing re-established the doc), so it counts for nothing.
        var s = new SessionBuilder().Start(M(0)).Open(M(0), doc, 0)
            .Pulse(M(10), doc).Disable(M(10.5)).Enable(M(12)).Pulse(M(13), doc).End(M(30));
        ActiveMinutesOf(Fold(s, doc)).Should().Be(0.5);
    }

    [Fact]
    public void Recovery_written_closes_clip_a_crashed_session()
    {
        var doc = SessionBuilder.Central("Crashed");
        var s = new SessionBuilder().Start(M(0)).Open(M(0), doc, 0)
            .Pulses(M(1), M(4), doc).Heartbeat(M(3.5))
            .RecoveryClose(M(3.5), doc).RecoveryEnd(M(3.5));
        // Pulses at 1, 2, 3 → [1, 4) but the session was truncated at its last event, 3.5.
        ActiveMinutesOf(Fold(s, doc)).Should().Be(2.5);
    }

    [Fact]
    public void A_gap_never_bridges_a_close_and_reopen_in_one_session()
    {
        var doc = SessionBuilder.Local("A");
        var s = new SessionBuilder().Start(M(0)).Open(M(0), doc, 0)
            .Pulse(M(3), doc).Close(M(4), doc)
            .Open(M(6), doc, 0).Pulse(M(7), doc).Close(M(30), doc).End(M(31));
        ActiveMinutesOf(Fold(s, doc)).Should().Be(2);
    }

    [Fact]
    public void Folded_active_time_splits_at_local_midnight()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Minus5", TimeSpan.FromHours(-5), "Minus5", "Minus5");
        var doc = SessionBuilder.Local("Late");
        // 04:50 UTC = 23:50 local on the 29th, edits to 05:10 UTC = 00:10 local on the 30th.
        var start = new DateTimeOffset(2026, 10, 30, 4, 50, 0, TimeSpan.Zero);
        var s = new SessionBuilder().Start(start.AddMinutes(-5)).Open(start.AddMinutes(-1), doc, 0)
            .Pulses(start, start.AddMinutes(20), doc).Close(start.AddMinutes(30), doc).End(start.AddMinutes(31));
        var c = Fold(s, doc, zone);
        c.Days.Select(d => d.Date).Should().Equal(new DateOnly(2026, 10, 29), new DateOnly(2026, 10, 30));
        c.Days[0].ActiveMs.Should().Be(10 * 60_000);
        c.Days[1].ActiveMs.Should().Be(10 * 60_000);
    }

    [Fact]
    public void Pulses_of_another_model_do_not_count()
    {
        var a = SessionBuilder.Central("A");
        var b = SessionBuilder.Central("B");
        var s = new SessionBuilder().Start(M(0)).Open(M(0), a, 0).Open(M(0), b, 0)
            .Pulses(M(1), M(6), b).Close(M(30), a).Close(M(30), b).End(M(31));
        ActiveMinutesOf(Fold(s, a)).Should().Be(0);
        ActiveMinutesOf(Fold(s, b)).Should().Be(5);
    }
}
