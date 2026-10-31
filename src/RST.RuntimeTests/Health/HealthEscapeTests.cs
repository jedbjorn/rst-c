// rst-c's own (not vendored). SC-010 (feature #18, spec #19, WU6): Escape closes Health the way Close
// does, and does nothing while Close could not (a scan in progress) or while a dialog Health opened is
// showing. Decision #62: while the Junk modal is open, Escape is its Cancel and closes the modal only.

using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using RST.Core.Theming;
using RST.RuntimeTests.Support;
using RST.UI.Dialogs;
using RST.UI.Health;
using RST.UI.Theming;
using Xunit;

namespace RST.RuntimeTests.Health;

[Collection(WpfThemeCollection.Name)]
public class HealthEscapeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 14, 0, 0, TimeSpan.Zero);

    private static void Sta(Action action) => Screenshots.RunSta(() =>
    {
        var culture = CultureInfo.GetCultureInfo("en-US");
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            RstTheme.Apply(UiTheme.Light);
            action();
        }
        finally
        {
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    });

    private sealed class NoTimer : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class Opened(HealthWindow window)
    {
        public HealthWindow Window { get; } = window;

        public bool Closed { get; set; }
    }

    private static Opened Open(FakeHealthService service, Func<Action, Task>? runner = null)
    {
        var vm = new HealthViewModel(service, new FakeDialogs(), new HealthViewModelOptions
        {
            RunInBackground = runner ?? (work =>
            {
                work();
                return Task.CompletedTask;
            }),
            StartTimer = (_, _) => new NoTimer(),
            Now = () => Now,
            Culture = CultureInfo.GetCultureInfo("en-US"),
            Zone = TimeZoneInfo.Utc,
            ToastSchedule = (_, _) => new NoTimer(),
        });
        vm.Load();
        var window = new HealthWindow(vm);
        var opened = new Opened(window);
        window.Closed += (_, _) => opened.Closed = true;
        WindowCapture.ShowOffScreen(window);
        return opened;
    }

    /// <summary>Raises Escape on the window (as a key press bubbles up to it) and returns whether it was handled.</summary>
    private static bool PressEscape(Window w) => PressEscape(w, w);

    /// <summary>Raises Escape on <paramref name="source"/>, from where it bubbles to the window; returns whether it was handled.</summary>
    private static bool PressEscape(UIElement source, Window w)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(w)!, 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent };
        source.RaiseEvent(args);
        WindowCapture.DoEvents();
        return args.Handled;
    }

    private static void CloseIfOpen(Opened o)
    {
        if (!o.Closed) o.Window.Close();
    }

    [Fact]
    public void Escape_closes_Health_as_Close_does() => Sta(() =>
    {
        var o = Open(new FakeHealthService { Snapshot = HealthSamples.Snapshot() });
        try
        {
            Assert.True(o.Window.CloseButton.IsEnabled);
            Assert.True(PressEscape(o.Window));
            Assert.True(o.Closed);
        }
        finally
        {
            CloseIfOpen(o);
        }
    });

    [Fact]
    public void Escape_during_a_scan_does_nothing() => Sta(() =>
    {
        var pending = new TaskCompletionSource();
        var o = Open(new FakeHealthService { Snapshot = HealthSamples.Healthy() }, _ => pending.Task);
        try
        {
            _ = o.Window.ViewModel.ScanAsync();
            WindowCapture.DoEvents();
            Assert.True(o.Window.ViewModel.IsScanning);
            Assert.False(o.Window.CloseButton.IsEnabled, "Close is disabled while the scan runs");
            Assert.False(PressEscape(o.Window));
            Assert.False(o.Closed);
        }
        finally
        {
            CloseIfOpen(o);
        }
    });

    /// <summary>
    /// Decision #62: Escape on the Junk modal is its Cancel, and that press closes the modal only
    /// (innermost first); the next press closes Health.
    /// </summary>
    [Fact]
    public void Escape_cancels_the_Junk_modal_then_closes_Health() => Sta(() =>
    {
        var service = new FakeHealthService { Snapshot = HealthSamples.Snapshot(), Targets = HealthSamples.Targets() };
        var o = Open(service);
        try
        {
            o.Window.ViewModel.OpenJunk();
            WindowCapture.DoEvents();
            Assert.True(o.Window.ViewModel.IsJunkOpen);
            var cancel = WindowCapture.Descendants<System.Windows.Controls.Button>(o.Window)
                .Single(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "Health.JunkCancel");
            Assert.True(PressEscape(cancel, o.Window));
            Assert.False(o.Window.ViewModel.IsJunkOpen, "Escape cancels the Junk modal");
            Assert.False(o.Closed, "one press closes the innermost modal only");
            Assert.Empty(service.CleanCalls);

            Assert.True(PressEscape(o.Window));
            Assert.True(o.Closed, "with the modal gone, Escape closes Health");
        }
        finally
        {
            CloseIfOpen(o);
        }
    });

    /// <summary>
    /// The busy rule: Escape on the Junk modal is its Cancel only while Cancel is enabled, so never while
    /// a scan runs. (The window behind is disabled while the modal shows, so the scan is started on the
    /// view-model; a clean closes the modal before it starts, so it cannot run behind an open one.)
    /// </summary>
    [Fact]
    public void Escape_on_the_Junk_modal_during_a_scan_does_nothing() => Sta(() =>
    {
        var pending = new TaskCompletionSource();
        var o = Open(new FakeHealthService { Snapshot = HealthSamples.Snapshot(), Targets = HealthSamples.Targets() }, _ => pending.Task);
        try
        {
            o.Window.ViewModel.OpenJunk();
            WindowCapture.DoEvents();
            Assert.True(o.Window.ViewModel.IsJunkOpen);
            var scan = o.Window.ViewModel.ScanAsync();
            WindowCapture.DoEvents();
            Assert.True(o.Window.ViewModel.IsScanning);
            Assert.False(o.Window.ViewModel.JunkCancelCommand.CanExecute(null), "Cancel is disabled while the scan runs");

            Assert.False(PressEscape(o.Window));
            Assert.True(o.Window.ViewModel.IsJunkOpen, "a busy modal keeps Escape, as its Cancel does");
            Assert.False(o.Closed);

            pending.SetResult();
            WindowCapture.DoEvents();
            Assert.True(scan.IsCompleted);
            Assert.True(PressEscape(o.Window));
            Assert.False(o.Window.ViewModel.IsJunkOpen, "with the scan over, Escape cancels the modal");
            Assert.False(o.Closed);
        }
        finally
        {
            CloseIfOpen(o);
        }
    });

    [Fact]
    public void Escape_on_the_Junk_modal_with_a_dialog_in_front_does_nothing() => Sta(() =>
    {
        var o = Open(new FakeHealthService { Snapshot = HealthSamples.Snapshot(), Targets = HealthSamples.Targets() });
        var dialog = new MessageDialog(new MessageDialogModel("Clean", "Clean", "Remove these files?", [], "Continue", HasCancel: true, Danger: true),
            MessageDialog.MessageId) { Owner = o.Window };
        try
        {
            o.Window.ViewModel.OpenJunk();
            WindowCapture.DoEvents();
            WindowCapture.ShowOffScreen(dialog);
            Assert.False(PressEscape(o.Window));
            Assert.True(o.Window.ViewModel.IsJunkOpen, "the dialog in front takes Escape, not the Junk modal behind it");
            Assert.False(o.Closed);
        }
        finally
        {
            if (dialog.IsVisible) dialog.Close();
            CloseIfOpen(o);
        }
    });

    [Fact]
    public void Escape_while_a_dialog_is_open_leaves_Health() => Sta(() =>
    {
        var o = Open(new FakeHealthService { Snapshot = HealthSamples.Snapshot() });
        var dialog = new MessageDialog(new MessageDialogModel("Clean", "Clean", "Remove these files?", [], "Continue", HasCancel: true, Danger: true),
            MessageDialog.MessageId) { Owner = o.Window };
        try
        {
            WindowCapture.ShowOffScreen(dialog);
            Assert.False(PressEscape(o.Window));
            Assert.False(o.Closed, "the dialog takes Escape, not Health");
        }
        finally
        {
            if (dialog.IsVisible) dialog.Close();
            CloseIfOpen(o);
        }
    });
}
