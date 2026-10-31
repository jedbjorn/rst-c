// rst-c's own (not vendored). SC-045 (issue #163): a dispatcher exception while an RST window is open
// inside Revit must not take Revit down. Health crashed Revit when a spinner's storyboard threw from a
// Loaded trigger inside ShowDialog: nothing caught it on the UI thread. The launchers show their window
// through here so a fault is logged and the window closes, and Revit keeps running.

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
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
/// <para><b>When.</b> The net subscribes to <see cref="Dispatcher.UnhandledException"/> immediately before
/// <see cref="Window.ShowDialog"/> and unsubscribes as soon as ShowDialog returns, so it never sees anything
/// raised before the window opens or after it closes. Handlers subscribed before it (Revit's, an add-in's
/// that subscribed earlier) run first, and it leaves alone an exception one of them has handled. Handlers
/// subscribed after it (an add-in that subscribes lazily while the window is open) run after it, and a
/// handler that ignores <c>Handled</c> still runs. An <see cref="Dispatcher.UnhandledExceptionFilter"/> that
/// sets <c>RequestCatch = false</c> bypasses the event, and so the net, entirely. A stack overflow ends the
/// process and cannot be caught by anything.</para>
/// <para><b>Whose (<see cref="Classify"/>).</b> It reads the stack frames of the exception and its inner
/// exceptions, by assembly. A frame in an RST assembly (<c>RST</c>, <c>RST.*</c>) makes the exception RST's,
/// and it is handled. A frame in any other assembly that is not part of the shared framework (Revit's API,
/// another add-in, and the libraries add-ins ship such as System.Reactive or Microsoft.Xaml.Behaviors), with
/// no RST frame, makes it someone else's, and it is left alone. "Shared framework" means an assembly loaded
/// from the .NET runtime's own directory (Microsoft.NETCore.App) or from WPF's (Microsoft.WindowsDesktop.App),
/// checked by location, not by name. A stack of shared-framework frames only is handled too, and logged as
/// unattributed: that is the shape of the #163 crash, whose exception carried no RST frame (it surfaced
/// from WPF's Loaded broadcast in a render pass). Such a fault could in principle come from other WPF
/// content that keeps rendering while the window is open (the ribbon, another add-in's pane); without the
/// net it would crash Revit, so it is handled, and the log line says it was not attributed.</para>
/// <para><b>What.</b> It shows nothing modal. The first exception is logged at Error with its full stack and
/// its classification; the window is marked <see cref="IsClosingAfterFault"/> (the Builder and the Loader
/// skip their own close checks and prompts on it: no Save/Discard question, no save from a faulted state)
/// and closed, queued so the close runs after the dispatcher has finished with the exception. If the
/// close fails, the window is hidden, which ends its modal loop; once ShowDialog has returned the close is
/// tried again and, failing that, the window's content is detached so nothing in it lays out or renders
/// again. Exceptions that repeat while it closes are handled without a line each: one line after the window
/// has closed gives their count. An exception thrown synchronously by ShowDialog itself is handled the same
/// way. Dialogs the window opens run nested inside its modal loop, so the same scope covers them; closing
/// the window closes them too. The handler itself never throws.</para>
/// </remarks>
public static class WindowGuard
{
    internal const string FaultTemplate =
        "{Window} window: unhandled exception on the UI thread ({Owner}); it was handled and the window closed so Revit keeps running";

    internal const string RepeatsTemplate =
        "{Window} window: {Count} more unhandled exception(s) while it closed after the error; handled, not logged one by one";

    internal const string RstFrame = "RST frame";
    internal const string FrameworkOnly = "framework-only, unattributed";
    internal const string FromShowDialog = "thrown by ShowDialog";

    /// <summary>Whose an exception is, from its stack frames.</summary>
    internal enum FaultOwner
    {
        /// <summary>A frame in an RST assembly: handled.</summary>
        Rst,

        /// <summary>Shared-framework frames only (or none): handled, logged as unattributed.</summary>
        FrameworkOnly,

        /// <summary>A frame in another non-framework assembly and none in RST's: left alone.</summary>
        Foreign,
    }

    private static readonly DependencyProperty ClosingAfterFaultProperty =
        DependencyProperty.RegisterAttached("ClosingAfterFault", typeof(bool), typeof(WindowGuard), new PropertyMetadata(false));

    /// <summary>
    /// True while the net is closing <paramref name="window"/> after a fault. A window's OnClosing checks it
    /// first and, when set, skips its own checks and prompts: the close goes through, nothing is saved.
    /// </summary>
    internal static bool IsClosingAfterFault(Window window) => (bool)window.GetValue(ClosingAfterFaultProperty);

    /// <summary>
    /// Shows <paramref name="window"/> modally and returns when it closes: true when it closed normally,
    /// false when the safety net closed it after an unhandled exception (already logged at Error).
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

        void Fault(Exception exception, string owner)
        {
            faulted = true;
            window.SetValue(ClosingAfterFaultProperty, true);
            Log.Error(exception, FaultTemplate, name, owner);
        }

