// rst-c's own (not vendored). SC-045 (issue #163): a dispatcher exception while an RST window is open
// inside Revit must not take Revit down. Health crashed Revit when a spinner's storyboard threw from a
// Loaded trigger inside ShowDialog: nothing caught it on the UI thread. The launchers show their window
// through here so a fault is logged and the window closes, and Revit keeps running.

using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using RST.Core.Logging;

namespace RST.UI.Dialogs;

/// <summary>
/// Shows an RST window modally with a safety net for its lifetime: an exception that reaches the UI
/// thread's dispatcher unhandled while the window is open is logged at Error, marked handled, and the
/// window is closed. Every in-Revit launcher (Health, the Loader, the Builder) uses it.
/// </summary>
/// <remarks>
/// The net is scoped to one window's modal loop. It subscribes to <see cref="Dispatcher.UnhandledException"/>
/// right before <see cref="Window.ShowDialog"/> and unsubscribes as soon as ShowDialog returns, so it never
/// sees anything raised before the window opens or after it closes. Being the last subscriber, it leaves an
/// exception alone when an earlier handler (Revit's or another add-in's) already handled it. It shows
/// nothing modal: a log line, then the close, which is queued so it runs after the dispatcher has finished
/// handling the exception. Dialogs the window opens (DialogService, the Loader's and Builder's dialogs)
/// run nested inside this window's modal loop, so the same scope covers them; closing the window closes
/// them too. Only the first exception closes the window; any that follow while it closes are logged and
/// handled the same way.
/// </remarks>
public static class WindowGuard
{
    internal const string FaultTemplate =
        "{Window} window: unhandled exception on the UI thread; it was handled and the window closed so Revit keeps running";

    internal const string FurtherFaultTemplate =
        "{Window} window: another unhandled exception while it was closing after an error; handled";

    /// <summary>
    /// Shows <paramref name="window"/> modally and returns when it closes: true when it closed normally,
    /// false when the safety net closed it after an unhandled exception (already logged at Error). An
    /// exception that ShowDialog itself throws (before the window's message loop runs) still propagates
    /// to the caller, as before.
    /// </summary>
    /// <param name="window">The window to show. Owner and placement are the caller's (<see cref="WindowPlacement.Own"/>).</param>
    /// <param name="name">The window's name for the log ("Health", "Loader", "Builder").</param>
    public static bool ShowDialog(Window window, string name)
    {
        ArgumentNullException.ThrowIfNull(window);
        var dispatcher = window.Dispatcher;
        var open = true;
        var faulted = false;

        void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            if (!open || e.Handled) return;
            e.Handled = true;
            if (faulted)
            {
                Log.Error(e.Exception, FurtherFaultTemplate, name);
                return;
            }
            faulted = true;
            Log.Error(e.Exception, FaultTemplate, name);
            // Not from inside the handler: the dispatcher is still unwinding the operation that threw.
            dispatcher.BeginInvoke(DispatcherPriority.Send, () => CloseAfterFault(window, name));
        }

        // A faulted window closes even where its own guard would keep it open (the Loader while it is
        // working, the Builder after its unsaved-changes check): this handler is the last to run.
        void OnClosing(object? sender, CancelEventArgs e)
        {
            if (faulted) e.Cancel = false;
        }

        dispatcher.UnhandledException += OnUnhandled;
        window.Closing += OnClosing;
        try
        {
            window.ShowDialog();
        }
        finally
        {
            open = false;
            dispatcher.UnhandledException -= OnUnhandled;
            window.Closing -= OnClosing;
        }
        return !faulted;
    }

    private static void CloseAfterFault(Window window, string name)
    {
        try
        {
            window.Close();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Window} window: close after an error failed", name);
        }
    }
}
