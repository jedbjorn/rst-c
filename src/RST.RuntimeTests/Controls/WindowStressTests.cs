// rst-c's own (not vendored). SC-046 (the crash-path audit's check 8; issue #163): Health crashed Revit from inside
// ShowDialog when its collapsed spinner was Loaded. A screenshot test shows a window and renders it once; this opens
// each RST window in a real nested ShowDialog loop, as its launcher does, with a dispatcher hook that fails the test
// on any unhandled exception, flips its busy, overlay and collapsed states on and off while the loop runs, and
// closes it by timer. SC-046 / #166 N5: swallowed guard faults also fail, and a cancelled Close
// must not defeat the modal timeout (Hide ends the loop, including any prompt opened by Close).

using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using RST.Core.Theming;
using RST.Core.Logging;
using RST.RuntimeTests.Builder;
using RST.RuntimeTests.Health;
using RST.RuntimeTests.Loader;
using RST.RuntimeTests.Support;
using RST.UI.Builder;
using RST.UI.Controls;
using RST.UI.Dialogs;
using RST.UI.Health;
using RST.UI.Loader;
using RST.UI.Theming;
using RST.UI.Mvvm;
using Xunit;

namespace RST.RuntimeTests.Controls;

[Collection(WpfThemeCollection.Name)]
public class WindowStressTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);

    /// <summary>How long each step's state shows before the next (several layout and render passes).</summary>
    private static readonly TimeSpan StepGap = TimeSpan.FromMilliseconds(60);

    public static TheoryData<UiTheme> Themes => new() { UiTheme.Light, UiTheme.Dark };

    private sealed class NoTimer : IDisposable
    {
        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Shows <paramref name="window"/> with ShowDialog and runs <paramref name="steps"/> one per tick inside its modal
    /// loop, then closes it. Every unhandled dispatcher exception is collected (and handled, so the test reports it
    /// rather than the host dying); a backstop closes the window if the steps stall.
    /// </summary>
    private static void RunModal(Window window, IReadOnlyList<(string Name, Action Act)> steps, Action? beforeClose = null, TimeSpan? timeout = null)
    {
        var faults = new List<string>();
        var events = new List<LogEvent>();
        var previousLog = Log.Logger;
        using var logger = new Logger(LogLevel.Verbose, new ListSink(events));
        void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            faults.Add(e.Exception.ToString());
            e.Handled = true;
        }

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000;
        window.Top = -32000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;

        var done = new List<string>();
        var backstopFired = false;
        var next = 0;
        var closing = false;
        var dispatcher = window.Dispatcher;
        var ticker = new DispatcherTimer(StepGap, DispatcherPriority.Background, (s, _) =>
        {
            if (next < steps.Count)
            {
                var (name, act) = steps[next++];
                act();
                done.Add(name);
                return;
            }
            ((DispatcherTimer)s!).Stop();
            closing = true;
            try
            {
                beforeClose?.Invoke();
                window.Close();
            }
            finally { closing = false; }
        }, dispatcher);
        var backstop = new DispatcherTimer(timeout ?? TimeSpan.FromSeconds(60), DispatcherPriority.Normal, (s, _) =>
        {
            ((DispatcherTimer)s!).Stop();
            backstopFired = true;
            ticker.Stop();
            // Queue this before Close: Closing may itself open a nested unsaved-changes prompt.
            // The nested loop runs the queued hide, so Close cannot hold us inside that prompt.
            dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => ModalCleanup.HideTree(window)));
            try
            {
                if (!closing)
                {
                    beforeClose?.Invoke();
                    window.Close();
                }
            }
            finally
            {
                ModalCleanup.HideTree(window);
            }
        }, dispatcher);
        window.Closed += (_, _) =>
        {
            ticker.Stop();
            backstop.Stop();
        };

        dispatcher.UnhandledException += OnUnhandled;
        Log.Logger = logger;
        try
        {
            window.ContentRendered += (_, _) => ticker.Start();
            backstop.Start();
            window.ShowDialog();
            WindowCapture.DoEvents(); // keep both nets for queued work after the modal returns
        }
        finally
        {
            ticker.Stop();
            backstop.Stop();
            ModalCleanup.HideTree(window);
            dispatcher.UnhandledException -= OnUnhandled;
            Log.Logger = previousLog;
        }

        lock (events)
            faults.AddRange(events.Where(IsGuardFault).Select(e => e.RenderMessage() + "\n" + e.Exception));
        Assert.True(faults.Count == 0, "guard-swallowed faults:\n" + string.Join("\n---\n", faults));
        Assert.False(backstopFired, $"the window did not close on its own; steps done: {string.Join(", ", done)}");
        Assert.Equal(steps.Select(s => s.Name), done);
    }

    private static bool IsGuardFault(LogEvent e) => e.Level >= LogLevel.Error &&
        // RelayCommand reports through UiGuard.Report and uses the same FaultTemplate.
        (e.MessageTemplate == UiGuard.FaultTemplate || e.MessageTemplate == TaskGuard.FaultTemplate ||
         e.MessageTemplate == WindowGuard.FaultTemplate);

    private sealed class ListSink(List<LogEvent> events) : ILogSink
    {
        public void Emit(LogEvent logEvent, IReadOnlyDictionary<string, object?> properties)
        {
            lock (events) events.Add(logEvent);
        }

        public void Dispose() { }
    }

    [Theory]
    [InlineData("handler")]
    [InlineData("command")]
    [InlineData("task")]
    public void The_harness_fails_on_guard_swallowed_faults(string route) => Sta(UiTheme.Light, () =>
    {
        void Fail() => throw new InvalidOperationException("SC-046 stress harness self-test");
        var error = Record.Exception(() => RunModal(new Window(), [("fault", () =>
        {
            if (route == "handler") UiGuard.Run("stress self-test", Fail);
            else if (route == "command") new RelayCommand(Fail).Execute(null);
            else Task.FromException(new InvalidOperationException("SC-046 stress harness self-test")).Forget("stress self-test");
        })]));
        Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(error);
        Assert.Contains("SC-046 stress harness self-test", error!.Message);
    });

    [Fact]
    public void The_backstop_ends_the_modal_even_when_Closing_cancels() => Sta(UiTheme.Light, () =>
    {
        var window = new Window();
        window.Closing += (_, e) => e.Cancel = true;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var error = Record.Exception(() => RunModal(window, [], timeout: TimeSpan.FromMilliseconds(250)));
        Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(error);
        Assert.Contains("did not close on its own", error!.Message);
        Assert.False(window.IsVisible);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10));
    });

    [Fact]
    public void The_backstop_also_ends_a_prompt_opened_by_Closing() => Sta(UiTheme.Light, () =>
    {
        var window = new Window();
        window.Closing += (_, e) =>
        {
            var prompt = new Window { Owner = window };
            prompt.ShowDialog();
            e.Cancel = true;
        };
        var error = Record.Exception(() => RunModal(window, [], timeout: TimeSpan.FromMilliseconds(250)));
        Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(error);
        Assert.Contains("did not close on its own", error!.Message);
        Assert.False(window.IsVisible);
    });

    private static void Sta(UiTheme theme, Action action) => Screenshots.RunSta(() =>
    {
        var culture = CultureInfo.GetCultureInfo("en-US");
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        // As in Revit: a dispatcher synchronization context, so awaits come back to the UI thread.
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            RstTheme.Apply(theme);
            action();
        }
        finally
        {
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void Health_in_a_modal_loop_survives_scanning_cleaning_the_Junk_modal_and_the_tabs(UiTheme theme) => Sta(theme, () =>
    {
        var scanGate = new TaskCompletionSource();
        var service = new FakeHealthService
        {
            Snapshot = HealthSamples.Snapshot(),
            ScanResult = HealthSamples.Snapshot(),
            Targets = [new RST.Core.Profiles.CleanupTarget { Id = "journals", Name = "Journals", Path = "%LocalAppData%" }],
            Activity = () => new RST.Core.Health.ActivityData { Model = RST.Core.Telemetry.Rollups.ModelActivity.Empty },
        };
        var vm = new HealthViewModel(service, new FakeDialogs(), new HealthViewModelOptions
        {
            // Scan's work runs at once; the scan stays "running" (the spinner shows) until the gate opens.
            RunInBackground = work =>
            {
                work();
                return scanGate.Task;
            },
            Now = () => Now,
            Culture = CultureInfo.GetCultureInfo("en-US"),
            Zone = TimeZoneInfo.Utc,
            ToastSchedule = (_, _) => new NoTimer(),
        });
        vm.Load();
        var window = new HealthWindow(vm);

        RunModal(window,
        [
            ("scan starts: the Scan button's spinner shows", () => vm.ScanCommand.Execute(null)),
            ("scan ends: the spinner collapses", () => scanGate.SetResult()),
            ("sections expand", () => { foreach (var s in vm.Sections) s.IsExpanded = true; }),
            ("sections collapse", () => { foreach (var s in vm.Sections) s.IsExpanded = false; }),
            ("busy overlay on", () => vm.Busy.Show("Cleaning", "Removing junk files…")),
            ("busy overlay off", () => vm.Busy.Hide()),
            ("Junk modal opens", vm.OpenJunk),
            ("Junk modal closes", vm.CancelJunk),
            ("toast shows", () => vm.Toast.Show("SC-046", BannerKind.Error, title: "Stress")),
            ("toast hides", vm.Toast.Dismiss),
            ("Activity tab", () => vm.SelectedTab = HealthViewModel.ActivityTab),
            ("range 1M", () => vm.Ranges[1].IsSelected = true),
            ("Health tab", () => vm.SelectedTab = HealthViewModel.HealthTab),
            ("busy overlay on again", () => vm.Busy.Show("Cleaning")),
            ("busy overlay off again", () => vm.Busy.Hide()),
        ]);
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_Loader_in_a_modal_loop_survives_selection_busy_and_toasts(UiTheme theme) => Sta(theme, () =>
    {
        var backend = LoaderFixtures.Backend();
        var dialogs = new LoaderTestDialogs();
        var vm = new ProfileLoaderViewModel(backend, dialogs, dialogs, new ManualClock().Schedule, _ => { });
        vm.LoadData(); // as the launcher does: the active profile is selected once the window has rendered
        var window = new ProfileLoaderWindow(vm);

        RunModal(window,
        [
            ("the active profile is selected", () => Assert.NotNull(vm.SelectedCard)),
            ("no selection: the detail collapses", () => vm.SelectedCard = null),
            ("another profile", () => vm.SelectedCard = vm.Profiles[1]),
            ("a profile with presets", () => vm.SelectedCard = vm.Profiles[^1]),
            ("busy overlay on", () => vm.Busy.Show("Loading Structure")),
            ("busy overlay off", () => vm.Busy.Hide()),
            ("toast shows", () => vm.Toast.Show("SC-046", BannerKind.Warning, title: "Stress")),
            ("toast hides", vm.Toast.Dismiss),
            ("back to the first", () => vm.SelectedCard = vm.Profiles[0]),
            ("busy overlay on again", () => vm.Busy.Show("Unloading")),
            ("busy overlay off again", () => vm.Busy.Hide()),
        ]);
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_Builder_in_a_modal_loop_survives_panels_dirty_state_and_toasts(UiTheme theme) => Sta(theme, () =>
    {
        var dialogs = new FakeDialogs();
        var vm = new ProfileBuilderViewModel(BuilderFixtures.Backend(), dialogs, new ManualClock().Schedule);
        vm.Initialize();
        var window = new ProfileBuilderWindow(vm);

        RunModal(window,
        [
            ("a panel is added", () => vm.AddPanelCommand.Execute(null)),
            ("the profile is renamed: dirty", () => vm.ProfileName = "Stress"),
            ("another panel", () => vm.AddPanelCommand.Execute(null)),
            ("the first panel is selected", () => vm.SelectPanel(0)),
            ("a panel moves down", () => vm.MovePanel(0, 1)),
            ("toast shows", () => vm.Toast.Show("SC-046", BannerKind.Success)),
            ("toast hides", vm.Toast.Dismiss),
            ("the name goes back", () => vm.ProfileName = ""),
        ],
        // The profile is dirty: the close asks, and Discard & Exit lets it close.
        beforeClose: () => dialogs.ChooseAnswers.Enqueue(DialogChoice.Secondary));
    });
}
