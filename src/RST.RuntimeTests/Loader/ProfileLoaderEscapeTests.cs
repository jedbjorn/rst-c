// rst-c's own (not vendored). SC-010 (feature #18, spec #19, WU6): Escape closes the Profile Loader the
// way Close does, and does nothing while Close could not (a load in progress), while a dialog the
// Loader opened is showing, or while a menu is open in it. WU6 review: a key-repeat never closes a
// window (N1; the Builder and Health share the check), Escape bubbles up from the controls that hold
// the focus, and a drag of the opacity slider (the mouse captured) holds it (F2).

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using RST.Core.Activation;
using RST.Core.Loader;
using RST.Core.Theming;
using RST.RuntimeTests.Support;
using RST.UI.Dialogs;
using RST.UI.Loader;
using RST.UI.Theming;
using Xunit;

namespace RST.RuntimeTests.Loader;

[Collection(WpfThemeCollection.Name)]
public class ProfileLoaderEscapeTests
{
    private static void Sta(Action action) => Screenshots.RunSta(() =>
    {
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

    private sealed class Opened(ProfileLoaderWindow window, ProfileLoaderViewModel model)
    {
        public ProfileLoaderWindow Window { get; } = window;

        public ProfileLoaderViewModel Model { get; } = model;

        public bool Closed { get; set; }
    }

    private static Opened Open(FakeLoaderBackend backend)
    {
        var dialogs = new LoaderTestDialogs();
        var clock = new ManualClock();
        var model = new ProfileLoaderViewModel(backend, dialogs, dialogs, clock.Schedule, _ => { });
        model.Initialize();
        var window = new ProfileLoaderWindow(model);
        var opened = new Opened(window, model);
        window.Closed += (_, _) => opened.Closed = true;
        WindowCapture.ShowOffScreen(window);
        return opened;
    }

    private static KeyEventArgs Escape(Window w) =>
        new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(w)!, 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent };

    /// <summary>Raises Escape on the window (as a key press bubbles up to it) and returns whether it was handled.</summary>
    private static bool PressEscape(Window w)
    {
        var args = Escape(w);
        w.RaiseEvent(args);
        WindowCapture.DoEvents();
        return args.Handled;
    }

    private static void CloseIfOpen(Opened o)
    {
        if (!o.Closed) o.Window.Close();
    }

    [Fact]
    public void Escape_closes_the_Loader_as_Close_does() => Sta(() =>
    {
        var o = Open(LoaderFixtures.Backend());
        try
        {
            Assert.True(o.Model.CloseCommand.CanExecute(null));
            Assert.True(PressEscape(o.Window));
            Assert.True(o.Closed);
        }
        finally
        {
            CloseIfOpen(o);
        }
    });

    [Fact]
    public void Other_keys_do_not_close_the_Loader() => Sta(() =>
    {
        var o = Open(LoaderFixtures.Backend());
        try
        {
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(o.Window)!, 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent };
            o.Window.RaiseEvent(args);
            WindowCapture.DoEvents();
            Assert.False(args.Handled);
            Assert.False(o.Closed);
        }
        finally
        {
            CloseIfOpen(o);
        }
    });

    [Fact]
    public void Escape_during_a_load_does_nothing() => Sta(() =>
    {
        var backend = LoaderFixtures.Backend();
        var pending = new TaskCompletionSource<LoadResult>();
        backend.OnLoad = _ => pending.Task;
        var o = Open(backend);
        try
        {
            o.Model.SelectedCard = o.Model.Profiles.Single(c => c.Name == "Structure");
            o.Model.DisableUnused = false;
            var load = o.Model.LoadAsync();
            Assert.False(load.IsCompleted);
            Assert.True(o.Model.IsWorking);
            Assert.False(o.Model.CloseCommand.CanExecute(null), "Close is disabled while the load runs");

            Assert.False(PressEscape(o.Window));
            Assert.False(o.Closed, "Escape must not interrupt the load's add-in pass");
            Assert.True(o.Model.IsWorking);

            pending.SetResult(new LoadResult(true, [], false, [], QaCheck.Clear));
            WindowCapture.DoEvents();
            Assert.True(load.IsCompleted);
            Assert.False(o.Model.IsWorking);
            // The 3 s "Loading" close (the fake clock holds it) still shows the busy overlay: Close stays
            // disabled, and Escape with it.
            Assert.True(o.Model.Busy.IsBusy);
            Assert.False(PressEscape(o.Window));
            Assert.False(o.Closed);
        }
        finally
        {
            CloseIfOpen(o);
        }
    });

