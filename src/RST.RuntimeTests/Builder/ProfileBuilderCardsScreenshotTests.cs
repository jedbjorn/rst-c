// rst-c's own (not vendored); the capture helpers follow ProfileBuilderShellScreenshotTests.
// Feature #18 (spec #19), WU3, AC4: the panel cards as tiles, rendered off-screen to PNG in light
// and dark for the review against the approved spike (decision #25): the default view with tiles
// (Structure: tiles, a 3-tool stack, a tile without a pack icon, custom tools), a card holding a
// stack (zoomed), a full stack refusing a fourth tool, and the New Stack and Edit Stack dialogs.
// Each capture also checks what it shows (the pane header, the counts, the stack header, the order
// pills, the dialog's enabled state). And the pane header's tab name opens General on the tab field.

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
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
public class ProfileBuilderCardsScreenshotTests
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

    private static string Id(DependencyObject d) => AutomationProperties.GetAutomationId(d);

    private static Color At(BitmapSource bitmap, Visual element, Visual root, double x, double y)
    {
        var p = element.TransformToAncestor(root).Transform(new Point(x, y));
        return Screenshots.PixelAt(bitmap, (int)Math.Floor(p.X), (int)Math.Floor(p.Y));
    }

    private static void AssertColor(Color expected, Color actual, string what) =>
        Assert.True(Math.Max(Math.Abs(expected.R - actual.R), Math.Max(Math.Abs(expected.G - actual.G), Math.Abs(expected.B - actual.B))) <= 2,
            $"{what}: expected {expected}, rendered {actual}");

    /// <summary>The Builder on Structure, arranged, shown off-screen.</summary>
    private static (ProfileBuilderWindow Window, BuilderHarness Harness) Open(Action<BuilderHarness>? arrange = null)
    {
        var h = BuilderHarness.Create();
        h.Model.SelectedProfileOption = h.Option("Structure");
        arrange?.Invoke(h);
        var window = new ProfileBuilderWindow(h.Model);
        WindowCapture.ShowOffScreen(window);
        return (window, h);
    }

    private static void Close(ProfileBuilderWindow window, BuilderHarness h)
    {
        h.Model.EndDrag();
        h.Dialogs.ChooseAnswers.Enqueue(DialogChoice.Secondary);
        window.Close();
    }

    private static BitmapSource Capture(ProfileBuilderWindow window, UiTheme theme, string what)
    {
        WindowCapture.DoEvents();
        var bitmap = WindowCapture.Content(window, BuilderSize);
        Screenshots.Save(bitmap, Name(what, theme));
        Assert.Equal(1450, bitmap.PixelWidth);
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
            Assert.False(dialog.ShowInTaskbar);
            return bitmap;
        }
        finally
        {
            dialog.Close();
        }
    }

    private static FrameworkElement Card(ProfileBuilderWindow w, int panel) =>
        WindowCapture.Descendants<Grid>(w.PanelCards).Single(g => g.Name == "CardRoot" && g.DataContext is PanelCardViewModel c && c.Index == panel);

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_default_view_shows_the_cards_as_tiles(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var backend = BuilderFixtures.Backend();
        var structure = backend.Library.Single(e => e.Profile.ProfileName == "Structure").Profile;
        structure.Panels[2].Slots[0].CommandId = "URL:https://wiki.studio.example";
        structure.Panels[2].Slots[1].CommandId = "URL:mailto:bim@studio.example";
        var h = BuilderHarness.Create(backend);
        h.Model.SelectedProfileOption = h.Option("Structure");
        var window = new ProfileBuilderWindow(h.Model);
        WindowCapture.ShowOffScreen(window);
        try
        {
            Capture(window, theme, "f18-builder-tiles");
            Assert.Equal("Studio Struct", h.Model.PanelsTabName);
            var counts = WindowCapture.Descendants<TextBlock>(window.PanelCards).Where(t => Id(t) == "Builder.ToolCount").Select(t => t.Text);
            Assert.Equal(["3 tools", "5 tools", "2 tools"], counts);
            var sources = WindowCapture.Descendants<TextBlock>(window.PanelCards).Where(t => Id(t) == "Builder.ToolSource").Select(t => t.Text);
            Assert.Equal(["Structure", "Structure", "Structure", "Kinship Tools", "Custom", "wiki.studio.example", "bim@studio.example"], sources);
            Assert.Equal("STACK · 3/3",
                WindowCapture.Descendants<TextBlock>(window.PanelCards).Single(t => Id(t) == "Builder.StackHeaderText").Text);
            // Tiles wrap in ribbon order: Frame's three tiles sit on one row, left to right.
            var frameTiles = WindowCapture.Descendants<Grid>(Card(window, 0)).Where(g => g.Name == "Tile").ToList();
            var xs = frameTiles.Select(t => t.TranslatePoint(new Point(), window.Root).X).ToList();
            Assert.Equal(xs.OrderBy(x => x), xs);
            Assert.Single(frameTiles.Select(t => Math.Round(t.TranslatePoint(new Point(), window.Root).Y)).Distinct());
            // No hover: the tiles' × and the cards' ▲ ▼ × are hidden, except on the selected card.
            Assert.All(WindowCapture.Descendants<StackPanel>(window.PanelCards).Where(s => s.Name == "Corner"), c => Assert.Equal(0, c.Opacity));
            var acts = WindowCapture.Descendants<StackPanel>(window.PanelCards).Where(s => s.Name == "HoverActs").Select(s => s.Opacity);
            Assert.Equal([1.0, 0.0, 0.0], acts);
            // Every tile's name and source fit (no clipped text at the tile's width).
            foreach (var box in WindowCapture.Descendants<TextBox>(window.PanelCards).Where(t => Id(t) == "Builder.ToolName"))
                Assert.True(box.ExtentWidth <= box.ViewportWidth + 0.5, $"\"{box.Text}\" is clipped");
        }
        finally
        {
            Close(window, h);
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void A_card_holding_a_stack(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var (window, h) = Open(x => x.Model.Panels[1].SelectCommand.Execute(null));
        try
        {
            WindowCapture.DoEvents();
            var card = Card(window, 1);
            var bitmap = Screenshots.Zoom(WindowCapture.Render(card, card.ActualWidth, card.ActualHeight), 2);
            Screenshots.Save(bitmap, Name("f18-builder-stack-card", theme));
            Assert.True(Screenshots.DistinctPixels(bitmap) > 100, "the card looks blank");
            var group = WindowCapture.Descendants<Grid>(card).Single(g => g.Name == "StackGroup");
            Assert.Equal(200, group.ActualWidth);
            var rows = WindowCapture.Descendants<Grid>(group).Where(g => g.Name == "StackRow").ToList();
            Assert.Equal(3, rows.Count);
            Assert.All(rows, r => Assert.Equal(16, WindowCapture.Descendants<Image>(r).Single().Width));
            Assert.Equal(2, WindowCapture.Descendants<Button>(group).Count(b => Id(b) is "Builder.EditStack" or "Builder.Unstack"));
        }
        finally
        {
            Close(window, h);
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void A_full_stack_refuses_a_fourth_tool(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var (window, h) = Open(x =>
        {
            x.Model.BeginDrag(BuilderDrag.FromSlot(0, 0));
            x.Model.DragOverTile(TileHit.On(1, 1, 100, 200));
        });
        try
        {
            var bitmap = Capture(window, theme, "f18-builder-stack-full");
            Assert.Equal(DropMarker.Refused, h.Model.Panels[1].StackAt(1).DropMarker);
            var group = WindowCapture.Descendants<Grid>(window.PanelCards).Single(g => g.Name == "StackGroup");
            var marker = WindowCapture.Descendants<Border>(group).Single(b => Id(b) == "Builder.StackDropMarker");
            Assert.Equal(Visibility.Visible, marker.Visibility);
            AssertColor(Palette(theme, UiPalette.Error), At(bitmap, marker, window.Root, marker.ActualWidth / 2, 0.5), "refusal ring");
            var note = WindowCapture.Descendants<Border>(group).Single(b => Id(b) == "Builder.StackFull");
            Assert.Equal(Visibility.Visible, note.Visibility);
            var text = (TextBlock)note.Child;
            Assert.Equal("A stack holds 2–3 tools. This one is full.", text.Text);
        }
        finally
        {
            Close(window, h);
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_new_stack_dialog_renders(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var h = BuilderHarness.Create();
        h.Model.SelectedProfileOption = h.Option("Structure");
        h.Model.Panels[0].NewStackCommand.Execute(null); // no script: cancelled, the model is kept
        var model = h.Modals.StackDialogs.Single().Model;
        FakeBuilderDialogs.Tick(model, "Column", "Beam");
        var dialog = new StackDialog(model);
        CaptureDialog(dialog, theme, "f18-builder-new-stack-dialog");
        Assert.Equal("New stack on “Frame”", model.Title);
        Assert.Equal(["#2", "#1", ""], model.Items.Select(i => i.OrderText));
        Assert.True(model.CanConfirm);
        Assert.Equal("2 selected · a stack holds 2–3 tools", model.Status);
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_edit_stack_dialog_renders(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var h = BuilderHarness.Create();
        h.Model.SelectedProfileOption = h.Option("Structure");
        h.Model.Panels[1].StackAt(1).EditCommand.Execute(null); // cancelled
        var model = h.Modals.StackDialogs.Single().Model;
        FakeBuilderDialogs.Untick(model, "Purge");
        FakeBuilderDialogs.Tick(model, "Kinship");
        var dialog = new StackDialog(model);
        CaptureDialog(dialog, theme, "f18-builder-edit-stack-dialog");
        Assert.Equal("Edit stack on “Review”", model.Title);
        Assert.Equal(["Warnings", "Audit", "Purge", "Kinship", "Notes"], model.Items.Select(i => i.Name));
        Assert.Equal(["#1", "#2", "", "#3", ""], model.Items.Select(i => i.OrderText));
        Assert.False(model.Items.Single(i => i.Name == "Notes").CanCheck, "a fourth tick is refused");
    });

    [Fact]
    public void Clicking_the_tab_name_opens_general_on_the_tab_field() => Sta(() =>
    {
        RstTheme.Apply(UiTheme.Light);
        var (window, h) = Open();
        try
        {
            window.ProfileTabs.SelectedItem = window.JunkCleanupTab;
            WindowCapture.DoEvents();
            window.TabNameButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            WindowCapture.DoEvents();
            Assert.Same(window.GeneralTab, window.ProfileTabs.SelectedItem);
            Assert.Same(window.TabBox, FocusManager.GetFocusedElement(window));
            Assert.Equal("Studio Struct", ((TextBlock)window.TabNameButton.Content).Text);

            h.Model.ProfileTab = "";
            Assert.Equal("(no tab name)", h.Model.PanelsTabName);
        }
        finally
        {
            Close(window, h);
        }
    });
}
