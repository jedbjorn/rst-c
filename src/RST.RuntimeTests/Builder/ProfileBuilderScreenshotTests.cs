// rst-c's own (not vendored); the capture helpers follow Loader/ProfileLoaderScreenshotTests.
// AC3 for the Profile Builder shell (spec #16, WU4): the window rendered off-screen to PNG in light
// and dark, driven by the view-model: a realistic profile in edit mode (filtered, expanded tree,
// selected panel, live preview), a new profile, the three-way unsaved-changes dialog, the
// catalogue's two empty states, and a toast. Each capture also samples the pixels or elements that
// prove its state. The window and its dialogs stay out of the taskbar (PLN1's VM finding on the
// Health window). Feature #18 retired the Branding popover and the URL button editor; the new
// header, menus, Profile tabs and dialogs are captured in ProfileBuilderShellScreenshotTests.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RST.Core.Theming;
using RST.RuntimeTests.Loader;
using RST.RuntimeTests.Support;
using RST.UI.Builder;
using RST.UI.Controls;
using RST.UI.Dialogs;
using RST.UI.Theming;
using Xunit;

namespace RST.RuntimeTests.Builder;

[Collection(WpfThemeCollection.Name)]
public class ProfileBuilderScreenshotTests
{
    /// <summary>The Builder's size (spec #16, Planner default 2), rendered unclipped by the CI desktop.</summary>
    private static readonly Size BuilderSize = new(1450, 900);

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

    private static Point Offset(Visual element, Visual root) => element.TransformToAncestor(root).Transform(new Point(0, 0));

    private static Color At(BitmapSource bitmap, Visual element, Visual root, double x, double y)
    {
        var p = element.TransformToAncestor(root).Transform(new Point(x, y));
        return Screenshots.PixelAt(bitmap, (int)Math.Floor(p.X), (int)Math.Floor(p.Y));
    }

    private sealed record Opened(ProfileBuilderWindow Window, ProfileBuilderViewModel Model, BuilderHarness Harness);

    private static Opened Open(FakeBuilderBackend backend, Action<BuilderHarness>? arrange = null)
    {
        var h = BuilderHarness.Create(backend);
        arrange?.Invoke(h);
        var window = new ProfileBuilderWindow(h.Model);
        WindowCapture.ShowOffScreen(window);
        return new Opened(window, h.Model, h);
    }

    private static void Close(Opened opened)
    {
        // The editor may hold unsaved edits: approve the close, as Discard & Exit would.
        opened.Harness.Dialogs.ChooseAnswers.Enqueue(DialogChoice.Secondary);
        opened.Window.Close();
    }

    /// <summary>
    /// The Builder at 1450 x 900. Just right of the Commands pane, under the "Panels" header line,
    /// is bare window background (the window color unless something covers it).
    /// </summary>
    private static BitmapSource CaptureBuilder(Opened opened, UiTheme theme, string what)
    {
        var bitmap = WindowCapture.Content(opened.Window, BuilderSize);
        Screenshots.Save(bitmap, Name(what, theme));
        Assert.Equal(1450, bitmap.PixelWidth);
        Assert.Equal(900, bitmap.PixelHeight);
        Assert.True(Screenshots.DistinctPixels(bitmap) > 200, $"{what} looks blank");
        return bitmap;
    }

    private static void AssertWindowBackground(BitmapSource bitmap, Opened opened, UiTheme theme, string what)
    {
        var scroller = opened.Window.CanvasScroller;
        AssertColor(Palette(theme, UiPalette.Window), At(bitmap, scroller, opened.Window.Root, 6, 6), $"{what} window background");
    }

