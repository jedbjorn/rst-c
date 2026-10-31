// rst-c's own (not vendored): WHEN the RST tray folds activity (spec #24, Tray Worker and Cleanup; F19 #24 WU3).
// ActivityFoldEngine holds every fold, seal and cleanup rule; this class only schedules it.
//
//   - one background thread at below-normal priority, never the UI thread;
//   - the first pass 1–3 minutes after the tray starts (sign-in stays light), then every 15 minutes after the
//     previous pass ended;
//   - a pass is RunPass: a listing compared with the ledger that ends at once when nothing changed; otherwise one
//     file per step, a pause of about a second between steps, then sealing and cleanup;
//   - the age backstop (RetentionPruner) runs after a pass that held the fold lock, at most once a day, so the
//     outbox cannot grow without bound when folding stalls; quiet passes stay a listing;
//   - at most one pass at a time: RequestPass (the Health page opening) starts one now unless one is running or
//     one ended within the last minute; requests that arrive together coalesce into one pass;
//   - the fold lock (in the engine) makes a second tray, in another sign-in session of the same user, skip;
//   - nothing it does throws out of the thread or reaches the UI: failures are per file (the engine), and anything
//     else is logged and retried on the next pass. It reads nothing about the update-check setting and sends
//     nothing anywhere. Collection off does not stop it: files already recorded still fold.
//
// One Information line per pass (see Describe), so a pass can be verified from the tray log alone.
// Time comes from a TimeProvider, so RST.Tests drives hours of the schedule in milliseconds.

namespace RST.Core.Telemetry.Rollups;

/// <summary>Why a fold pass ran.</summary>
public enum FoldTrigger
{
    /// <summary>The start slot (1–3 minutes after start) or a 15-minute slot.</summary>
    Scheduled,

    /// <summary><see cref="IActivityFoldService.RequestPass"/>: the Health page opened.</summary>
    Requested,
}

/// <summary>What <see cref="IActivityFoldService.RequestPass"/> did.</summary>
public enum FoldRequestOutcome
{
    /// <summary>A pass starts now (or was already queued to; requests coalesce). <c>PassCompleted</c> follows.</summary>
    Queued,

    /// <summary>A pass is running; no second one starts. Its <c>PassCompleted</c> follows.</summary>
    AlreadyRunning,

    /// <summary>A pass ended within <see cref="ActivityFoldWorkerOptions.RequestMinGap"/>; nothing starts, the
    /// rollups already hold its result.</summary>
    RanRecently,

    /// <summary>The worker is not started, or is stopping; nothing starts.</summary>
    Stopped,
}

/// <summary>A fold pass ended (raised on the worker thread: marshal to the UI thread before touching UI).</summary>
public sealed class FoldPassCompletedEventArgs : EventArgs
{
    public FoldPassCompletedEventArgs(FoldTrigger trigger, FoldPassResult result, int pruned, TimeSpan duration, DateTimeOffset endedUtc)
    {
        Trigger = trigger;
        Result = result;
        Pruned = pruned;
        Duration = duration;
        EndedUtc = endedUtc;
    }

    public FoldTrigger Trigger { get; }

    public FoldPassResult Result { get; }

    /// <summary>Raw files the age backstop deleted after this pass (0 when it did not run).</summary>
    public int Pruned { get; }

    public TimeSpan Duration { get; }

    public DateTimeOffset EndedUtc { get; }

    /// <summary>True when the rollups may have changed (a refresh is worth it).</summary>
    public bool StoreChanged => !Result.Skipped && !Result.NothingChanged;
}

/// <summary>What the tray UI (spec #25's Health page) uses: ask for a pass, and hear when one ends.</summary>
public interface IActivityFoldService
{
    /// <summary>Start a pass now unless one is running or one ended within the last minute. Thread-safe; never
    /// starts a second concurrent pass; never blocks; never throws.</summary>
    FoldRequestOutcome RequestPass();

    /// <summary>A pass ended, whatever started it. Raised on the worker thread; a handler that throws is logged.</summary>
    event EventHandler<FoldPassCompletedEventArgs>? PassCompleted;

    /// <summary>True while a pass runs.</summary>
    bool IsRunning { get; }

