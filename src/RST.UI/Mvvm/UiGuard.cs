// rst-c's own (not vendored). SC-046 (follow-up to issue #163): an exception in an RST window's code-behind
// handler, command or binding setter ran on Revit's UI thread with nothing above it but Revit. WindowGuard
// (SC-045) catches what reaches the dispatcher while a launcher's window is open; this stops it at its source,
// everywhere: the failure is logged at Error with where it happened, and the window keeps running.
// RST.Tests' source scans keep every code-behind handler in RST.UI going through Run.

using System.Windows;
using System.Windows.Input;
using RST.Core.Logging;
using RST.UI.Controls;
using RST.UI.Resources;

namespace RST.UI.Mvvm;

/// <summary>Runs UI work so a failure is logged, not fatal.</summary>
public static class UiGuard
{
    // Not "the window kept running": a closing handler's failure lets the window close (SC-046 review N2).
    internal const string FaultTemplate = "UI: {Site} failed; the error was logged";

    internal const string CancelledTemplate = "UI: {Site} was cancelled";

    /// <summary>
    /// Runs <paramref name="action"/>; an exception is logged at Error with <paramref name="site"/> and goes no
    /// further. For code-behind event handlers and the work two-way binding setters do (WPF swallows an exception
    /// from a setter without logging it).
    /// </summary>
    /// <param name="site">Where, for the log: "Builder window: drop on a card".</param>
    public static void Run(string site, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Report(site, ex, owner: null, toast: false);
        }
    }

    /// <summary>
    /// <see cref="Run(string, Action)"/>, and on a failure also runs <paramref name="onFailure"/> (which may not
    /// throw further): a drag-over handler that fails says "no drop" rather than leaving the pointer's promise.
    /// </summary>
    public static void Run(string site, Action action, Action onFailure)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            try
            {
                onFailure();
            }
            catch (Exception inner)
            {
                Log.Debug(inner, "UI: the failure step of {Site} failed too", site);
            }
            Report(site, ex, owner: null, toast: false);
        }
    }

    /// <summary>
    /// Starts <paramref name="work"/> and lets it run on unawaited; a failure, synchronous or later, is logged
    /// and toasted as a command's is (<see cref="Report"/>). For async work posted after an input event.
    /// </summary>
    /// <param name="owner">The view-model the work belongs to; its toast shows the failure.</param>
    public static void Start(string site, Func<Task> work, object? owner = null) =>
        RunAsync(site, work, owner).Forget(site);

    /// <summary>Awaits <paramref name="work"/>; a failure is reported (<see cref="Report"/>). The task never faults.</summary>
    public static async Task RunAsync(string site, Func<Task> work, object? owner = null)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            Report(site, ex, owner, toast: true);
        }
    }

    /// <summary>
    /// Logs a UI failure at Error with <paramref name="site"/> (a cancellation only at Debug). With
    /// <paramref name="toast"/>, the window's toast says that it did not work: the toast of
    /// <paramref name="owner"/> when it has one (<see cref="IHasToast"/>), else that of the window with the
    /// keyboard focus or the window that owns it. Without a toast it is only logged. Never throws.
    /// </summary>
    public static void Report(string site, Exception exception, object? owner, bool toast)
    {
        try
        {
            if (exception is OperationCanceledException)
            {
                Log.Debug(exception, CancelledTemplate, site);
                return;
            }
            Log.Error(exception, FaultTemplate, site);
            if (toast) ToastHostFor(owner)?.Toast.Show(UiStrings.Common_ActionFailed, BannerKind.Error);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "UI: reporting the failure of {Site} failed", site);
        }
    }

    /// <summary>The toast a failure shows in: the owner's, else the focused window's (or its owner's).</summary>
    internal static IHasToast? ToastHostFor(object? owner)
    {
        if (owner is IHasToast host) return host;
        // Only on a UI thread: asking for the focus elsewhere would start a dispatcher on that thread.
        if (System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread) is null) return null;
        if (Keyboard.FocusedElement is not DependencyObject focused) return null;
        for (var window = Window.GetWindow(focused); window is not null; window = window.Owner)
            if (window.DataContext is IHasToast windowHost) return windowHost;
        return null;
    }
}
