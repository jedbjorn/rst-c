// rst-c's own (not vendored): WHEN the RST tray checks for updates (spec #20 §4; F17 WU3).
//
//   - automatically: once after the tray starts, after a random 2–10 minute delay (so a hundred PCs signing in at
//     9 am do not all ask GitHub in the same second), then every 24 hours;
//   - when the user clicks Check now, whatever the setting.
//
// Before each AUTOMATIC run the loop asks automaticChecksOn() (the user's setting and the UpdateChecks preset, read
// fresh). When it says no, or throws, nothing runs: no request is made, and the next 24-hour slot is armed. A
// failed run is the check's own business (it logs once); the loop never retries early, so a failure costs one
// try a day, never a nag.
//
// One timer, re-armed for the next slot: no polling. Time comes from a TimeProvider, so RST.Tests and
// RuntimeTests run 48 simulated hours in milliseconds. Windows timers pause while a PC sleeps; Resync() (the
// tray calls it on resume) catches up a slot that came due during sleep. One run at a time: a Check now during
// a running check joins it, and a Check now within CheckNowMinGap (30 s) of the last run's end makes no request
// (the pages already show that run's result), so repeated clicks never become repeated requests.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace RST.Core.Updates;

/// <summary>Why a check runs.</summary>
public enum UpdateCheckTrigger
{
    /// <summary>The 2–10 minute start slot or a 24-hour slot.</summary>
    Automatic,

    /// <summary>The user clicked Check now (or the menu's Check for updates).</summary>
    User,
}

