// rst-c's own (not vendored): the unsaved-changes prompt on a real ProfileBuilderWindow (spec #16,
// Planner default 3; WU4 review B1 and N1). The title-bar ✕ (Window.Close) with Save & Exit,
// Discard & Exit and Cancel; Escape, which closes through the same path unless a menu or the icon
// picker has it (SC-010); and the switch from the header's profile menu (feature #18) with Save &
// Switch, Discard & Switch and Cancel, driven through WPF with the view-model's real dispatcher
// posting, so the Closing event and the menu click behave as in Revit. The ⚙ and profile toggles
// follow their menus and ignore the mouse while open (the Branding popover's VM finding D2), and
// ⚙ → Branding… opens the real dialog modally.

using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RST.Core.Theming;
using RST.RuntimeTests.Loader;
using RST.RuntimeTests.Support;
using RST.UI.Builder;
using RST.UI.Dialogs;
using RST.UI.Theming;
using Xunit;

namespace RST.RuntimeTests.Builder;

[Collection(WpfThemeCollection.Name)]
public class ProfileBuilderWindowFlowTests
{
    private sealed class Opened
    {
        public required ProfileBuilderWindow Window { get; init; }

        public required FakeBuilderBackend Backend { get; init; }

        public required FakeDialogs Dialogs { get; init; }

        public bool Closed { get; set; }
    }

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

    /// <summary>
    /// A real window over the fixture backend, with the dispatcher-posting view-model (post: null). It
    /// opens on the loaded Architecture (F5); these flows start from + New profile, picked first.
    /// </summary>
    private static Opened Open()
    {
        var backend = BuilderFixtures.Backend();
        var dialogs = new FakeDialogs();
        var model = new ProfileBuilderViewModel(backend, dialogs, new ManualClock().Schedule);
        model.Initialize();
        model.NewProfileCommand.Execute(null);
        var window = new ProfileBuilderWindow(model);
        var opened = new Opened { Window = window, Backend = backend, Dialogs = dialogs };
        window.Closed += (_, _) => opened.Closed = true;
        WindowCapture.ShowOffScreen(window);
        return opened;
    }

    private static void Settle()
    {
        for (var i = 0; i < 3; i++) WindowCapture.DoEvents();
    }

    private static void Cleanup(Opened o)
    {
        if (o.Closed) return;
        o.Dialogs.ChooseAnswers.Enqueue(DialogChoice.Secondary);
        o.Window.Close();
    }

    [Fact]
    public void Title_bar_close_then_Save_and_exit_saves_and_closes_the_window() => Sta(() =>
    {
        var o = Open();
        try
        {
            o.Window.Model.ProfileName = "Draft";
            o.Window.Model.ProfileTab = "Draft Tab";
            o.Dialogs.ChooseAnswers.Enqueue(DialogChoice.Primary);
            o.Window.Close();
            Settle();
            Assert.Single(o.Backend.Saved);
            Assert.True(o.Closed, "the window closes once the save has succeeded");
            Assert.Single(o.Dialogs.ChooseCalls);
        }
        finally
        {
            Cleanup(o);
        }
    });

    [Fact]
    public void Title_bar_close_then_Save_and_exit_with_a_failed_save_stays_open() => Sta(() =>
    {
        var o = Open();
        try
        {
            o.Window.Model.ProfileName = "Draft"; // no tab: the save is refused
            o.Dialogs.ChooseAnswers.Enqueue(DialogChoice.Primary);
            o.Window.Close();
            Settle();
            Assert.Empty(o.Backend.Saved);
            Assert.False(o.Closed);
            Assert.True(o.Window.IsVisible);
            Assert.Equal("Tab name required.", o.Window.Model.Toast.Text);

            // Nothing was approved: a second ✕ asks again (WU4 re-review nit).
            o.Dialogs.ChooseAnswers.Enqueue(DialogChoice.Cancel);
            o.Window.Close();
            Settle();
            Assert.Equal(2, o.Dialogs.ChooseCalls.Count);
            Assert.False(o.Closed);
        }
        finally
        {
            Cleanup(o);
        }
    });

    [Fact]
    public void Title_bar_close_then_Discard_and_exit_closes_without_saving() => Sta(() =>
    {
        var o = Open();
        o.Window.Model.ProfileName = "Draft";
        o.Dialogs.ChooseAnswers.Enqueue(DialogChoice.Secondary);
        o.Window.Close();
        Settle();
        Assert.True(o.Closed);
        Assert.Empty(o.Backend.Saved);
    });

