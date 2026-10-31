// rst-c's own (not vendored); the capture helpers follow ProfileBuilderCardsScreenshotTests.
// Feature #18 (spec #19), WU4, AC4: the Commands pane's Custom Tools and their dialogs, rendered
// off-screen to PNG in light and dark for the review against the approved spike (decision #25): the
// default view with the Custom Tools group (one row hovered, as the spike), the Commands pane
// zoomed, the Create new tool dialog, the Edit dialog with its warning (and with refusals), the
// Remove confirmation, and the icon picker window. Each capture also checks what it shows.

using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RST.Core.Builder;
using RST.Core.Profiles;
using RST.Core.Theming;
using RST.RuntimeTests.Loader;
using RST.RuntimeTests.Support;
using RST.UI.Builder;
using RST.UI.Dialogs;
using RST.UI.Theming;
using Xunit;

namespace RST.RuntimeTests.Builder;

[Collection(WpfThemeCollection.Name)]
public sealed class ProfileBuilderCustomToolsScreenshotTests : IDisposable
{
    private static readonly Size BuilderSize = new(1450, 900);

    private readonly TempFolder _dir = new();

    public void Dispose() => _dir.Dispose();

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

    private static void AssertColor(Color expected, Color actual, string what) =>
        Assert.True(Math.Max(Math.Abs(expected.R - actual.R), Math.Max(Math.Abs(expected.G - actual.G), Math.Abs(expected.B - actual.B))) <= 2,
            $"{what}: expected {expected}, rendered {actual}");

    private (ProfileBuilderWindow Window, BuilderHarness Harness) Open(Action<FakeBuilderBackend>? arrange = null)
    {
        var backend = CustomToolFixtures.Backend(_dir.Path);
        arrange?.Invoke(backend);
        var h = BuilderHarness.Create(backend);
        h.Model.SelectedProfileOption = h.Option("Structure");
        var window = new ProfileBuilderWindow(h.Model);
        WindowCapture.ShowOffScreen(window);
        return (window, h);
    }

    private static void Close(ProfileBuilderWindow window, BuilderHarness h)
    {
        h.Dialogs.ChooseAnswers.Enqueue(DialogChoice.Secondary);
        window.Close();
    }

    private static BitmapSource CaptureDialog(Window dialog, UiTheme theme, string what)
    {
        WindowCapture.ShowOffScreen(dialog);
        try
        {
            WindowCapture.DoEvents();
            var bitmap = WindowCapture.Client(dialog);
            Screenshots.Save(bitmap, Name(what, theme));
            AssertColor(Palette(theme, UiPalette.Window), Screenshots.PixelAt(bitmap, 1, 1), $"{what} corner");
            Assert.True(Screenshots.DistinctPixels(bitmap) > 30, $"{what} looks blank");
            Assert.False(dialog.ShowInTaskbar);
            AssertNothingClipped(dialog, what);
            return bitmap;
        }
        finally
        {
            dialog.Close();
        }
    }

