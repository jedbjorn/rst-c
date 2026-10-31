// rst-c's own (not vendored). SC-046 (follow-up to issue #163): failures in RST's commands, code-behind handlers,
// binding setters and async work are logged at Error with where they happened, and go no further. A command's
// failure also says so in its window's toast. Each test names the crash-path audit finding it covers.

using System.Globalization;
using System.Windows.Threading;
using RST.Core.Activation;
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
using RST.UI.Mvvm;
using RST.UI.Resources;
using Xunit;

namespace RST.RuntimeTests.Controls;

// The theme collection runs alone, so the process-wide Log.Logger swap sees only these tests.
[Collection(WpfThemeCollection.Name)]
public class CrashPathHardeningTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);

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

    private static List<LogEvent> Faults(List<LogEvent> events) =>
        events.Where(e => e.MessageTemplate == UiGuard.FaultTemplate).ToList();

    private static string SiteOf(LogEvent e) => (string)e.PropertyValues[0]!;

    private sealed class NoTimer : IDisposable
    {
        public void Dispose()
        {
        }
    }

    /// <summary>A view-model with a toast, whose commands fail.</summary>
    private sealed class ToastHost : IHasToast
    {
        public TransientMessage Toast { get; } = new((_, _) => new NoTimer());

        public void Fail() => throw new InvalidOperationException("SC-046 test: the command failed");

        public void FailWith(string what) => throw new InvalidOperationException("SC-046 test: " + what);

        public void Cancel() => throw new OperationCanceledException();

        public List<string> Steps { get; } = [];

        public async Task FailLaterAsync()
        {
            Steps.Add("before the await");
            await Task.Yield();
            Steps.Add("after the await");
            throw new InvalidOperationException("SC-046 test: failed after the first await");
        }
    }

    /// <summary>The family dialogs, with a Confirm that cannot open.</summary>
    private sealed class BrokenConfirmDialogs : IDialogService
    {
        public void ShowMessage(string title, string message, string automationId = MessageDialog.MessageId)
        {
        }

        public bool Confirm(string title, string message, IReadOnlyList<string> items, string confirmText, bool danger, string automationId) =>
            throw new InvalidOperationException("SC-046 test: the confirm could not open");

        public DialogChoice Choose(string title, string message, string primaryText, string secondaryText, string automationId) => DialogChoice.Cancel;

        public RST.Core.Colors.Rgb? PickColor(RST.Core.Colors.Rgb current, IReadOnlyList<RST.Core.Colors.Rgb> palette, string title) => null;

        public string? OpenFile(string title, string filter) => null;

        public string? SaveFile(string title, string filter, string fileName) => null;
    }

    // ---- R2: RelayCommand -------------------------------------------------------------------------

    [Fact]
    public void R2_a_command_that_throws_is_logged_with_its_site_and_toasted_and_nothing_escapes()
    {
        var host = new ToastHost();
        var command = new RelayCommand(host.Fail);
        Exception? escaped = null;
        var events = CaptureLog(() => escaped = Record.Exception(() => command.Execute(null)));

        Assert.Null(escaped);
        var fault = Assert.Single(Faults(events));
        Assert.Equal(LogLevel.Error, fault.Level);
        Assert.Equal("ToastHost: host.Fail", SiteOf(fault));
        Assert.Contains("the command failed", fault.Exception!.Message);
        Assert.True(host.Toast.IsOpen);
        Assert.Equal(BannerKind.Error, host.Toast.Kind);
        Assert.Equal(UiStrings.Common_ActionFailed, host.Toast.Text);
    }

    [Fact]
    public void R2_a_parameter_command_that_throws_is_guarded_the_same_way()
    {
        var host = new ToastHost();
        var command = new RelayCommand<string>(host.FailWith);
        var events = CaptureLog(() => command.Execute("with a parameter"));

        var fault = Assert.Single(Faults(events));
        Assert.Equal("ToastHost: host.FailWith", SiteOf(fault));
        Assert.True(host.Toast.IsOpen);
    }

    [Fact]
    public void R2_a_lambda_command_names_the_type_that_wrote_it_and_without_a_toast_it_is_only_logged()
    {
        var calls = 0;
        var command = new RelayCommand(() =>
        {
            calls++;
            throw new FormatException("SC-046 test");
        });
        var events = CaptureLog(() => command.Execute(null));

        Assert.Equal(1, calls);
        var fault = Assert.Single(Faults(events));
        Assert.StartsWith("CrashPathHardeningTests: () => {", SiteOf(fault));
    }

    [Fact]
    public void R2_a_cancelled_command_is_not_an_error()
    {
        var host = new ToastHost();
        var events = CaptureLog(() => new RelayCommand(host.Cancel).Execute(null));

        Assert.DoesNotContain(events, e => e.Level >= LogLevel.Warning);
        Assert.Contains(events, e => e.MessageTemplate == UiGuard.CancelledTemplate && e.Level == LogLevel.Debug);
        Assert.False(host.Toast.IsOpen);
    }

    [Fact]
    public void R2_a_command_that_may_not_run_does_not_run()
    {
        var ran = false;
        new RelayCommand(() => ran = true, () => false).Execute(null);
        Assert.False(ran);
    }

    // ---- R5 / T3: AsyncRelayCommand ---------------------------------------------------------------

    [Fact]
    public void R5_an_async_command_that_fails_after_its_first_await_is_logged_and_toasted()
    {
        var host = new ToastHost();
        var command = new AsyncRelayCommand(host.FailLaterAsync);
        Exception? escaped = null;
        var events = CaptureLog(() => escaped = Record.Exception(() => command.ExecuteAsync().GetAwaiter().GetResult()));

        Assert.Null(escaped);
        Assert.Equal(new[] { "before the await", "after the await" }, host.Steps);
        var fault = Assert.Single(Faults(events));
        Assert.Equal("ToastHost: host.FailLaterAsync", SiteOf(fault));
        Assert.True(host.Toast.IsOpen);
        Assert.Equal(UiStrings.Common_ActionFailed, host.Toast.Text);
    }

    [Fact]
    public void R5_Execute_runs_the_handler_synchronously_up_to_its_first_await_as_the_old_discard_did()
    {
        var gate = new TaskCompletionSource();
        var steps = new List<string>();
        async Task Work()
        {
            steps.Add("started");
            await gate.Task;
            steps.Add("finished");
        }
        var command = new AsyncRelayCommand(Work);
        CaptureLog(() =>
        {
            command.Execute(null);
            Assert.Equal(new[] { "started" }, steps);
            gate.SetResult();
        });
        Assert.Equal(new[] { "started", "finished" }, steps);
    }

    [Fact]
    public void R5_UiGuard_Start_reports_a_failure_of_posted_async_work()
    {
        var host = new ToastHost();
        var events = CaptureLog(() =>
        {
            UiGuard.Start("Builder: save and switch", host.FailLaterAsync, host);
            // The work's continuation runs on the thread pool here (no synchronization context): wait for it.
            SpinWait.SpinUntil(() => host.Toast.IsOpen, TimeSpan.FromSeconds(10));
        });

        var fault = Assert.Single(Faults(events));
        Assert.Equal("Builder: save and switch", SiteOf(fault));
        Assert.True(host.Toast.IsOpen);
    }

    // ---- R3 / R4: code-behind handlers ------------------------------------------------------------

    [Fact]
    public void R3_R4_UiGuard_Run_logs_a_handler_failure_and_lets_the_handler_return()
    {
        Exception? escaped = null;
        var events = CaptureLog(() => escaped = Record.Exception(() =>
            UiGuard.Run("Builder window: drop on a card", () => throw new IndexOutOfRangeException("SC-046 test"))));

        Assert.Null(escaped);
        var fault = Assert.Single(Faults(events));
        Assert.Equal("Builder window: drop on a card", SiteOf(fault));
        Assert.IsType<IndexOutOfRangeException>(fault.Exception);
    }

    [Fact]
    public void R3_the_Loader_selects_the_active_profile_after_ContentRendered_not_inside_it() => Sta(() =>
    {
        RST.UI.Theming.RstTheme.Apply(RST.Core.Theming.UiTheme.Light);
        var backend = LoaderFixtures.Backend();
        backend.OnMigrate = () => ActivationReport.Empty(ActivationIntent.Load); // the RST 3.0 report opens a modal dialog
        var dialogs = new LoaderTestDialogs();
        var model = new ProfileLoaderViewModel(backend, dialogs, dialogs, new ManualClock().Schedule, _ => { });
        model.LoadData(); // as the launcher does: the selection waits for the window
        var window = new ProfileLoaderWindow(model);
        var renderedHandlersDone = false;
        bool? reportOpenedAfterRendered = null;
        window.ContentRendered += (_, _) => renderedHandlersDone = true; // after the window's own handler
        dialogs.DuringAddinReport = () => reportOpenedAfterRendered = renderedHandlersDone;
        var events = CaptureLog(() =>
        {
            WindowCapture.ShowOffScreen(window);
            WindowCapture.DoEvents();
            window.Close();
        });

        Assert.Single(dialogs.AddinReports);
        Assert.True(reportOpenedAfterRendered, "the report's modal loop must not run inside ContentRendered");
        Assert.Empty(Faults(events));
    });

    [Fact]
    public void R3_a_failure_while_the_Loader_selects_the_active_profile_is_logged_and_the_window_stays_up() => Sta(() =>
    {
        RST.UI.Theming.RstTheme.Apply(RST.Core.Theming.UiTheme.Light);
        var backend = LoaderFixtures.Backend();
        backend.OnMigrate = () => ActivationReport.Empty(ActivationIntent.Load);
        var dialogs = new LoaderTestDialogs { DuringAddinReport = () => throw new InvalidOperationException("SC-046 test: the report failed") };
        var model = new ProfileLoaderViewModel(backend, dialogs, dialogs, new ManualClock().Schedule, _ => { });
        model.LoadData();
        var window = new ProfileLoaderWindow(model);
        var unhandled = new List<Exception>();
        void OnUnhandled(object s, DispatcherUnhandledExceptionEventArgs e)
        {
            unhandled.Add(e.Exception);
            e.Handled = true;
        }
        Dispatcher.CurrentDispatcher.UnhandledException += OnUnhandled;
        var events = CaptureLog(() =>
        {
            WindowCapture.ShowOffScreen(window);
            WindowCapture.DoEvents();
        });
        Dispatcher.CurrentDispatcher.UnhandledException -= OnUnhandled;

        Assert.Empty(unhandled);
        Assert.True(window.IsVisible);
        Assert.Equal("Loader window: select the active profile", SiteOf(Assert.Single(Faults(events))));
        window.Close();
    });

    [Fact]
    public void R4_MovePanel_ignores_a_source_index_that_is_out_of_range()
    {
        var model = new ProfileBuilderViewModel(BuilderFixtures.Backend(), new FakeDialogs(), new ManualClock().Schedule, a => a());
        model.Initialize();
        var before = model.Panels.Select(p => p.Name).ToList();

        Assert.Null(Record.Exception(() => model.MovePanel(-1, 1)));
        Assert.Null(Record.Exception(() => model.MovePanel(before.Count, -1)));
        Assert.Null(Record.Exception(() => model.MovePanel(before.Count + 5, -6)));

        Assert.Equal(before, model.Panels.Select(p => p.Name));
    }

    // ---- R5: Health's confirm inside the clean's try ----------------------------------------------

    private static HealthViewModel Health(IHealthService service, IDialogService dialogs, Func<DateTimeOffset>? now = null) =>
        new(service, dialogs, new HealthViewModelOptions
        {
            RunInBackground = work =>
            {
                work();
                return Task.CompletedTask;
            },
            StartTimer = (_, _) => new NoTimer(),
            Now = now ?? (() => Now),
            Culture = CultureInfo.GetCultureInfo("en-US"),
            Zone = TimeZoneInfo.Utc,
            ToastSchedule = (_, _) => new NoTimer(),
        });

    [Fact]
    public void R5_a_clean_whose_confirm_cannot_open_says_the_clean_failed_and_leaves_Health_idle()
    {
        var service = new FakeHealthService { Targets = [new RST.Core.Profiles.CleanupTarget { Id = "journals", Name = "Journals", Path = "x" }] };
        var vm = Health(service, new BrokenConfirmDialogs());
        vm.OpenJunk();
        var events = CaptureLog(() => ((AsyncRelayCommand)vm.JunkRunCommand).ExecuteAsync().GetAwaiter().GetResult());

        Assert.True(vm.IsIdle);
        Assert.False(vm.Busy.IsBusy);
        Assert.Equal(UiStrings.Health_CleanupFailedTitle, vm.Toast.Title);
        Assert.Equal(BannerKind.Error, vm.Toast.Kind);
        Assert.Contains(events, e => e.Level == LogLevel.Error && e.MessageTemplate == "Health: Clean Junk Files failed");
        Assert.Empty(service.CleanCalls);
    }

    // ---- R6: work in two-way binding setters ------------------------------------------------------

    [Fact]
    public void R6_Healths_tab_setter_logs_a_failure_instead_of_letting_WPF_swallow_it()
    {
        var service = new FakeHealthService { Activity = _ => new RST.Core.Health.ActivityData() };
        var vm = Health(service, new FakeDialogs(), now: () => throw new InvalidOperationException("SC-046 test: no clock"));
        Exception? escaped = null;
        var events = CaptureLog(() => escaped = Record.Exception(() => vm.SelectedTab = HealthViewModel.ActivityTab));

        Assert.Null(escaped);
        Assert.Equal(HealthViewModel.ActivityTab, vm.SelectedTab);
        Assert.Equal("Health: switch tab", SiteOf(Assert.Single(Faults(events))));
    }

    [Fact]
    public void R6_Healths_range_pill_logs_a_failure()
    {
        var service = new FakeHealthService { Activity = _ => new RST.Core.Health.ActivityData() };
        var vm = Health(service, new FakeDialogs(), now: () => throw new InvalidOperationException("SC-046 test: no clock"));
        var events = CaptureLog(() => Assert.Null(Record.Exception(() => vm.Ranges[1].IsSelected = true)));

        Assert.Contains("Health: range pill", Faults(events).Select(SiteOf));
    }

    [Fact]
    public void R6_the_Loaders_card_setter_logs_a_failure()
    {
        var backend = LoaderFixtures.Backend();
        backend.Prefs.Profiles.Remove(LoaderFixtures.StructId); // Structure has presets: never picked here, it asks to adopt them
        var dialogs = new LoaderTestDialogs();
        // The adopt question cannot open.
        var model = new ProfileLoaderViewModel(backend, new BrokenConfirmDialogs(), dialogs, new ManualClock().Schedule, _ => { });
        model.LoadData();
        var withPresets = model.Profiles.First(p => p.Profile.Presets is not null && p.Id is not null && backend.Prefs.Get(p.Id) is null);
        Exception? escaped = null;
        var events = CaptureLog(() => escaped = Record.Exception(() => model.SelectedCard = withPresets));

        Assert.Null(escaped);
        Assert.Equal("Loader: select a profile", SiteOf(Assert.Single(Faults(events))));
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
}
