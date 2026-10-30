// rst-c's own (not vendored); the capture helpers follow splash-it
// tests/SplashIt.RuntimeTests/Settings/SettingsWindowScreenshotTests.cs (splash-it commit ba86a71).

using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RST.Core.Colors;
using RST.Core.Theming;
using RST.RuntimeTests.Support;
using RST.UI.Dialogs;
using RST.UI.Swatches;
using RST.UI.Theming;
using Xunit;

namespace RST.RuntimeTests.Gallery;

/// <summary>
/// AC3 (spec #16, WU1): the style gallery, the MessageDialog (confirm and message) and the
/// ColorPickerDialog, rendered off-screen to PNG in light and dark. The gallery capture has the
/// real confirm dialog's capture drawn into its MessageDialog slot. Each test also samples the
/// pixels that prove the new styles' states (switch on/off, selected/active cards, selected tab
/// and tree row, success toast, busy scrim), so a broken template fails here as well as in
/// review. The PNGs land in TestResults/screenshots; CI uploads them as RST-screenshots.
/// </summary>
[Collection(WpfThemeCollection.Name)]
public class StyleGalleryScreenshotTests
{
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

    private static Color At(BitmapSource bitmap, Visual element, Visual root, double x, double y)
    {
        var p = element.TransformToAncestor(root).Transform(new Point(x, y));
        return Screenshots.PixelAt(bitmap, (int)Math.Floor(p.X), (int)Math.Floor(p.Y));
    }

    private static void AssertColor(Color expected, Color actual, string what) =>
        Assert.True(Distance(expected, actual) <= 2, $"{what}: expected {expected}, rendered {actual}");

    private static T Part<T>(Control control, string name) where T : FrameworkElement =>
        (T)control.Template.FindName(name, control) ?? throw new InvalidOperationException($"{name} not in {control.Name}'s template");

    /// <summary>A window shown off-screen, its client area captured, saved, checked, then closed.</summary>
    private static BitmapSource CaptureDialog(Window dialog, UiTheme theme, string name)
    {
        WindowCapture.ShowOffScreen(dialog);
        try
        {
            var bitmap = WindowCapture.Client(dialog);
            Screenshots.Save(bitmap, Name(name, theme));
            Assert.Equal(Palette(theme, UiPalette.Window), Screenshots.PixelAt(bitmap, 1, 1));
            Assert.True(Screenshots.DistinctPixels(bitmap) > 30, $"{name} looks blank");
            return bitmap;
        }
        finally
        {
            dialog.Close();
        }
    }

    private static MessageDialog ConfirmDialog() => new(
        new MessageDialogModel("Delete Profile", "Delete \"Architecture\"?",
            "This profile is loaded. It is unloaded first, then deleted. This cannot be undone.", [],
            "Delete", HasCancel: true, Danger: true),
        "DeleteProfileDialog");

