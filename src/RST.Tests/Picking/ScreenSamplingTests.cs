// Copied from splash-it tests/SplashIt.Core.Tests/Picking/ScreenSamplingTests.cs (splash-it commit ba86a71; originally gantt-it tests/GanttIt.Core.Tests/Picking/ScreenSamplingTests.cs, gantt-it commit a96d0a1), MIT, same owner.
// Changes: namespaces (SplashIt.* to RST.*); explicit Xunit usings.

using RST.Core.Colors;
using RST.Core.Picking;
using Xunit;

namespace RST.Tests.Picking;

public class ScreenSamplingTests
{
    // A mixed layout in physical pixels: a 150 % 4K-ish monitor left of a 100 % primary,
    // and a smaller monitor above the primary (negative Y).
    private static readonly PixelRect Left = new(-2560, -200, 2560, 1440);
    private static readonly PixelRect Primary = new(0, 0, 1920, 1080);
    private static readonly PixelRect Above = new(200, -768, 1366, 768);

    /// <summary>
    /// A snapshot whose every pixel has its own color: the absolute screen position packed
    /// into 24 bits (13 for x + 4096, 11 for y + 800), unique across the whole test layout.
    /// An off-by-one or origin error in the lookup therefore always reads a different color,
    /// on positive coordinates as much as negative ones.
    /// </summary>
    private static ScreenSnapshot Snapshot(PixelRect bounds)
    {
        var stride = bounds.Width * 4 + 8; // padded rows must not shift pixels
        var pixels = new byte[stride * bounds.Height];
        for (var row = 0; row < bounds.Height; row++)
            for (var col = 0; col < bounds.Width; col++)
            {
                var i = row * stride + col * 4;
                var c = Expected(bounds.Left + col, bounds.Top + row);
                pixels[i] = c.B;
                pixels[i + 1] = c.G;
                pixels[i + 2] = c.R;
                pixels[i + 3] = 0; // undefined in screen captures
            }
        return new ScreenSnapshot(bounds, pixels, stride);
    }

    private static Rgb Expected(int x, int y)
    {
        Assert.InRange(x, -4096, 4095);
        Assert.InRange(y, -800, 1247);
        var v = ((x + 4096) << 11) | (y + 800);
        return new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    [Fact]
    public void TheTestEncoding_IsUniquePerPixel()
    {
        var seen = new HashSet<Rgb>();
        var all = PixelRect.Union([Primary, Left, Above]);
        for (var y = all.Top; y < all.Bottom; y += 7)
            for (var x = all.Left; x < all.Right; x += 3)
                Assert.True(seen.Add(Expected(x, y)));
        Assert.NotEqual(Expected(1, 0), Expected(257, 0));
        Assert.NotEqual(Expected(0, 1), Expected(0, 257));
    }

    [Fact]
    public void Union_IsTheVirtualScreen_IncludingNegativeCoordinates()
    {
        var all = PixelRect.Union([Primary, Left, Above]);
        Assert.Equal(PixelRect.FromEdges(-2560, -768, 1920, 1240), all);
        Assert.Equal(default, PixelRect.Union([]));
    }

    [Theory]
    [InlineData(-2560, -200)] // top-left pixel of the left monitor
    [InlineData(-1, 0)]       // last column of the left monitor, next to the primary
    [InlineData(0, 0)]        // primary origin
    [InlineData(1919, 1079)]  // primary's last pixel
    [InlineData(200, -768)]   // monitor above the primary
    [InlineData(1565, -1)]
    public void ColorAt_ReadsExactlyThePixelAtThatScreenPosition(int x, int y)
    {
        var snapshot = Snapshot(PixelRect.Union([Primary, Left, Above]));
        Assert.Equal(Expected(x, y), snapshot.ColorAt(x, y));
    }

    [Fact]
    public void ColorAt_OutsideTheCapture_IsNull()
    {
        var snapshot = Snapshot(Primary);
        Assert.Null(snapshot.ColorAt(-1, 0));
        Assert.Null(snapshot.ColorAt(1920, 0));
        Assert.Null(snapshot.ColorAt(0, 1080));
    }

    [Fact]
    public void Neighbourhood_IsCentredOnThePointer_WithNullsPastTheEdge()
    {
        var snapshot = Snapshot(Primary);
        var cells = snapshot.Neighbourhood(0, 0, 2);
        Assert.Equal(25, cells.Length);
        Assert.Equal(Expected(0, 0), cells[12]);   // centre
        Assert.Null(cells[0]);                      // (-2, -2)
        Assert.Equal(Expected(2, 2), cells[24]);   // (2, 2)
        Assert.Equal(Expected(1, 0), cells[13]);
    }

    [Fact]
    public void IndexAt_FindsTheMonitor_OrTheNearestOneInAGap()
    {
        var monitors = new[] { Primary, Left, Above };
        Assert.Equal(0, PixelRect.IndexAt(monitors, 10, 10));
        Assert.Equal(1, PixelRect.IndexAt(monitors, -5, 100));
        Assert.Equal(2, PixelRect.IndexAt(monitors, 300, -5));
        Assert.Equal(2, PixelRect.IndexAt(monitors, 1600, -300)); // gap right of the top monitor
        Assert.Equal(-1, PixelRect.IndexAt([], 0, 0));
    }

    [Fact]
    public void ToOverlay_IsProportional_WhateverScaleWpfUses()
    {
        // 2560 px monitor shown by WPF as 1706.67 DIPs (150 %): pixel 1500 lands at its centre ×(1/1.5).
        var (x, y) = PickerGeometry.ToOverlay(Left, 2560 / 1.5, 1440 / 1.5, -2560 + 1500, -200 + 900);
        Assert.Equal(1500.5 / 1.5, x, 9);
        Assert.Equal(900.5 / 1.5, y, 9);

        // Same monitor where WPF didn't scale (1:1).
        var (x1, y1) = PickerGeometry.ToOverlay(Primary, 1920, 1080, 0, 1079);
        Assert.Equal(0.5, x1, 9);
        Assert.Equal(1079.5, y1, 9);
    }

    [Fact]
    public void PlaceBeside_FlipsAtTheRightAndBottomEdges()
    {
        Assert.Equal((124.0, 124.0), PickerGeometry.PlaceBeside(100, 100, 50, 50, 1000, 1000, 24));
        Assert.Equal((926.0 - 50, 124.0), PickerGeometry.PlaceBeside(950, 100, 50, 50, 1000, 1000, 24));
        Assert.Equal((124.0, 926.0 - 50), PickerGeometry.PlaceBeside(100, 950, 50, 50, 1000, 1000, 24));
        // A box bigger than the area stays at the origin.
        Assert.Equal((0.0, 0.0), PickerGeometry.PlaceBeside(10, 10, 2000, 2000, 1000, 1000, 24));
    }

    [Fact]
    public void Clamp_KeepsANudgedPointerOnTheCapture()
    {
        var all = PixelRect.Union([Primary, Left]);
        Assert.Equal((-2560, -200), all.Clamp(-3000, -500));
        Assert.Equal((1919, 1239), all.Clamp(5000, 5000));
    }

    [Fact]
    public void Snapshot_RejectsABufferThatDoesNotMatch()
    {
        Assert.Throws<ArgumentException>(() => new ScreenSnapshot(new PixelRect(0, 0, 10, 10), new byte[399], 40));
        Assert.Throws<ArgumentException>(() => new ScreenSnapshot(new PixelRect(0, 0, 10, 10), new byte[400], 39));
        Assert.Throws<ArgumentException>(() => new ScreenSnapshot(default, [], 0));
    }
}
