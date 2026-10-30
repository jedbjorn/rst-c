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

    // ── the Branding popover (VM finding D2 and the Pick… note, WU5) ──

    /// <summary>A press on the Branding button as WPF delivers it: the preview, then (unless handled) the click.</summary>
    private static void PressBranding(ProfileBuilderWindow w)
    {
        var args = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
        };
        w.BrandingButton.RaiseEvent(args);
        if (!args.Handled) w.BrandingButton.Command.Execute(null);
        Settle();
    }

    [Fact]
    public void Branding_toggles_open_and_closed_even_when_the_popup_closes_itself_first() => Sta(() =>
    {
        var o = Open();
        try
        {
            PressBranding(o.Window);
            Assert.True(o.Window.BrandingPopup.IsOpen, "the first click opens the popover");

            // The real sequence with StaysOpen=False: the popup closes on the press (an outside click),
            // then WPF hands the same press to the button. It must stay closed.
            o.Window.BrandingPopup.IsOpen = false;
            PressBranding(o.Window);
            Assert.False(o.Window.BrandingPopup.IsOpen, "clicking Branding while it was open closes it");
            Assert.False(o.Window.Model.IsBrandingOpen);

            // The popup still open when the press arrives (no outside-click close): the press closes it.
            Thread.Sleep(350);
            PressBranding(o.Window);
            Assert.True(o.Window.BrandingPopup.IsOpen, "a later click opens it again");
            PressBranding(o.Window);
            Assert.False(o.Window.BrandingPopup.IsOpen);
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
            o.Window.Model.IsBrandingOpen = true;
            Settle();
            o.Dialogs.OpenResult = @"C:\logo.png";
            o.Dialogs.DuringOpen = () => o.Window.BrandingPopup.IsOpen = false; // the dialog takes the focus
            o.Window.Model.PickLogoCommand.Execute(null);
            Settle();
            Assert.True(o.Window.BrandingPopup.IsOpen, "the popover is back for the URL");
            Assert.Equal("branding.png", o.Window.LogoName.Text);
        }
        finally
        {
            o.Window.Model.IsBrandingOpen = false;
            Cleanup(o);
        }
    });
}
