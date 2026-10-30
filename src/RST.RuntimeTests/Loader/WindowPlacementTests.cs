// rst-c's own (not vendored): WindowPlacement's work-area clamp (WU1 review N3, fixed in WU3).

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using RST.RuntimeTests.Support;
using RST.UI.Dialogs;
using Xunit;

namespace RST.RuntimeTests.Loader;

[Collection(WpfThemeCollection.Name)]
public class WindowPlacementTests
{
    [Fact]
    public void A_centre_screen_window_larger_than_the_work_area_opens_inside_it() => Screenshots.RunSta(() =>
    {
        // No Revit handle: CenterScreen. A 1450 x 900 window on an 800 x 600 work area must not
        // start with its title bar above or left of the work area.
        var area = new Rect(0, 0, 800, 600);
        var window = new Window { Width = 1450, Height = 900, ShowActivated = false, ShowInTaskbar = false, Title = "placement", Content = new System.Windows.Controls.Border() };
        try
        {
            WindowPlacement.Own(window, IntPtr.Zero);
            Assert.Equal(WindowStartupLocation.CenterScreen, window.WindowStartupLocation);
            window.SourceInitialized += (_, _) => WindowPlacement.CapToWorkArea(window, area);
            window.Show();
            WindowCapture.DoEvents();
            WindowCapture.DoEvents();

            Assert.Equal(800, window.MaxWidth);
            Assert.Equal(600, window.MaxHeight);
            Assert.True(window.Left >= area.Left && window.Top >= area.Top, $"window at {window.Left},{window.Top}, {window.ActualWidth}x{window.ActualHeight}; Win32 {Native(window)}");
            Assert.True(window.Left + window.ActualWidth <= area.Right + 1, $"right edge {window.Left + window.ActualWidth}");
            Assert.True(window.Top + window.ActualHeight <= area.Bottom + 1, $"bottom edge {window.Top + window.ActualHeight}");
        }
        finally
        {
            window.Close();
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    });

    [Fact]
    public void A_hand_placed_window_is_left_where_it_was_put() => Screenshots.RunSta(() =>
    {
        var window = new Window { Width = 300, Height = 200, ShowActivated = false, ShowInTaskbar = false, Title = "manual" };
        try
        {
            window.SourceInitialized += (_, _) => WindowPlacement.CapToWorkArea(window, new Rect(0, 0, 800, 600));
            WindowCapture.ShowOffScreen(window);
            Assert.Equal(-32000, window.Left);
        }
        finally
        {
            window.Close();
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    });

    private static string Native(Window window)
    {
        GetWindowRect(new WindowInteropHelper(window).Handle, out var r);
        return $"{r.Left},{r.Top} - {r.Right},{r.Bottom}";
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
