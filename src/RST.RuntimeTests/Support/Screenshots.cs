// Copied from splash-it tests/SplashIt.RuntimeTests/Support/Screenshots.cs (splash-it commit ba86a71), MIT, same owner.
// Changes: namespaces (SplashIt.* to RST.*); finds the repo by RST-C.sln; reads $RST_SCREENSHOTS; artifact RST-screenshots.

using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RST.RuntimeTests.Support;

/// <summary>
/// The off-screen screenshot pipeline: runs WPF code on its own STA thread, renders
/// an element with <see cref="RenderTargetBitmap"/> and writes a PNG under
/// <see cref="Folder"/>. CI uploads that folder as the RST-screenshots artifact.
/// </summary>
public static class Screenshots
{
    /// <summary>
    /// <c>$RST_SCREENSHOTS</c> when set, else <c>&lt;repo&gt;/TestResults/screenshots</c>
    /// (the folder holding RST-C.sln), else under the test output folder.
    /// </summary>
    public static string Folder
    {
        get
        {
            var fromEnv = Environment.GetEnvironmentVariable("RST_SCREENSHOTS");
            if (!string.IsNullOrWhiteSpace(fromEnv)) return Path.GetFullPath(fromEnv);

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "RST-C.sln")))
                    return Path.Combine(dir.FullName, "TestResults", "screenshots");

            return Path.Combine(AppContext.BaseDirectory, "TestResults", "screenshots");
        }
    }

    /// <summary>Runs <paramref name="action"/> on a new STA thread and rethrows its exception, if any.</summary>
    public static void RunSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    /// <summary>
    /// Measures, arranges and renders <paramref name="element"/> at its desired size
    /// (96 DPI, Pbgra32). Call on an STA thread.
    /// </summary>
    public static BitmapSource Render(FrameworkElement element)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        element.Arrange(new Rect(element.DesiredSize));
        element.UpdateLayout();

        var width = (int)Math.Ceiling(element.ActualWidth);
        var height = (int)Math.Ceiling(element.ActualHeight);
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException($"Nothing to render: {element.GetType().Name} is {width}x{height}.");

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Writes <paramref name="bitmap"/> as <c>&lt;Folder&gt;/&lt;name&gt;.png</c> and returns the path.</summary>
    public static string Save(BitmapSource bitmap, string name)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, name + ".png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    /// <summary>
    /// <paramref name="bitmap"/> enlarged <paramref name="factor"/> times, each pixel a block
    /// (nearest neighbour), so thin bars stay sharp: the "2x" zoomed captures.
    /// </summary>
    public static BitmapSource Zoom(BitmapSource bitmap, int factor)
    {
        var source = bitmap.Format == PixelFormats.Pbgra32 ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Pbgra32, null, 0);
        int w = source.PixelWidth, h = source.PixelHeight, stride = w * 4;
        var pixels = new byte[stride * h];
        source.CopyPixels(pixels, stride, 0);
        int zw = w * factor, zh = h * factor, zstride = zw * 4;
        var zoomed = new byte[zstride * zh];
        for (var y = 0; y < zh; y++)
            for (var x = 0; x < zw; x++)
                Buffer.BlockCopy(pixels, (y / factor) * stride + (x / factor) * 4, zoomed, y * zstride + x * 4, 4);
        var result = BitmapSource.Create(zw, zh, 96, 96, PixelFormats.Pbgra32, null, zoomed, zstride);
        result.Freeze();
        return result;
    }

    /// <summary>The number of distinct pixel values in <paramref name="bitmap"/> (a blank image has 1).</summary>
    public static int DistinctPixels(BitmapSource bitmap)
    {
        var converted = bitmap.Format == PixelFormats.Pbgra32 ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Pbgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        var distinct = new HashSet<int>();
        for (var i = 0; i < pixels.Length; i += 4)
            distinct.Add(BitConverter.ToInt32(pixels, i));
        return distinct.Count;
    }

    /// <summary>The pixel at (<paramref name="x"/>, <paramref name="y"/>) as opaque RGB.</summary>
    public static Color PixelAt(BitmapSource bitmap, int x, int y)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromRgb(pixel[2], pixel[1], pixel[0]);
    }
}
