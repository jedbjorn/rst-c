// rst-c's own (not vendored). SC-046 R7 and R8: the Health window's scan and clean outlive the window. R7: closed during
// a scan or clean (the title-bar X, or WindowGuard closing it), the work still finishes, since a clean's deletions cannot
// be undone, but its result or failure is dropped: no render, no Activity re-read, no toast and no toast timer, and a
// work-end update queued on the dispatcher before the close does nothing when it arrives. R8: the end of the work is
// raised on the worker; the view-model passes it to the dispatcher it was built on even when SynchronizationContext.Current
// was null there (Revit's external-command entry has no contract to provide one), so a window reopened while an older
// scan runs gets its PropertyChanged and its command requery on its own thread, and Scan and Clean come back. Each test
// watches the dispatcher's UnhandledException with no WindowGuard around the window: any fault that would have needed
// the modal's net is recorded, not swallowed.

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using RST.Core.Health;
using RST.Core.Logging;
using RST.Core.Theming;
using RST.RuntimeTests.Support;
using RST.UI.Health;
using RST.UI.Mvvm;
using RST.UI.Theming;
using Xunit;

namespace RST.RuntimeTests.Health;

[Collection(WpfThemeCollection.Name)]
public class HealthWorkLifetimeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>An STA thread with a dispatcher and, as in Revit's external-command entry, no SynchronizationContext.</summary>
    private static void Sta(Action action) => Screenshots.RunSta(() =>
    {
        var culture = CultureInfo.GetCultureInfo("en-US");
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        SynchronizationContext.SetSynchronizationContext(null);
        var faults = new List<Exception>();
        void OnFault(object? sender, DispatcherUnhandledExceptionEventArgs e)
        {
            faults.Add(e.Exception);
            e.Handled = true;
        }
        var dispatcher = Dispatcher.CurrentDispatcher;
        dispatcher.UnhandledException += OnFault;
        try
        {
            action();
            Assert.Empty(faults);
        }
        finally
        {
            dispatcher.UnhandledException -= OnFault;
            dispatcher.InvokeShutdown();
        }
    });

    private sealed class NoTimer : IDisposable
    {
        public void Dispose()
        {
        }
    }

    /// <summary>The production runner (Task.Run); every toast timer the model starts is counted.</summary>
    private static HealthViewModel ViewModel(FakeHealthService service, Action? onTimer = null) =>
        new(service, new FakeDialogs { ConfirmResult = true }, new HealthViewModelOptions
        {
            Culture = CultureInfo.GetCultureInfo("en-US"),
            Zone = TimeZoneInfo.Utc,
            ToastSchedule = (_, _) =>
            {
                onTimer?.Invoke();
                return new NoTimer();
            },
        });

    private static HealthWindow Open(HealthViewModel vm)
    {
        vm.Load();
        var window = new HealthWindow(vm);
        WindowCapture.ShowOffScreen(window);
        Assert.True(PumpUntil(() => window.FirstFrameDrawn, Timeout), "the first frame");
        return window;
    }

    /// <summary>Lets the dispatcher run until <paramref name="condition"/> holds.</summary>
    private static bool PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (condition()) return true;
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(20), DispatcherPriority.Background,
                (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
            timer.Start();
            Dispatcher.PushFrame(frame);
            timer.Stop();
        }
        return condition();
    }

    /// <summary>Runs <paramref name="action"/> as a click does: from the dispatcher, under its synchronization context.</summary>
    private static void Click(Action action) => Dispatcher.CurrentDispatcher.InvokeAsync(action);

    private static LogScope CaptureLog() => new();

    private sealed class LogScope : IDisposable
    {
        private readonly Logger _previous = Log.Logger;

        public LogScope() => Log.Logger = new Logger(LogLevel.Verbose, new ListSink(Events));

        public List<LogEvent> Events { get; } = [];

        public bool Has(string text)
        {
            lock (Events) return Events.Any(e => e.RenderMessage().Contains(text, StringComparison.Ordinal));
        }

        public List<LogEvent> AtLeast(LogLevel level)
        {
            lock (Events) return Events.Where(e => e.Level >= level).ToList();
        }

        public void Dispose() => Log.Logger = _previous;
    }

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

    // ---- R7: work that ends after the window closed ---------------------------------------------------------------

    [Theory]
    [InlineData("Scan", false)]
    [InlineData("Scan", true)]
    [InlineData("Clean", false)]
    [InlineData("Clean", true)]
    public void A_scan_or_clean_that_ends_after_the_window_closed_finishes_but_shows_nothing(string work, bool fails) => Sta(() =>
    {
        RstTheme.Apply(UiTheme.Dark);
        using var log = CaptureLog();
        using var gate = new ManualResetEventSlim(false);
        var service = new FakeHealthService
        {
            Targets = HealthSamples.Targets(),
            ScanResult = HealthSamples.Snapshot(),
            CleanResult = HealthSamples.CleanResult(),
            TracksWork = true,
            WorkGate = gate,
        };
        if (fails)
        {
            service.ScanError = new IOException("WMI stopped answering");
            service.CleanError = new IOException("the cache folder is gone");
        }
        var timers = 0;
        var vm = ViewModel(service, () => timers++);
        var window = Open(vm);
        var closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            // The Activity tab has been shown, so a scan's end would re-read it.
            vm.SelectedTab = HealthViewModel.ActivityTab;
            vm.SelectedTab = HealthViewModel.HealthTab;
            Assert.Equal(HealthFormat.Missing, vm.Device);
            var activityReads = service.ActivityCalls;

            if (work == "Scan")
            {
                Click(() => window.ScanButton.Command.Execute(null));
            }
            else
            {
                Click(() =>
                {
                    window.CleanButton.Command.Execute(null);
                    vm.JunkRunCommand.Execute(null); // every target ticked; the confirm says yes
                });
            }
            Assert.True(PumpUntil(() => service.WorkStarted == 1 && service.IsWorkRunning, Timeout), work + " is running");
            Assert.True(work == "Scan" ? vm.IsScanning : vm.Busy.IsBusy);
            Assert.False(vm.CloseCommand.CanExecute(null));

            // Closed as the title-bar X (or WindowGuard) closes it: Close is disabled while the work runs. The window
            // disposes its model.
            window.Close();
            Assert.True(closed);
            var timersAtClose = timers;
            var changes = new List<string>();
            vm.PropertyChanged += (_, e) => changes.Add("vm." + e.PropertyName);
            vm.Toast.PropertyChanged += (_, e) => changes.Add("toast." + e.PropertyName);
            vm.Busy.PropertyChanged += (_, e) => changes.Add("busy." + e.PropertyName);
            vm.Activity.PropertyChanged += (_, e) => changes.Add("activity." + e.PropertyName);

            // The work ends, with success or with a failure, after the window and its modal net are gone.
            gate.Set();
            Assert.True(PumpUntil(() => !service.IsWorkRunning && log.Has("finished after the window closed"), Timeout),
                "the late end is noticed");
            WindowCapture.DoEvents();
            Thread.Sleep(100);
            WindowCapture.DoEvents();

            // It ran to its end (nothing is rolled back) ...
            Assert.Equal(work == "Scan" ? 1 : 0, service.ScanCalls);
            Assert.Equal(work == "Clean" ? 1 : 0, service.CleanCalls.Count);

            // ... and nothing was shown: no render, no Activity re-read, no toast, no toast timer, no overlay change.
            Assert.Empty(changes);
            Assert.Equal(timersAtClose, timers);
            Assert.False(vm.Toast.IsOpen);
            Assert.Equal(HealthFormat.Missing, vm.Device);
            Assert.Equal(activityReads, service.ActivityCalls);

            // Nothing failed but the work itself (logged as before): no guard fault, no dispatcher fault (Sta asserts).
            var problems = log.AtLeast(LogLevel.Warning);
            if (fails)
            {
                var failure = Assert.Single(problems);
                Assert.Equal(LogLevel.Error, failure.Level);
                Assert.Contains(work == "Scan" ? "Scan System failed" : "Clean Junk Files failed", failure.RenderMessage(), StringComparison.Ordinal);
            }
            else
            {
                Assert.Empty(problems);
            }
            lock (log.Events) Assert.DoesNotContain(log.Events, e => e.MessageTemplate == UiGuard.FaultTemplate);
        }
        finally
        {
            gate.Set();
            if (!closed) window.Close();
        }
    });

    [Fact]
    public void A_work_end_queued_before_the_window_closed_does_nothing_when_it_arrives() => Sta(() =>
    {
        RstTheme.Apply(UiTheme.Light);
        using var log = CaptureLog();
        var service = new FakeHealthService { IsWorkRunning = true }; // another window's scan
        var vm = ViewModel(service);
        var window = Open(vm);
        Assert.False(vm.CanStartWork);
        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        // The worker ends the work: the update is queued on this dispatcher, not run on the worker.
        var end = Task.Run(service.FinishWork);
        while (!end.IsCompleted) Thread.Sleep(10); // not a pumping wait: the queued update must not run here
        end.GetAwaiter().GetResult();
        Assert.Empty(changes);

        // The window closes before the dispatcher gets to it; then it runs, and does nothing.
        window.Close();
        changes.Clear();
        WindowCapture.DoEvents();
        PumpUntil(() => false, TimeSpan.FromMilliseconds(200));
        Assert.Empty(changes);
        lock (log.Events) Assert.DoesNotContain(log.Events, e => e.Level >= LogLevel.Warning);
    });

    // ---- R8: the end of the work reaches the model's own dispatcher -----------------------------------------------

    [Fact]
    public void A_window_reopened_while_an_older_scan_runs_is_told_on_its_own_dispatcher_and_Scan_comes_back() => Sta(() =>
    {
        RstTheme.Apply(UiTheme.Light);
        using var log = CaptureLog();
        using var gate = new ManualResetEventSlim(false);
        var service = new FakeHealthService { ScanResult = HealthSamples.Snapshot(), TracksWork = true, WorkGate = gate };
        var ui = Environment.CurrentManagedThreadId;

        // The first window, built with no SynchronizationContext, starts a scan and is closed while it runs.
        Assert.Null(SynchronizationContext.Current);
        var first = ViewModel(service);
        var firstWindow = Open(first);
        Click(() => firstWindow.ScanButton.Command.Execute(null));
        Assert.True(PumpUntil(() => service.WorkStarted == 1 && service.IsWorkRunning, Timeout), "the first window's scan runs");
        firstWindow.Close();

        // Reopened, again with no SynchronizationContext, while that scan still runs: Scan and Clean wait for it.
        Assert.Null(SynchronizationContext.Current);
        var second = ViewModel(service);
        var secondWindow = Open(second);
        try
        {
            Assert.False(second.CanStartWork);
            Assert.False(secondWindow.ScanButton.IsEnabled);
            Assert.False(secondWindow.CleanButton.IsEnabled);
            var notified = new List<(string? Name, int Thread)>();
            second.PropertyChanged += (_, e) =>
            {
                lock (notified) notified.Add((e.PropertyName, Environment.CurrentManagedThreadId));
            };
            var requeried = new List<int>();
            EventHandler onRequery = (_, _) =>
            {
                lock (requeried) requeried.Add(Environment.CurrentManagedThreadId);
            };
            CommandManager.RequerySuggested += onRequery; // weakly held: kept alive below

            // The scan ends on its worker, which raises WorkFinished there.
            gate.Set();
            bool Told()
            {
                lock (notified) return notified.Any(n => n.Name == nameof(HealthViewModel.CanStartWork));
            }
            Assert.True(PumpUntil(() => service.FinishedOnThread != 0 && Told() && secondWindow.ScanButton.IsEnabled && secondWindow.CleanButton.IsEnabled, Timeout),
                "the reopened window is told, and Scan and Clean come back");
            Assert.NotEqual(ui, service.FinishedOnThread);
            Assert.True(second.CanStartWork);
            lock (notified)
            {
                Assert.Contains(notified, n => n.Name == nameof(HealthViewModel.CanStartWork));
                Assert.All(notified, n => Assert.Equal(ui, n.Thread));
            }
            lock (requeried)
            {
                Assert.NotEmpty(requeried);
                Assert.All(requeried, t => Assert.Equal(ui, t));
            }
            CommandManager.RequerySuggested -= onRequery;
            GC.KeepAlive(onRequery);

            // The first window's scan ended after it closed: nothing rendered there.
            Assert.True(PumpUntil(() => log.Has("finished after the window closed"), Timeout), "the closed window drops its scan");
            Assert.Equal(HealthFormat.Missing, first.Device);
            Assert.False(first.IsScanning);
            lock (log.Events) Assert.DoesNotContain(log.Events, e => e.Level >= LogLevel.Warning);
        }
        finally
        {
            gate.Set();
            secondWindow.Close();
        }
    });
}
