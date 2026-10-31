// rst-c's own (not vendored). SC-046 (T6, T7): the tray's process-wide nets. RST.Tray.exe is RST's own process, so it
// may hook the AppDomain and the TaskScheduler (the engine never does: inside Revit those belong to Revit).
//   - a UI exception is logged and handled, so the tray never vanishes from under the user; but the same exception
//     coming back again and again (a fault that recurs on every dispatcher pass) ends the tray cleanly instead of
//     looping for ever (RST.Core RepeatingFault);
//   - an exception on another thread that nothing catches ends the process (the runtime's rule): it is logged at
//     Fatal and the log is flushed first, so the cause is on disk;
//   - a faulted task nobody observed is logged and marked observed.

using System.Threading.Tasks;
using RST.Core.Logging;
using RST.Core.Tray;

namespace RST.Tray.Shell;

internal sealed class TrayFaults
{
    /// <summary>The exit code when the tray quits over a repeating UI exception.</summary>
    public const int RepeatingFaultExitCode = 3;

    internal const string UiTemplate = "unhandled UI exception";

    internal const string RepeatingTemplate =
        "the same UI exception came back more than {Limit} times in {Seconds} s; the tray quits instead of looping";

    private readonly RepeatingFault _repeats;
    private readonly Action<string, int> _quit;
    private bool _quitting;

    /// <param name="quit">Ends the tray cleanly: a reason and an exit code (TrayHost.Quit).</param>
    /// <param name="repeats">The repeat counter; the tray's limits by default.</param>
    public TrayFaults(Action<string, int> quit, RepeatingFault? repeats = null)
    {
        _quit = quit ?? throw new ArgumentNullException(nameof(quit));
        _repeats = repeats ?? RepeatingFault.ForTray();
    }

    /// <summary>
    /// A UI exception the dispatcher did not handle: logged, and the caller marks it handled. True when it is one
    /// repeat too many: the tray has been asked to quit (once; repeats after that are not logged one by one).
    /// </summary>
    public bool OnUiException(Exception exception)
    {
        if (_quitting)
        {
            Log.Debug(exception, "UI exception while quitting over a repeating one");
            return true;
        }
        Log.Error(exception, UiTemplate);
        if (!_repeats.Record(exception)) return false;
        _quitting = true;
        Log.Error(RepeatingTemplate, RepeatingFault.TrayLimit, RepeatingFault.TrayPeriod.TotalSeconds);
        _quit("repeating UI exception", RepeatingFaultExitCode);
        return true;
    }

    /// <summary>Hooks the AppDomain and the TaskScheduler (RST.Tray.exe's own process only; never in Revit).</summary>
    public static void HookProcess()
    {
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTask;
    }

    internal static void OnDomainException(object? sender, UnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.ExceptionObject as Exception, "unhandled exception off the UI thread (process ending: {Terminating})", e.IsTerminating);
        if (e.IsTerminating) Log.CloseAndFlush();
    }

    internal static void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "a background task failed and nothing observed it");
        e.SetObserved();
    }
}
