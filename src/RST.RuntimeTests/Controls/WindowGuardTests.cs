// rst-c's own (not vendored). SC-045 (issue #163): the launchers' safety net. A window that throws on
// its UI thread while open through WindowGuard is logged at Error and closed, and the exception does not
// escape to the caller (in Revit: the command, then Revit). Outside the window's lifetime, and for an
// exception someone else already handled, the net stays out of the way.

using System.Windows;
using System.Windows.Threading;
using RST.Core.Logging;
using RST.RuntimeTests.Support;
using RST.UI.Dialogs;
using Xunit;

namespace RST.RuntimeTests.Controls;

// The theme collection runs alone, so the process-wide Log.Logger swap below sees only these tests.
[Collection(WpfThemeCollection.Name)]
public class WindowGuardTests
{
    private static void Sta(Action action) => Screenshots.RunSta(() =>
    {
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            action();
        }
        finally
        {
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    });

    private static List<LogEvent> CaptureLog(Action action)
    {
        var events = new List<LogEvent>();
        var previous = Log.Logger;
        Log.Logger = new Logger(LogLevel.Verbose, new ListSink(events));
        try
        {
            action();
        }
        finally
        {
            Log.Logger = previous;
        }
        return events;
    }

    /// <summary>A small window shown off-screen, closed by a backstop after <paramref name="backstop"/> so a broken net fails rather than hangs.</summary>
    private static Window OffScreen(TimeSpan backstop, Action? onBackstop = null)
    {
        var window = new Window
        {
            Width = 120,
            Height = 80,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        var timer = new DispatcherTimer(backstop, DispatcherPriority.Normal, (s, _) =>
        {
            ((DispatcherTimer)s!).Stop();
            onBackstop?.Invoke();
            if (window.IsVisible) window.Close();
        }, window.Dispatcher);
        window.Closed += (_, _) => timer.Stop();
        timer.Start();
        return window;
    }

    private static List<LogEvent> Faults(List<LogEvent> events) =>
        events.Where(e => e.MessageTemplate == WindowGuard.FaultTemplate).ToList();

    [Fact]
    public void LoadedThrows_IsLoggedAtError_TheWindowCloses_AndNothingEscapes() => Sta(() =>
    {
        var boom = new InvalidOperationException("SC-045 test: Loaded threw");
        var backstopFired = false;
        var closed = false;
        var result = true;
        Exception? escaped = null;

        var events = CaptureLog(() =>
        {
            var window = OffScreen(TimeSpan.FromSeconds(10), () => backstopFired = true);
            window.Loaded += (_, _) => throw boom;
            window.Closed += (_, _) => closed = true;
            escaped = Record.Exception(() => result = WindowGuard.ShowDialog(window, "Test"));
        });

        Assert.Null(escaped);
        Assert.False(result);
        Assert.True(closed);
        Assert.False(backstopFired, "the window closed only on the test's backstop, not on the safety net");
        var fault = Assert.Single(Faults(events));
        Assert.Equal(LogLevel.Error, fault.Level);
        Assert.Same(boom, fault.Exception);
        Assert.Equal("Test", fault.PropertyValues[0]);
    });

    [Fact]
    public void AWindowThatRefusesToClose_StillClosesAfterAFault() => Sta(() =>
    {
        var backstopFired = false;
        var result = true;
        var events = CaptureLog(() =>
        {
            var refusing = true;
            var window = OffScreen(TimeSpan.FromSeconds(10), () =>
            {
                backstopFired = true;
                refusing = false; // so the backstop can close it when the net could not
            });
            window.Closing += (_, e) => e.Cancel = refusing; // the Loader while it works, the Builder's unsaved check
            window.Loaded += (_, _) => throw new InvalidOperationException("SC-045 test");
            result = WindowGuard.ShowDialog(window, "Test");
        });

        Assert.False(result);
        Assert.False(backstopFired, "the guard's close was refused");
        Assert.Single(Faults(events));
    });

    [Fact]
    public void ANormalClose_ReturnsTrue_AndLogsNothing() => Sta(() =>
    {
        var result = false;
        var events = CaptureLog(() =>
        {
            var window = OffScreen(TimeSpan.FromSeconds(10));
            window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.Background, window.Close);
            result = WindowGuard.ShowDialog(window, "Test");
        });

        Assert.True(result);
        Assert.DoesNotContain(events, e => e.Level >= LogLevel.Error);
    });

    [Fact]
    public void AnExceptionAlreadyHandled_IsLeftAlone() => Sta(() =>
    {
        var earlier = 0;
        var result = false;
        var events = CaptureLog(() =>
        {
            // Subscribed first, as Revit or another add-in would be: it handles the exception itself.
            void Earlier(object s, DispatcherUnhandledExceptionEventArgs e)
            {
                earlier++;
                e.Handled = true;
            }
            Dispatcher.CurrentDispatcher.UnhandledException += Earlier;
            try
            {
                var window = OffScreen(TimeSpan.FromMilliseconds(500));
                window.Loaded += (_, _) => throw new InvalidOperationException("SC-045 test");
                result = WindowGuard.ShowDialog(window, "Test");
            }
            finally
            {
                Dispatcher.CurrentDispatcher.UnhandledException -= Earlier;
            }
        });

        Assert.Equal(1, earlier);
        Assert.True(result); // the backstop closed it; the net did not claim the exception
        Assert.Empty(Faults(events));
    });

    [Fact]
    public void AfterTheWindowCloses_TheNetIsGone() => Sta(() =>
    {
        bool? handledWhenSeen = null;
        var events = CaptureLog(() =>
        {
            var window = OffScreen(TimeSpan.FromSeconds(10));
            window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.Background, window.Close);
            WindowGuard.ShowDialog(window, "Test");

            // Subscribed after the net: had the net still been subscribed, it would have run first and handled it.
            void Later(object s, DispatcherUnhandledExceptionEventArgs e)
            {
                handledWhenSeen = e.Handled;
                e.Handled = true;
            }
            Dispatcher.CurrentDispatcher.UnhandledException += Later;
            try
            {
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Normal,
                    new Action(() => throw new InvalidOperationException("SC-045 test: after close")));
                WindowCapture.DoEvents();
            }
            finally
            {
                Dispatcher.CurrentDispatcher.UnhandledException -= Later;
            }
        });

        Assert.False(handledWhenSeen);
        Assert.Empty(Faults(events));
    });

    private sealed class ListSink(List<LogEvent> events) : ILogSink
    {
        public void Emit(LogEvent logEvent, IReadOnlyDictionary<string, object?> properties)
        {
            lock (events) events.Add(logEvent);
        }

        public void Dispose()
        {
        }
    }
}
