// Copied from splash-it src/SplashIt.UI/Swatches/ScreenPickOverlay.xaml.cs (splash-it commit ba86a71; originally gantt-it src/GanttIt.Revit/UI/Swatches/ScreenPickOverlay.xaml.cs, gantt-it commit a96d0a1), MIT, same owner.
// Changes: namespaces (SplashIt.* to RST.*).

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RST.Core.Picking;
using RST.UI.Mvvm;

namespace RST.UI.Swatches;

/// <summary>
/// A topmost, borderless window covering one monitor during "Pick from screen": the
/// frozen screenshot of that monitor and the magnifier. Input is forwarded to
/// <see cref="ScreenColorPicker"/>, which owns the pick.
/// </summary>
public partial class ScreenPickOverlay : Window
{
    /// <summary>Pixels shown either side of the centre pixel in the magnifier.</summary>
    public const int Radius = 7;

    private const int Span = Radius * 2 + 1;
    private const double CellSize = 10;
    private const double LoupeOffset = 24;
    private const int WmWindowPosChanging = 0x0046;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;

    private readonly WriteableBitmap _cells = new(Span, Span, 96, 96, PixelFormats.Bgra32, null);
    private readonly byte[] _cellBuffer = new byte[Span * Span * 4];
    private PixelRect? _pinned;

    internal ScreenPickOverlay(PixelRect monitor)
    {
        InitializeComponent();
        Monitor = monitor;

        var zoom = Span * CellSize;
        Zoom.Width = zoom;
        Zoom.Height = zoom;
        Cells.Source = _cells;
        Cells.Width = zoom;
        Cells.Height = zoom;
        GridLines.Width = zoom;
        GridLines.Height = zoom;
        GridLines.Fill = GridBrush();
        Place(CentreOuter, Radius * CellSize - 1, CellSize + 2);
        Place(CentreInner, Radius * CellSize, CellSize);

        SourceInitialized += (_, _) => UiGuard.Run("Screen picker overlay: source initialized", () =>
        {
            Handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(Handle)?.AddHook(PinHook);
        });
    }

    /// <summary>The monitor this overlay covers, in physical pixels.</summary>
    internal PixelRect Monitor { get; }

    internal IntPtr Handle { get; private set; }

    internal void ShowScreenshot(BitmapSource image) => Screenshot.Source = image;

    /// <summary>
    /// Covers the monitor exactly and keeps it that way: every later move or resize
    /// (for example WPF answering WM_DPICHANGED when the window lands on a monitor with
    /// another scale factor) is pinned back to the monitor's rectangle.
    /// </summary>
    internal void Cover()
    {
        _pinned = Monitor;
        NativeScreen.PlaceTopmost(Handle, Monitor);
        // Belt and braces: if a DPI change still moved it, place it once more.
        if (NativeScreen.WindowRect(Handle) != (Monitor.Left, Monitor.Top, Monitor.Right, Monitor.Bottom))
            NativeScreen.PlaceTopmost(Handle, Monitor);
        UpdateLayout(); // the magnifier is placed from the new client size straight away
    }

    /// <summary>Shows the magnifier for physical pixel (x, y), which lies on this monitor.</summary>
    internal void ShowLoupe(ScreenSnapshot snapshot, int x, int y)
    {
        var colors = snapshot.Neighbourhood(x, y, Radius);
        for (var i = 0; i < colors.Length; i++)
        {
            var c = colors[i];
            var o = i * 4;
            _cellBuffer[o] = c?.B ?? 0;
            _cellBuffer[o + 1] = c?.G ?? 0;
            _cellBuffer[o + 2] = c?.R ?? 0;
            _cellBuffer[o + 3] = c is null ? (byte)0 : (byte)255;
        }
        _cells.WritePixels(new Int32Rect(0, 0, Span, Span), _cellBuffer, Span * 4, 0);

        var centre = colors[Radius * Span + Radius];
        if (centre is { } color)
        {
            Swatch.Background = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
            HexText.Text = color.ToHex();
        }

        if (Loupe.Visibility != Visibility.Visible)
        {
            Loupe.Visibility = Visibility.Visible;
            Loupe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }
        var width = LoupeLayer.ActualWidth > 0 ? LoupeLayer.ActualWidth : ActualWidth;
        var height = LoupeLayer.ActualHeight > 0 ? LoupeLayer.ActualHeight : ActualHeight;
        var (px, py) = PickerGeometry.ToOverlay(Monitor, width, height, x, y);
        var size = Loupe.DesiredSize;
        var (left, top) = PickerGeometry.PlaceBeside(px, py, size.Width, size.Height, width, height, LoupeOffset);
        Canvas.SetLeft(Loupe, left);
        Canvas.SetTop(Loupe, top);
    }

    internal void HideLoupe() => Loupe.Visibility = Visibility.Collapsed;

    private IntPtr PinHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmWindowPosChanging && _pinned is { } r && lParam != IntPtr.Zero)
        {
            var pos = Marshal.PtrToStructure<WindowPos>(lParam);
            pos.X = r.Left;
            pos.Y = r.Top;
            pos.Width = r.Width;
            pos.Height = r.Height;
            pos.Flags &= ~(SwpNoMove | SwpNoSize);
            Marshal.StructureToPtr(pos, lParam, false);
        }
        return IntPtr.Zero;
    }

    private static void Place(FrameworkElement element, double at, double size)
    {
        Canvas.SetLeft(element, at);
        Canvas.SetTop(element, at);
        element.Width = size;
        element.Height = size;
    }

    /// <summary>Faint lines between the magnified pixels.</summary>
    private static Brush GridBrush()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), 1);
        var lines = new GeometryGroup();
        lines.Children.Add(new LineGeometry(new Point(CellSize - 0.5, 0), new Point(CellSize - 0.5, CellSize)));
        lines.Children.Add(new LineGeometry(new Point(0, CellSize - 0.5), new Point(CellSize, CellSize - 0.5)));
        var drawing = new GeometryDrawing(null, pen, lines);
        var brush = new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, CellSize, CellSize),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, CellSize, CellSize),
            ViewboxUnits = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        return brush;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos
    {
        public IntPtr Hwnd;
        public IntPtr InsertAfter;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public uint Flags;
    }
}
