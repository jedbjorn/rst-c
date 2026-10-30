// rst-c's own (not vendored): the unsaved-changes prompt on a real ProfileBuilderWindow (spec #16,
// Planner default 3; WU4 review B1 and N1). The title-bar ✕ (Window.Close) with Save & Exit,
// Discard & Exit and Cancel, and the dropdown switch (the ComboBox's SelectedItem) with Save &
// Switch, Discard & Switch and Cancel, driven through WPF with the view-model's real dispatcher
// posting, so the Closing event and the ComboBox's selection write behave as in Revit.

using System.Windows;
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

    /// <summary>A real window over the fixture backend, with the dispatcher-posting view-model (post: null).</summary>
    private static Opened Open()
    {
        var backend = BuilderFixtures.Backend();
        var dialogs = new FakeDialogs();
        var model = new ProfileBuilderViewModel(backend, dialogs, new ManualClock().Schedule);
        model.Initialize();
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

    private static ProfileOptionViewModel Option(Opened o, string name) =>
        o.Window.Model.ProfileOptions.Single(x => x.Entry?.Profile.ProfileName == name);

    [Fact]
    public void Dropdown_switch_then_Save_and_switch_saves_then_shows_the_target() => Sta(() =>
    {
        var o = Open();
        try
        {
            o.Window.Model.ProfileName = "Draft";
            o.Window.Model.ProfileTab = "Draft Tab";
            o.Dialogs.ChooseAnswers.Enqueue(DialogChoice.Primary);
            o.Window.ProfileSelector.SelectedItem = Option(o, "Structure");
            Settle();
            Assert.Single(o.Backend.Saved);
            Assert.Equal("Structure", o.Window.Model.ProfileName);
            var shown = Assert.IsType<ProfileOptionViewModel>(o.Window.ProfileSelector.SelectedItem);
            Assert.Equal("Structure", shown.Entry?.Profile.ProfileName);
            Assert.Contains(o.Window.Model.ProfileOptions, x => x.Display == "Draft");
            Assert.False(o.Window.Model.IsDirty);
        }
        finally
        {
            Cleanup(o);
        }
    });

    [Fact]
    public void Dropdown_switch_then_Discard_and_switch_shows_the_target() => Sta(() =>
    {
        var o = Open();
        try
        {
            o.Window.Model.ProfileName = "Draft";
            o.Dialogs.ChooseAnswers.Enqueue(DialogChoice.Secondary);
            o.Window.ProfileSelector.SelectedItem = Option(o, "Structure");
            Settle();
            Assert.Empty(o.Backend.Saved);
            Assert.Equal("Structure", o.Window.Model.ProfileName);
            Assert.Equal("Structure", ((ProfileOptionViewModel)o.Window.ProfileSelector.SelectedItem).Entry?.Profile.ProfileName);
        }
        finally
        {
            Cleanup(o);
        }
    });

    [Fact]
    public void Dropdown_switch_then_Cancel_snaps_the_dropdown_back() => Sta(() =>
    {
        var o = Open();
        try
        {
            o.Window.Model.ProfileName = "Draft";
            o.Dialogs.ChooseAnswers.Enqueue(DialogChoice.Cancel);
            o.Window.ProfileSelector.SelectedItem = Option(o, "Structure");
            Settle();
            Assert.Equal("Draft", o.Window.Model.ProfileName);
            Assert.True(((ProfileOptionViewModel)o.Window.ProfileSelector.SelectedItem).IsNew, "the dropdown shows + New Profile again");
        }
        finally
        {
            Cleanup(o);
        }
    });

    // ── the Branding popover (VM finding D2: the ToggleButton pattern; the Pick… note, WU5) ──

    /// <summary>Branding ▾ toggled the way a click or Space does (the ToggleButton's own toggle).</summary>
    private static void ToggleBranding(ProfileBuilderWindow w)
    {
        var peer = new System.Windows.Automation.Peers.ToggleButtonAutomationPeer(w.BrandingButton);
        ((System.Windows.Automation.Provider.IToggleProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle)).Toggle();
        Settle();
    }

    /// <summary>What a mouse press at the button's centre would hit (null when nothing hit-testable is there).</summary>
    private static DependencyObject? HitAtBrandingButton(ProfileBuilderWindow w)
    {
        var centre = w.BrandingButton.TranslatePoint(new Point(w.BrandingButton.ActualWidth / 2, w.BrandingButton.ActualHeight / 2), w);
        return w.InputHitTest(centre) as DependencyObject;
    }

    private static bool IsUnder(DependencyObject? hit, DependencyObject ancestor)
    {
        for (var d = hit; d is not null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
            if (ReferenceEquals(d, ancestor)) return true;
        return false;
    }

    [Fact]
    public void Branding_toggle_follows_the_popup_and_ignores_the_mouse_while_it_is_open() => Sta(() =>
    {
        var o = Open();
        try
        {
            var w = o.Window;
            Assert.True(IsUnder(HitAtBrandingButton(w), w.BrandingButton), "closed: a press at the button reaches it");

            ToggleBranding(w);
            Assert.True(w.BrandingPopup.IsOpen, "the toggle opens the popover");
            Assert.True(w.BrandingButton.IsChecked);
            Assert.True(w.Model.IsBrandingOpen);
            Assert.False(w.BrandingButton.IsHitTestVisible, "while open the button ignores the mouse");
            Assert.False(IsUnder(HitAtBrandingButton(w), w.BrandingButton),
                "a press at the button cannot reach it, so the press that dismisses the popup cannot reopen it (D2)");

            // That press is an outside click for the StaysOpen=False popup, which closes itself: everything follows.
            w.BrandingPopup.IsOpen = false;
            Settle();
            Assert.False(w.BrandingButton.IsChecked);
            Assert.False(w.Model.IsBrandingOpen);
            Assert.True(w.BrandingButton.IsHitTestVisible);
            Assert.True(IsUnder(HitAtBrandingButton(w), w.BrandingButton));

            // Keyboard (Space / Enter on the focused toggle) opens and closes it too.
            ToggleBranding(w);
            Assert.True(w.BrandingPopup.IsOpen);
            ToggleBranding(w);
            Assert.False(w.BrandingPopup.IsOpen);
            Assert.False(w.Model.IsBrandingOpen);
        }
        finally
        {
            Cleanup(o);
        }
    });

    [Fact]
    public void Escape_in_the_popover_closes_it() => Sta(() =>
    {
        var o = Open();
        try
        {
            var w = o.Window;
            ToggleBranding(w);
            Assert.True(w.BrandingPopup.IsOpen);
            var args = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(w.BrandingUrlBox) ?? PresentationSource.FromVisual(w)!, 0, System.Windows.Input.Key.Escape)
            {
                RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent,
            };
            w.BrandingUrlBox.RaiseEvent(args);
            Settle();
            Assert.True(args.Handled);
            Assert.False(w.BrandingPopup.IsOpen);
            Assert.False(w.BrandingButton.IsChecked);
        }
        finally
        {
            Cleanup(o);
        }
    });

    [Fact]
    public void The_popover_reopens_after_the_logo_file_dialog() => Sta(() =>
    {
        var o = Open();
        try
        {
            ToggleBranding(o.Window);
            Assert.True(o.Window.BrandingPopup.IsOpen);
            o.Dialogs.OpenResult = @"C:\logo.png";
            o.Dialogs.DuringOpen = () => o.Window.BrandingPopup.IsOpen = false; // the dialog takes the focus
            o.Window.Model.PickLogoCommand.Execute(null);
            Settle();
            Assert.True(o.Window.BrandingPopup.IsOpen, "the popover is back for the URL");
            Assert.True(o.Window.BrandingButton.IsChecked, "the toggle follows the popup");
            Assert.Equal("branding.png", o.Window.LogoName.Text);
        }
        finally
        {
            o.Window.Model.IsBrandingOpen = false;
            Cleanup(o);
        }
    });
}