    [Fact]
    public void Title_bar_close_then_Cancel_keeps_the_window_and_the_edits() => Sta(() =>
    {
        var o = Open();
        try
        {
            o.Window.Model.ProfileName = "Draft";
            o.Dialogs.ChooseAnswers.Enqueue(DialogChoice.Cancel);
            o.Window.Close();
            Settle();
            Assert.False(o.Closed);
            Assert.True(o.Window.IsVisible);
            Assert.Equal("Draft", o.Window.Model.ProfileName);
            Assert.Empty(o.Backend.Saved);

            // A second ✕ asks again (nothing was approved).
            o.Dialogs.ChooseAnswers.Enqueue(DialogChoice.Cancel);
            o.Window.Close();
            Settle();
            Assert.Equal(2, o.Dialogs.ChooseCalls.Count);
            Assert.False(o.Closed);
        }
        finally
        {
            Cleanup(o);
        }
    });

    [Fact]
    public void A_clean_window_closes_without_asking() => Sta(() =>
    {
        var o = Open();
        o.Window.Close();
        Settle();
        Assert.True(o.Closed);
        Assert.Empty(o.Dialogs.ChooseCalls);
    });

    // ── the profile menu ─────────────────────────────────────────────────────

    /// <summary>The toggle as a click or Space toggles it (its own toggle).</summary>
    private static void Toggle(ToggleButton button)
    {
        var peer = new ToggleButtonAutomationPeer(button);
        ((IToggleProvider)peer.GetPattern(PatternInterface.Toggle)).Toggle();
        Settle();
    }

    private static void Invoke(MenuItem item)
    {
        var peer = new MenuItemAutomationPeer(item);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
        Settle();
    }

    /// <summary>Opens the profile menu and clicks <paramref name="name"/> (the click runs its command, then the menu closes).</summary>
    private static void PickInMenu(Opened o, string name)
    {
        var w = o.Window;
        Toggle(w.ProfileButton);
        Assert.True(w.ProfileMenu.IsOpen);
        Invoke(w.ProfileMenuItems.Single(i => ((ProfileOptionViewModel)i.Header).Display == name));
    }

    [Fact]
    public void Menu_switch_then_Save_and_switch_saves_then_shows_the_target() => Sta(() =>
    {
        var o = Open();
        try
        {
            o.Window.Model.ProfileName = "Draft";
            o.Window.Model.ProfileTab = "Draft Tab";
            o.Dialogs.ChooseAnswers.Enqueue(DialogChoice.Primary);
            PickInMenu(o, "Structure");
            Assert.Equal("Save & Switch", Assert.Single(o.Dialogs.ChooseCalls).PrimaryText);
            Assert.Single(o.Backend.Saved);
            Assert.Equal("Structure", o.Window.Model.ProfileName);
            Assert.Equal("Structure", o.Window.ProfileButtonName.Text);
            Assert.Contains(o.Window.Model.ProfileOptions, x => x.Display == "Draft");
            Assert.False(o.Window.Model.IsDirty);
            Assert.False(o.Window.ProfileMenu.IsOpen);
            Assert.False(o.Window.ProfileButton.IsChecked);
        }
        finally
        {
            Cleanup(o);
        }
    });

    [Fact]
    public void Menu_switch_then_Discard_and_switch_shows_the_target() => Sta(() =>
    {
        var o = Open();
        try
        {
            o.Window.Model.ProfileName = "Draft";
            o.Dialogs.ChooseAnswers.Enqueue(DialogChoice.Secondary);
            PickInMenu(o, "Structure");
            Assert.Empty(o.Backend.Saved);
            Assert.Equal("Structure", o.Window.Model.ProfileName);
            Assert.True(o.Window.Model.SelectedProfileOption!.IsCurrent);
        }
        finally
        {
            Cleanup(o);
        }
    });