    [Fact]
    public void Escape_while_a_Loader_dialog_is_open_leaves_the_Loader() => Sta(() =>
    {
        var o = Open(LoaderFixtures.Backend());
        var dialog = new ConfirmAddinChangesWindow(new ConfirmAddinChangesModel(["Twinmotion (2026)"], [], [], [])) { Owner = o.Window };
        try
        {
            WindowCapture.ShowOffScreen(dialog);
            Assert.Contains(dialog, o.Window.OwnedWindows.OfType<Window>());

            Assert.False(PressEscape(o.Window));
            Assert.False(o.Closed, "the dialog takes Escape, not the Loader");

            dialog.Close();
            WindowCapture.DoEvents();
            Assert.True(PressEscape(o.Window));
            Assert.True(o.Closed, "with the dialog gone, Escape closes the Loader");
        }
        finally
        {
            if (dialog.IsVisible) dialog.Close();
            CloseIfOpen(o);
        }
    });

    [Fact]
    public void Escape_while_a_menu_is_open_leaves_the_Loader() => Sta(() =>
    {
        var o = Open(LoaderFixtures.Backend());
        var import = WindowCapture.Descendants<Button>(o.Window)
            .Single(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "Loader.Import");
        var menu = new ContextMenu { Items = { new MenuItem { Header = "Item" } } };
        import.ContextMenu = menu;
        try
        {
            menu.PlacementTarget = import;
            menu.IsOpen = true;
            WindowCapture.DoEvents();
            Assert.True(menu.IsOpen);

            Assert.False(PressEscape(o.Window));
            Assert.False(o.Closed, "the menu takes Escape, not the Loader");
        }
        finally
        {
            menu.IsOpen = false;
            CloseIfOpen(o);
        }
    });

    [Fact]
    public void The_report_after_an_unload_holds_Escape_until_it_closes() => Sta(() =>
    {
        // The Unload's add-in report is a real owned dialog (the fake opens it non-modally over the
        // Loader); Escape on the Loader while it shows does nothing.
        var backend = LoaderFixtures.Backend();
        var item = LoaderFixtures.Inventory().Items.First(i => i.Name == "DiRoots.One");
        var entry = new PlanEntry(2026, "DiRoots.One", null, AddinScope.Machine, item.ManifestPath, item.FileName, item.AddinIds,
            PlanAction.Enable, null, false, false, false, null);
        backend.OnUnload = () => new UnloadOutcome(true, null,
            new ActivationReport(ActivationIntent.Unload, [ActivationOutcome.For(entry, OutcomeResult.Done)], []));
        var dialogs = new LoaderTestDialogs();
        var model = new ProfileLoaderViewModel(backend, dialogs, dialogs, new ManualClock().Schedule, _ => { });
        model.Initialize();
        var window = new ProfileLoaderWindow(model);
        var closed = false;
        window.Closed += (_, _) => closed = true;
        var escapeHandled = (bool?)null;
        dialogs.DuringAddinReport = () =>
        {
            var report = new AddinReportWindow(dialogs.AddinReports[^1]) { Owner = window };
            WindowCapture.ShowOffScreen(report);
            escapeHandled = PressEscape(window);
            report.Close();
        };
        WindowCapture.ShowOffScreen(window);
        try
        {
            model.UnloadCommand.Execute(null);
            WindowCapture.DoEvents();
            Assert.Single(dialogs.AddinReports);
            Assert.False(escapeHandled, "Escape was taken by the Loader while its report showed");
            Assert.False(closed);
        }
        finally
        {
            if (!closed) window.Close();
        }
    });

