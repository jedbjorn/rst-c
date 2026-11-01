// rst-c's own (not vendored). SC-046: the tray's side of the crash-path hardening. The Add-ins page's async commands
// log a failure instead of losing it (T3), the start shows the icon and wires the show signal first (T2), and a UI
// exception that keeps coming back quits the tray once, cleanly (T7). R25 leg only, with the other tray tests.

using System.Windows.Threading;
using RST.Core.Logging;
using RST.Core.Tray;
using RST.RuntimeTests.Support;
using RST.Tray;
using RST.Tray.Shell;
using RST.UI.Mvvm;
using Xunit;

namespace RST.RuntimeTests.Tray;

// The theme collection runs alone, so the process-wide Log.Logger swap sees only these tests.
[Collection(WpfThemeCollection.Name)]
public class TrayHardeningTests
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

    // ---- T3: the tray's Add-ins page --------------------------------------------------------------

    [Fact]
    public async Task T3_an_Apply_whose_report_cannot_open_is_logged_not_lost()
    {
        using var f = new AddInsFixture();
        f.Dialogs.DuringAddinReport = () => throw new InvalidOperationException("SC-046 test: the report failed");
        var vm = f.ViewModel();
        await vm.RefreshAsync();
        vm.SelectedProfile = vm.Profiles.Single(p => p.Name == "F16 Lean");
        Exception? escaped = null;
        // The fixture runs the backend inline, so the command has finished when Execute returns.
        var events = CaptureLog(() => escaped = Record.Exception(() => vm.ApplyCommand.Execute(null)));

        Assert.Null(escaped);
        Assert.Equal("AddInsViewModel: ApplyAsync", SiteOf(Assert.Single(Faults(events))));
        Assert.True(vm.IsIdle);
    }

    // ---- T2: the tray's start order ---------------------------------------------------------------

    [Fact]
    public void T2_the_tray_shows_its_icon_and_wires_the_show_signal_before_anything_else() => Sta(() =>
    {
        var sid = "S-1-5-21-46-" + Guid.NewGuid().ToString("N");
        using var instance = SingleInstance.TryClaim(sid, signalFirst: false, log: _ => { })!;
        using var host = new TrayHost(TrayArguments.Parse(["--startup"]), instance);

        var steps = host.StartSteps();

        Assert.Equal(new[] { "icon", "show signal" }, steps.Take(2).Select(s => s.Name));
        Assert.True(steps[0].Essential, "no icon: the tray cannot be seen, so it exits rather than holding the mutex");
        Assert.Equal(new[] { "icon" }, steps.Where(s => s.Essential).Select(s => s.Name));
        Assert.Contains("update checks", steps.Select(s => s.Name));
        Assert.Contains("window", steps.Select(s => s.Name));
    });

    // ---- T7: a UI exception that keeps coming back ------------------------------------------------

    [Fact]
    public void T7_the_same_UI_exception_past_the_limit_quits_the_tray_once_with_its_exit_code()
    {
        var quits = new List<(string Reason, int Code)>();
        var now = Now;
        var faults = new TrayFaults((reason, code) => quits.Add((reason, code)), new RepeatingFault(3, TimeSpan.FromSeconds(5), () => now));
        Exception boom;
        try
        {
            throw new InvalidOperationException("SC-046 test: layout failed");
        }
        catch (InvalidOperationException ex)
        {
            boom = ex;
        }
        var results = new List<bool>();
        var events = CaptureLog(() =>
        {
            for (var i = 0; i < 6; i++) results.Add(faults.OnUiException(boom));
        });

        Assert.Equal(new[] { false, false, false, true, true, true }, results);
        var quit = Assert.Single(quits);
        Assert.Equal(TrayFaults.RepeatingFaultExitCode, quit.Code);
        Assert.NotEqual(0, quit.Code);
        Assert.Equal(4, events.Count(e => e.MessageTemplate == TrayFaults.UiTemplate));
        Assert.Single(events, e => e.MessageTemplate == TrayFaults.RepeatingTemplate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Launch_logs_year_profile_and_pid_once_only_after_a_process_starts(bool fails)
    {
        using var f = new AddInsFixture();
        var vm = f.ViewModel();
        await vm.RefreshAsync();
        vm.SelectedProfile = vm.Profiles.Single(p => p.Name == "F16 Lean");
        if (fails) f.Backend.OnLaunch = _ => throw new InvalidOperationException("could not start");
        var events = CaptureLog(() => vm.LaunchAsync(2026).GetAwaiter().GetResult());
        var launches = events.Where(e => e.MessageTemplate == "profiles: started Revit {Year} with {Profile} (pid {Pid})");
        if (fails) Assert.Empty(launches);
        else Assert.Equal(new object[] { 2026, "F16 Lean", 4242 }, Assert.Single(launches).PropertyValues);
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
