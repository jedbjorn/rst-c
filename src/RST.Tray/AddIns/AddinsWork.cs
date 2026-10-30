// rst-c's own (not vendored): the Add-ins page's file work in flight (F17 WU2, review N7). An Apply or Restore All
// writes Revit's settings files and then RST's record; a tray that ended between the two would leave add-ins switched
// off that the record does not list, so Restore All could not bring them back. TrayHost waits for the work here
// (it takes milliseconds) before it quits. The page is not modal, so Quit is reachable while it runs.

namespace RST.Tray.AddIns;

internal sealed class AddinsWork
{
    private readonly object _gate = new();
    private Task _current = Task.CompletedTask;

    /// <summary>Runs <paramref name="work"/> on the thread pool, tracked until it ends.</summary>
    public Task<T> Run<T>(Func<T> work)
    {
        var task = Task.Run(work);
        lock (_gate) _current = Task.WhenAll(_current, task);
        return task;
    }

    /// <summary>True when nothing is running.</summary>
    public bool IsIdle
    {
        get
        {
            lock (_gate) return _current.IsCompleted;
        }
    }

    /// <summary>Waits up to <paramref name="timeout"/> for the work in flight. True when it ended. Never throws.</summary>
    public bool WaitIdle(TimeSpan timeout)
    {
        Task current;
        lock (_gate) current = _current;
        try
        {
            return current.Wait(timeout);
        }
        catch (AggregateException)
        {
            return true; // it ended (with an error the page already reports)
        }
    }
}