    [Fact]
    public void The_window_has_its_title_size_and_placement_and_stays_out_of_the_taskbar() => Sta(() =>
    {
        RstTheme.Apply(UiTheme.Light);
        var window = new ProfileBuilderWindow(BuilderHarness.Create().Model);
        // Planner defaults 1 and 2: the title (wrong today), the size, centred on Revit.
        Assert.Equal("RST — Profile Builder", window.Title);
        Assert.Equal(1450, window.Width);
        Assert.Equal(900, window.Height);
        Assert.Equal(WindowStartupLocation.CenterOwner, window.WindowStartupLocation);
        Assert.False(window.ShowInTaskbar, "the Builder must not get its own taskbar entry");
        var prompt = new MessageDialog(new MessageDialogModel("t", "t", "m", [], "Save & Exit", HasCancel: true) { SecondaryText = "Discard & Exit" },
            MessageDialog.DiscardChangesId);
        Assert.False(prompt.ShowInTaskbar, "the unsaved-changes prompt must not get its own taskbar entry");
        prompt.Close();
        window.Close();
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void Edit_mode_with_a_filtered_tree_a_selected_panel_and_the_live_preview(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var opened = Open(BuilderFixtures.Backend(), h =>
        {
            h.Model.SelectedProfileOption = h.Option("Structure");
            h.Model.Panels[1].SelectCommand.Execute(null); // Review: a pack icon, a stack and a non-pack slot
            h.Model.FilterText = "l";
            h.Model.AllCatalogRoots.Single(r => r.Label == "pyRevit").Children[0].IsExpanded = false; // one collapsed panel
        });
        try
        {
            var bitmap = CaptureBuilder(opened, theme, "builder-edit");
            AssertWindowBackground(bitmap, opened, theme, "builder-edit");
            var root = opened.Window.Root;
            Assert.True(opened.Model.IsEditMode);
            Assert.Equal("Structure", opened.Window.ProfileNameBox.Text);

            // The tree: filtered, the native group open, match counts.
            var tree = opened.Window.CatalogTreeView;
            Assert.Equal("Autodesk Native", opened.Model.CatalogRoots[0].Label);
            Assert.True(opened.Model.CatalogRoots[0].IsExpanded);
            Assert.True(WindowCapture.Descendants<TreeViewItem>(tree).Count() > 10, "the filtered tree shows its rows");

            // The selected card carries the accent ring; the others do not.
            var cards = WindowCapture.Descendants<Border>(opened.Window.PanelCards).Where(b => b.Name == "Ring").ToList();
            Assert.Equal(3, cards.Count);
            Assert.Equal(new[] { false, true, false }, cards.Select(c => c.Visibility == Visibility.Visible).ToArray());
            var ring = cards[1];
            AssertColor(Palette(theme, UiPalette.AccentForeground), At(bitmap, ring, root, 1, ring.ActualHeight / 2), "selected panel ring");

            // The selected card's stripe shows its colour (the Properties pane's swatch is retired, feature #18).
            var stripe = WindowCapture.Descendants<Border>(opened.Window.PanelCards)
                .Where(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "Builder.PanelStripe").ElementAt(1);
            AssertColor(Color.FromRgb(0x14, 0xb8, 0xa6), At(bitmap, stripe, root, stripe.ActualWidth / 2, 1), "panel colour stripe");

            // The live preview: three panels on the theme's ribbon canvas, pack icons drawn.
            var preview = opened.Window.Preview;
            Assert.Equal(3, preview.DrawnPanels.Count);
            Assert.Equal(theme == UiTheme.Dark, preview.IsDarkCanvas);
            Assert.True(WindowCapture.Descendants<Image>(preview).Count(i => i.Source is not null) >= 5, "pack icons in the preview");
            Assert.True(WindowCapture.Descendants<Image>(opened.Window.PanelCards).Count(i => i.Source is not null && i.IsVisible) >= 5, "slot thumbnails");
        }
        finally
        {
            Close(opened);
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void A_new_profile_starts_with_one_panel(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var opened = Open(BuilderFixtures.Backend());
        try
        {
            var bitmap = CaptureBuilder(opened, theme, "builder-new");
            AssertWindowBackground(bitmap, opened, theme, "builder-new");
            Assert.True(opened.Model.SelectedProfileOption!.IsNew);
            Assert.Equal("New profile", opened.Window.ProfileButtonName.Text);
            Assert.Single(opened.Model.Panels);
            Assert.False(opened.Model.CatalogRoots[0].IsExpanded, "the native group is collapsed by default");
            Assert.Single(opened.Window.Preview.DrawnPanels);
        }
        finally
        {
            Close(opened);
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_unsaved_changes_prompt_offers_three_choices(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        // The view-model asks; the capture shows exactly what DialogService.Choose opens.
        var h = BuilderHarness.Create();
        h.Model.ProfileName = "Draft";
        h.Dialogs.ChooseAnswers.Enqueue(DialogChoice.Cancel);
        _ = h.Model.ExitAsync();
        var (title, message, primary, secondary, automationId) = h.Dialogs.ChooseCalls.Single();
        var dialog = new MessageDialog(new MessageDialogModel(title, title, message, [], primary, HasCancel: true) { SecondaryText = secondary }, automationId);
        WindowCapture.ShowOffScreen(dialog);
        try
        {
            // The bindings resolve once the dialog is shown.
            var secondaryButton = (Button)dialog.FindName("SecondaryButton");
            Assert.Equal(Visibility.Visible, secondaryButton.Visibility);
            Assert.Equal("Discard & Exit", secondaryButton.Content);
            Assert.Equal("Save & Exit", ((Button)dialog.FindName("ConfirmButton")).Content);
            Assert.True(((Button)dialog.FindName("ConfirmButton")).IsDefault, "Save & Exit is the default");
            Assert.Equal(Visibility.Visible, ((Button)dialog.FindName("CancelButton")).Visibility);
            var bitmap = WindowCapture.Client(dialog);
            Screenshots.Save(bitmap, Name("builder-unsaved-dialog", theme));
            AssertColor(Palette(theme, UiPalette.Window), Screenshots.PixelAt(bitmap, 1, 1), "builder-unsaved-dialog corner");
            Assert.True(Screenshots.DistinctPixels(bitmap) > 30, "builder-unsaved-dialog looks blank");
        }
        finally
        {
            dialog.Close();
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_catalogue_empty_states(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var empty = Open(new FakeBuilderBackend());
        try
        {
            CaptureBuilder(empty, theme, "builder-catalog-empty");
            Assert.True(empty.Window.FindName("CatalogTreeView") is TreeView);
            Assert.Contains(WindowCapture.Descendants<TextBlock>(empty.Window.Root),
                t => t.IsVisible && t.Text.StartsWith("No commands available.", StringComparison.Ordinal));
        }
        finally
        {
            Close(empty);
        }

        var nomatch = Open(BuilderFixtures.Backend(), h => h.Model.FilterText = "zzz");
        try
        {
            CaptureBuilder(nomatch, theme, "builder-catalog-nomatch");
            Assert.Contains(WindowCapture.Descendants<TextBlock>(nomatch.Window.Root), t => t.IsVisible && t.Text == "No matches for \"zzz\".");
            // The tree's only other item, the Custom Tools group (feature #18, WU4), hides too: none of its tools match.
            Assert.DoesNotContain(WindowCapture.Descendants<TreeViewItem>(nomatch.Window.CatalogTreeView), t => t.IsVisible);
        }
        finally
        {
            Close(nomatch);
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void A_toast_shows_over_the_window(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var opened = Open(BuilderFixtures.Backend(), h =>
        {
            h.Model.SelectedProfileOption = h.Option("Structure");
            h.Model.ProfileTab = "Studio Structure";
            Assert.True(h.Model.SaveAsync().Result);
        });
        try
        {
            Assert.True(opened.Model.Toast.IsOpen);
            Assert.Equal(BannerKind.Success, opened.Model.Toast.Kind);
            Assert.StartsWith("Saved as Structure_", opened.Model.Toast.Text);
            CaptureBuilder(opened, theme, "builder-toast");
        }
        finally
        {
            Close(opened);
        }
    });
}
