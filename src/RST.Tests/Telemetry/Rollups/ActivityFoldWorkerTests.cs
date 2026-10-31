// ActivityFoldWorkerTests.cs — spec #24 Tray Worker (F19 #24 WU3): when the tray folds. The schedule (first pass
// 1–3 min after start, then 15 min after each pass ends), one pass at a time, request-a-pass-now coalescing (spec #25:
// the Health page opening), the once-a-day age backstop, the one log line per pass, a clean stop, and that nothing
// the pass or a listener does escapes the worker. A ManualTimeProvider drives hours of schedule in milliseconds; a
// fake pass stands in for the engine, and two tests run the real engine over a temp store (fold, seal, clean up; a
// second tray holding the lock).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using RST.Core.Telemetry;
using RST.Core.Telemetry.Rollups;
using RST.Tests.Updates;
using Xunit;

namespace RST.Tests.Telemetry.Rollups;

public sealed class ActivityFoldWorkerTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly ManualTimeProvider _time = new(Start);
    private readonly FakePass _pass = new();
    private readonly ConcurrentQueue<string> _info = new();
    private readonly ConcurrentQueue<string> _warn = new();
    private readonly BlockingCollection<FoldPassCompletedEventArgs> _completed = new();
    private readonly List<ActivityFoldWorker> _workers = new();

    public void Dispose()
    {
        _pass.Release();
        foreach (var w in _workers) w.Dispose();
    }

    private ActivityFoldWorker Worker(
        TimeSpan? firstDelay = null,
        Func<int>? backstop = null,
        ActivityFoldWorker.PassRunner? runner = null,
        TimeSpan? stepPause = null,
        Action<string>? info = null)
    {
        var w = new ActivityFoldWorker(
            runner ?? _pass.Run,
            backstop,
            _time,
            new ActivityFoldWorkerOptions
            {
                FirstDelay = () => firstDelay ?? TimeSpan.FromMinutes(2),
                StepPause = stepPause ?? TimeSpan.Zero,
                StopWait = TimeSpan.FromSeconds(2),
            },
            info ?? (m => _info.Enqueue(m)),
            m => _warn.Enqueue(m));
        w.PassCompleted += (_, e) => _completed.Add(e);
        _workers.Add(w);
        return w;
    }

    private FoldPassCompletedEventArgs NextPass()
    {
        _completed.TryTake(out var e, Wait).Should().BeTrue("a pass should have completed");
        return e!;
    }

    /// <summary>No pass completes within a short real-time window (the fake timers fire synchronously, so a pass that
    /// was due has already been signalled by now).</summary>
    private void NoPass()
    {
        _completed.TryTake(out var e, TimeSpan.FromMilliseconds(150)).Should().BeFalse("no pass was due, but one ran: " + e?.Trigger);
    }

    // ---- the schedule ------------------------------------------------------------------------------------------

    [Fact]
    public void The_first_pass_runs_at_the_start_slot_then_every_15_minutes_after_the_previous_one_ended()
    {
        var w = Worker(firstDelay: TimeSpan.FromMinutes(2));
        w.Start();
        w.NextDueUtc.Should().Be(Start + TimeSpan.FromMinutes(2));

        _time.Advance(TimeSpan.FromMinutes(2) - TimeSpan.FromSeconds(1));
        NoPass();
        _pass.Calls.Should().Be(0);

        _time.Advance(TimeSpan.FromSeconds(1));
        var first = NextPass();
        first.Trigger.Should().Be(FoldTrigger.Scheduled);
        first.EndedUtc.Should().Be(Start + TimeSpan.FromMinutes(2));
        w.LastPassEndedUtc.Should().Be(first.EndedUtc);
        w.NextDueUtc.Should().Be(first.EndedUtc + TimeSpan.FromMinutes(15));

        _time.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1));
        NoPass();
        _time.Advance(TimeSpan.FromSeconds(1));
        NextPass().Trigger.Should().Be(FoldTrigger.Scheduled);

        // Two hours later: one pass per slot, never a burst.
        for (var i = 0; i < 8; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(15));
            NextPass();
        }
        NoPass();
        _pass.Calls.Should().Be(10);
        _pass.MaxConcurrent.Should().Be(1);
    }

    [Fact]
    public void The_default_start_slot_is_between_one_and_three_minutes_after_start()
    {
        for (var i = 0; i < 20; i++)
        {
            using var w = new ActivityFoldWorker(_pass.Run, null, _time);
            w.Start();
            w.NextDueUtc.Should().BeOnOrAfter(Start + TimeSpan.FromMinutes(1)).And.BeOnOrBefore(Start + TimeSpan.FromMinutes(3));
        }
        var defaults = new ActivityFoldWorkerOptions();
        defaults.Interval.Should().Be(TimeSpan.FromMinutes(15));
        defaults.StepPause.Should().Be(TimeSpan.FromSeconds(1));
        defaults.RequestMinGap.Should().Be(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void A_pass_runs_on_one_background_worker_thread_never_the_callers()
    {
        var w = Worker();
        w.Start();
        _time.Advance(TimeSpan.FromMinutes(2));
        NextPass();
        w.RequestPass(); // within a minute: nothing
        _time.Advance(TimeSpan.FromMinutes(15));
        NextPass();

        _pass.Threads.Should().HaveCount(2);
        _pass.Threads.Select(t => t.ManagedThreadId).Distinct().Should().ContainSingle("every pass runs on the worker's one thread");
        _pass.Threads.Should().OnlyContain(t => t.ManagedThreadId != Environment.CurrentManagedThreadId && t.IsBackground && t.Name == "RST activity fold");
        if (OperatingSystem.IsWindows())
            _pass.Threads.Should().OnlyContain(t => t.Priority == ThreadPriority.BelowNormal);
    }

    [Fact]
    public void A_slot_that_comes_due_while_a_pass_runs_starts_no_second_pass()
    {
        var w = Worker();
        w.Start();
        _pass.Block();
        _time.Advance(TimeSpan.FromMinutes(2));
        _pass.WaitEntered(Wait).Should().BeTrue();
        w.IsRunning.Should().BeTrue();
        w.NextDueUtc.Should().BeNull("no slot is armed while a pass runs");

        _time.Advance(TimeSpan.FromHours(1)); // a long pass
        w.RequestPass().Should().Be(FoldRequestOutcome.AlreadyRunning);
        _pass.Calls.Should().Be(1);

        _pass.Release();
        var done = NextPass();
        w.IsRunning.Should().BeFalse();
        w.NextDueUtc.Should().Be(done.EndedUtc + TimeSpan.FromMinutes(15), "the next slot counts from the end of the pass");
        NoPass();
        _pass.MaxConcurrent.Should().Be(1);
    }

    [Fact]
    public void A_clock_set_back_does_not_strand_the_next_slot()
    {
        var w = Worker();
        w.Start();
        _time.Advance(TimeSpan.FromMinutes(2));
        NextPass();

        // The wall clock goes back a day; the (relative) timer still fires 15 minutes later and the pass runs.
        _time.JumpClock(TimeSpan.FromDays(-1));
        _time.Advance(TimeSpan.FromMinutes(15));
        NextPass().Trigger.Should().Be(FoldTrigger.Scheduled);
    }

    // ---- request a pass now (spec #25: the Health page opens) ---------------------------------------------------

    [Fact]
    public void A_request_starts_a_pass_now_unless_one_ended_within_the_last_minute()
    {
        var w = Worker(firstDelay: TimeSpan.FromMinutes(3));
        w.RequestPass().Should().Be(FoldRequestOutcome.Stopped, "not started yet");
        w.Start();

        w.RequestPass().Should().Be(FoldRequestOutcome.Queued);
        var requested = NextPass();
        requested.Trigger.Should().Be(FoldTrigger.Requested);
        w.NextDueUtc.Should().Be(requested.EndedUtc + TimeSpan.FromMinutes(15), "a requested pass replaces the start slot");

        _time.Advance(TimeSpan.FromSeconds(59));
        w.RequestPass().Should().Be(FoldRequestOutcome.RanRecently);
        NoPass();

        _time.Advance(TimeSpan.FromSeconds(1));
        w.RequestPass().Should().Be(FoldRequestOutcome.Queued);
        NextPass().Trigger.Should().Be(FoldTrigger.Requested);

        // The start slot (3 min) never ran on its own: the requested passes took its place.
        _time.Advance(TimeSpan.FromMinutes(14));
        NoPass();
        _pass.Calls.Should().Be(2);
    }

    [Fact]
    public void Requests_from_many_threads_at_once_coalesce_into_one_pass()
    {
        var w = Worker();
        w.Start();
        _pass.Block();

        var outcomes = new ConcurrentBag<FoldRequestOutcome>();
        Parallel.For(0, 64, _ => outcomes.Add(w.RequestPass()));
        _pass.WaitEntered(Wait).Should().BeTrue();
        Parallel.For(0, 64, _ => outcomes.Add(w.RequestPass()));
        _pass.Release();
        NextPass();
        Parallel.For(0, 64, _ => outcomes.Add(w.RequestPass()));
        NoPass();

        _pass.Calls.Should().Be(1);
        _pass.MaxConcurrent.Should().Be(1);
        outcomes.Should().Contain(FoldRequestOutcome.Queued)
            .And.Contain(FoldRequestOutcome.AlreadyRunning)
            .And.Contain(FoldRequestOutcome.RanRecently)
            .And.NotContain(FoldRequestOutcome.Stopped);
    }

    [Fact]
    public void Pass_completed_reports_every_pass_with_whether_the_store_changed()
    {
        var w = Worker();
        w.Start();
        _pass.Results.Enqueue(new FoldPassResult(false, false, 2, 0, 0, 0));
        _pass.Results.Enqueue(new FoldPassResult(false, true, 0, 0, 0, 0));
        _pass.Results.Enqueue(new FoldPassResult(true, false, 0, 0, 0, 0));

        w.RequestPass();
        var folded = NextPass();
        folded.Result.Folded.Should().Be(2);
        folded.StoreChanged.Should().BeTrue();

        _time.Advance(TimeSpan.FromMinutes(15));
        NextPass().StoreChanged.Should().BeFalse("a quiet pass changed nothing");
        _time.Advance(TimeSpan.FromMinutes(15));
        NextPass().StoreChanged.Should().BeFalse("a pass skipped for the lock changed nothing");
    }

    // ---- never throws, never stops ------------------------------------------------------------------------------

    [Fact]
    public void A_pass_that_throws_is_logged_reported_as_failed_and_retried_at_the_next_slot()
    {
        var w = Worker();
        w.Start();
        _pass.ThrowNext = true;

        _time.Advance(TimeSpan.FromMinutes(2));
        var failed = NextPass();
        failed.Result.Failed.Should().Be(1);
        _warn.Should().Contain(m => m.Contains("pass failed (retried next pass)") && m.Contains("fake engine broke"));
        w.NextDueUtc.Should().Be(failed.EndedUtc + TimeSpan.FromMinutes(15));

        _time.Advance(TimeSpan.FromMinutes(15));
        NextPass().Result.Failed.Should().Be(0);
    }

    [Fact]
    public void A_listener_that_throws_and_a_log_that_throws_take_nothing_down()
    {
        var w = Worker(info: _ => throw new IOException("log disk full"));
        w.PassCompleted += (_, _) => throw new InvalidOperationException("the page is gone");
        w.Start();

        w.RequestPass().Should().Be(FoldRequestOutcome.Queued);
        NextPass();

        _time.Advance(TimeSpan.FromMinutes(15));
        NextPass();
        _pass.Calls.Should().Be(2);
        _warn.Should().Contain(m => m.Contains("pass-completed listener failed") && m.Contains("the page is gone"));
    }

    [Fact]
    public void Dispose_stops_a_running_pass_between_steps_without_waiting_for_the_pause()
    {
        var steps = 0;
        var cancelled = new ManualResetEventSlim();
        var entered = new ManualResetEventSlim();
        FoldPassResult Endless(Action betweenSteps, CancellationToken cancel)
        {
            entered.Set();
            while (!cancel.IsCancellationRequested)
            {
                Interlocked.Increment(ref steps);
                betweenSteps(); // a 30 s pause, cut short by Dispose
            }
            cancelled.Set();
            return new FoldPassResult(false, false, steps, 0, 0, 0);
        }

        var w = Worker(runner: Endless, stepPause: TimeSpan.FromSeconds(30));
        w.Start();
        w.RequestPass();
        entered.Wait(Wait).Should().BeTrue();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        w.Dispose();
        watch.Stop();

        cancelled.IsSet.Should().BeTrue("the pass saw the cancellation before Dispose returned");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        steps.Should().Be(1, "the pause after the first step was cut short, no second step started");
        w.RequestPass().Should().Be(FoldRequestOutcome.Stopped);
        w.NextDueUtc.Should().BeNull();
        w.Dispose(); // idempotent
    }

    [Fact]
    public void Dispose_with_a_stuck_step_waits_only_the_stop_wait()
    {
        var w = Worker();
        w.Start();
        _pass.Block();
        w.RequestPass();
        _pass.WaitEntered(Wait).Should().BeTrue();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        w.Dispose();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        _warn.Should().Contain(m => m.Contains("still running after 2 s"));
        _pass.Release();
    }

    // ---- the age backstop ---------------------------------------------------------------------------------------

    [Fact]
    public void The_age_backstop_runs_after_the_first_pass_then_at_most_once_a_day_and_never_when_the_lock_was_held()
    {
        var prunes = 0;
        var w = Worker(backstop: () => { prunes++; return 4; });
        w.Start();

        _pass.Results.Enqueue(new FoldPassResult(true, false, 0, 0, 0, 0)); // another tray holds the lock
        _time.Advance(TimeSpan.FromMinutes(2));
        NextPass().Pruned.Should().Be(0);
        prunes.Should().Be(0, "a pass skipped for the lock prunes nothing either");

        _time.Advance(TimeSpan.FromMinutes(15));
        NextPass().Pruned.Should().Be(4);
        prunes.Should().Be(1);

        for (var i = 0; i < 4 * 24 - 1; i++) // the rest of the day
        {
            _time.Advance(TimeSpan.FromMinutes(15));
            NextPass().Pruned.Should().Be(0);
        }
        prunes.Should().Be(1, "quiet passes stay a listing; the backstop is daily");

        _time.Advance(TimeSpan.FromMinutes(15));
        NextPass().Pruned.Should().Be(4);
        prunes.Should().Be(2);
        _info.Should().Contain(m => m.Contains("4 pruned by age"));
    }

    [Fact]
    public void A_backstop_that_throws_is_logged_and_tried_again_next_pass()
    {
        var calls = 0;
        var w = Worker(backstop: () => ++calls == 1 ? throw new IOException("outbox unreadable") : 0);
        w.Start();
        w.RequestPass();
        NextPass();
        _warn.Should().Contain(m => m.Contains("age backstop failed") && m.Contains("outbox unreadable"));

        _time.Advance(TimeSpan.FromMinutes(15));
        NextPass();
        calls.Should().Be(2);
    }

    // ---- the log line --------------------------------------------------------------------------------------------

    [Fact]
    public void Each_pass_logs_one_line_with_its_counts_and_duration()
    {
        ActivityFoldWorker.Describe(FoldTrigger.Scheduled, new FoldPassResult(false, false, 3, 1, 2, 2) { SkippedFiles = 1 }, 0, TimeSpan.FromSeconds(3.24))
            .Should().Be("activity fold pass (scheduled): 3 folded, 2 sealed, 2 deleted, 1 skipped, 1 failed in 3.2 s");
        ActivityFoldWorker.Describe(FoldTrigger.Requested, new FoldPassResult(false, true, 0, 0, 0, 0), 0, TimeSpan.FromMilliseconds(12))
            .Should().Be("activity fold pass (requested): nothing changed (12 ms)");
        ActivityFoldWorker.Describe(FoldTrigger.Scheduled, new FoldPassResult(true, false, 0, 0, 0, 0), 0, TimeSpan.FromMilliseconds(3))
            .Should().Be("activity fold pass (scheduled): skipped, another RST tray holds the fold lock (3 ms)");
        ActivityFoldWorker.Describe(FoldTrigger.Scheduled, new FoldPassResult(false, false, 0, 0, 1, 0), 2, TimeSpan.FromMilliseconds(40))
            .Should().Be("activity fold pass (scheduled): 0 folded, 1 sealed, 0 deleted, 0 skipped, 0 failed, 2 pruned by age in 40 ms");

        var w = Worker();
        w.Start();
        _time.Advance(TimeSpan.FromMinutes(2));
        NextPass();
        _info.Count(m => m.StartsWith("activity fold pass", StringComparison.Ordinal)).Should().Be(1);
        _info.Should().Contain(m => m.StartsWith("activity fold: first pass at", StringComparison.Ordinal));
    }

    // ---- the real engine -----------------------------------------------------------------------------------------

    private static DateTimeOffset D(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void Over_the_real_engine_the_worker_folds_then_seals_then_cleans_up_and_leaves_live_files_alone()
    {
        using var f = new RollupStoreFixture(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
        var doc = SessionBuilder.Central("Tower");
        var closed = Enumerable.Range(20, 5).Select(day =>
            new SessionBuilder().Start(D(day, 8)).Open(D(day, 9), doc).Pulses(D(day, 10), D(day, 10, 10), doc)
                .Close(D(day, 12), doc).End(D(day, 12, 1)).Write(f.Outbox, D(day, 13))).ToArray();
        // Collection was switched off in this one: it is already recorded, so it still folds.
        var disabled = new SessionBuilder().Start(D(25, 8)).Open(D(25, 9), doc).Pulse(D(25, 10), doc).Disable(D(25, 11))
            .End(D(25, 12)).Write(f.Outbox, D(25, 13));
        var live = new SessionBuilder().Start(D(31, 8)).Open(D(31, 9), doc).Pulse(D(31, 10), doc).Write(f.Outbox);
        f.LiveFiles.Add(live);

        var w = Worker(runner: (between, cancel) => f.Engine.RunPass(between, cancel));
        w.Start();

        _time.Advance(TimeSpan.FromMinutes(2));
        var folded = NextPass().Result;
        folded.Folded.Should().Be(6);
        folded.SkippedFiles.Should().Be(1, "the live file");
        File.Exists(f.ModelPath(doc)).Should().BeTrue();
        _info.Should().Contain("activity fold pass (scheduled): 6 folded, 0 sealed, 0 deleted, 1 skipped, 0 failed in 0 ms");

        _time.Advance(TimeSpan.FromMinutes(15));
        NextPass().Result.Sealed.Should().Be(6);
        f.OutboxNames().Should().HaveCount(7);

        _time.Advance(TimeSpan.FromMinutes(15));
        NextPass().Result.Deleted.Should().Be(3, "sealed in both copies, beyond the newest 3");
        closed.Take(3).Should().OnlyContain(p => !File.Exists(p));
        closed.Skip(3).Should().OnlyContain(p => File.Exists(p));
        File.Exists(disabled).Should().BeTrue();
        File.Exists(live).Should().BeTrue();
        f.Rollup(doc)!.Days.Sum(d => d.ActiveMs).Should().Be(5 * 10 * 60_000 + 60_000);
    }

    [Fact]
    public void A_second_tray_holding_the_fold_lock_makes_this_one_skip_the_pass()
    {
        using var f = new RollupStoreFixture(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
        var doc = SessionBuilder.Central("Tower");
        new SessionBuilder().Start(D(20, 8)).Open(D(20, 9), doc).Pulse(D(20, 10), doc).Close(D(20, 12), doc)
            .End(D(20, 12, 1)).Write(f.Outbox, D(20, 13));
        var prunes = 0;
        var w = Worker(runner: (between, cancel) => f.Engine.RunPass(between, cancel), backstop: () => ++prunes);
        w.Start();

        Directory.CreateDirectory(f.Rollups);
        using (var otherTray = new FileStream(Path.Combine(f.Rollups, RollupStore.LockFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            _time.Advance(TimeSpan.FromMinutes(2));
            NextPass().Result.Skipped.Should().BeTrue();
        }
        _info.Should().Contain(m => m.Contains("skipped, another RST tray holds the fold lock"));
        File.Exists(f.ModelPath(doc)).Should().BeFalse("nothing was written alongside the other tray");
        prunes.Should().Be(0);

        _time.Advance(TimeSpan.FromMinutes(15));
        NextPass().Result.Folded.Should().Be(1, "the lock is free again: the next pass folds");
        prunes.Should().Be(1);
    }

    // ---- the fake engine -------------------------------------------------------------------------------------------

    private sealed class FakePass
    {
        private readonly ManualResetEventSlim _open = new(true);
        private readonly SemaphoreSlim _entered = new(0);
        private int _calls;
        private int _inside;
        private int _maxInside;

        public ConcurrentQueue<FoldPassResult> Results { get; } = new();
        public ConcurrentQueue<Thread> ThreadsSeen { get; } = new();
        public IReadOnlyList<Thread> Threads => ThreadsSeen.ToList();
        public volatile bool ThrowNext;

        public int Calls => Volatile.Read(ref _calls);
        public int MaxConcurrent => Volatile.Read(ref _maxInside);

        public void Block() => _open.Reset();
        public void Release() => _open.Set();
        public bool WaitEntered(TimeSpan timeout) => _entered.Wait(timeout);

        public FoldPassResult Run(Action betweenSteps, CancellationToken cancel)
        {
            Interlocked.Increment(ref _calls);
            var inside = Interlocked.Increment(ref _inside);
            int max;
            while ((max = Volatile.Read(ref _maxInside)) < inside && Interlocked.CompareExchange(ref _maxInside, inside, max) != max) { }
            ThreadsSeen.Enqueue(Thread.CurrentThread);
            try
            {
                _entered.Release();
                _open.Wait(TimeSpan.FromSeconds(30));
                betweenSteps();
                if (ThrowNext)
                {
                    ThrowNext = false;
                    throw new InvalidOperationException("fake engine broke");
                }
                return Results.TryDequeue(out var r) ? r : new FoldPassResult(false, false, 1, 0, 0, 0);
            }
            finally
            {
                Interlocked.Decrement(ref _inside);
            }
        }
    }
}