    [Fact]
    public void Menu_switch_then_Cancel_keeps_the_editor() => Sta(() =>
    {
        var o = Open();
        try
        {
            o.Window.Model.ProfileName = "Draft";
            o.Dialogs.ChooseAnswers.Enqueue(DialogChoice.Cancel);
            PickInMenu(o, "Structure");
            Assert.Single(o.Dialogs.ChooseCalls);
            Assert.Equal("Draft", o.Window.Model.ProfileName);
            Assert.True(o.Window.Model.SelectedProfileOption!.IsNew, "the editor stays on the new profile");
            Assert.Equal("Draft", o.Window.ProfileButtonName.Text);
        }
        finally
        {
            Cleanup(o);
        }
    });

    [Fact]
    public void New_profile_with_unsaved_changes_asks_too() => Sta(() =>
    {
        var o = Open();
        try
        {
            PickInMenu(o, "Structure");
            o.Window.Model.MinVersion = "9.9.9";
            o.Dialogs.ChooseAnswers.Enqueue(DialogChoice.Secondary);
            Toggle(o.Window.ProfileButton);
            Invoke(o.Window.ProfileMenu.Items.OfType<MenuItem>().Single(i => AutomationProperties.GetAutomationId(i) == "Builder.NewProfile"));
            Assert.Single(o.Dialogs.ChooseCalls);
            Assert.True(o.Window.Model.SelectedProfileOption!.IsNew);
            Assert.Equal("New profile", o.Window.ProfileButtonName.Text);
        }
        finally
        {
            Cleanup(o);
        }
    });

    // ── the header's toggles (the popover's VM finding D2) ─────────────────────

