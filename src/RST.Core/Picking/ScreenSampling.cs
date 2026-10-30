// Copied from splash-it src/SplashIt.Core/Picking/ScreenSampling.cs (splash-it commit ba86a71; originally gantt-it src/GanttIt.Core/Picking/ScreenSampling.cs, gantt-it commit a96d0a1), MIT, same owner.
// Changes: namespaces (SplashIt.* to RST.*); a comment names RST.UI.

using RST.Core.Colors;

namespace RST.Core.Picking;

// Pure geometry and pixel lookup behind "Pick from screen" (the eyedropper).
// Everything here is in physical screen pixels: the WPF side (RST.UI) captures the
// virtual screen, reads the pointer and positions the overlays in the same
// per-monitor-DPI-aware coordinate space, so a pointer position maps to a
// snapshot pixel by a plain offset whatever the monitors' scale factors are.

/// <summary>A rectangle in physical screen pixels. Left/Top inclusive, Right/Bottom exclusive.</summary>
public readonly record struct PixelRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;

    public int Bottom => Top + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    public static PixelRect FromEdges(int left, int top, int right, int bottom) => new(left, top, right - left, bottom - top);

    /// <summary>The smallest rectangle holding every rectangle (the virtual screen of a set of monitors).</summary>
    public static PixelRect Union(IEnumerable<PixelRect> rects)
    {
        int? l = null, t = null, r = null, b = null;
        foreach (var rect in rects)
        {
            if (rect.IsEmpty) continue;
            l = Math.Min(l ?? rect.Left, rect.Left);
            t = Math.Min(t ?? rect.Top, rect.Top);
            r = Math.Max(r ?? rect.Right, rect.Right);
            b = Math.Max(b ?? rect.Bottom, rect.Bottom);
        }
        return l is null ? default : FromEdges(l.Value, t!.Value, r!.Value, b!.Value);
    }

    /// <summary>
    /// Index of the rectangle containing the point, or of the nearest one when the
    /// point falls in a gap between monitors; -1 for an empty list.
    /// </summary>
    public static int IndexAt(IReadOnlyList<PixelRect> rects, int x, int y)
    {
        var best = -1;
        long bestDistance = long.MaxValue;
        for (var i = 0; i < rects.Count; i++)
        {
            var r = rects[i];
            if (r.Contains(x, y)) return i;
            long dx = x < r.Left ? r.Left - x : x >= r.Right ? x - r.Right + 1 : 0;
            long dy = y < r.Top ? r.Top - y : y >= r.Bottom ? y - r.Bottom + 1 : 0;
            var d = dx * dx + dy * dy;
            if (d < bestDistance)
            {
                bestDistance = d;
                best = i;
            }
        }
        return best;
    }

    /// <summary>The point moved inside the rectangle (for keyboard nudges at an edge).</summary>
    public (int X, int Y) Clamp(int x, int y) =>
        (Math.Clamp(x, Left, Math.Max(Left, Right - 1)), Math.Clamp(y, Top, Math.Max(Top, Bottom - 1)));
}

/// <summary>
/// A frozen capture of the virtual screen as 32-bit BGRX rows, top-down, the
/// layout of a top-down 32 bpp DIB. The fourth byte is ignored (screen captures
/// leave it undefined).
/// </summary>
public sealed class ScreenSnapshot
{
    public ScreenSnapshot(PixelRect bounds, byte[] pixels, int stride)
    {
        if (bounds.IsEmpty) throw new ArgumentException("The captured area is empty.", nameof(bounds));
        if (stride < bounds.Width * 4) throw new ArgumentException("The stride is shorter than a row.", nameof(stride));
        if (pixels.Length < (long)stride * bounds.Height) throw new ArgumentException("The pixel buffer is too small.", nameof(pixels));
        Bounds = bounds;
        Pixels = pixels;
        Stride = stride;
    }

    /// <summary>The captured area in physical screen pixels (its Left/Top may be negative).</summary>
    public PixelRect Bounds { get; }

    public byte[] Pixels { get; }

    public int Stride { get; }

    /// <summary>The color of the screen pixel at (<paramref name="x"/>, <paramref name="y"/>); null outside the capture.</summary>
    public Rgb? ColorAt(int x, int y)
    {
        if (!Bounds.Contains(x, y)) return null;
        var i = (y - Bounds.Top) * Stride + (x - Bounds.Left) * 4;
        return new Rgb(Pixels[i + 2], Pixels[i + 1], Pixels[i]);
    }

    /// <summary>
    /// The (2·<paramref name="radius"/>+1)² pixels centred on (x, y), row by row,
    /// for the magnifier. Pixels outside the capture are null.
    /// </summary>
    public Rgb?[] Neighbourhood(int x, int y, int radius)
    {
        var size = radius * 2 + 1;
        var cells = new Rgb?[size * size];
        for (var row = 0; row < size; row++)
            for (var col = 0; col < size; col++)
                cells[row * size + col] = ColorAt(x - radius + col, y - radius + row);
        return cells;
    }
}

/// <summary>Placement math for the overlay windows and the magnifier.</summary>
public static class PickerGeometry
{
    /// <summary>
    /// Where the centre of physical pixel (x, y) falls in an overlay that covers
    /// <paramref name="monitor"/> with a client area of <paramref name="width"/> ×
    /// <paramref name="height"/> in the overlay's own units (WPF DIPs). Proportional,
    /// so it holds whatever scale factor WPF applies to the overlay.
    /// </summary>
    public static (double X, double Y) ToOverlay(PixelRect monitor, double width, double height, int x, int y) =>
        ((x - monitor.Left + 0.5) * width / monitor.Width, (y - monitor.Top + 0.5) * height / monitor.Height);

    /// <summary>
    /// Top-left of a <paramref name="boxWidth"/> × <paramref name="boxHeight"/> box shown
    /// <paramref name="offset"/> below-right of the pointer, flipped to the left and/or
    /// above when it would leave the area, and kept inside it.
    /// </summary>
    public static (double X, double Y) PlaceBeside(
        double pointerX, double pointerY, double boxWidth, double boxHeight, double areaWidth, double areaHeight, double offset)
    {
        var x = pointerX + offset;
        if (x + boxWidth > areaWidth) x = pointerX - offset - boxWidth;
        var y = pointerY + offset;
        if (y + boxHeight > areaHeight) y = pointerY - offset - boxHeight;
        x = Math.Max(0, Math.Min(x, areaWidth - boxWidth));
        y = Math.Max(0, Math.Min(y, areaHeight - boxHeight));
        return (x, y);
    }
}