    /// <summary>No visible text is cut: each TextBlock that does not wrap gets the width it asks for.</summary>
    private static void AssertNothingClipped(DependencyObject root, string what)
    {
        foreach (var t in WindowCapture.Descendants<TextBlock>(root).Where(t => t.IsVisible && t.TextWrapping == TextWrapping.NoWrap && t.Text.Length > 0))
        {
            var probe = new TextBlock { Text = t.Text, FontFamily = t.FontFamily, FontSize = t.FontSize, FontWeight = t.FontWeight, FontStyle = t.FontStyle };
            TextOptions.SetTextFormattingMode(probe, TextOptions.GetTextFormattingMode(t));
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert.True(t.ActualWidth + 0.5 >= probe.DesiredSize.Width, $"{what}: \"{t.Text}\" is clipped ({t.ActualWidth:0.0} < {probe.DesiredSize.Width:0.0})");
        }
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_default_view_shows_the_Custom_Tools_group(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var (window, h) = Open();
        try
        {
            // The spike's hover on the Help row: its ✎ × and the hover fill (forced; a capture has no pointer).
            var help = WindowCapture.Descendants<Border>(window.CatalogTreeView)
                .Single(b => Id(b) == "Builder.CustomTool" && ((CustomToolItemViewModel)b.DataContext).Label == "Help");
            WindowCapture.Descendants<StackPanel>(help).Single(sp => sp.Name == "Acts").Visibility = Visibility.Visible;
            help.Background = (Brush)window.FindResource("HoverBrush");
            // And the Review stack's Issues row (a custom tool in a stack, review N3) with its ✎ × shown.
            var issues = WindowCapture.Descendants<Grid>(window.PanelCards).Single(g => g.Name == "StackRow" && g.DataContext is ToolTileViewModel { Name: "Issues" });
            WindowCapture.Descendants<StackPanel>(issues).Single(sp => sp.Name == "Corner").Opacity = 1;
            Assert.Equal(Visibility.Visible, WindowCapture.Descendants<Button>(issues).Single(b => Id(b) == "Builder.EditCustomTool").Visibility);
            WindowCapture.DoEvents();

            var bitmap = WindowCapture.Content(window, BuilderSize);
            Screenshots.Save(bitmap, Name("f18-builder-custom-tools", theme));
            Assert.True(Screenshots.DistinctPixels(bitmap) > 200);

            var group = WindowCapture.Descendants<Border>(window.CatalogTreeView).Single(b => Id(b) == "Builder.CustomToolsGroup");
            var p = group.TranslatePoint(new Point(6, group.ActualHeight - 3), window.Root);
            AssertColor(Palette(theme, UiPalette.InfoSoft), Screenshots.PixelAt(bitmap, (int)p.X, (int)p.Y), "the group is set apart (InfoSoft)");
            Assert.Equal(["BIM Standards", "Help", "Issues", "Support", "Wiki"],
                WindowCapture.Descendants<TextBlock>(window.CatalogTreeView).Where(t => Id(t) == "Builder.CustomToolLabel").Select(t => t.Text));
            Assert.Equal(["URL", "EMAIL", "URL", "EMAIL", "URL"],
                WindowCapture.Descendants<TextBlock>(window.CatalogTreeView).Where(t => Id(t) == "Builder.CustomToolKind").Select(t => t.Text));
            Assert.InRange(window.CatalogTreeView.TranslatePoint(new Point(window.CatalogTreeView.ActualWidth, 0), window.Root).X, 318, 320); // the 320 px pane, less its divider
            AssertNothingClipped(window.CatalogTreeView, "the Commands pane");

            // The header's left end and the Commands pane at 2x, for reading the rows.
            var pane = new CroppedBitmap(bitmap, new Int32Rect(0, 0, 330, 560));
            Screenshots.Save(Screenshots.Zoom(pane, 2), Name("f18-builder-commands-pane", theme));
        }
        finally
        {
            Close(window, h);
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_create_dialog_renders(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var model = CustomToolDialogModel.ForCreate("Frame", [], null);
        model.Label = "Wiki";
        model.Address = "wiki.studio.example";
        Assert.Equal("Opens: https://wiki.studio.example", model.Opens);
        Assert.Equal("link external blue", model.IconName);
        var dialog = new CustomToolDialog(model);
        CaptureDialog(dialog, theme, "f18-builder-create-tool");
        Assert.Equal("Create", dialog.ConfirmButton.Content);

        // Email, as typed; and the refusal under the fields.
        var email = CustomToolDialogModel.ForCreate("Frame", [], null);
        email.IsEmail = true;
        email.Address = "bim@studio.example";
        Assert.False(email.TryConfirm());
        Assert.Equal("Opens: mailto:bim@studio.example", email.Opens);
        CaptureDialog(new CustomToolDialog(email), theme, "f18-builder-create-tool-refused");
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_edit_dialog_and_the_remove_confirmation_render(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var (window, h) = Open();
        try
        {
            // ✎ on Help: the warning names both profiles; the button says how many.
            CustomToolDialogModel? edit = null;
            h.Modals.CustomTools.Enqueue(d =>
            {
                edit = d;
                return false;
            });
            h.Model.EditCustomTool(h.Model.CustomToolsGroup.Find(CustomToolFixtures.Help)!);
            Assert.NotNull(edit);
            edit!.Label = "BIM help"; // an edit in progress: the warning names what will change
            var dialog = new CustomToolDialog(edit);
            CaptureDialog(dialog, theme, "f18-builder-edit-tool");
            Assert.Equal("Update in 2 profiles", dialog.ConfirmButton.Content);
            Assert.Contains("Architecture, Structure", dialog.WarningText.Text);

            // × on Help: the confirmation lists the profiles, Cancel is the default.
            h.Dialogs.ConfirmResult = false;
            h.Model.RemoveCustomTool(h.Model.CustomToolsGroup.Find(CustomToolFixtures.Help)!);
            var call = h.Dialogs.ConfirmCalls.Single();
            var confirm = new MessageDialog(new MessageDialogModel(call.Title, call.Title, call.Message, h.Dialogs.ConfirmItems.Single(),
                call.ConfirmText, HasCancel: true, Danger: call.Danger), call.AutomationId);
            Assert.True(((Button)confirm.FindName("CancelButton")).IsDefault);
            CaptureDialog(confirm, theme, "f18-builder-remove-tool");
        }
        finally
        {
            Close(window, h);
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_edit_dialog_lists_the_profiles_it_will_not_change(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var (window, h) = Open(b =>
        {
            var stale = CustomToolFixtures.Architecture();
            using var fs = File.Create(Path.Combine(b.ProfilesDir!, "Architecture_2024-01-01.json"));
            ProfileSerializer.Write(stale, fs);
        });
        try
        {
            CustomToolDialogModel? edit = null;
            h.Modals.CustomTools.Enqueue(d =>
            {
                edit = d;
                return false;
            });
            h.Model.EditCustomTool(h.Model.CustomToolsGroup.Find(CustomToolFixtures.Help)!);
            edit!.Label = "BIM help";
            Assert.Equal("Update in 1 profile", edit.ConfirmText);
            Assert.Equal(2, edit.Refusals.Count);
            CaptureDialog(new CustomToolDialog(edit), theme, "f18-builder-edit-tool-refused");
        }
        finally
        {
            Close(window, h);
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_icon_picker_window_renders(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var model = CustomToolDialogModel.ForCreate("Frame", IconPackSource.Read(RST.UI.Loader.LoaderAssets.IconPackDirectory), null);
        var picker = model.CreatePicker(_ => { });
        Assert.NotEmpty(picker.Rows);
        var window = new IconPickerWindow(model.CreatePicker);
        WindowCapture.ShowOffScreen(window);
        try
        {
            WindowCapture.DoEvents();
            var bitmap = WindowCapture.Client(window);
            Screenshots.Save(bitmap, Name("f18-builder-tool-icon-picker", theme));
            Assert.True(bitmap.PixelWidth >= 700, $"the picker's card fits ({bitmap.PixelWidth} px)");
            Assert.False(window.ShowInTaskbar);
        }
        finally
        {
            window.Close();
        }
    });
}