    /// <summary>What a mouse press at the button's centre would hit (null when nothing hit-testable is there).</summary>
    private static DependencyObject? HitAt(ProfileBuilderWindow w, FrameworkElement button)
    {
        var centre = button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), w);
        return w.InputHitTest(centre) as DependencyObject;
    }

    private static bool IsUnder(DependencyObject? hit, DependencyObject ancestor)
    {
        for (var d = hit; d is not null; d = VisualTreeHelper.GetParent(d))
            if (ReferenceEquals(d, ancestor)) return true;
        return false;
    }

    [Fact]
    public void The_menu_toggles_follow_their_menus_and_ignore_the_mouse_while_open() => Sta(() =>
    {
        var o = Open();
        try
        {
            var w = o.Window;
            foreach (var (button, menu) in new[] { (w.SettingsButton, w.SettingsMenu), (w.ProfileButton, w.ProfileMenu) })
            {
                Assert.True(IsUnder(HitAt(w, button), button), "closed: a press at the button reaches it");
                Toggle(button);
                Assert.True(menu.IsOpen, "the toggle opens its menu");
                Assert.True(button.IsChecked);
                Assert.False(button.IsHitTestVisible, "while open the button ignores the mouse");
                Assert.False(IsUnder(HitAt(w, button), button), "the press that dismisses the menu cannot reopen it (D2)");

                // That press is an outside click, and the menu closes itself: the toggle follows.
                menu.IsOpen = false;
                Settle();
                Assert.False(button.IsChecked);
                Assert.True(button.IsHitTestVisible);
            }
        }
        finally
        {
            Cleanup(o);
        }
    });

    [Fact]
    public void The_settings_menu_opens_the_branding_dialog() => Sta(() =>
    {
        var modals = new FakeBuilderDialogs();
        var dialogs = new FakeDialogs();
        var model = new ProfileBuilderViewModel(BuilderFixtures.Backend(), dialogs, new ManualClock().Schedule, builderDialogs: modals);
        model.Initialize();
        var window = new ProfileBuilderWindow(model);
        WindowCapture.ShowOffScreen(window);
        try
        {
            Toggle(window.SettingsButton);
            Invoke(window.BrandingMenuItem);
            Assert.Same(model, Assert.Single(modals.Brandings));
            Assert.False(window.SettingsMenu.IsOpen);
            Assert.False(model.IsDirty, "branding is machine-wide, never a profile change");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void The_real_branding_dialog_opens_modally_and_owns_the_logo_file_dialog() => Sta(() =>
    {
        // BuilderDialogs over a real DialogService: while Branding is open, its Pick… file dialog is
        // owned by the Branding dialog (one owned by the disabled Builder would re-enable the Builder
        // when it closed); afterwards the Builder owns its dialogs again.
        var service = new DialogService();
        var builderDialogs = new BuilderDialogs(service);
        var model = new ProfileBuilderViewModel(BuilderFixtures.Backend(), service, new ManualClock().Schedule, builderDialogs: builderDialogs);
        model.Initialize();
        var window = new ProfileBuilderWindow(model);
        service.Owner = window;
        builderDialogs.Owner = window;
        WindowCapture.ShowOffScreen(window);
        BrandingDialog? seen = null;
        Window? ownerDuring = null;
        var gaveUp = false;
        // While the modal dialog runs its loop, look for it among the Builder's owned windows (it is
        // owned by the Builder), every 100 ms for at most 5 s. Found: note what it shows, close it.
        // Never found: close whatever the Builder owns, so ShowDialog returns and the asserts fail
        // instead of the test hanging (review N4).
        var attempts = 0;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            seen = window.OwnedWindows.OfType<BrandingDialog>().FirstOrDefault();
            if (seen is null && ++attempts < 50) return;
            timer.Stop();
            ownerDuring = service.Owner;
            if (seen is not null)
            {
                seen.Close();
                return;
            }
            gaveUp = true;
            foreach (var owned in window.OwnedWindows.OfType<Window>().ToList()) owned.Close();
        };
        try
        {
            timer.Start();
            model.BrandingCommand.Execute(null); // returns when the dialog closes
            timer.Stop();
            Assert.False(gaveUp, "the Branding dialog was not among the Builder's owned windows within 5 s");
            Assert.NotNull(seen);
            Assert.Same(window, seen!.Owner);
            Assert.False(seen.ShowInTaskbar);
            Assert.Same(seen, ownerDuring);
            Assert.Same(window, service.Owner);
        }
        finally
        {
            window.Close();
        }
    });

    // ── Escape (SC-010) ───────────────────────────────────────────────────────

    private static KeyEventArgs Escape(Window w) =>
        new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(w)!, 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent };

    [Fact]
    public void Escape_closes_a_clean_window_without_asking() => Sta(() =>
    {
        var o = Open();
        var args = Escape(o.Window);
        o.Window.RaiseEvent(args);
        Settle();
        Assert.True(args.Handled);
        Assert.True(o.Closed);
        Assert.Empty(o.Dialogs.ChooseCalls);
    });

    [Fact]
    public void Escape_with_unsaved_changes_asks_as_Exit_does() => Sta(() =>
    {
        var o = Open();
        try
        {
            o.Window.Model.ProfileName = "Draft";
            o.Dialogs.ChooseAnswers.Enqueue(DialogChoice.Cancel);
            o.Window.RaiseEvent(Escape(o.Window));
            Settle();
            Assert.False(o.Closed, "Cancel keeps the window and the edits");
            Assert.Equal("Save & Exit", Assert.Single(o.Dialogs.ChooseCalls).PrimaryText);
            Assert.Equal("Draft", o.Window.Model.ProfileName);

            o.Dialogs.ChooseAnswers.Enqueue(DialogChoice.Secondary);
            o.Window.RaiseEvent(Escape(o.Window));
            Settle();
            Assert.True(o.Closed, "Discard & Exit closes");
            Assert.Empty(o.Backend.Saved);
        }
        finally
        {
            Cleanup(o);
        }
    });

    [Fact]
    public void Escape_on_an_open_menu_only_closes_the_menu() => Sta(() =>
    {
        var o = Open();
        try
        {
            o.Window.Model.ProfileName = "Draft";
            Toggle(o.Window.ProfileButton);
            Assert.True(o.Window.ProfileMenu.IsOpen);
            o.Window.RaiseEvent(Escape(o.Window));
            Settle();
            Assert.False(o.Closed);
            Assert.Empty(o.Dialogs.ChooseCalls);
        }
        finally
        {
            o.Window.ProfileMenu.IsOpen = false;
            Cleanup(o);
        }
    });

    [Fact]
    public void Escape_while_the_icon_picker_is_open_leaves_the_window() => Sta(() =>
    {
        var o = Open();
        try
        {
            PickInMenu(o, "Structure");
            o.Window.Model.Panels[0].Tile(0).PickIconCommand.Execute(null);
            Settle();
            Assert.True(o.Window.Model.IsIconPickerOpen);
            o.Window.RaiseEvent(Escape(o.Window));
            Settle();
            Assert.False(o.Closed, "the picker takes Escape, not the window");
            Assert.Empty(o.Dialogs.ChooseCalls);
        }
        finally
        {
            Cleanup(o);
        }
    });
}
