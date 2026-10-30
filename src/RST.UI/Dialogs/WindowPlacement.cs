// Adapted from splash-it src/SplashIt.UI/Settings/SettingsLauncher.cs (the owner) and
// src/SplashIt.UI/Settings/SettingsWindow.xaml.cs (OnSourceInitialized, the work-area cap)
// (splash-it commit ba86a71), MIT, same owner.
// Changes: one shared helper for rst-c's three windows instead of per-window code; the width is
// capped as well as the height; no owner falls back to CenterScreen (spec #16, Beyond Intention); the window is moved
// back inside the work area whether it was centred on its owner or on the screen, reading its bounds from Win32 (WU3).

using System.Runtime.InteropServices;
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
        // A window placed by hand (Manual: the screenshot tests' off-screen windows) stays where it was put.
        if (window.WindowStartupLocation == WindowStartupLocation.Manual) return;
        // Centred on a tall or wide owner, or (with no owner) centred on the screen at its full size
        // before the cap applied, the window could start outside the work area, with its title bar
        // off screen (WU1 review N3): both modes are moved back inside. WPF may still re-centre the
        // window after Loaded, once its size is known, so the clamp runs again after the first render.
        window.Loaded += (_, _) => KeepInside(window, area);
        window.ContentRendered += (_, _) => KeepInside(window, area);
    }

    private static void KeepInside(Window window, Rect area)
    {
        // A centred window's Left/Top can still be NaN here, so the bounds come from Win32.
        if (Bounds(window) is not { } b) return;
        var top = b.Top;
        var left = b.Left;
        if (top + b.Height > area.Bottom) top = area.Bottom - b.Height;
        if (top < area.Top) top = area.Top;
        if (left + b.Width > area.Right) left = area.Right - b.Width;
        if (left < area.Left) left = area.Left;
        if (Math.Abs(top - b.Top) > 0.5) window.Top = top;
        if (Math.Abs(left - b.Left) > 0.5) window.Left = left;
    }

    /// <summary>The window's on-screen bounds in DIPs (from Win32), or null before it has a handle.</summary>
    private static Rect? Bounds(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return null;
        if (PresentationSource.FromVisual(window) is not { CompositionTarget: { } target }) return null;
        var fromDevice = target.TransformFromDevice;
        return new Rect(fromDevice.Transform(new Point(r.Left, r.Top)), fromDevice.Transform(new Point(r.Right, r.Bottom)));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
}