    // ── WU6 review N1 and F2 ──────────────────────────────────────────────────

    /// <summary>
    /// N1: only a fresh press closes. Holding Escape on a dialog closes the dialog with the first press;
    /// the repeats then reach the window behind it and must do nothing. The Builder, the Loader and
    /// Health all gate on this one predicate (KeyEventArgs.IsRepeat cannot be set from a test).
    /// </summary>
    [Theory]
    [InlineData(Key.Escape, false, false, true)]
    [InlineData(Key.Escape, true, false, false)]
    [InlineData(Key.Escape, false, true, false)]
    [InlineData(Key.Escape, true, true, false)]
    [InlineData(Key.Enter, false, false, false)]
    public void Only_a_fresh_unhandled_Escape_press_closes(Key key, bool isRepeat, bool handled, bool closes) =>
        Assert.Equal(closes, EscapeGuard.IsFreshEscape(key, isRepeat, handled));

    /// <summary>Raises Escape on <paramref name="source"/>, from where it bubbles to the window; returns whether it was handled.</summary>
    private static bool PressEscapeOn(UIElement source, Window w)
    {
        var args = Escape(w);
        source.RaiseEvent(args);
        WindowCapture.DoEvents();
        Assert.Same(source, args.Source);
        return args.Handled;
    }

    private static Slider OpacitySlider(ProfileLoaderWindow window) =>
        WindowCapture.Descendants<Slider>(window)
            .Single(s => System.Windows.Automation.AutomationProperties.GetAutomationId(s) == "Loader.OpacityOverride");

    [Fact]
    public void Escape_from_a_focused_card_bubbles_up_and_closes_the_Loader() => Sta(() =>
    {
        var o = Open(LoaderFixtures.Backend());
        try
        {
            var card = (ListBoxItem)o.Window.Cards.ItemContainerGenerator.ContainerFromItem(o.Model.Profiles[1]);
            Assert.NotNull(card);
            card.Focus();
            Assert.True(PressEscapeOn(card, o.Window));
            Assert.True(o.Closed, "Escape from a card reaches the Loader");
        }
        finally
        {
            CloseIfOpen(o);
        }
    });

    [Fact]
    public void Escape_from_the_opacity_slider_bubbles_up_and_closes_the_Loader() => Sta(() =>
    {
        var o = Open(LoaderFixtures.Backend());
        try
        {
            var slider = OpacitySlider(o.Window);
            slider.Focus();
            Assert.True(PressEscapeOn(slider, o.Window));
            Assert.True(o.Closed, "Escape from the slider reaches the Loader");
        }
        finally
        {
            CloseIfOpen(o);
        }
    });

    [Fact]
    public void Escape_while_the_opacity_slider_is_dragged_leaves_the_Loader() => Sta(() =>
    {
        var o = Open(LoaderFixtures.Backend());
        try
        {
            var thumb = ((Track)OpacitySlider(o.Window).Template.FindName("PART_Track", OpacitySlider(o.Window))).Thumb;
            var captured = thumb.CaptureMouse();
            Assert.True(captured, "the runner granted the capture, so the test is not vacuous");
            Assert.False(PressEscapeOn(thumb, o.Window));
            Assert.False(o.Closed, "a drag in progress holds Escape");

            thumb.ReleaseMouseCapture();
            WindowCapture.DoEvents();
            Assert.True(PressEscapeOn(thumb, o.Window));
            Assert.True(o.Closed, "with the drag over, Escape closes the Loader");
        }
        finally
        {
            if (Mouse.Captured is not null) Mouse.Capture(null);
            CloseIfOpen(o);
        }
    });
}