    /// <summary>When the last pass ended (null: none yet).</summary>
    DateTimeOffset? LastPassEndedUtc { get; }
}

public sealed class ActivityFoldWorkerOptions
{
    public TimeSpan FirstDelayMin { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan FirstDelayMax { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>The start delay; null = uniform random between <see cref="FirstDelayMin"/> and <see cref="FirstDelayMax"/>.</summary>
    public Func<TimeSpan>? FirstDelay { get; init; }

    /// <summary>From the end of one pass to the next scheduled one.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>The pause after each folded file, so a big backlog never hogs the disk.</summary>
    public TimeSpan StepPause { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>A request this soon after the last pass ended starts nothing.</summary>
    public TimeSpan RequestMinGap { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>How often the age backstop runs (after a pass that held the lock).</summary>
    public TimeSpan BackstopInterval { get; init; } = TimeSpan.FromHours(24);

    /// <summary>How long <see cref="ActivityFoldWorker.Dispose"/> waits for a running step to stop.</summary>
    public TimeSpan StopWait { get; init; } = TimeSpan.FromSeconds(2);
}

public sealed class ActivityFoldWorker : IActivityFoldService, IDisposable
{
    /// <summary>One whole pass (<see cref="ActivityFoldEngine.RunPass"/>): call <paramref name="betweenSteps"/> after
    /// each folded file, stop between steps when <paramref name="cancel"/> is set.</summary>
    public delegate FoldPassResult PassRunner(Action betweenSteps, CancellationToken cancel);

    private readonly PassRunner _runPass;
    private readonly Func<int>? _backstop;
    private readonly TimeProvider _time;
    private readonly ActivityFoldWorkerOptions _o;
    private readonly Action<string> _info;
    private readonly Action<string> _warn;
    private readonly object _gate = new();
    // Never disposed: a step still running after Dispose's bounded wait may touch them; the process ends soon.
    private readonly AutoResetEvent _wake = new(false);
    private readonly CancellationTokenSource _stop = new();
    private Thread? _thread;
    private ITimer? _timer;
    private DateTimeOffset? _nextDue;
    private DateTimeOffset? _lastEnded;
    private DateTimeOffset? _lastBackstop;
    private bool _requested;
    private bool _running;
    private bool _disposed;

    /// <param name="runPass">The pass (the engine's RunPass in the tray; a fake in tests).</param>
    /// <param name="backstopPrune">The age backstop; returns the files it deleted. Null: none.</param>
    /// <param name="time">The clock and timers (TimeProvider.System in the tray).</param>
    /// <param name="options">The schedule; null = production's.</param>
    /// <param name="info">The one line per pass.</param>
    /// <param name="warn">Anything that went wrong outside the engine's own per-file logging.</param>
    public ActivityFoldWorker(
        PassRunner runPass,
        Func<int>? backstopPrune,
        TimeProvider time,
        ActivityFoldWorkerOptions? options = null,
        Action<string>? info = null,
        Action<string>? warn = null)
    {
        _runPass = runPass ?? throw new ArgumentNullException(nameof(runPass));
        _backstop = backstopPrune;
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _o = options ?? new ActivityFoldWorkerOptions();
        _info = info ?? (_ => { });
        _warn = warn ?? _info;
    }

    /// <summary>The tray's worker over the real engine.</summary>
    public static ActivityFoldWorker ForEngine(
        ActivityFoldEngine engine,
        Func<int>? backstopPrune,
        TimeProvider time,
        ActivityFoldWorkerOptions? options = null,
        Action<string>? info = null,
        Action<string>? warn = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return new ActivityFoldWorker((between, cancel) => engine.RunPass(between, cancel), backstopPrune, time, options, info, warn);
    }

    public event EventHandler<FoldPassCompletedEventArgs>? PassCompleted;

    /// <summary>When the next scheduled pass is due (null before Start, while a pass runs, or after Dispose).</summary>
    public DateTimeOffset? NextDueUtc
    {
        get { lock (_gate) return _nextDue; }
    }

    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    public DateTimeOffset? LastPassEndedUtc
    {
        get { lock (_gate) return _lastEnded; }
    }

    /// <summary>Starts the thread and arms the start slot, 1–3 minutes from now. Once.</summary>
    public void Start()
    {
        DateTimeOffset first;
        lock (_gate)
        {
            if (_disposed || _thread is not null) return;
            var thread = new Thread(Loop) { IsBackground = true, Name = "RST activity fold" };
            try { thread.Priority = ThreadPriority.BelowNormal; }
            catch (Exception) { /* a platform that refuses priorities runs it at normal */ }
            _timer = _time.CreateTimer(_ => Wake(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            first = _time.GetUtcNow() + FirstDelay();
            ArmLocked(first);
            _thread = thread;
            thread.Start();
        }
        Say(_info, $"activity fold: first pass at {first:u}, then every {_o.Interval.TotalMinutes:0} min");
    }

    public FoldRequestOutcome RequestPass()
    {
        try
        {
            lock (_gate)
            {
                if (_disposed || _thread is null) return FoldRequestOutcome.Stopped;
                if (_running) return FoldRequestOutcome.AlreadyRunning;
                if (_requested) return FoldRequestOutcome.Queued;
                // A last pass "in the future" (the clock was set back since) is no recent pass.
                if (_lastEnded is { } ended && _time.GetUtcNow() - ended is var since && since >= TimeSpan.Zero && since < _o.RequestMinGap)
                    return FoldRequestOutcome.RanRecently;
                _requested = true;
            }
            Wake();
            return FoldRequestOutcome.Queued;
        }
        catch (Exception ex)
        {
            Say(_warn, "activity fold: a pass request failed: " + ex.Message);
            return FoldRequestOutcome.Stopped;
        }
    }

    /// <summary>
    /// Stops the worker: no new pass starts, a running pass stops after its current step (its writes are atomic),
    /// and this waits at most <see cref="ActivityFoldWorkerOptions.StopWait"/> for that. Idempotent; never throws.
    /// </summary>
    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _nextDue = null;
            _requested = false;
            try { _timer?.Dispose(); }
            catch (Exception) { /* stopping anyway */ }
            _timer = null;
            thread = _thread;
        }
        try
        {
            _stop.Cancel();
            _wake.Set();
        }
        catch (Exception) { /* stopping anyway */ }
        if (thread is null || thread == Thread.CurrentThread) return;
        try
        {
            if (!thread.Join(_o.StopWait))
                Say(_warn, $"activity fold: a step was still running after {_o.StopWait.TotalSeconds:0.#} s; the tray exits anyway (the next start re-checks it)");
        }
        catch (Exception) { /* exiting anyway */ }
    }

    // ---- the thread --------------------------------------------------------------------------------------------

    private void Wake()
    {
        try { _wake.Set(); }
        catch (ObjectDisposedException) { }
    }

    private void Loop()
    {
        while (true)
        {
            try
            {
                WaitHandle.WaitAny([_wake, _stop.Token.WaitHandle]);
                if (_stop.IsCancellationRequested) return;
                if (TakeDue() is { } trigger) RunOne(trigger);
            }
            catch (Exception ex)
            {
                // Nothing may end the thread but Dispose: keep the next slot armed and go on waiting.
                Say(_warn, "activity fold: the worker failed unexpectedly (retried next pass): " + ex.Message);
                try
                {
                    lock (_gate)
                    {
                        _running = false;
                        if (!_disposed) ArmLocked(_time.GetUtcNow() + _o.Interval);
                    }
                }
                catch (Exception) { /* the next wake re-arms */ }
                if (_stop.IsCancellationRequested) return;
            }
        }
    }

    /// <summary>The trigger to run now, or null when the wake was early (the timer is re-armed).</summary>
    private FoldTrigger? TakeDue()
    {
        lock (_gate)
        {
            if (_disposed) return null;
            FoldTrigger trigger;
            if (_requested)
            {
                _requested = false;
                trigger = FoldTrigger.Requested;
            }
            else if (_nextDue is { } due)
            {
                var now = _time.GetUtcNow();
                // A slot further away than any the worker sets means the clock was set back: run now.
                if (due > now && due - now <= _o.Interval + _o.FirstDelayMax)
                {
                    ArmLocked(due);
                    return null;
                }
                trigger = FoldTrigger.Scheduled;
            }
            else
            {
                return null;
            }
            _running = true;
            _nextDue = null;
            _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return trigger;
        }
    }

    private void RunOne(FoldTrigger trigger)
    {
        var started = _time.GetTimestamp();
        FoldPassResult result;
        try
        {
            result = _runPass(Pause, _stop.Token);
        }
        catch (Exception ex)
        {
            Say(_warn, "activity fold: pass failed (retried next pass): " + ex.Message);
            result = new FoldPassResult(false, false, 0, 1, 0, 0);
        }

        var pruned = 0;
        if (!result.Skipped && !_stop.IsCancellationRequested && BackstopDue())
        {
            try
            {
                pruned = _backstop?.Invoke() ?? 0;
                lock (_gate) _lastBackstop = _time.GetUtcNow();
            }
            catch (Exception ex)
            {
                Say(_warn, "activity fold: the age backstop failed (retried next pass): " + ex.Message);
            }
        }

        var duration = _time.GetElapsedTime(started);
        DateTimeOffset ended;
        lock (_gate)
        {
            ended = _time.GetUtcNow();
            _running = false;
            _lastEnded = ended;
            if (!_disposed) ArmLocked(ended + _o.Interval);
        }
        Say(_info, Describe(trigger, result, pruned, duration));

        var args = new FoldPassCompletedEventArgs(trigger, result, pruned, duration, ended);
        foreach (var handler in PassCompleted?.GetInvocationList() ?? [])
        {
            try { ((EventHandler<FoldPassCompletedEventArgs>)handler)(this, args); }
            catch (Exception ex) { Say(_warn, "activity fold: a pass-completed listener failed: " + ex.Message); }
        }
    }

    private bool BackstopDue()
    {
        if (_backstop is null) return false;
        lock (_gate)
        {
            if (_lastBackstop is not { } last) return true;
            var since = _time.GetUtcNow() - last;
            return since < TimeSpan.Zero || since >= _o.BackstopInterval;
        }
    }

    /// <summary>Between two folded files: about a second, cut short by Dispose.</summary>
    private void Pause()
    {
        if (_o.StepPause > TimeSpan.Zero) _stop.Token.WaitHandle.WaitOne(_o.StepPause);
    }

    /// <summary>The pass's one log line: "activity fold pass (scheduled): 3 folded, 1 sealed, 2 deleted, 1 skipped,
    /// 0 failed, 0 pruned in 3.2 s".</summary>
    internal static string Describe(FoldTrigger trigger, FoldPassResult r, int pruned, TimeSpan duration)
    {
        var why = trigger == FoldTrigger.Requested ? "requested" : "scheduled";
        var took = duration.TotalSeconds >= 1 ? $"{duration.TotalSeconds:0.0} s" : $"{duration.TotalMilliseconds:0} ms";
        if (r.Skipped) return $"activity fold pass ({why}): skipped, another RST tray holds the fold lock ({took})";
        var tail = pruned > 0 ? $", {pruned} pruned by age" : "";
        if (r.NothingChanged) return $"activity fold pass ({why}): nothing changed{tail} ({took})";
        return $"activity fold pass ({why}): {r.Folded} folded, {r.Sealed} sealed, {r.Deleted} deleted, " +
               $"{r.SkippedFiles} skipped, {r.Failed} failed{tail} in {took}";
    }

    private void ArmLocked(DateTimeOffset due)
    {
        _nextDue = due;
        var wait = due - _time.GetUtcNow();
        if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
        _timer?.Change(wait, Timeout.InfiniteTimeSpan);
    }

    private TimeSpan FirstDelay()
    {
        TimeSpan delay;
        try
        {
            delay = _o.FirstDelay?.Invoke()
                ?? _o.FirstDelayMin + TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * Math.Max(0, (_o.FirstDelayMax - _o.FirstDelayMin).TotalMilliseconds));
        }
        catch (Exception)
        {
            delay = _o.FirstDelayMin;
        }
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        return delay > _o.FirstDelayMax ? _o.FirstDelayMax : delay;
    }

    private static void Say(Action<string> log, string message)
    {
        try { log(message); }
        catch { /* logging must never take the worker down */ }
    }
}