    [Theory]
    [MemberData(nameof(Themes))]
    public void MessageDialog_Renders(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var confirm = ConfirmDialog();
        // A destructive confirm defaults to Cancel (spec #16, Fidelity Rules).
        Assert.True(((Button)confirm.FindName("CancelButton")).IsDefault);
        Assert.False(((Button)confirm.FindName("ConfirmButton")).IsDefault);
        CaptureDialog(confirm, theme, "message-dialog-confirm");

        var message = new MessageDialog(
            new MessageDialogModel("Import Failed", "Import Failed", "The file is not an RST profile zip.", ["Architecture.zip: profile.json is missing"]),
            MessageDialog.MessageId);
        CaptureDialog(message, theme, "message-dialog-message");
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void ColorPickerDialog_Renders(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        PickerSession.RecentColors.Add(Rgb.Parse("#10B981"));
        PickerSession.RecentColors.Add(Rgb.Parse("#F59E0B"));
        var dialog = new ColorPickerDialog(Rgb.Parse("#4F8EF7"), StyleGalleryWindow.SamplePalette, "Panel color", null, SystemParameters.WorkArea);
        WindowCapture.ShowOffScreen(dialog);
        var swatches = WindowCapture.Descendants<Button>(dialog)
            .Count(b => AutomationProperties.GetAutomationId(b).StartsWith("ColorPicker.Swatch", StringComparison.Ordinal));
        Assert.Equal(12, swatches);
        dialog.Close();

        CaptureDialog(new ColorPickerDialog(Rgb.Parse("#4F8EF7"), StyleGalleryWindow.SamplePalette, "Panel color", null, SystemParameters.WorkArea),
            theme, "color-picker-dialog");
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void StyleGallery_RendersEveryStyle(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var confirm = ConfirmDialog();
        WindowCapture.ShowOffScreen(confirm);
        var dialog = WindowCapture.Client(confirm);
        confirm.Close();

        var window = new StyleGalleryWindow();
        window.DialogSlot.Width = dialog.PixelWidth;
        window.DialogSlot.Height = dialog.PixelHeight;
        WindowCapture.ShowOffScreen(window);
        try
        {
            var bitmap = WindowCapture.Content(window);
            var root = window.Root;
            WriteEnvironment(window, bitmap);

            Assert.True(bitmap.PixelWidth >= 1200 && bitmap.PixelHeight >= 700, $"{bitmap.PixelWidth}x{bitmap.PixelHeight}");
            Assert.True(Screenshots.DistinctPixels(bitmap) > 500, "the gallery looks blank");
            AssertColor(Palette(theme, UiPalette.Window), Screenshots.PixelAt(bitmap, 2, 2), "window corner");
            AssertColor(Palette(theme, UiPalette.Window), Screenshots.PixelAt(bitmap, bitmap.PixelWidth - 3, bitmap.PixelHeight - 3), "window corner");

            // ToggleSwitch: on is the check box accent; off is an outlined surface track.
            var on = Part<Border>(window.SwitchOn, "Track");
            AssertColor(Palette(theme, UiPalette.CheckFill), At(bitmap, on, root, 5, on.ActualHeight / 2), "switch on track");
            var off = Part<Border>(window.SwitchOff, "Track");
            AssertColor(Palette(theme, UiPalette.Surface), At(bitmap, off, root, 22, off.ActualHeight / 2), "switch off track");
            AssertColor(Palette(theme, UiPalette.StrongBorder), At(bitmap, off, root, 15, 0.5), "switch off outline");

            // Cards: the 1 px border; the selected 2 px accent ring; the active 2 px success ring.
            AssertColor(Palette(theme, UiPalette.Border), At(bitmap, window.CardNormal, root, 0.5, window.CardNormal.ActualHeight / 2), "normal card border");
            AssertColor(Palette(theme, UiPalette.AccentForeground), At(bitmap, window.CardSelected, root, 1, window.CardSelected.ActualHeight / 2), "selected card ring");
            AssertColor(Palette(theme, UiPalette.Success), At(bitmap, window.CardActive, root, 1, window.CardActive.ActualHeight / 2), "active card ring");
            AssertColor(Palette(theme, UiPalette.Surface), At(bitmap, window.CardActive, root, 6, 4), "active card fill");

            // The selected tab's underline and the selected tree row.
            var underline = Part<Border>(window.TabSelected, "Underline");
            AssertColor(Palette(theme, UiPalette.AccentForeground), At(bitmap, underline, root, underline.ActualWidth / 2, 1), "selected tab underline");
            var row = Part<Border>(window.TreeSelected, "Bd");
            AssertColor(Palette(theme, UiPalette.AccentSoft), At(bitmap, row, root, row.ActualWidth - 4, row.ActualHeight / 2), "selected tree row");

            // A success toast, and the busy scrim over the panel's surface.
            var toast = (Border)VisualTreeHelper.GetChild(window.ToastSuccess, 0);
            AssertColor(Palette(theme, UiPalette.SuccessSoft), At(bitmap, toast, root, toast.ActualWidth - 6, toast.ActualHeight / 2), "success toast");
            var palette = UiPalette.For(theme);
            var scrimOverSurface = ToColor(palette[UiPalette.Scrim].Over(palette[UiPalette.Surface]));
            AssertColor(scrimOverSurface, At(bitmap, window.Overlay, root, window.Overlay.ActualWidth / 2, 4), "busy scrim");

            var slot = window.DialogSlot.TransformToAncestor(root).Transform(new Point(0, 0));
            var composed = WindowCapture.Compose(bitmap, dialog, new Point(Math.Round(slot.X), Math.Round(slot.Y)), Palette(theme, UiPalette.StrongBorder));
            Screenshots.Save(composed, Name("style-gallery", theme));
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>
    /// Records the CI desktop next to the screenshots: Windows caps a window at roughly the
    /// desktop size, which is why <see cref="WindowCapture.Content"/> renders unclipped.
    /// </summary>
    private static void WriteEnvironment(Window window, BitmapSource content)
    {
        Directory.CreateDirectory(Screenshots.Folder);
        var lines = new[]
        {
            FormattableString.Invariant($"primary screen (DIPs): {SystemParameters.PrimaryScreenWidth} x {SystemParameters.PrimaryScreenHeight}"),
            FormattableString.Invariant($"work area (DIPs): {SystemParameters.WorkArea.Width} x {SystemParameters.WorkArea.Height}"),
            FormattableString.Invariant($"max window track size (DIPs): {SystemParameters.MaximumWindowTrackWidth} x {SystemParameters.MaximumWindowTrackHeight}"),
            FormattableString.Invariant($"gallery window as shown: {window.ActualWidth} x {window.ActualHeight}"),
            FormattableString.Invariant($"gallery content rendered: {content.PixelWidth} x {content.PixelHeight}"),
            $"runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}",
        };
        File.WriteAllLines(Path.Combine(Screenshots.Folder, "capture-environment.txt"), lines);
    }
}
