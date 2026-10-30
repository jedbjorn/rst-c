// Adapted from splash-it tests/SplashIt.RuntimeTests/ScreenshotPipelineTests.cs (splash-it commit ba86a71), MIT, same owner.
// Changes: namespaces (SplashIt.* to RST.*); the smoke render draws the light and dark palettes' swatches (rst-c has no SplashItDefaults colour lists).

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RST.Core.Theming;
using RST.RuntimeTests.Support;
using Xunit;

namespace RST.RuntimeTests;

/// <summary>
/// Smoke test for the screenshot pipeline the window lanes use: build a small WPF tree
/// off-screen on an STA thread, render it to PNG, and check the image is not blank. It draws
/// every opaque palette key of the light and dark palettes as a labelled swatch on the theme's
/// window color.
/// </summary>
public class ScreenshotPipelineTests
{
    private static SolidColorBrush Brush(UiColor c)
    {
        var brush = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }

    private static FrameworkElement ThemePanel(UiTheme theme)
    {
        var palette = UiPalette.For(theme);
        var text = Brush(palette[UiPalette.Text]);
        var stack = new StackPanel { Margin = new Thickness(12) };
        stack.Children.Add(new TextBlock
        {
            Text = $"RST palette — {theme.ToString().ToLowerInvariant()} (window {palette[UiPalette.Window]})",
            Foreground = text,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 8),
        });

        var swatches = new WrapPanel { Width = 6 * 112 };
        foreach (var (key, color) in palette.Where(p => p.Value.IsOpaque).OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var cell = new StackPanel { Width = 108, Margin = new Thickness(0, 0, 4, 6) };
            cell.Children.Add(new Border
            {
                Height = 22,
                Background = Brush(color),
                BorderBrush = Brush(palette[UiPalette.Border]),
                BorderThickness = new Thickness(1),
            });
            cell.Children.Add(new TextBlock
            {
                Text = key.Replace("Brush", "", StringComparison.Ordinal),
                Foreground = text,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            swatches.Children.Add(cell);
        }
        stack.Children.Add(swatches);

        return new Border { Background = Brush(palette[UiPalette.Window]), Child = stack };
    }

    [Fact]
    public void RendersAWpfTree_OffScreen_ToANonBlankPng()
    {
        Screenshots.RunSta(() =>
        {
            var root = new StackPanel();
            root.Children.Add(ThemePanel(UiTheme.Light));
            root.Children.Add(ThemePanel(UiTheme.Dark));

            var bitmap = Screenshots.Render(root);
            var path = Screenshots.Save(bitmap, "smoke-palette");

            Assert.True(File.Exists(path), path);
            Assert.True(new FileInfo(path).Length > 1000, "the PNG is suspiciously small");
            Assert.True(bitmap.PixelWidth >= 400 && bitmap.PixelHeight >= 150, $"{bitmap.PixelWidth}x{bitmap.PixelHeight}");
            // Two windows, ~40 swatches each and anti-aliased text: far more than a blank image's one value.
            Assert.True(Screenshots.DistinctPixels(bitmap) > 50, "the render looks blank");

            // The top-left corner is the light window color; the bottom-left the dark one.
            Assert.Equal(Color.FromRgb(0xF4, 0xF5, 0xF7), Screenshots.PixelAt(bitmap, 1, 1));
            Assert.Equal(Color.FromRgb(0x1F, 0x23, 0x29), Screenshots.PixelAt(bitmap, 1, bitmap.PixelHeight - 2));
        });
    }
}
