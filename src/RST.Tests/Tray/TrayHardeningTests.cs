// rst-c's own. SC-046: the tray's start runs its steps so a failed one leaves the tray up without that feature
// (T2), the same UI exception coming back too often is counted (T7), and work started and not awaited is observed
// and logged (TaskGuard, T3/R5).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using RST.Core.Logging;
using RST.Core.Tray;
using RST.Tests.Logging;
using Xunit;

namespace RST.Tests.Tray;

// Swaps the process-wide Log.Logger: runs alone, as the log facade's own tests do.
[Collection(LogFacadeTests.Collection)]
public sealed class TrayHardeningTests
{
    private static List<LogEvent> CaptureLog(Action action)
    {
        var events = new List<LogEvent>();
        var previous = Log.Logger;
        Log.Logger = new Logger(LogLevel.Verbose, new ListSink(events));
        try
        {
            action();
        }
        finally
        {
            Log.Logger = previous;
        }
        return events;
    }

    private sealed class ListSink(List<LogEvent> events) : ILogSink
    {
        public void Emit(LogEvent logEvent, IReadOnlyDictionary<string, object?> properties)
        {
            lock (events) events.Add(logEvent);
        }

        public void Dispose()
        {
        }
    }

    // ---- T2: the start's steps ----------------------------------------------------------------

    [Fact]
    public void A_failed_step_is_logged_and_the_steps_after_it_still_run()
    {
        var ran = new List<string>();
        string? failed = "not run";
        var boom = new InvalidOperationException("no settings file");
        var events = CaptureLog(() => failed = TrayStartup.Run(
        [
            new("icon", () => ran.Add("icon"), Essential: true),
            new("show signal", () => ran.Add("show signal")),
            new("settings and theme", () => throw boom),
            new("update checks", () => ran.Add("update checks")),
            new("window", () => ran.Add("window")),
        ]));

        failed.Should().BeNull("the tray is up, without its settings");
        ran.Should().Equal("icon", "show signal", "update checks", "window");
        var line = events.Should().ContainSingle(e => e.Level >= LogLevel.Error).Subject;
        line.Level.Should().Be(LogLevel.Error);
        line.MessageTemplate.Should().Be(TrayStartup.DegradedTemplate);
        line.PropertyValues[0].Should().Be("settings and theme");
        line.Exception.Should().BeSameAs(boom);
    }

    [Fact]
    public void A_failed_essential_step_stops_the_start_and_is_named()
    {
        var ran = new List<string>();
        string? failed = null;
        var events = CaptureLog(() => failed = TrayStartup.Run(
        [
            new("icon", () => throw new InvalidOperationException("no shell"), Essential: true),
            new("show signal", () => ran.Add("show signal")),
        ]));

        failed.Should().Be("icon");
        ran.Should().BeEmpty("nothing runs after a step the tray cannot run without");
        events.Should().ContainSingle(e => e.Level == LogLevel.Fatal && e.MessageTemplate == TrayStartup.FatalTemplate);
        TrayStartup.FatalExitCode.Should().NotBe(0);
    }

    [Fact]
    public void Every_step_succeeding_reports_nothing()
    {
        var events = CaptureLog(() => TrayStartup.Run([new("icon", () => { }, Essential: true), new("window", () => { })]).Should().BeNull());
        events.Should().NotContain(e => e.Level >= LogLevel.Warning);
    }

    // ---- T7: a UI exception that keeps coming back --------------------------------------------

    private static Exception Thrown(string message)
    {
        try
        {
            throw new InvalidOperationException(message);
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }

    [Fact]
    public void The_same_exception_more_than_the_limit_within_the_window_trips()
    {
        var now = new DateTimeOffset(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);
        var repeats = new RepeatingFault(limit: 3, window: TimeSpan.FromSeconds(5), () => now);
        var ex = Thrown("layout failed");
        Enumerable.Range(0, 3).Select(_ => repeats.Record(ex)).ToList().Should().AllBeEquivalentTo(false);
        repeats.Record(ex).Should().BeTrue("the fourth within 5 s");
    }

    [Fact]
    public void Repeats_spread_wider_than_the_window_or_a_different_exception_do_not_trip()
    {
        var now = new DateTimeOffset(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);
        var repeats = new RepeatingFault(limit: 3, window: TimeSpan.FromSeconds(5), () => now);
        var ex = Thrown("a click failed");
        for (var i = 0; i < 20; i++)
        {
            repeats.Record(ex).Should().BeFalse("one every 2 s is at most 3 in any 5 s");
            now += TimeSpan.FromSeconds(2);
        }

        var other = Thrown("something else");
        for (var i = 0; i < 10; i++)
        {
            repeats.Record(i % 2 == 0 ? ex : other).Should().BeFalse("alternating exceptions start the count again");
        }
    }

    [Fact]
    public void The_tray_limits_are_far_above_what_clicks_can_produce()
    {
        RepeatingFault.TrayLimit.Should().BeGreaterThanOrEqualTo(20);
        RepeatingFault.TrayPeriod.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(10));
    }

    // ---- TaskGuard: work nobody awaits ---------------------------------------------------------

    [Fact]
    public void A_faulted_task_that_nobody_awaits_is_logged_with_its_site()
    {
        var boom = new InvalidOperationException("the check failed");
        var events = CaptureLog(() =>
        {
            var source = new TaskCompletionSource();
            source.Task.Forget("tray: check now");
            source.SetException(boom); // fails after Forget, as a running task would
        });

        var line = events.Should().ContainSingle(e => e.Level == LogLevel.Error).Subject;
        line.MessageTemplate.Should().Be(TaskGuard.FaultTemplate);
        line.PropertyValues[0].Should().Be("tray: check now");
        line.Exception.Should().BeSameAs(boom);
    }

    [Fact]
    public void A_task_already_failed_or_cancelled_or_null_is_handled_at_once()
    {
        var events = CaptureLog(() =>
        {
            Task.FromException(new InvalidOperationException("done")).Forget("failed");
            Task.FromCanceled(new System.Threading.CancellationToken(true)).Forget("cancelled");
            Task.CompletedTask.Forget("fine");
            ((Task?)null).Forget("none");
        });

        events.Should().ContainSingle(e => e.Level == LogLevel.Error).Which.PropertyValues[0].Should().Be("failed");
        events.Should().ContainSingle(e => e.Level == LogLevel.Debug).Which.PropertyValues[0].Should().Be("cancelled");
    }
}
