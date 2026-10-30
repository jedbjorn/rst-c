// rst-c's own (not vendored); the capture helpers follow Gallery/StyleGalleryScreenshotTests.
// AC3 for the Profile Loader (spec #16, WU3): the window and its dialogs rendered off-screen to
// PNG in light and dark, driven by the view-model over the three-profile fixture set: the main
// view (one profile active, another selected), no profiles, Confirm Add-in Changes, the Add-in
// Check (all clear, and restored + missing), the busy "Loading …" state, a toast and the
// destructive delete confirm. Each capture also samples the pixels that prove its state.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RST.Core.Loader;
using RST.Core.Theming;
using RST.RuntimeTests.Support;
using RST.UI.Controls;
using RST.UI.Dialogs;
using RST.UI.Loader;
using RST.UI.Theming;
using Xunit;

namespace RST.RuntimeTests.Loader;

[Collection(WpfThemeCollection.Name)]
public class ProfileLoaderScreenshotTests
{
    /// <summary>The Loader's size (spec #16, Planner default 2), rendered unclipped by the CI desktop.</summary>
    private static readonly Size LoaderSize = new(1450, 900);

    public static TheoryData<UiTheme> Themes => new() { UiTheme.Light, UiTheme.Dark };

    private static string Name(string what, UiTheme theme) => $"{what}-{theme.ToString().ToLowerInvariant()}";

    private static Color Palette(UiTheme theme, string key) => ToColor(UiPalette.For(theme)[key]);

    private static Color ToColor(UiColor c) => Color.FromRgb(c.R, c.G, c.B);

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

