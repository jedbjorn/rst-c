// Adapted from splash-it src/SplashIt.Revit/Theming/RevitThemeDetector.cs (splash-it commit ba86a71;
// originally gantt-it src/GanttIt.Revit/UI/Theming/GanttItTheme.cs, gantt-it commit a96d0a1), MIT, same owner.
// Changes: namespaces (SplashIt.* to RST.*); the debug override reads RST_THEME; a failed read is
// logged through Serilog; ApplyToWindows (detect, then RstTheme.Apply) is the one call the window
// launchers make before a window opens.

using System.Windows;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using RST.Core.Theming;
using RST.UI.Theming;
using Serilog;

namespace RST.Engine.Theming;

/// <summary>
/// Revit's effective UI theme as plain light or dark (spec #16, Theme following). RST's WPF
/// windows take it through <see cref="RstTheme.Apply"/> when they open; there is no live
/// re-theme while a window is open.
/// </summary>
public static class RevitThemeDetector
{
    /// <summary>
    /// Revit's effective UI theme. With Revit's "Use system setting" option on
    /// (<see cref="UIThemeManager.FollowSystemColorTheme"/>) the Windows app theme is read
    /// directly, so the result is right whether or not <see cref="UIThemeManager.CurrentTheme"/>
    /// resolves the system setting itself. Anything unreadable falls back to light, and so
    /// does Windows high contrast. Never throws.
    /// </summary>
    public static UiTheme Detect()
    {
#if DEBUG
        // Debug builds only: RST_THEME=dark|light forces a theme for quick captures.
        switch (Environment.GetEnvironmentVariable("RST_THEME")?.Trim().ToLowerInvariant())
        {
            case "dark": return UiTheme.Dark;
            case "light": return UiTheme.Light;
        }
#endif
        try
        {
            if (SystemParameters.HighContrast) return UiTheme.Light;
            if (UIThemeManager.FollowSystemColorTheme && WindowsAppTheme() is { } system) return system;
            return UIThemeManager.CurrentTheme == UITheme.Dark ? UiTheme.Dark : UiTheme.Light;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "RevitThemeDetector: theme read failed, using light");
            return UiTheme.Light;
        }
    }

    /// <summary>
    /// Detects Revit's theme and makes it the theme of the RST windows opened from now on.
    /// Call on Revit's UI thread right before a window opens; returns the theme applied.
    /// </summary>
    public static UiTheme ApplyToWindows()
    {
        var theme = Detect();
        RstTheme.Apply(theme);
        Log.Debug("RevitThemeDetector: windows open in the {Theme} theme", theme);
        return theme;
    }

    /// <summary>The Windows "app mode" (Settings › Personalization › Colors), or null when it can't be read.</summary>
    private static UiTheme? WindowsAppTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light ? (light == 0 ? UiTheme.Dark : UiTheme.Light) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
