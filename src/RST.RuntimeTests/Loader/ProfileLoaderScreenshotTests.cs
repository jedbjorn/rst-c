// rst-c's own (not vendored); the capture helpers follow Gallery/StyleGalleryScreenshotTests.
// AC3 for the Profile Loader (spec #16, WU3): the window and its dialogs rendered off-screen to
// PNG in light and dark, driven by the view-model over the three-profile fixture set: the main
// view (one profile active, another selected, its add-in pills in every state), no profiles, the
// busy "Loading …" state, a toast and the destructive delete confirm. Spec #18 (AC4): Confirm Add-in
// Changes from a real two-year plan, and the one add-in report (the Load's Add-in Check all clear
// and with every section, Unload, the RST 3.0 move, Restore All). Each capture also samples the
// pixels that prove its state. Feature #18 (spec #19, WU6, AC8): the Loader in the Builder's flavour
// (the shared header and pane header), as f18-loader-* beside the Builder's f18-builder-* captures.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RST.Core.Activation;
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
        // Planner defaults 1 and 2: the title and the size (before Windows caps a shown window at the CI desktop).
        var unshown = new ProfileLoaderWindow(new ProfileLoaderViewModel(new FakeLoaderBackend(), new LoaderTestDialogs(), new LoaderTestDialogs()));
        Assert.Equal("RST — Profile Loader", unshown.Title);
        Assert.Equal(1450, unshown.Width);
        Assert.Equal(900, unshown.Height);
        Assert.Equal(WindowStartupLocation.CenterOwner, unshown.WindowStartupLocation);

        var opened = Open(LoaderFixtures.Backend(), (vm, _) => vm.SelectedCard = vm.Profiles.Single(c => c.Name == "Structure"));
        try
        {
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

            // Three panels; the pack icons are drawn (Planner default 9), the non-pack slot is not. The
            // Checks stack's 3 tools have no pack icon and draw the ribbon's default 16 px icon (spec #17).
            Assert.Equal(3, preview.DrawnPanels.Count);
            var images = WindowCapture.Descendants<Image>(preview).Where(i => i.Source is not null).ToList();
            Assert.Equal(6, images.Count(i => !ReferenceEquals(i.Source, PackIcons.Default16)));
            Assert.Equal(3, images.Count(i => ReferenceEquals(i.Source, PackIcons.Default16)));

            // The RSTify switches are on and off; the add-in rows show every state (spec #18, In Scope 7).
            Assert.Contains(opened.Model.TabToggles, t => t.IsHidden);
            Assert.Contains(opened.Model.TabToggles, t => !t.IsHidden);
            Assert.Equal(new[] { "Native", "Loaded", "Loaded", "Disabled", "Locked", "Not Found" }, opened.Model.RequiredAddins.Select(r => r.StatusLabel));

            // Review N2: the HTML's pill colors: Native and Loaded green ("present"), Disabled and Not
            // Found amber; Locked (by the administrator) red.
            var pills = WindowCapture.Descendants<ContentControl>(opened.Window.Root)
                .Where(c => System.Windows.Automation.AutomationProperties.GetAutomationId(c) == "Loader.AddinStatus").ToList();
            Assert.Equal(6, pills.Count);
            var expected = new[] { UiPalette.SuccessSoft, UiPalette.SuccessSoft, UiPalette.SuccessSoft, UiPalette.WarningSoft, UiPalette.ErrorSoft, UiPalette.WarningSoft };
            for (var i = 0; i < pills.Count; i++)
                AssertColor(Palette(theme, expected[i]), At(bitmap, pills[i], root, 4, pills[i].ActualHeight / 2), $"status pill {i} fill");
        }
        finally
        {
            opened.Window.Close();
        }
    });

    /// <summary>
    /// Feature #18 (spec #19, WU6, AC8; decision #31): the Loader has the Builder's flavour. Its header
    /// is the shared HeaderStrip (the Tint of decision #24: AccentSoft, an InfoBorder bottom line, 54 px)
    /// with the shared title and divider, and its Profiles pane has the Builder's pane header row
    /// (PaneHeader: 42 px, a line under it). The PNGs are named to sit beside f18-builder-default-*.
    /// </summary>
    private static void AssertBuilderFlavour(ProfileLoaderWindow window, BitmapSource bitmap, UiTheme theme, string what)
    {
        var root = window.Root;
        var header = window.Header;
        Assert.Same(window.FindResource("HeaderStrip"), header.Style);
        Assert.Equal(54, header.ActualHeight);
        Assert.Equal(new Thickness(16, 0, 16, 0), header.Padding);
        AssertColor(Palette(theme, UiPalette.AccentSoft), At(bitmap, header, root, header.ActualWidth / 2, 4), $"{what} header tint");
        AssertColor(Palette(theme, UiPalette.AccentSoft), At(bitmap, header, root, header.ActualWidth - 4, header.ActualHeight / 2), $"{what} header tint, right");
        AssertColor(Palette(theme, UiPalette.InfoBorder), At(bitmap, header, root, header.ActualWidth / 2, header.ActualHeight - 0.5), $"{what} header bottom line");

        var title = WindowCapture.Descendants<TextBlock>(header).Single(t => System.Windows.Automation.AutomationProperties.GetAutomationId(t) == "Loader.Title");
        Assert.Same(window.FindResource("HeaderTitle"), title.Style);
        Assert.Equal(14, title.FontSize);
        Assert.Equal(FontWeights.SemiBold, title.FontWeight);
        var divider = WindowCapture.Descendants<System.Windows.Shapes.Rectangle>(header).Single();
        Assert.Same(window.FindResource("HeaderDivider"), divider.Style);
        AssertColor(Palette(theme, UiPalette.InfoBorder), At(bitmap, divider, root, 0.5, divider.ActualHeight / 2), $"{what} header divider");

        var pane = window.ProfilesHeader;
        Assert.Same(window.FindResource("PaneHeader"), pane.Style);
        Assert.Equal(42, pane.ActualHeight);
        AssertColor(Palette(theme, UiPalette.SurfaceAlt), At(bitmap, pane, root, pane.ActualWidth / 2, 4), $"{what} Profiles pane header");
        AssertColor(Palette(theme, UiPalette.Border), At(bitmap, pane, root, pane.ActualWidth / 2, pane.ActualHeight - 0.5), $"{what} Profiles pane header line");
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_Loader_has_the_Builder_flavour(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var opened = Open(LoaderFixtures.Backend(), (vm, _) => vm.SelectedCard = vm.Profiles.Single(c => c.Name == "Structure"));
        try
        {
            var bitmap = CaptureLoader(opened, theme, "f18-loader-main");
            AssertBuilderFlavour(opened.Window, bitmap, theme, "main");
        }
        finally
        {
            opened.Window.Close();
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_empty_Loader_has_the_Builder_flavour(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var opened = Open(new FakeLoaderBackend { AllTabs = LoaderFixtures.Ribbon });
        try
        {
            var bitmap = CaptureLoader(opened, theme, "f18-loader-empty");
            AssertBuilderFlavour(opened.Window, bitmap, theme, "empty");
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
            var bitmap = CaptureLoader(opened, theme, "loader-busy", ToColor(palette[UiPalette.Scrim].Over(palette[UiPalette.Window])));
            // The disabled card list keeps its pane color under the scrim.
            AssertColor(ToColor(palette[UiPalette.Scrim].Over(palette[UiPalette.SurfaceAlt])), Screenshots.PixelAt(bitmap, 150, 850), "card list pane under the scrim");
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
        backend.OnImport = _ => ImportOutcome.Fail("Not a profile package.");
        var opened = Open(backend, (vm, dialogs) =>
        {
            dialogs.OpenResult = @"C:\Downloads\shared.zip";
            vm.ImportCommand.Execute(null);
        });
        try
        {
            Assert.True(opened.Model.Toast.IsOpen);
            Assert.Equal("Import Failed", opened.Model.Toast.Title);
            CaptureLoader(opened, theme, "loader-toast");
        }
        finally
        {
            opened.Window.Close();
        }
    });

    /// <summary>The Structure load's plan over the fixture's 2026 and 2027 add-ins (Disable on).</summary>
    private static (FakeLoaderBackend Backend, ActivationPlan Plan) StructurePlan()
    {
        var backend = LoaderFixtures.Backend();
        LoaderFixtures.IdeateSwitchedOffByRst(backend);
        var structure = backend.Profiles.Single(p => p.Profile.Id == LoaderFixtures.StructId).Profile;
        // Structure requires Ideate too, so rst-c's own switch-off of it is undone.
        structure.RequiredAddins.Add(new RST.Core.Profiles.RequiredAddin { TabName = "Ideate", AddinFile = "IdeateBIMLink.addin" });
        return (backend, backend.PlanLoad(structure.ProfileName, structure.Id, disableUnused: true).Plan!);
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Confirm_addin_changes_shows_the_plan_per_year(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var model = ConfirmAddinChangesModel.From(StructurePlan().Plan);
        Assert.Equal(new[] { "Enscape (2026, 2027)", "DiRoots.One (2026, 2027)" }, model.SwitchingOff);
        Assert.Equal(new[] { "Ideate BIMLink (2026, 2027)" }, model.SwitchingOn);
        Assert.Equal(new[] { "PyRevitLoader (2026)" }, model.Staying);
        Assert.Equal(new[] { "Revizto (2027): locked on by your administrator" }, model.CannotChange);
        var dialog = new ConfirmAddinChangesWindow(model);
        Assert.Equal("Confirm Add-in Changes", dialog.Title);
        Assert.False(dialog.ShowInTaskbar);
        // Review N1: changing add-ins is destructive, so Enter means Cancel.
        Assert.False(((Button)dialog.FindName("ConfirmButton")).IsDefault);
        Assert.True(((Button)dialog.FindName("CancelButton")).IsDefault);
        Assert.Equal(Visibility.Visible, ((FrameworkElement)dialog.FindName("CannotChangePanel")).Visibility);
        // "Try Disable" is gone (spec #18, In Scope 5).
        Assert.Null(dialog.FindName("TryPanel"));
        CaptureDialog(dialog, theme, "loader-confirm-addin-changes");

        // Nothing known to be blocked: no "Can't Be Changed" panel; "None" fills an empty column.
        var plain = new ConfirmAddinChangesWindow(new ConfirmAddinChangesModel(["Twinmotion (2026)"], [], [], [], [2026]));
        WindowCapture.ShowOffScreen(plain);
        try
        {
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)plain.FindName("CannotChangePanel")).Visibility);
            Assert.Contains(WindowCapture.Descendants<TextBlock>(plain), t => t.Text == "None" && t.IsVisible);
            Assert.Contains(WindowCapture.Descendants<TextBlock>(plain), t => t.Text == "For your Windows user, in Revit 2026." && t.IsVisible);
        }
        finally
        {
            plain.Close();
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_add_in_report_renders_all_clear_and_with_every_section(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var clear = new AddinReportWindow(AddinReportModel.ForLoad("Architecture", ActivationReport.Empty(ActivationIntent.Load), QaCheck.Clear, _ => { }));
        Assert.Equal("Add-in Check", clear.Title);
        CaptureDialog(clear, theme, "loader-addin-check-clear");

        // The Structure load: switched off, back on (required), locked, missing, required but off.
        var (backend, plan) = StructurePlan();
        var structure = backend.Profiles.Single(p => p.Profile.Id == LoaderFixtures.StructId).Profile;
        var session = AddinSessionContext.For("2026", backend.Inventory, LoaderFixtures.Ribbon, backend.Lookup);
        var report = RST.Tests.Loader.FakeAddinSwitching.AllDone(plan);
        var model = AddinReportModel.ForLoad(structure.ProfileName, report, QaCheck.For(structure, session, plan), _ => { });
        Assert.Equal(new[] { "Enscape (2026, 2027)", "DiRoots.One (2026, 2027)" }, model.SwitchedOff);
        Assert.Equal(new[] { "Ideate BIMLink (2026, 2027) — required" }, model.SwitchedOn);
        Assert.Equal(new[] { "Revizto (2027): locked on by your administrator" }, model.NotChanged);
        Assert.Equal(2, model.RequiredOff.Count);
        Assert.Single(model.Missing);
        var issues = new AddinReportWindow(model);
        CaptureDialog(issues, theme, "loader-addin-check-issues");
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_same_report_serves_unload_restore_all_and_the_rst_3_move(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var item = LoaderFixtures.Inventory().Items.First(i => i.Name == "DiRoots.One");
        var entry = new PlanEntry(2026, "DiRoots.One", null, AddinScope.Machine, item.ManifestPath, item.FileName, item.AddinIds,
            PlanAction.Enable, null, false, false, false, null);
        var unload = new ActivationReport(ActivationIntent.Unload,
        [
            ActivationOutcome.For(entry, OutcomeResult.Done),
            ActivationOutcome.For(entry with { Year = 2027 }, OutcomeResult.Done),
            ActivationOutcome.For(entry with { Name = "Old Tool", Year = 2025, Renamed = true }, OutcomeResult.NotChanged, ChangeReason.NeedsAdmin),
        ], ["Revit 2027: \u201CDisable all Add-Ins\u201D is ticked in Autodesk's Add-Ins Manager, so no add-in loads there."]);
        var unloadModel = AddinReportModel.ForUnload(unload);
        Assert.Equal(new[] { "DiRoots.One (2026, 2027)" }, unloadModel.SwitchedOn);
        Assert.Equal(new[] { "Old Tool (2025): installed for all users; renaming its file back needs administrator rights" }, unloadModel.NotChanged);
        CaptureDialog(new AddinReportWindow(unloadModel), theme, "loader-addin-report-unload");

        var moved = new ActivationReport(ActivationIntent.MigrateLegacy,
            [ActivationOutcome.For(entry with { Action = PlanAction.Disable, Migrate = true, Name = "Kinship" }, OutcomeResult.Done)], []);
        var migration = new AddinReportWindow(AddinReportModel.ForMigration(moved));
        Assert.Equal("Add-ins Switched Off by RST 3.0", migration.Title);
        CaptureDialog(migration, theme, "loader-addin-report-migration");

        var restore = new AddinReportWindow(AddinReportModel.ForRestoreAll(ActivationReport.Empty(ActivationIntent.RestoreAll)));
        WindowCapture.ShowOffScreen(restore);
        try
        {
            Assert.Contains(WindowCapture.Descendants<TextBlock>(restore), t => t.Text == "No add-in needed changing." && t.IsVisible);
            Assert.Contains(WindowCapture.Descendants<TextBlock>(restore), t => t.Text == "Add-in changes apply the next time Revit starts." && t.IsVisible);
        }
        finally
        {
            restore.Close();
        }
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
