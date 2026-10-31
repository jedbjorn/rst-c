// rst-c's own (not vendored). SC-046: work started and not awaited (`_ = SomethingAsync()`) failed silently: the
// task's exception was never observed, so nothing reached the log. Forget starts nothing; it watches a task that is
// already running and logs how it failed. RST.Tests' source scan bans the bare discard in product code.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace RST.Core.Logging;

/// <summary>Observes a task nobody awaits, so a failure is logged instead of lost.</summary>
public static class TaskGuard
{
    internal const string FaultTemplate = "{Site}: work that was not awaited failed; the error was logged";

    /// <summary>
    /// Lets <paramref name="task"/> run on, unawaited. When it fails, the exception is logged at Error with
    /// <paramref name="site"/> (and marked observed); a cancellation is logged at Debug. Never throws.
    /// </summary>
    /// <param name="task">The running task (null: nothing to watch).</param>
    /// <param name="site">Where it was started, for the log ("tray: check now").</param>
    public static void Forget(this Task? task, string site)
    {
        if (task is null) return;
        if (task.IsCompleted)
        {
            Observe(task, site);
            return;
        }
        task.ContinueWith(t => Observe(t, site), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static void Observe(Task task, string site)
    {
        try
        {
            if (task.IsFaulted && task.Exception is { } aggregate)
                Log.Error(aggregate.InnerExceptions.Count == 1 ? aggregate.InnerException : aggregate, FaultTemplate, site);
            else if (task.IsCanceled)
                Log.Debug("{Site}: cancelled", site);
        }
        catch
        {
            // The log never throws; nothing else here can.
        }
    }
}
