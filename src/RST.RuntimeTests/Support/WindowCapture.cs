// Adapted from splash-it tests/SplashIt.RuntimeTests/Support/WindowCapture.cs (splash-it commit ba86a71), MIT, same owner.
// Changes: namespaces (SplashIt.* to RST.*); Content renders a window's content at a chosen (or its desired)
// size, unclipped by the window, because the CI desktop (about 1024 x 768) is smaller than rst-c's 1450 x 900 windows.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RST.RuntimeTests.Support;

/// <summary>
/// Renders real windows to bitmaps: the window is shown far off-screen without activation,
/// its dispatcher queue is drained so bindings, templates and layout settle, and its client
/// area is rendered. The title bar (non-client, drawn by DWM) is not part of the image.
/// </summary>
public static class WindowCapture
{
    /// <summary>Shows <paramref name="window"/> off-screen (not activated) and lets it lay out.</summary>
    public static void ShowOffScreen(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000;
        window.Top = -32000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Show();
        DoEvents();
        window.UpdateLayout();
        DoEvents();
    }

    /// <summary>Runs every queued dispatcher operation down to <see cref="DispatcherPriority.ApplicationIdle"/>.</summary>
    public static void DoEvents()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    /// <summary>The client area of a shown window.</summary>
    public static BitmapSource Client(Window window)
    {
        var root = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
        return Render(root, root.ActualWidth, root.ActualHeight);
    }

    /// <summary>
    /// The window's <see cref="ContentControl.Content"/> laid out at <paramref name="size"/> (null: its desired
    /// size) and rendered. Unlike <see cref="Client"/>, the result is not clipped to the window, whose size
    /// Windows caps at the desktop's: on the ~1024 x 768 CI desktop a 1450 x 900 window would come out cut.
    /// The content must paint its own background (the window's Background is on the window, not the content).
    /// </summary>
    public static BitmapSource Content(Window window, Size? size = null)
    {
        if (window.Content is not FrameworkElement root)
            throw new InvalidOperationException($"{window.GetType().Name}'s content is not a FrameworkElement.");
        DoEvents();
        var target = size ?? Desired(root);
        root.Measure(target);
        root.Arrange(new Rect(target));
        return Render(root, target.Width, target.Height);
    }

    private static Size Desired(FrameworkElement root)
    {
        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return new Size(Math.Ceiling(root.DesiredSize.Width), Math.Ceiling(root.DesiredSize.Height));
    }

    /// <summary>Renders a laid-out <paramref name="visual"/> at 96 DPI, from its own top-left corner.</summary>
    public static BitmapSource Render(Visual visual, double width, double height)
    {
        var w = (int)Math.Ceiling(width);
        var h = (int)Math.Ceiling(height);
        if (w <= 0 || h <= 0) throw new InvalidOperationException($"Nothing to render: {visual.GetType().Name} is {w}x{h}.");
        var brush = new VisualBrush(visual)
        {
            Stretch = Stretch.None,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, w, h),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, w, h),
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
        };
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
            dc.DrawRectangle(brush, null, new Rect(0, 0, w, h));
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// <paramref name="background"/> with <paramref name="overlay"/> drawn at <paramref name="at"/>,
    /// framed by a 1 px <paramref name="frame"/> line and a soft shadow, the canvas growing to fit.
    /// </summary>
    public static BitmapSource Compose(BitmapSource background, BitmapSource overlay, Point at, Color frame)
    {
        var width = Math.Max(background.PixelWidth, at.X + overlay.PixelWidth + 8);
        var height = Math.Max(background.PixelHeight, at.Y + overlay.PixelHeight + 8);
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)), null, new Rect(0, 0, width, height));
            dc.DrawImage(background, new Rect(0, 0, background.PixelWidth, background.PixelHeight));
            var box = new Rect(at.X, at.Y, overlay.PixelWidth, overlay.PixelHeight);
            for (var i = 4; i >= 1; i--)
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(18 * (5 - i)), 0, 0, 0)), null,
                    new Rect(box.X - i + 3, box.Y - i + 4, box.Width + 2 * i, box.Height + 2 * i));
            dc.DrawImage(overlay, box);
            dc.DrawRectangle(null, new Pen(new SolidColorBrush(frame), 1), new Rect(box.X - 0.5, box.Y - 0.5, box.Width + 1, box.Height + 1));
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Every visual of type <typeparamref name="T"/> under <paramref name="root"/>.</summary>
    public static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }
}
