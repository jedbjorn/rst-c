// rst-c's own (not vendored). SC-045 (issue #163): a dispatcher exception while an RST window is open
// inside Revit must not take Revit down. Health crashed Revit when a spinner's storyboard threw from a
// Loaded trigger inside ShowDialog: nothing caught it on the UI thread. The launchers show their window
// through here so a fault is logged and the window closes, and Revit keeps running.

using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using RST.Core.Logging;

namespace RST.UI.Dialogs;

/// <summary>
/// Shows an RST window modally with a safety net for its lifetime: an RST exception that reaches the UI
/// thread's dispatcher unhandled while the window is open is logged at Error, marked handled, and the
/// window is closed. Every in-Revit launcher (Health, the Loader, the Builder) uses it.
/// </summary>
/// <remarks>
/// <para><b>When.</b> The net is scoped to one window's modal loop. It subscribes to
/// <see cref="Dispatcher.UnhandledException"/> right before <see cref="Window.ShowDialog"/> and unsubscribes as
/// soon as ShowDialog returns, so it never sees anything raised before the window opens or after it closes.
/// Being the last subscriber, it leaves an exception alone when an earlier handler (Revit's or another
/// add-in's) already handled it.</para>
/// <para><b>Whose.</b> It takes only exceptions that are RST's, judged from the stack frames of the exception
/// and its inner exceptions (<see cref="IsRstsToHandle"/>): any frame in an <c>RST</c> namespace makes it RST's.
/// A frame from any other code that is not the .NET/WPF framework (Revit's API, another add-in) with no RST
/// frame makes it someone else's, and it is left alone. A stack of framework frames only (WPF's own layout,
/// render, binding or storyboard code, with nothing above it) is handled too: that is the shape of the #163
/// crash, whose exception carried no RST frame (it surfaced from WPF's Loaded broadcast in a render pass), and
/// while an RST window's modal loop runs, such an exception comes from that window's visual tree.</para>
/// <para><b>What.</b> It shows nothing modal: the first exception is logged at Error with its full stack, then
/// the window is closed, queued so the close runs after the dispatcher has finished with the exception. The
/// window closes even where its own guard would keep it open (the Loader while working, the Builder after its
/// unsaved-changes check). Exceptions that repeat while it closes (a layout or render fault recurs on every
/// pass) are handled without a line each: one line after the window has closed gives their count. Dialogs the
/// window opens run nested inside its modal loop, so the same scope covers them; closing the window closes
/// them too.</para>
/// </remarks>
public static class WindowGuard
{
    internal const string FaultTemplate =
        "{Window} window: unhandled exception on the UI thread; it was handled and the window closed so Revit keeps running";

    internal const string RepeatsTemplate =
        "{Window} window: {Count} more unhandled exception(s) while it closed after the error; handled, not logged one by one";

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
        var repeats = 0;

        void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            if (!open || e.Handled || !IsRstsToHandle(e.Exception)) return;
            e.Handled = true;
            if (faulted)
            {
                repeats++;
                return;
            }
            faulted = true;
            Log.Error(e.Exception, FaultTemplate, name);
            // Not from inside the handler: the dispatcher is still unwinding the operation that threw.
            dispatcher.BeginInvoke(DispatcherPriority.Send, () => CloseAfterFault(window, name));
        }

        // The last Closing handler to run (after the window's own OnClosing), so a faulted window closes.
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
        if (repeats > 0) Log.Error(RepeatsTemplate, name, repeats);
        return !faulted;
    }

    /// <summary>
    /// True when the net should take <paramref name="exception"/>: a frame of it (or of an inner exception) is
    /// in an RST namespace, or every frame is framework code (.NET, WPF). False when some frame is other code
    /// (Revit's API, another add-in) and none is RST's.
    /// </summary>
    internal static bool IsRstsToHandle(Exception exception)
    {
        var foreign = false;
        foreach (var ex in Chain(exception))
        {
            foreach (var frame in new StackTrace(ex, false).GetFrames())
            {
                var type = frame.GetMethod()?.DeclaringType;
                if (type is null) continue;
                if (IsRstNamespace(type.Namespace)) return true;
                if (!IsFramework(type.Assembly)) foreign = true;
            }
        }
        return !foreign;
    }

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        var pending = new Stack<Exception>();
        pending.Push(exception);
        var seen = 0;
        while (pending.Count > 0 && seen++ < 32)
        {
            var ex = pending.Pop();
            yield return ex;
            if (ex is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions) pending.Push(inner);
            else if (ex.InnerException is { } inner)
                pending.Push(inner);
        }
    }

    private static bool IsRstNamespace(string? ns) =>
        ns is not null && (ns == "RST" || ns.StartsWith("RST.", StringComparison.Ordinal));

    /// <summary>.NET and WPF: the base class library, WindowsBase, PresentationCore/Framework, System.Xaml, UI Automation.</summary>
    private static bool IsFramework(Assembly assembly)
    {
        var n = assembly.GetName().Name ?? "";
        return n is "mscorlib" or "netstandard" or "WindowsBase" or "PresentationCore" or "DirectWriteForwarder" or "ReachFramework"
            || n.StartsWith("System", StringComparison.Ordinal)
            || n.StartsWith("Microsoft.", StringComparison.Ordinal)
            || n.StartsWith("PresentationFramework", StringComparison.Ordinal)
            || n.StartsWith("PresentationUI", StringComparison.Ordinal)
            || n.StartsWith("UIAutomation", StringComparison.Ordinal);
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
