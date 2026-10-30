// Adapted from splash-it src/SplashIt.UI/Settings/SettingsLauncher.cs (the owner) and
// src/SplashIt.UI/Settings/SettingsWindow.xaml.cs (OnSourceInitialized, the work-area cap)
// (splash-it commit ba86a71), MIT, same owner.
// Changes: one shared helper for rst-c's three windows instead of per-window code; the width is
// capped as well as the height; no owner falls back to CenterScreen (spec #16, Beyond Intention).

using System.Windows;
using System.Windows.Interop;

namespace RST.UI.Dialogs;

/// <summary>
/// Owner and placement for rst-c's windows (spec #16, Planner default 2): owned by Revit's main
/// window and centred on it, never larger than the monitor's work area. Launchers call
/// <see cref="Own"/> before <c>ShowDialog</c>; each window calls <see cref="CapToWorkArea"/>
/// from its <c>OnSourceInitialized</c>.
/// </summary>
public static class WindowPlacement
{
    /// <summary>
    /// Makes <paramref name="window"/> owned by the Win32 window <paramref name="owner"/>
    /// (<c>UIApplication.MainWindowHandle</c>) and centred on it. A zero handle (tests, an odd
    /// host) leaves it unowned and centred on the screen.
    /// </summary>
    public static void Own(Window window, IntPtr owner)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (owner == IntPtr.Zero)
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }
        _ = new WindowInteropHelper(window) { Owner = owner };
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
    }

    /// <summary>
    /// Caps the window at <paramref name="workArea"/> (null: the work area of the monitor it opens
    /// on), so on a short screen the content scrolls and the title bar and buttons stay visible.
    /// Call from <c>OnSourceInitialized</c>.
    /// </summary>
    public static void CapToWorkArea(Window window, Rect? workArea = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        var area = workArea ?? MonitorWorkArea.Of(window);
        if (area.Height > 0) window.MaxHeight = area.Height;
        if (area.Width > 0) window.MaxWidth = area.Width;
        if (window.WindowStartupLocation != WindowStartupLocation.CenterOwner) return;
        window.Loaded += (_, _) =>
        {
            // Centred on a tall or wide owner, the window could start outside the work area.
            if (window.Top < area.Top) window.Top = area.Top;
            if (window.Top + window.ActualHeight > area.Bottom) window.Top = Math.Max(area.Top, area.Bottom - window.ActualHeight);
            if (window.Left < area.Left) window.Left = area.Left;
            if (window.Left + window.ActualWidth > area.Right) window.Left = Math.Max(area.Left, area.Right - window.ActualWidth);
        };
    }
}
