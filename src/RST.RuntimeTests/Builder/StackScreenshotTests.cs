// rst-c's own (not vendored); the capture helpers follow ProfileBuilderScreenshotTests and
// ProfileLoaderScreenshotTests. Spec #17 (Ribbon Stacks), AC3 / T-A5: the Builder and the Loader
// with a 2-item and a 3-item stack, rendered off-screen to PNG in light and dark. The Builder shot
// has the Properties pane scrolled to the Stacks section (a selected stack, an unplaced stack with
// one tool, flagged), the panel cards with their read-only stack slots, and the live preview; the
// Loader shot has the stack profile selected. Each capture checks that the preview draws the
// stack rows with their 16 px pack icons.

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
using RST.UI.Loader;
using RST.UI.Theming;
using Xunit;

namespace RST.RuntimeTests.Builder;

[Collection(WpfThemeCollection.Name)]
public class StackScreenshotTests
{
    private static readonly Size WindowSize = new(1450, 900);

    public static TheoryData<UiTheme> Themes => new() { UiTheme.Light, UiTheme.Dark };

    private static string Name(string what, UiTheme theme) => $"{what}-{theme.ToString().ToLowerInvariant()}";

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

    private static string Id(DependencyObject d) => System.Windows.Automation.AutomationProperties.GetAutomationId(d);

    /// <summary>The preview's 16 px stack-row icons (the large tools' icons are 20 px).</summary>
    private static int StackRowIcons(RibbonPreview preview) =>
        WindowCapture.Descendants<Image>(preview).Count(i => i.Source is BitmapSource { PixelWidth: 16 } && i.Width == 16);

    private static BitmapSource Capture(Window window, UiTheme theme, string what)
    {
        var bitmap = WindowCapture.Content(window, WindowSize);
        Screenshots.Save(bitmap, Name(what, theme));
        Assert.Equal(1450, bitmap.PixelWidth);
        Assert.Equal(900, bitmap.PixelHeight);
        Assert.True(Screenshots.DistinctPixels(bitmap) > 200, $"{what} looks blank");
        return bitmap;
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_Builder_shows_stack_cards_stack_slots_and_the_stacks_in_the_preview(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var h = BuilderHarness.Create(StackFixtures.Backend());
        h.Model.SelectedProfileOption = h.Option("Stack Studio");
        // A stack being authored: one tool, not on a panel yet (both flags show).
        h.Model.AddStackCommand.Execute(null);
        h.Model.AddCatalogCommand.Execute(h.Command("ID_VIEW_SECTION"));
        // The Openings stack selected (its ring), the Model panel selected.
        h.Model.Stacks[0].SelectCommand.Execute(null);
        var window = new ProfileBuilderWindow(h.Model);
        WindowCapture.ShowOffScreen(window);
        try
        {
            // Lay out at full size, then scroll the Properties pane to the stack cards.
            WindowCapture.Content(window, WindowSize);
            var top = window.StackCards.TransformToAncestor((Visual)window.PropertiesScroller.Content).Transform(new Point(0, 0)).Y;
            window.PropertiesScroller.ScrollToVerticalOffset(top - 12);
            window.UpdateLayout();

            Capture(window, theme, "builder-stacks");

            Assert.Equal(new[] { "Openings", "Datum", "Help", "Stack 1" }, h.Model.Stacks.Select(c => c.Name));
            var rings = WindowCapture.Descendants<Border>(window.StackCards).Where(b => b.Name == "StackRing").ToList();
            Assert.Equal(new[] { true, false, false, false }, rings.Select(r => r.Visibility == Visibility.Visible).ToArray());
            var underfilled = WindowCapture.Descendants<ContentControl>(window.StackCards).Where(c => Id(c) == "Builder.StackUnderfilled" && c.IsVisible).ToList();
            var unplaced = WindowCapture.Descendants<ContentControl>(window.StackCards).Where(c => Id(c) == "Builder.StackUnplaced" && c.IsVisible).ToList();
            Assert.Single(underfilled);
            Assert.Single(unplaced);
            Assert.True(WindowCapture.Descendants<Image>(window.StackCards).Count(i => i.Source is not null && i.IsVisible) >= 7, "stack tool thumbnails");

            // The preview: 2 panels; 3 stacks drawn as rows with 16 px icons (7 tools); the unplaced stack is not drawn.
            var preview = window.Preview;
            Assert.Equal(2, preview.DrawnPanels.Count);
            Assert.Equal(7, StackRowIcons(preview));
        }
        finally
        {
            h.Dialogs.ChooseAnswers.Enqueue(DialogChoice.Secondary);
            window.Close();
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_Loader_preview_draws_the_stacks_with_their_icons(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var backend = new FakeLoaderBackend { AllTabs = LoaderFixtures.Ribbon }.With(LoaderFixtures.Architecture(), StackFixtures.Profile());
        backend.ActiveIs(LoaderFixtures.Architecture(), ["Analyze"]);
        var dialogs = new LoaderTestDialogs();
        var model = new ProfileLoaderViewModel(backend, dialogs, dialogs, new ManualClock().Schedule, _ => { });
        model.Initialize();
        model.SelectedCard = model.Profiles.Single(c => c.Name == "Stack Studio");
        var window = new ProfileLoaderWindow(model);
        WindowCapture.ShowOffScreen(window);
        try
        {
            Capture(window, theme, "loader-stacks");

            var preview = window.Preview;
            Assert.Equal(2, preview.DrawnPanels.Count);
            Assert.Equal(7, StackRowIcons(preview));
            // The two large tools draw their 32 px icons at 20 px.
            Assert.Equal(2, WindowCapture.Descendants<Image>(preview).Count(i => i.Source is not null && i.Width == 20));
        }
        finally
        {
            window.Close();
        }
    });
}
