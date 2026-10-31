// rst-c's own (not vendored). SC-010 (feature #18, spec #19, WU6): Escape closes Health the way Close
// does, and does nothing while Close could not (a scan in progress), while the Junk modal is open, or
// while a dialog Health opened is showing.

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
    private static bool PressEscape(Window w)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(w)!, 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent };
        w.RaiseEvent(args);
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

    [Fact]
    public void Escape_while_the_Junk_modal_is_open_leaves_Health() => Sta(() =>
    {
        var o = Open(new FakeHealthService { Snapshot = HealthSamples.Snapshot(), Targets = HealthSamples.Targets() });
        try
        {
            o.Window.ViewModel.OpenJunk();
            WindowCapture.DoEvents();
            Assert.True(o.Window.ViewModel.IsJunkOpen);
            Assert.False(PressEscape(o.Window));
            Assert.False(o.Closed, "the Junk modal is open: Escape does not close the window behind it");
            Assert.True(o.Window.ViewModel.IsJunkOpen);

            o.Window.ViewModel.CancelJunk();
            WindowCapture.DoEvents();
            Assert.True(PressEscape(o.Window));
            Assert.True(o.Closed, "with the modal gone, Escape closes Health");
        }
        finally
        {
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
