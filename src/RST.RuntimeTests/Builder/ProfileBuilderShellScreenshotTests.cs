// rst-c's own (not vendored); the capture helpers follow ProfileBuilderScreenshotTests.
// Feature #18 (spec #19), WU2, AC4: the Builder's new window shell rendered off-screen to PNG in
// light and dark, for the review against the approved spike (decision #25): the default view (the
// Tint header, the 320 px Commands pane, the 330 px Profile pane on General), the ⚙ menu and the
// profile menu (each a popup window: its content is rendered and composed under its button, as the
// Branding popover's capture was), each Profile tab, and the Branding, Rename and Duplicate
// dialogs. Each capture also checks the state it shows (the header's Tint, the pane widths, the
// ticked profile, the refusal line).

using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RST.Core.Builder;
using RST.Core.Theming;
using RST.RuntimeTests.Support;
using RST.UI.Builder;
using RST.UI.Dialogs;
using RST.UI.Theming;
using Xunit;

namespace RST.RuntimeTests.Builder;

[Collection(WpfThemeCollection.Name)]
public class ProfileBuilderShellScreenshotTests
{
    private static readonly Size BuilderSize = new(1450, 900);

    public static TheoryData<UiTheme> Themes => new() { UiTheme.Light, UiTheme.Dark };

    private static string Name(string what, UiTheme theme) => $"{what}-{theme.ToString().ToLowerInvariant()}";

    private static Color Palette(UiTheme theme, string key)
    {
        var c = UiPalette.For(theme)[key];
        return Color.FromRgb(c.R, c.G, c.B);
    }

