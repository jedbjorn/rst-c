// Adapted from splash-it src/SplashIt.UI/Theming/SplashItTheme.cs (splash-it commit ba86a71; originally gantt-it src/GanttIt.Revit/UI/Theming/GanttItTheme.cs, gantt-it commit a96d0a1), MIT, same owner.
// Changes: namespaces (SplashIt.* to RST.*); named RstTheme; RST.Engine's RevitThemeDetector passes the theme in, as splash-it's Revit side does.

using RST.Core.Theming;

namespace RST.UI.Theming;

/// <summary>
/// Which palette RST's windows use: Revit's UI theme, as detected by RST.Engine's
/// RevitThemeDetector (spec #16, Theme following) and passed in through <see cref="Apply"/>. It is
/// read when a window loads Theme.xaml (each load builds a new <see cref="ThemePalette"/>),
/// so a window keeps the theme it opened with, as in gantt-it.
/// </summary>
public static class RstTheme
{
    /// <summary>The theme of the next window to open (and its dialogs). Light until the first <see cref="Apply"/>.</summary>
    public static UiTheme Current { get; private set; } = UiTheme.Light;

    public static bool IsDark => Current == UiTheme.Dark;

    /// <summary>
    /// Sets the theme for the windows opened from now on. The Revit side calls it with its
    /// detected theme before opening each window (gantt-it's rules: light for high
    /// contrast or anything unreadable). UI thread only.
    /// </summary>
    public static void Apply(UiTheme theme) => Current = theme;
}
