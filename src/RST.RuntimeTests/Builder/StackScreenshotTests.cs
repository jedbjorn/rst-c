// rst-c's own (not vendored); the capture helpers follow ProfileBuilderScreenshotTests and
// ProfileLoaderScreenshotTests. Spec #17 (Ribbon Stacks), AC3 (revised 2026-10-30, decision #19):
// a profile with a 2-item and a 3-item stack in the Builder (the panel cards with their stack
// groups, feature #18 WU3, and the Live Preview) and in the Loader, rendered off-screen to PNG in light and
// dark. Each capture checks that the preview draws the stack rows with 16 px icons; a stack tool
// with no pack icon draws the ribbon's default_16.png, also captured zoomed (preview-default16).

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
    public void The_Builder_keeps_the_stacks_and_its_preview_draws_them(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var h = BuilderHarness.Create(StackFixtures.Backend());
        h.Model.SelectedProfileOption = h.Option("Stack Studio");
        var window = new ProfileBuilderWindow(h.Model);
        WindowCapture.ShowOffScreen(window);
        try
        {
            Capture(window, theme, "builder-stacks");

            // The panel cards: the stacks as groups of their tools (feature #18, WU3), never by name.
            var stacks = WindowCapture.Descendants<Grid>(window.PanelCards).Where(g => g.Name == "StackGroup").Select(g => ((StackGroupViewModel)g.DataContext).Rows.Count);
            Assert.Equal(new[] { 3, 2, 2 }, stacks);

            // The preview: 2 panels; 3 stacks drawn as rows with 16 px icons (7 tools).
            var preview = window.Preview;
            Assert.Equal(2, preview.DrawnPanels.Count);
            Assert.Equal(7, StackFixtures.StackRowIcons(preview).Count);
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
            Assert.Equal(7, StackFixtures.StackRowIcons(preview).Count);
            // The two large tools draw their 32 px icons at 20 px.
            Assert.Equal(2, WindowCapture.Descendants<Image>(preview).Count(i => i.Source is not null && i.Width == 20));

            // Window has no pack icon: its row draws the ribbon's default_16.png. The Model panel, 3x.
            Assert.Single(StackFixtures.StackRowIcons(preview), i => ReferenceEquals(i.Source, PackIcons.Default16));
            var modelPanel = (FrameworkElement)preview.DrawnPanels[0];
            var zoom = Screenshots.Zoom(WindowCapture.Render(modelPanel, modelPanel.ActualWidth, modelPanel.ActualHeight), 3);
            Screenshots.Save(zoom, Name("preview-default16", theme));
        }
        finally
        {
            window.Close();
        }
    });
}
