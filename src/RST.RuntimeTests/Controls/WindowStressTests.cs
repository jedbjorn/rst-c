// rst-c's own (not vendored). SC-046 (the crash-path audit's check 8; issue #163): Health crashed Revit from inside
// ShowDialog when its collapsed spinner was Loaded. A screenshot test shows a window and renders it once; this opens
// each RST window in a real nested ShowDialog loop, as its launcher does, with a dispatcher hook that fails the test
// on any unhandled exception, flips its busy, overlay and collapsed states on and off while the loop runs, and
// closes it by timer.

using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using RST.Core.Theming;
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
    private static void RunModal(Window window, IReadOnlyList<(string Name, Action Act)> steps, Action? beforeClose = null)
    {
        var faults = new List<string>();
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
            beforeClose?.Invoke();
            window.Close();
        }, dispatcher);
        var backstop = new DispatcherTimer(TimeSpan.FromSeconds(60), DispatcherPriority.Normal, (s, _) =>
        {
            ((DispatcherTimer)s!).Stop();
            backstopFired = true;
            beforeClose?.Invoke();
            window.Close();
        }, dispatcher);
        window.Closed += (_, _) =>
        {
            ticker.Stop();
            backstop.Stop();
        };

        dispatcher.UnhandledException += OnUnhandled;
        try
        {
            window.ContentRendered += (_, _) => ticker.Start();
            backstop.Start();
            window.ShowDialog();
        }
        finally
        {
            dispatcher.UnhandledException -= OnUnhandled;
        }

        Assert.Empty(faults);
        Assert.False(backstopFired, $"the window did not close on its own; steps done: {string.Join(", ", done)}");
        Assert.Equal(steps.Select(s => s.Name), done);
    }

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
            Activity = days => new RST.Core.Health.ActivityData { RangeDays = days, SessionStartUtc = Now.AddHours(-1) },
        };
        var vm = new HealthViewModel(service, new FakeDialogs(), new HealthViewModelOptions
        {
            // Scan's work runs at once; the scan stays "running" (the spinner shows) until the gate opens.
            RunInBackground = work =>
            {
                work();
                return scanGate.Task;
            },
            StartTimer = (_, _) => new NoTimer(),
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