        void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            if (!open || e.Handled) return;
            try
            {
                var owner = Classify(e.Exception);
                if (owner == FaultOwner.Foreign) return;
                e.Handled = true;
                if (faulted)
                {
                    repeats++;
                    return;
                }
                Fault(e.Exception, owner == FaultOwner.Rst ? RstFrame : FrameworkOnly);
                // Not from inside the handler: the dispatcher is still unwinding the operation that threw.
                dispatcher.BeginInvoke(DispatcherPriority.Send, () => CloseAfterFault(window, name));
            }
            catch
            {
                // The net must not throw from the dispatcher's handler, which would take Revit down with it.
                e.Handled = true;
                if (!faulted)
                {
                    faulted = true;
                    try { window.SetValue(ClosingAfterFaultProperty, true); } catch { /* best effort */ }
                    try { dispatcher.BeginInvoke(DispatcherPriority.Send, () => CloseAfterFault(window, name)); } catch { /* best effort */ }
                }
            }
        }

        // The window's own OnClosing runs first and skips its checks on the marker; this clears a Cancel
        // anything else set, so a faulted window closes.
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
        catch (Exception ex)
        {
            // Thrown before or outside the modal loop (OnSourceInitialized, a first layout pass): the window
            // may be up and still faulting, so it is closed here like any other fault.
            if (!faulted) Fault(ex, FromShowDialog);
            else repeats++;
            CloseAfterFault(window, name);
        }
        finally
        {
            open = false;
            dispatcher.UnhandledException -= OnUnhandled;
            window.Closing -= OnClosing;
        }
        if (faulted) EnsureGone(window, name);
        if (repeats > 0) Log.Error(RepeatsTemplate, name, repeats);
        return !faulted;
    }

    /// <summary>
    /// Whose <paramref name="exception"/> is, from the frames of it and its inner exceptions: an RST assembly
    /// frame wins; otherwise a frame outside the shared framework makes it foreign; otherwise framework-only.
    /// </summary>
    internal static FaultOwner Classify(Exception exception)
    {
        var foreign = false;
        foreach (var ex in Chain(exception))
        {
            foreach (var frame in new StackTrace(ex, false).GetFrames())
            {
                var assembly = frame.GetMethod()?.DeclaringType?.Assembly;
                if (assembly is null) continue;
                if (IsRst(assembly)) return FaultOwner.Rst;
                if (!IsSharedFramework(assembly)) foreign = true;
            }
        }
        return foreign ? FaultOwner.Foreign : FaultOwner.FrameworkOnly;
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

    /// <summary>RST's own assemblies: RST.Core, RST.UI, RST.Engine, RST.Bootstrap, RST.Tray (and its tests).</summary>
    private static bool IsRst(Assembly assembly)
    {
        var n = assembly.GetName().Name;
        return n is not null && (n == "RST" || n.StartsWith("RST.", StringComparison.Ordinal));
    }

    /// <summary>
    /// The shared frameworks' directories as loaded in this process: the .NET runtime's (System.Private.CoreLib,
    /// System.*) and WPF's (PresentationFramework, PresentationCore, WindowsBase, System.Xaml).
    /// </summary>
    private static readonly string[] SharedFrameworkDirectories = new[]
        {
            RuntimeEnvironment.GetRuntimeDirectory(),
            DirectoryOf(typeof(object).Assembly),
            DirectoryOf(typeof(Window).Assembly),
            DirectoryOf(typeof(Dispatcher).Assembly),
        }
        .Where(d => !string.IsNullOrEmpty(d))
        .Select(d => Path.TrimEndingDirectorySeparator(Path.GetFullPath(d!)))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static bool IsSharedFramework(Assembly assembly)
    {
        var dir = DirectoryOf(assembly);
        if (string.IsNullOrEmpty(dir)) return false;
        dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        return SharedFrameworkDirectories.Any(d => string.Equals(d, dir, StringComparison.OrdinalIgnoreCase));
    }

    private static string? DirectoryOf(Assembly assembly)
    {
        if (assembly.IsDynamic) return null;
        try
        {
            var location = assembly.Location;
            return string.IsNullOrEmpty(location) ? null : Path.GetDirectoryName(location);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Closes the faulted window; if that fails, hides it, which ends its modal loop.</summary>
    private static void CloseAfterFault(Window window, string name)
    {
        try
        {
            window.Close();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Window} window: close after an error failed; hiding it", name);
            try
            {
                window.Hide();
            }
            catch (Exception hideEx)
            {
                Log.Error(hideEx, "{Window} window: hide after an error failed", name);
            }
        }
    }

    /// <summary>After ShowDialog: a faulted window that is still up is closed again, or its content detached.</summary>
    private static void EnsureGone(Window window, string name)
    {
        try
        {
            if (PresentationSource.FromVisual(window) is null) return; // closed: its tree no longer lays out or renders
            window.Close();
            if (PresentationSource.FromVisual(window) is null) return;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Window} window: second close after an error failed", name);
        }
        try
        {
            window.Content = null;
            Log.Error("{Window} window: could not be closed after an error; its content was detached", name);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Window} window: detaching its content after an error failed", name);
        }
    }
}