    private static int Distance(Color a, Color b) =>
        Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));

    private static void AssertColor(Color expected, Color actual, string what) =>
        Assert.True(Distance(expected, actual) <= 2, $"{what}: expected {expected}, rendered {actual}");

    private static Color At(BitmapSource bitmap, Visual element, Visual root, double x, double y)
    {
        var p = element.TransformToAncestor(root).Transform(new Point(x, y));
        return Screenshots.PixelAt(bitmap, (int)Math.Floor(p.X), (int)Math.Floor(p.Y));
    }

    private sealed record Opened(ProfileLoaderWindow Window, ProfileLoaderViewModel Model, LoaderTestDialogs Dialogs);

    private static Opened Open(FakeLoaderBackend backend, Action<ProfileLoaderViewModel, LoaderTestDialogs>? arrange = null)
    {
        var dialogs = new LoaderTestDialogs();
        var clock = new ManualClock();
        var model = new ProfileLoaderViewModel(backend, dialogs, dialogs, clock.Schedule, _ => { });
        model.Initialize();
        arrange?.Invoke(model, dialogs);
        var window = new ProfileLoaderWindow(model);
        WindowCapture.ShowOffScreen(window);
        return new Opened(window, model, dialogs);
    }

    /// <summary>
    /// The Loader at 1450 x 900. The right margin of the detail pane, halfway down, is bare window
    /// background: <paramref name="background"/> there (the window color unless an overlay covers it).
    /// </summary>
    private static BitmapSource CaptureLoader(Opened opened, UiTheme theme, string what, Color? background = null)
    {
        var bitmap = WindowCapture.Content(opened.Window, LoaderSize);
        Screenshots.Save(bitmap, Name(what, theme));
        Assert.Equal(1450, bitmap.PixelWidth);
        Assert.Equal(900, bitmap.PixelHeight);
        AssertColor(background ?? Palette(theme, UiPalette.Window), Screenshots.PixelAt(bitmap, bitmap.PixelWidth - 6, 450), $"{what} window background");
        Assert.True(Screenshots.DistinctPixels(bitmap) > 200, $"{what} looks blank");
        return bitmap;
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
            return bitmap;
        }
        finally
        {
            dialog.Close();
        }
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Main_view_renders_with_an_active_and_a_selected_profile(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var opened = Open(LoaderFixtures.Backend(), (vm, _) => vm.SelectedCard = vm.Profiles.Single(c => c.Name == "Structure"));
        try
        {
            Assert.Equal("RST — Profile Loader", opened.Window.Title);
            Assert.Equal(1450, opened.Window.Width);
            Assert.Equal(900, opened.Window.Height);
            var bitmap = CaptureLoader(opened, theme, "loader-main");
            var root = opened.Window.Root;

            // Cards: Architecture is active (success ring), Structure selected (accent ring).
            var cards = opened.Window.Cards;
            var active = (ListBoxItem)cards.ItemContainerGenerator.ContainerFromItem(opened.Model.Profiles[0]);
            var selected = (ListBoxItem)cards.ItemContainerGenerator.ContainerFromItem(opened.Model.Profiles[1]);
            AssertColor(Palette(theme, UiPalette.Success), At(bitmap, active, root, 1, active.ActualHeight / 2), "active card ring");
            AssertColor(Palette(theme, UiPalette.AccentForeground), At(bitmap, selected, root, 1, selected.ActualHeight / 2), "selected card ring");

            // The ribbon canvas follows the window theme (Revit's ribbon colors), not the OS.
            var preview = opened.Window.Preview;
            Assert.Equal(theme == UiTheme.Dark, preview.IsDarkCanvas);
            var canvas = preview.CanvasBorder;
            AssertColor(theme == UiTheme.Dark ? RibbonPreview.DarkCanvas : RibbonPreview.LightCanvas,
                At(bitmap, canvas, root, canvas.ActualWidth - 4, canvas.ActualHeight - 4), "ribbon canvas");

            // Three panels; the pack icons are drawn (Planner default 9), the non-pack slot is not.
            Assert.Equal(3, preview.DrawnPanels.Count);
            var icons = WindowCapture.Descendants<Image>(preview).Count(i => i.Source is not null);
            Assert.Equal(6, icons);

            // The RSTify switches are on and off; the add-in rows show every reachable status.
            Assert.Contains(opened.Model.TabToggles, t => t.IsHidden);
            Assert.Contains(opened.Model.TabToggles, t => !t.IsHidden);
            Assert.Equal(new[] { "Native", "Loaded", "Loaded", "Not Found", "Not Found" }, opened.Model.RequiredAddins.Select(r => r.StatusLabel));
        }
        finally
        {
            opened.Window.Close();
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void No_profiles_shows_the_empty_state(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var opened = Open(new FakeLoaderBackend { AllTabs = LoaderFixtures.Ribbon });
        try
        {
            CaptureLoader(opened, theme, "loader-empty");
            Assert.False(opened.Model.HasProfiles);
            Assert.Equal(Visibility.Collapsed, opened.Window.Cards.Visibility);
        }
        finally
        {
            opened.Window.Close();
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void Loading_shows_the_busy_overlay(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var backend = LoaderFixtures.Backend();
        backend.OnLoad = _ => Task.FromResult(new LoadResult(true, [], true, [], QaCheck.Clear));
        var opened = Open(backend, (vm, _) =>
        {
            vm.SelectedCard = vm.Profiles.Single(c => c.Name == "Structure");
            vm.DisableUnused = false;
            Assert.True(vm.LoadAsync().IsCompleted);
        });
        try
        {
            Assert.True(opened.Model.Busy.IsBusy);
            Assert.Equal("Loading Structure", opened.Model.Busy.Title);
            Assert.False(opened.Window.Body.IsEnabled);
            // The scrim covers the whole window.
            var palette = UiPalette.For(theme);
            CaptureLoader(opened, theme, "loader-busy", ToColor(palette[UiPalette.Scrim].Over(palette[UiPalette.Window])));
        }
        finally
        {
            opened.Window.Close();
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void A_toast_shows_over_the_window(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var backend = LoaderFixtures.Backend();
        backend.OnRestore = () => new RestoreOutcome(true, null, true, ["Kinship.addin", "Enscape.addin"], []);
        var opened = Open(backend, (vm, _) => vm.RestoreCommand.Execute(null));
        try
        {
            Assert.True(opened.Model.Toast.IsOpen);
            Assert.Equal("Please Restart Revit", opened.Model.Toast.Title);
            CaptureLoader(opened, theme, "loader-toast");
        }
        finally
        {
            opened.Window.Close();
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void Confirm_addin_changes_renders_its_three_columns(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var model = new ConfirmAddinChangesModel(
            ["Kinship", "pyRevit", "Enscape", "RST"],
            ["Twinmotion", "Lumion LiveSync", "Dynamo Player", "BIM 360 Glue Connector for Revit"],
            ["FormIt Converter", "eTransmit"]);
        var dialog = new ConfirmAddinChangesWindow(model);
        Assert.Equal("Confirm Add-in Changes", dialog.Title);
        Assert.True(((Button)dialog.FindName("ConfirmButton")).IsDefault);
        Assert.Equal(Visibility.Visible, ((FrameworkElement)dialog.FindName("TryPanel")).Visibility);
        CaptureDialog(dialog, theme, "loader-confirm-addin-changes");

        // Without Try Disable entries the column is gone and "None" fills an empty column.
        var plain = new ConfirmAddinChangesWindow(new ConfirmAddinChangesModel(["Kinship"], [], []));
        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)plain.FindName("TryPanel")).Visibility);
        plain.Close();
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void Addin_check_renders_all_clear_and_with_issues(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        CaptureDialog(new AddinCheckWindow(new AddinCheckModel("Architecture", QaCheck.Clear, _ => { })), theme, "loader-addin-check-clear");

        var qa = new QaCheck(
            [new QaRestoredItem("Kinship", "KinshipTools.addin"), new QaRestoredItem("Ideate", "Ideate.BIMLink.addin")],
            [new QaMissingItem("Lumion", "Lumion.addin", "https://lumion.com"), new QaMissingItem("Guardian", "Guardian.addin", "")]);
        var issues = new AddinCheckWindow(new AddinCheckModel("Structure", qa, _ => { }));
        Assert.Equal("Add-in Check", issues.Title);
        CaptureDialog(issues, theme, "loader-addin-check-issues");
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void Delete_confirm_is_the_destructive_family_dialog(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        // The view-model asks; the capture shows exactly what DialogService.Confirm would open.
        var opened = Open(LoaderFixtures.Backend(), (vm, dialogs) => vm.DeleteCommand.Execute(null));
        opened.Window.Close();
        var (title, message, confirmText, danger, automationId) = opened.Dialogs.Confirms.Single();
        Assert.True(danger);
        var dialog = new MessageDialog(new MessageDialogModel(title, title, message, [], confirmText, HasCancel: true, Danger: danger), automationId);
        Assert.True(((Button)dialog.FindName("CancelButton")).IsDefault, "a destructive confirm defaults to Cancel");
        CaptureDialog(dialog, theme, "loader-delete-confirm");
    });
}
