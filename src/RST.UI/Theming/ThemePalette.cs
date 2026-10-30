// Copied from splash-it src/SplashIt.UI/Theming/ThemePalette.cs (splash-it commit ba86a71; originally gantt-it src/GanttIt.Revit/UI/Theming/ThemePalette.cs, gantt-it commit a96d0a1), MIT, same owner.
// Changes: namespaces (SplashIt.* to RST.*); reads RstTheme where splash-it reads SplashItTheme.

using System.Windows;
using System.Windows.Media;
using RST.Core.Theming;

namespace RST.UI.Theming;

/// <summary>
/// The palette brushes for <see cref="RstTheme.Current"/>, merged first into
/// Theme.xaml so its styles and every view's <c>{StaticResource …Brush}</c> resolve
/// to the light or dark color. A new instance is built each time a RST window
/// (or dialog) loads Theme.xaml, which is how the theme is applied per window open.
/// </summary>
public sealed class ThemePalette : ResourceDictionary
{
    public ThemePalette()
    {
        foreach (var (key, color) in UiPalette.For(RstTheme.Current))
            Add(key, Brush(color));

        if (!RstTheme.IsDark) return;

        // Dark only: the built-in WPF templates that draw with system colors (the
        // default focus rectangle, scroll viewer corners) pick these up instead of
        // the light Windows colors. Menu colors are not overridden: the Windows menu
        // keeps a fixed light background, so Theme.xaml replaces the menus instead.
        var palette = UiPalette.For(UiTheme.Dark);
        Add(SystemColors.WindowBrushKey, Brush(palette[UiPalette.Surface]));
        Add(SystemColors.WindowTextBrushKey, Brush(palette[UiPalette.Text]));
        Add(SystemColors.ControlBrushKey, Brush(palette[UiPalette.ScrollTrack]));
        Add(SystemColors.ControlTextBrushKey, Brush(palette[UiPalette.Text]));
        Add(SystemColors.GrayTextBrushKey, Brush(palette[UiPalette.MutedText]));
        Add(SystemColors.InfoBrushKey, Brush(palette[UiPalette.ToolTip]));
        Add(SystemColors.InfoTextBrushKey, Brush(palette[UiPalette.ToolTipText]));
    }

    private static SolidColorBrush Brush(UiColor c)
    {
        var brush = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }
}
