// rst-c's own (not vendored): the Activity chart geometry, ported from buildChartSvg of the
// retired src/RST.UI/Assets/health_viewer.html (spec #16, WU2). The same 760 x 190 box and
// paddings, four gridlines with y labels, up to five date ticks, the monotone curve and one
// dot (with its hover label) per point. The WPF ActivityChart control draws this layout.

namespace RST.Core.Health;

/// <summary>One plotted value: <see cref="X"/> in milliseconds since the Unix epoch, <see cref="Label"/> the dot's tooltip.</summary>
public sealed record ActivityChartPoint(double X, double Y, string Label);

/// <summary>A horizontal gridline at chart <see cref="Y"/>, labelled with its value.</summary>
public sealed record ChartGridLine(double Y, string Label);

/// <summary>A date tick centred at chart <see cref="X"/>.</summary>
public sealed record ChartTick(double X, string Label);

/// <summary>A dot at chart coordinates, with its tooltip.</summary>
public sealed record ChartDot(double X, double Y, string Label);

/// <summary>A chart laid out in its 760 x 190 logical box.</summary>
public sealed record ActivityChartLayout(
    ChartMode Mode,
    IReadOnlyList<ChartGridLine> GridLines,
    IReadOnlyList<ChartTick> Ticks,
    IReadOnlyList<ChartDot> Dots,
    MonotoneCurve? Curve)
{
    public const double Width = 760;
    public const double Height = 190;
    public const double PadLeft = 46;
    public const double PadRight = 14;
    public const double PadTop = 14;
    public const double PadBottom = 26;
    public const int GridLineCount = 4;
    public const int MaxTicks = 5;

    /// <summary>Inner plot width.</summary>
    public const double InnerWidth = Width - PadLeft - PadRight;

    /// <summary>Inner plot height.</summary>
    public const double InnerHeight = Height - PadTop - PadBottom;

    /// <summary>
    /// Lays out <paramref name="points"/> (ascending x). The y axis runs from 0 to 1.12 × the
    /// largest value (1.12 when every value is 0 or less); one x collapses to the centre. Up to
    /// five ticks, spread evenly over the points. <paramref name="yFormat"/> labels the
    /// gridlines, <paramref name="xFormat"/> the ticks.
    /// </summary>
    public static ActivityChartLayout Build(
        IReadOnlyList<ActivityChartPoint> points, Func<double, string> yFormat, Func<double, string> xFormat)
    {
        var mode = ActivityChartMath.Mode(points.Count);
        if (mode == ChartMode.Empty) return new ActivityChartLayout(mode, [], [], [], null);

        var xMin = points.Min(p => p.X);
        var xMax = points.Max(p => p.X);
        var yTop = Math.Max(points.Max(p => p.Y), 0);
        if (yTop == 0 || double.IsNaN(yTop)) yTop = 1;
        var yMax = yTop * 1.12;
        double Sx(double x) => xMax == xMin ? PadLeft + InnerWidth / 2 : PadLeft + (x - xMin) / (xMax - xMin) * InnerWidth;
        double Sy(double y) => PadTop + InnerHeight - y / yMax * InnerHeight;

        var grid = new List<ChartGridLine>(GridLineCount);
        for (var g = 0; g < GridLineCount; g++)
        {
            var gy = PadTop + InnerHeight * g / (GridLineCount - 1);
            var gv = yMax * (1 - (double)g / (GridLineCount - 1));
            grid.Add(new ChartGridLine(gy, yFormat(gv)));
        }

        var tickCount = Math.Min(points.Count, MaxTicks);
        var ticks = new List<ChartTick>(tickCount);
        for (var t = 0; t < tickCount; t++)
        {
            var idx = tickCount == 1 ? 0 : (int)HealthFormat.JsRound((double)t * (points.Count - 1) / (tickCount - 1));
            ticks.Add(new ChartTick(Sx(points[idx].X), xFormat(points[idx].X)));
        }

        var curve = mode == ChartMode.Curve
            ? ActivityChartMath.MonotoneCubic(points.Select(p => new ChartPoint(Sx(p.X), Sy(p.Y))).ToList())
            : null;
        var dots = points.Select(p => new ChartDot(Sx(p.X), Sy(p.Y), p.Label)).ToList();
        return new ActivityChartLayout(mode, grid, ticks, dots, curve);
    }
}