    private static void Sta(Action action) => Screenshots.RunSta(() =>
    {
        try
        {
            action();
        }
        finally
        {
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    });

    private static Color At(BitmapSource bitmap, Visual element, Visual root, double x, double y)
    {
        var p = element.TransformToAncestor(root).Transform(new Point(x, y));
        return Screenshots.PixelAt(bitmap, (int)Math.Floor(p.X), (int)Math.Floor(p.Y));
    }

    private static void AssertColor(Color expected, Color actual, string what) =>
        Assert.True(Math.Max(Math.Abs(expected.R - actual.R), Math.Max(Math.Abs(expected.G - actual.G), Math.Abs(expected.B - actual.B))) <= 2,
            $"{what}: expected {expected}, rendered {actual}");

    /// <summary>The Builder on Architecture (the loaded profile), arranged, shown off-screen.</summary>
    private static (ProfileBuilderWindow Window, BuilderHarness Harness) Open(Action<BuilderHarness>? arrange = null)
    {
        var h = BuilderHarness.Create();
        h.Model.SelectedProfileOption = h.Option("Architecture");
        arrange?.Invoke(h);
        var window = new ProfileBuilderWindow(h.Model);
        WindowCapture.ShowOffScreen(window);
        return (window, h);
    }

    private static void Close(ProfileBuilderWindow window, BuilderHarness h)
    {
        h.Dialogs.ChooseAnswers.Enqueue(DialogChoice.Secondary);
        window.Close();
    }

    private static BitmapSource Capture(ProfileBuilderWindow window, UiTheme theme, string what)
    {
        var bitmap = WindowCapture.Content(window, BuilderSize);
        Screenshots.Save(bitmap, Name(what, theme));
        Assert.Equal(1450, bitmap.PixelWidth);
        Assert.True(Screenshots.DistinctPixels(bitmap) > 200, $"{what} looks blank");
        return bitmap;
    }

    private static void Toggle(ToggleButton button)
    {
        var peer = new ToggleButtonAutomationPeer(button);
        ((IToggleProvider)peer.GetPattern(PatternInterface.Toggle)).Toggle();
        for (var i = 0; i < 3; i++) WindowCapture.DoEvents();
    }

    /// <summary>The window with <paramref name="menu"/>'s content drawn under <paramref name="button"/>, where WPF places it.</summary>
    private static BitmapSource CaptureMenu(ProfileBuilderWindow window, ToggleButton button, ContextMenu menu, UiTheme theme, string what)
    {
        var background = WindowCapture.Content(window, BuilderSize);
        menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        menu.Arrange(new Rect(menu.DesiredSize));
        menu.UpdateLayout();
        var content = WindowCapture.Render(menu, menu.ActualWidth, menu.ActualHeight);
        var at = button.TransformToAncestor(window.Root).Transform(new Point(0, button.ActualHeight + menu.VerticalOffset));
        var composed = WindowCapture.Compose(background, content, at, Palette(theme, UiPalette.Border));
        Screenshots.Save(composed, Name(what, theme));
        Assert.True(Screenshots.DistinctPixels(content) > 30, $"{what}: the menu looks blank");
        return composed;
    }

    private static BitmapSource CaptureDialog(Window dialog, UiTheme theme, string what)
    {
        WindowCapture.ShowOffScreen(dialog);
        try
        {
            var bitmap = WindowCapture.Client(dialog);
            Screenshots.Save(bitmap, Name(what, theme));
            AssertColor(Palette(theme, UiPalette.Window), Screenshots.PixelAt(bitmap, 1, 1), $"{what} corner");
            Assert.True(Screenshots.DistinctPixels(bitmap) > 30, $"{what} looks blank");
            Assert.False(dialog.ShowInTaskbar);
            return bitmap;
        }
        finally
        {
            dialog.Close();
        }
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_default_view_has_the_tint_header_and_the_new_panes(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var (window, h) = Open();
        try
        {
            var bitmap = Capture(window, theme, "f18-builder-default");
            // Decision #24: the header is AccentSoft with an InfoBorder bottom line.
            AssertColor(Palette(theme, UiPalette.AccentSoft), At(bitmap, window.Header, window.Root, window.Header.ActualWidth / 2, 4), "header tint");
            AssertColor(Palette(theme, UiPalette.InfoBorder),
                At(bitmap, window.Header, window.Root, window.Header.ActualWidth / 2, window.Header.ActualHeight - 0.5), "header bottom line");
            Assert.Equal(54, window.Header.ActualHeight);
            // The panes: Commands 320, Profile 330 (decision #25's build notes).
            Assert.Equal(320, window.Panes.ColumnDefinitions[0].ActualWidth);
            Assert.Equal(330, window.Panes.ColumnDefinitions[2].ActualWidth);
            // The profile button: the name and "(loaded)"; General is the open tab; clean: no indicator, Save off.
            Assert.Equal("Architecture", window.ProfileButtonName.Text);
            Assert.True(h.Model.IsLoadedProfile);
            Assert.Same(window.GeneralTab, window.ProfileTabs.SelectedItem);
            Assert.Equal("Architecture", window.ProfileNameBox.Text);
            Assert.True(window.ProfileNameBox.IsReadOnly);
            Assert.Equal(Visibility.Collapsed, window.UnsavedIndicator.Visibility);
            Assert.False(window.SaveButton.IsEnabled, "Save is enabled only with changes");
        }
        finally
        {
            Close(window, h);
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_settings_menu_holds_branding_for_this_machine(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var (window, h) = Open();
        try
        {
            Toggle(window.SettingsButton);
            Assert.True(window.SettingsMenu.IsOpen);
            Assert.True(window.SettingsButton.IsChecked);
            Assert.False(window.SettingsButton.IsHitTestVisible, "while open the button ignores the mouse (D2)");
            CaptureMenu(window, window.SettingsButton, window.SettingsMenu, theme, "f18-builder-settings-menu");
            var items = window.SettingsMenu.Items.OfType<MenuItem>().ToList();
            Assert.Equal(["This machine — not saved in profiles", "Branding…"], items.Select(i => (string)i.Header));
            Assert.False(items[0].Focusable, "the heading is not an item");
            Assert.Equal("logo + link on every tab", items[1].InputGestureText);
        }
        finally
        {
            window.SettingsMenu.IsOpen = false;
            Close(window, h);
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_profile_menu_lists_the_profiles_then_new_rename_and_duplicate(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var (window, h) = Open(x => x.Model.SelectedProfileOption = x.Option("Structure"));
        try
        {
            Toggle(window.ProfileButton);
            Assert.True(window.ProfileMenu.IsOpen);
            CaptureMenu(window, window.ProfileButton, window.ProfileMenu, theme, "f18-builder-profile-menu");
            var profiles = window.ProfileMenuItems;
            Assert.Equal(["Architecture", "Structure"], profiles.Select(i => ((ProfileOptionViewModel)i.Header).Display));
            Assert.Equal(["loaded · tab \"Studio Arch\"", "tab \"Studio Struct\""], profiles.Select(i => i.InputGestureText));
            Assert.Equal([false, true], profiles.Select(i => ((ProfileOptionViewModel)i.Header).IsCurrent));
            var tail = window.ProfileMenu.Items.OfType<MenuItem>().Skip(3).Select(i => (string)i.Header);
            Assert.Equal(["+ New profile", "Rename…", "Duplicate…"], tail);
        }
        finally
        {
            window.ProfileMenu.IsOpen = false;
            Close(window, h);
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void Each_profile_tab_renders(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var (window, h) = Open(x =>
        {
            x.Model.SelectedProfileOption = x.Option("Structure");
            x.Model.CleanupRows[4].Enabled = false; // an edit: the unsaved-changes indicator shows
        });
        try
        {
            Capture(window, theme, "f18-builder-tab-general");
            Assert.Equal(Visibility.Visible, window.UnsavedIndicator.Visibility);
            Assert.True(window.SaveButton.IsEnabled);
            Assert.Equal("60%", h.Model.PanelOpacityText);

            window.ProfileTabs.SelectedItem = window.LoaderDefaultsTab;
            WindowCapture.DoEvents();
            Capture(window, theme, "f18-builder-tab-loader-defaults");
            var switches = WindowCapture.Descendants<CheckBox>(window.ProfileTabs).Where(c => c.IsVisible).ToList();
            Assert.Equal(1 + h.Model.PresetTabs.Count, switches.Count);

            window.ProfileTabs.SelectedItem = window.JunkCleanupTab;
            WindowCapture.DoEvents();
            Capture(window, theme, "f18-builder-tab-junk-cleanup");
            var names = WindowCapture.Descendants<TextBox>(window.ProfileTabs)
                .Where(t => t.IsVisible && AutomationProperties.GetAutomationId(t) == "Builder.CleanupName").Select(t => t.Text);
            Assert.Equal(h.Model.CleanupRows.Select(r => r.Name), names);
        }
        finally
        {
            Close(window, h);
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_branding_dialog_renders(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var backend = BuilderFixtures.Backend();
        backend.Branding = new BrandingState(true, "branding.png", "https://studio.example");
        var h = BuilderHarness.Create(backend);
        var dialog = new BrandingDialog(h.Model);
        WindowCapture.ShowOffScreen(dialog);
        Assert.Equal("branding.png", dialog.LogoName.Text);
        Assert.Equal("https://studio.example", dialog.BrandingUrlBox.Text);
        Assert.True(dialog.DoneButton.IsCancel, "Escape closes it");
        dialog.Close();
        CaptureDialog(new BrandingDialog(h.Model), theme, "f18-builder-branding-dialog");
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_rename_and_duplicate_dialogs_render(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var h = BuilderHarness.Create();
        h.Model.SelectedProfileOption = h.Option("Structure");

        h.Modals.Names.Enqueue("Structure");
        h.Model.RenameCommand.Execute(null);
        var rename = h.Modals.NameDialogs.Single().Model;
        Assert.Equal("Rename profile", rename.Title);
        Assert.Equal("Same profile, new name. Users who have it loaded keep it.", rename.Help);
        CaptureDialog(new ProfileNameDialog(rename), theme, "f18-builder-rename-dialog");

        // The refusal under the box: another saved profile has the name (case-insensitive).
        var refused = ProfileNameDialogModel.ForRename("Structure", n => ProfileNaming.Check(n, BuilderFixtures.StructId, h.Backend.Library.Select(e => e.Profile)));
        refused.Name = "architecture";
        Assert.False(refused.TryConfirm());
        var refusedDialog = new ProfileNameDialog(refused);
        CaptureDialog(refusedDialog, theme, "f18-builder-rename-refused");
        Assert.Equal("A profile named \"architecture\" already exists. Choose a different name.", refusedDialog.ErrorLine.Text);

        h.Model.DuplicateCommand.Execute(null); // no names scripted: cancelled
        var duplicate = h.Modals.NameDialogs[1].Model;
        Assert.Equal("Duplicate profile", duplicate.Title);
        Assert.Equal("Structure copy", duplicate.Name);
        CaptureDialog(new ProfileNameDialog(duplicate), theme, "f18-builder-duplicate-dialog");
    });
}
