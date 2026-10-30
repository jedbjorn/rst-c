// Copied from splash-it src/SplashIt.UI/Dialogs/MonitorWorkArea.cs (splash-it commit ba86a71; originally gantt-it src/GanttIt.Revit/UI/Dialogs/MonitorWorkArea.cs, gantt-it commit a96d0a1), MIT, same owner.
// Changes: namespaces (SplashIt.* to RST.*).

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RST.UI.Dialogs;

/// <summary>
/// The work area of the monitor a window is on, in DIPs. <see cref="SystemParameters.WorkArea"/>
/// only describes the primary monitor, which puts popups on the wrong screen when
/// Revit runs on a secondary one.
/// </summary>
public static class MonitorWorkArea
{
    private const int MonitorDefaultToNearest = 2;

    /// <summary>The work area of <paramref name="window"/>'s monitor; the primary work area if it can't be read.</summary>
    public static Rect Of(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero || PresentationSource.FromVisual(window) is not { CompositionTarget: { } target })
                return SystemParameters.WorkArea;

            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfoW(monitor, ref info)) return SystemParameters.WorkArea;

            var fromDevice = target.TransformFromDevice;
            var topLeft = fromDevice.Transform(new Point(info.Work.Left, info.Work.Top));
            var bottomRight = fromDevice.Transform(new Point(info.Work.Right, info.Work.Bottom));
            return new Rect(topLeft, bottomRight);
        }
        catch (Exception)
        {
            return SystemParameters.WorkArea;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public int Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);
}