public sealed class UpdateCheckLoop : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    public static readonly TimeSpan FirstDelayMin = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan FirstDelayMax = TimeSpan.FromMinutes(10);

    /// <summary>A Check now this soon after the last run ended shows that run's result instead of asking again.</summary>
    public static readonly TimeSpan CheckNowMinGap = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time;
    private readonly Func<bool> _automaticChecksOn;
    private readonly Func<UpdateCheckTrigger, CancellationToken, Task> _check;
    private readonly Func<TimeSpan> _firstDelay;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private ITimer? _timer;
    private Task? _running;
    private DateTimeOffset? _nextDue;
    private DateTimeOffset? _lastRunEnded;
    private bool _offLogged;
    private bool _disposed;

    /// <param name="time">The clock and timers (TimeProvider.System in the tray).</param>
    /// <param name="automaticChecksOn">Whether automatic checks may run now. Asked before every automatic run.</param>
    /// <param name="check">The check itself. It should not throw; if it does, the loop logs it.</param>
    /// <param name="firstDelay">The start delay; default a uniform random 2–10 minutes.</param>
    /// <param name="log">Where the loop's own lines go.</param>
    public UpdateCheckLoop(
        TimeProvider time,
        Func<bool> automaticChecksOn,
        Func<UpdateCheckTrigger, CancellationToken, Task> check,
        Func<TimeSpan>? firstDelay = null,
        Action<string>? log = null)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _automaticChecksOn = automaticChecksOn ?? throw new ArgumentNullException(nameof(automaticChecksOn));
        _check = check ?? throw new ArgumentNullException(nameof(check));
        _firstDelay = firstDelay ?? RandomFirstDelay;
        _log = log ?? (_ => { });
    }

    /// <summary>When the next automatic slot is (null before Start or after Dispose).</summary>
    public DateTimeOffset? NextDueUtc
    {
        get { lock (_gate) return _nextDue; }
    }

    /// <summary>The running check, if any (tests await it).</summary>
    public Task? Running
    {
        get { lock (_gate) return _running is { IsCompleted: false } r ? r : null; }
    }

    /// <summary>Arms the start slot, 2–10 minutes from now. Once.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _timer is not null) return;
            _timer = _time.CreateTimer(_ => OnTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            ArmLocked(_time.GetUtcNow() + ClampFirstDelay());
        }
    }

    /// <summary>
    /// The user turned automatic checks back on: the next slot moves to 2–10 minutes from now, unless it is
    /// sooner already.
    /// </summary>
    public void ChecksTurnedOn()
    {
        lock (_gate)
        {
            if (_disposed || _timer is null) return;
            _offLogged = false;
            var soon = _time.GetUtcNow() + ClampFirstDelay();
            if (_nextDue is not { } due || soon < due) ArmLocked(soon);
        }
    }

    /// <summary>
    /// The PC woke up (or the clock jumped): a slot that came due while it slept runs 2–10 minutes from now,
    /// once the network is back; a slot the clock was set back from (more than a day away) is re-anchored the
    /// same way; otherwise the timer is re-armed for the slot's wall-clock time.
    /// </summary>
    public void Resync()
    {
        lock (_gate)
        {
            if (_disposed || _timer is null || _nextDue is not { } due) return;
            var now = _time.GetUtcNow();
            ArmLocked(due <= now ? now + ClampFirstDelay() : SlotAfterClockChangeLocked(due, now));
        }
    }

    /// <summary>
    /// Checks now, whatever the setting (the user asked). Joins a check already running. Within
    /// <see cref="CheckNowMinGap"/> of the last run's end it makes no request (completed task; the last result stands).
    /// </summary>
    public Task CheckNowAsync()
    {
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            if (_running is { IsCompleted: false } running) return running;
            // A last run "in the future" (the clock was set back since) is no recent run: the user's check goes ahead.
            if (_lastRunEnded is { } ended && _time.GetUtcNow() - ended is var since && since >= TimeSpan.Zero && since < CheckNowMinGap)
            {
                _log($"update checks: Check now {since.TotalSeconds:0} s after the last check; showing its result, no request made");
                return Task.CompletedTask;
            }
        }
        return Run(UpdateCheckTrigger.User);
    }

    /// <summary>When the last run ended (null: none yet).</summary>
    public DateTimeOffset? LastRunEndedUtc
    {
        get { lock (_gate) return _lastRunEnded; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _nextDue = null;
            _timer?.Dispose();
            _timer = null;
        }
        _stop.Cancel();
        _stop.Dispose();
    }

    // SC-046 (T1): the timer callback runs on a thread-pool thread with no handler above it, so anything it throws
    // ends the tray. It never throws now: a failure is logged and the loop tries to keep its next slot armed.
    private void OnTimer()
    {
        try
        {
            OnTimerCore();
        }
        catch (Exception ex)
        {
            Say("update checks: the timer failed unexpectedly (" + ex.Message + "); the next slot is kept");
            try
            {
                lock (_gate)
                {
                    if (!_disposed) ArmLocked(_nextDue ?? _time.GetUtcNow() + Interval);
                }
            }
            catch (Exception)
            {
                // Nothing more can be done from the timer thread. Check now still works, and Resync (on resume)
                // arms the timer again.
            }
        }
    }

    private void OnTimerCore()
    {
        lock (_gate)
        {
            if (_disposed) return;
            var now = _time.GetUtcNow();
            if (_nextDue is { } due && now < due)
            {
                // Fired before its wall-clock slot (a wait is at most a day, or the clock changed): wait for the
                // slot, unless the clock was set back from it.
                ArmLocked(SlotAfterClockChangeLocked(due, now));
                return;
            }
            // The next slot is fixed before the check runs, so a slow or failed check never shifts the schedule.
            ArmLocked(now + Interval);
        }

        bool on;
        try
        {
            on = _automaticChecksOn();
        }
        catch (Exception ex)
        {
            _log("update checks: could not read the setting (" + ex.Message + "); skipped, as if off");
            on = false;
        }
        if (!on)
        {
            if (!_offLogged) _log("update checks: automatic checks are off; no request made (Check now still works)");
            _offLogged = true;
            return;
        }
        _offLogged = false;
        _ = Run(UpdateCheckTrigger.Automatic);
    }

    private Task Run(UpdateCheckTrigger trigger)
    {
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            if (_running is { IsCompleted: false } running) return running;
            _running = RunCore(trigger);
            return _running;
        }
    }

    private async Task RunCore(UpdateCheckTrigger trigger)
    {
        // Leave the caller's lock (and the timer thread) before the check starts.
        await Task.Yield();
        CancellationToken token;
        try
        {
            token = _stop.Token;
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        try
        {
            await _check(trigger, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The tray is quitting.
        }
        catch (Exception ex)
        {
            Say("update checks: the check failed unexpectedly: " + ex.Message);
        }
        finally
        {
            lock (_gate) _lastRunEnded = _time.GetUtcNow();
        }
    }

    /// <summary>
    /// The longest single wait the timer is armed for. A slot further away (the clock was set back) is reached in
    /// steps: the timer fires early, OnTimer sees the slot is not due yet and arms the next step. Timer.Change
    /// refuses a wait over about 49.7 days (uint.MaxValue - 1 ms) and a negative one, on the timer thread, which
    /// ended the tray when the clock moved back by about 48 days or more (SC-046, T1).
    /// </summary>
    internal static readonly TimeSpan MaxWait = Interval;

    private void ArmLocked(DateTimeOffset due)
    {
        _nextDue = due;
        _timer?.Change(WaitUntil(due), Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// <paramref name="due"/>, unless it is more than <see cref="Interval"/> away: no slot is ever armed further
    /// ahead than that (the start and turned-on slots are 2–10 minutes, the rest now + 24 hours), so the clock was
    /// set back. Keeping it would stop automatic checks until the clock caught up (61 days after a 60-day jump); the
    /// slot is re-anchored as at start, 2–10 minutes from now (SC-046 review N1).
    /// </summary>
    private DateTimeOffset SlotAfterClockChangeLocked(DateTimeOffset due, DateTimeOffset now)
    {
        if (due - now <= Interval) return due;
        var slot = now + ClampFirstDelay();
        Say($"update checks: the next slot was {(due - now).TotalDays:0.#} days away, so the clock was set back; next check at {slot:u}");
        return slot;
    }

    /// <summary>The wait to arm for <paramref name="due"/>: never negative, never over <see cref="MaxWait"/>.</summary>
    private TimeSpan WaitUntil(DateTimeOffset due)
    {
        var wait = due - _time.GetUtcNow();
        if (wait < TimeSpan.Zero) return TimeSpan.Zero;
        return wait > MaxWait ? MaxWait : wait;
    }

    /// <summary>The loop's own log line; a log that throws never reaches the timer thread.</summary>
    private void Say(string message)
    {
        try
        {
            _log(message);
        }
        catch (Exception)
        {
            // Nothing to report it to.
        }
    }

    private TimeSpan ClampFirstDelay()
    {
        TimeSpan delay;
        try
        {
            delay = _firstDelay();
        }
        catch (Exception)
        {
            delay = FirstDelayMax;
        }
        if (delay < FirstDelayMin) return FirstDelayMin;
        return delay > FirstDelayMax ? FirstDelayMax : delay;
    }

    /// <summary>A uniform random delay between <see cref="FirstDelayMin"/> and <see cref="FirstDelayMax"/>.</summary>
    public static TimeSpan RandomFirstDelay() =>
        FirstDelayMin + TimeSpan.FromSeconds(Random.Shared.NextDouble() * (FirstDelayMax - FirstDelayMin).TotalSeconds);
}
