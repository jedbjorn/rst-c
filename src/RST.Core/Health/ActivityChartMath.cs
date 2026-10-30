// rst-c's own (not vendored): the Activity tab's chart maths, ported from monotoneTangents,
// monotoneCubicPath and chartMode of the retired src/RST.UI/Assets/health_viewer.html
// (spec #16, WU2). Fritsch–Carlson monotone cubic interpolation, so the smoothed curve never
// overshoots between points (a fast sync beside a slow one never draws a negative duration),
// with the SC-031 fix kept: points that share an x collapse to their mean y first.

using System.Globalization;
using System.Text;

namespace RST.Core.Health;

/// <summary>A chart point, in data or in chart coordinates.</summary>
public readonly record struct ChartPoint(double X, double Y);

/// <summary>One cubic Bézier segment: two control points and the end point (the start is the previous end).</summary>
public readonly record struct CubicSegment(ChartPoint Control1, ChartPoint Control2, ChartPoint End);

/// <summary>A curve as a start point and its cubic Bézier segments (the SVG path <c>M … C … C …</c>).</summary>
public sealed record MonotoneCurve(ChartPoint Start, IReadOnlyList<CubicSegment> Segments);

/// <summary>How a chart draws its points: nothing, dots only, or dots and a curve.</summary>
public enum ChartMode
{
    Empty,
    Dots,
    Curve,
}

public static class ActivityChartMath
{
    /// <summary><c>chartMode</c>: 0 points are empty, 1 point is a dot without a curve, 2 or more get a curve.</summary>
    public static ChartMode Mode(int pointCount) => pointCount switch
    {
        <= 0 => ChartMode.Empty,
        1 => ChartMode.Dots,
        _ => ChartMode.Curve,
    };

    /// <summary>
    /// <c>monotoneTangents</c>: Fritsch–Carlson tangents at each point (ascending, distinct
    /// <paramref name="xs"/>). Monotone on every segment and flat where the data is flat, so the
    /// interpolant stays within each segment's y-range.
    /// </summary>
    public static double[] MonotoneTangents(IReadOnlyList<double> xs, IReadOnlyList<double> ys)
    {
        var n = xs.Count;
        if (n == 0) return [];
        if (n == 1) return [0];
        var d = new double[n - 1];
        for (var i = 0; i < n - 1; i++) d[i] = (ys[i + 1] - ys[i]) / (xs[i + 1] - xs[i]);
        var m = new double[n];
        m[0] = d[0];
        for (var i = 1; i < n - 1; i++) m[i] = d[i - 1] * d[i] <= 0 ? 0 : (d[i - 1] + d[i]) / 2;
        m[n - 1] = d[n - 2];
        for (var i = 0; i < n - 1; i++)
        {
            if (d[i] == 0)
            {
                m[i] = 0;
                m[i + 1] = 0;
                continue;
            }
            var a = m[i] / d[i];
            var b = m[i + 1] / d[i];
            var s = a * a + b * b;
            if (s > 9)
            {
                var t = 3 / Math.Sqrt(s);
                m[i] = t * a * d[i];
                m[i + 1] = t * b * d[i];
            }
        }
        return m;
    }

    /// <summary>
    /// The monotone cubic Hermite curve through <paramref name="points"/> (ascending x) as cubic
    /// Béziers. Points sharing an x collapse to their mean y first (SC-031: an equal-x pair
    /// zeroed a secant denominator and put NaN into the path); the caller still dots every raw
    /// point. Null below two distinct x: the caller draws dots only.
    /// </summary>
    public static MonotoneCurve? MonotoneCubic(IReadOnlyList<ChartPoint> points)
    {
        var xs = new List<double>();
        var sums = new List<double>();
        var counts = new List<int>();
        foreach (var p in points)
        {
            if (xs.Count > 0 && p.X == xs[^1])
            {
                sums[^1] += p.Y;
                counts[^1]++;
            }
            else
            {
                xs.Add(p.X);
                sums.Add(p.Y);
                counts.Add(1);
            }
        }
        var n = xs.Count;
        if (n < 2) return null;
        var ys = new double[n];
        for (var i = 0; i < n; i++) ys[i] = sums[i] / counts[i];
        var m = MonotoneTangents(xs, ys);
        var segments = new CubicSegment[n - 1];
        for (var i = 0; i < n - 1; i++)
        {
            var h = xs[i + 1] - xs[i];
            segments[i] = new CubicSegment(
                new ChartPoint(xs[i] + h / 3, ys[i] + m[i] * h / 3),
                new ChartPoint(xs[i + 1] - h / 3, ys[i + 1] - m[i + 1] * h / 3),
                new ChartPoint(xs[i + 1], ys[i + 1]));
        }
        return new MonotoneCurve(new ChartPoint(xs[0], ys[0]), segments);
    }

    /// <summary>
    /// <c>monotoneCubicPath</c>: the curve as an SVG path string, two decimals per number
    /// (<c>M1.00 1.50C1.33 …</c>), or "" when there is no curve. Kept for the ported tests and
    /// for logging; the WPF chart draws <see cref="MonotoneCubic"/> directly.
    /// </summary>
    public static string MonotoneCubicPath(IReadOnlyList<ChartPoint> points)
    {
        if (MonotoneCubic(points) is not { } curve) return "";
        var sb = new StringBuilder();
        sb.Append('M').Append(F(curve.Start.X)).Append(' ').Append(F(curve.Start.Y));
        foreach (var s in curve.Segments)
        {
            sb.Append('C').Append(F(s.Control1.X)).Append(' ').Append(F(s.Control1.Y))
              .Append(' ').Append(F(s.Control2.X)).Append(' ').Append(F(s.Control2.Y))
              .Append(' ').Append(F(s.End.X)).Append(' ').Append(F(s.End.Y));
        }
        return sb.ToString();
    }

    private static string F(double v) => v.ToString("F2", CultureInfo.InvariantCulture);
}
