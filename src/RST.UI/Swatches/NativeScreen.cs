// Copied from splash-it src/SplashIt.UI/Swatches/NativeScreen.cs (splash-it commit ba86a71; originally gantt-it src/GanttIt.Revit/UI/Swatches/NativeScreen.cs, gantt-it commit a96d0a1), MIT, same owner.
// Changes: namespaces (SplashIt.* to RST.*); comments say RST.

using System.ComponentModel;
using System.Runtime.InteropServices;
using RST.Core.Picking;
using RST.UI.Resources;

namespace RST.UI.Swatches;

/// <summary>
/// The user32/gdi32/dwmapi calls behind "Pick from screen". Every coordinate here is
/// in physical pixels: callers wrap screen reads in <see cref="PerMonitorDpi"/>, so
/// monitor rectangles, the capture, the pointer and window positions share one space
/// even on mixed-DPI layouts and with monitors left of or above the primary.
/// </summary>
internal static class NativeScreen
{
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);
    private static readonly IntPtr HwndTopmost = new(-1);

    private const int SwHide = 0;
    private const int SwShow = 5;
    private const int SwShowNoActivate = 8;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const int SrcCopy = 0x00CC0020;
    private const int CaptureBlt = 0x40000000;
    private const int DwmwaTransitionsForceDisabled = 3;

    /// <summary>
    /// Runs the calling thread per-monitor-DPI-aware (v2) until disposed, so screen
    /// coordinates are physical pixels and windows created meanwhile get their own
    /// monitor's DPI. A no-op where Windows doesn't support it (before 10 1607) or the
    /// thread is already per-monitor aware.
    /// </summary>
    public static IDisposable PerMonitorDpi() => new DpiScope();

    private sealed class DpiScope : IDisposable
    {
        private IntPtr _previous;

        public DpiScope()
        {
            try
            {
                _previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
            }
            catch (EntryPointNotFoundException)
            {
                _previous = IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            if (_previous == IntPtr.Zero) return;
            try
            {
                SetThreadDpiAwarenessContext(_previous);
            }
            catch (EntryPointNotFoundException)
            {
                // Not reachable: the constructor's call succeeded.
            }
            _previous = IntPtr.Zero;
        }
    }

    /// <summary>Every monitor's full rectangle (not just its work area), in enumeration order.</summary>
    public static List<PixelRect> Monitors()
    {
        var monitors = new List<PixelRect>();
        MonitorEnumProc proc = (IntPtr monitor, IntPtr _, ref NativeRect rect, IntPtr _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            var r = GetMonitorInfoW(monitor, ref info) ? info.Monitor : rect;
            monitors.Add(PixelRect.FromEdges(r.Left, r.Top, r.Right, r.Bottom));
            return true;
        };
        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), UiStrings.Pick_ListFailed);
        GC.KeepAlive(proc);
        return monitors;
    }

    /// <summary>Copies <paramref name="bounds"/> of the screen (normally the whole virtual screen) as it is now.</summary>
    public static ScreenSnapshot Capture(PixelRect bounds)
    {
        var stride = (long)bounds.Width * 4;
        if (bounds.IsEmpty || stride * bounds.Height > int.MaxValue)
            throw new InvalidOperationException(UiStrings.Pick_AreaTooLarge(bounds.Width, bounds.Height));

        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) throw new Win32Exception(UiStrings.Pick_NoAccess);
        var memory = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var old = IntPtr.Zero;
        try
        {
            memory = CreateCompatibleDC(screen);
            if (memory == IntPtr.Zero) throw new Win32Exception("CreateCompatibleDC failed.");

            var header = new BitmapInfoHeader
            {
                Size = Marshal.SizeOf<BitmapInfoHeader>(),
                Width = bounds.Width,
                Height = -bounds.Height, // top-down rows
                Planes = 1,
                BitCount = 32,
                Compression = 0, // BI_RGB
            };
            bitmap = CreateDIBSection(screen, ref header, 0, out var bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) throw new Win32Exception("CreateDIBSection failed.");
            old = SelectObject(memory, bitmap);

            if (!BitBlt(memory, 0, 0, bounds.Width, bounds.Height, screen, bounds.Left, bounds.Top, SrcCopy | CaptureBlt))
                throw new Win32Exception(Marshal.GetLastWin32Error(), UiStrings.Pick_CopyRefused);
            GdiFlush();

            var pixels = new byte[stride * bounds.Height];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return new ScreenSnapshot(bounds, pixels, (int)stride);
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(memory, old);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public static (int X, int Y) CursorPosition()
    {
        if (!GetCursorPos(out var p)) throw new Win32Exception(Marshal.GetLastWin32Error(), UiStrings.Pick_PointerFailed);
        return (p.X, p.Y);
    }

    public static void SetCursorPosition(int x, int y) => SetCursorPos(x, y);

    /// <summary>Places a window topmost over <paramref name="rect"/> without activating it.</summary>
    public static void PlaceTopmost(IntPtr hwnd, PixelRect rect) =>
        SetWindowPos(hwnd, HwndTopmost, rect.Left, rect.Top, rect.Width, rect.Height, SwpNoActivate | SwpShowWindow);

    public static (int Left, int Top, int Right, int Bottom) WindowRect(IntPtr hwnd) =>
        GetWindowRect(hwnd, out var r) ? (r.Left, r.Top, r.Right, r.Bottom) : default;

    /// <summary>
    /// Hides a window at the Win32 level only. WPF's <c>Window.Hide()</c> would end a
    /// <c>ShowDialog</c>, closing the color picker and the RST window; ShowWindow
    /// leaves WPF's modal loops, position and state untouched.
    /// </summary>
    public static void Hide(IntPtr hwnd) => ShowWindow(hwnd, SwHide);

    public static void Show(IntPtr hwnd, bool activate) => ShowWindow(hwnd, activate ? SwShow : SwShowNoActivate);

    /// <summary>Turns the DWM show/hide animation off (or back on), so a hidden window is gone at once.</summary>
    public static void SetTransitionsDisabled(IntPtr hwnd, bool disabled)
    {
        var value = disabled ? 1 : 0;
        try
        {
            DwmSetWindowAttribute(hwnd, DwmwaTransitionsForceDisabled, ref value, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // No DWM: there is no animation to turn off.
        }
    }

    /// <summary>Waits for the desktop compositor to present a frame (so hidden windows are off the screen).</summary>
    public static void FlushCompositor()
    {
        try
        {
            DwmFlush();
        }
        catch (DllNotFoundException)
        {
            // No DWM.
        }
    }

    // ---- interop -----------------------------------------------------------------------------

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref NativeRect rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public int Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfoHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, int rop);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GdiFlush();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}
