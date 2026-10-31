// rst-c's own (not vendored): the xUnit port of tests/ui/activity-chart.test.mjs (spec #16, WU2,
// AC5). Every case of the Node test, now against the C# port in RST.Core.Health. As in the Node
// test, the curve is checked by parsing the emitted path and sampling its Bézier segments densely:
// it never overshoots (non-negative data never dips below zero), stays inside each segment's
// y-range, stays flat on flat data, and survives equal-x points (SC-031). The six stat-card
// labels are checked by the Health window tests in RST.RuntimeTests.

using System.Globalization;
using System.Text.RegularExpressions;
using RST.Core.Health;
using RST.Core.Telemetry;
using Xunit;

namespace RST.Tests.Health;

public class ActivityChartTests
{
    private static List<ChartPoint> Series(double step, params double[] ys) =>
        ys.Select((y, i) => new ChartPoint(i * step, y)).ToList();

    /// <summary>The Node test's sampleBezierYs: parses <c>M x y</c> and each <c>C</c> segment and samples it.</summary>
    private static List<ChartPoint> SampleBezier(string path, int samplesPerSegment = 64)
    {
        var move = Regex.Match(path, @"^M(-?[\d.]+) (-?[\d.]+)");
        Assert.True(move.Success, "path has no M: " + path);
        double x0 = Parse(move.Groups[1].Value), y0 = Parse(move.Groups[2].Value);
        var samples = new List<ChartPoint> { new(x0, y0) };
        foreach (Match m in Regex.Matches(path, @"C((?:-?[\d.]+ ){5}-?[\d.]+)"))
        {
            var v = m.Groups[1].Value.Trim().Split(' ').Select(Parse).ToArray();
            double c1x = v[0], c1y = v[1], c2x = v[2], c2y = v[3], x1 = v[4], y1 = v[5];
            for (var s = 1; s <= samplesPerSegment; s++)
            {
                double t = (double)s / samplesPerSegment, u = 1 - t;
                samples.Add(new ChartPoint(
                    u * u * u * x0 + 3 * u * u * t * c1x + 3 * u * t * t * c2x + t * t * t * x1,
                    u * u * u * y0 + 3 * u * u * t * c1y + 3 * u * t * t * c2y + t * t * t * y1));
            }
            x0 = x1;
            y0 = y1;
        }
        return samples;
    }

    private static double Parse(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    [Fact]
    public void Curve_NeverDipsBelowZero_OnNonNegativeData()
    {
        // A Catmull-Rom spline through this data undershoots between the 8 and the two 0.1s.
        var sampled = SampleBezier(ActivityChartMath.MonotoneCubicPath(Series(100, 0, 0.1, 8, 0.1, 0.05, 9, 0)));
        Assert.True(sampled.Min(p => p.Y) >= -1e-9, $"min={sampled.Min(p => p.Y):F4}");
    }

    [Fact]
    public void Curve_StaysWithinEachSegmentsYRange()
    {
        double[] data = [2, 7, 7.2, 1, 1, 5];
        var sampled = SampleBezier(ActivityChartMath.MonotoneCubicPath(Series(50, data)));
        foreach (var p in sampled)
        {
            var i = Math.Min((int)Math.Floor(p.X / 50), data.Length - 2);
            var lo = Math.Min(data[i], data[i + 1]) - 1e-6;
            var hi = Math.Max(data[i], data[i + 1]) + 1e-6;
            Assert.InRange(p.Y, lo, hi);
        }
    }

    [Fact]
    public void FlatSeries_RendersAsAFlatLine()
    {
        var sampled = SampleBezier(ActivityChartMath.MonotoneCubicPath(Series(10, 5, 5, 5, 5)));
        Assert.All(sampled, p => Assert.True(Math.Abs(p.Y - 5) < 1e-9, $"y={p.Y}"));
    }

    [Fact]
    public void Tangent_AtALocalMaximum_IsZero()
    {
        var m = ActivityChartMath.MonotoneTangents([0, 1, 2], [0, 10, 0]);
        Assert.Equal(0, m[1]);
    }

    [Fact]
    public void Tangents_OfZeroAndOnePoints()
    {
        Assert.Empty(ActivityChartMath.MonotoneTangents([], []));
        Assert.Equal(new[] { 0.0 }, ActivityChartMath.MonotoneTangents([3], [4]));
    }

    [Fact]
    public void EqualXPair_EmitsAFinitePath_ThroughTheMeanY()
    {
        // SC-031: two events in the same instant zeroed a secant denominator and emitted NaN.
        var p = ActivityChartMath.MonotoneCubicPath([new(1, 1), new(1, 2), new(2, 3)]);
        Assert.NotEqual("", p);
        Assert.DoesNotContain("NaN", p, StringComparison.Ordinal);
        Assert.StartsWith("M1.00 1.50", p, StringComparison.Ordinal);
        var sampled = SampleBezier(p);
        Assert.NotEmpty(sampled);
        Assert.All(sampled, q => Assert.True(double.IsFinite(q.X) && double.IsFinite(q.Y)));
    }

    [Fact]
    public void AllPointsAtOneX_EmitNoPath()
    {
        Assert.Equal("", ActivityChartMath.MonotoneCubicPath([new(5, 1), new(5, 2)]));
        Assert.Null(ActivityChartMath.MonotoneCubic([new(5, 1), new(5, 2)]));
    }

    [Fact]
    public void FewerThanTwoPoints_EmitNoPath()
    {
        Assert.Equal("", ActivityChartMath.MonotoneCubicPath([new(0, 3)]));
        Assert.Equal("", ActivityChartMath.MonotoneCubicPath([]));
    }

    [Theory]
    [InlineData(0, ChartMode.Empty)]
    [InlineData(1, ChartMode.Dots)]
    [InlineData(2, ChartMode.Curve)]
    [InlineData(30, ChartMode.Curve)]
    public void ChartMode_GatesTheCurve(int points, ChartMode expected) =>
        Assert.Equal(expected, ActivityChartMath.Mode(points));

    [Fact]
    public void Path_FormatMatchesTheSvgPath()
    {
        // Two decimals, invariant, "M x y" then one "C c1x c1y c2x c2y x y" per segment, no separators.
        var p = ActivityChartMath.MonotoneCubicPath([new(0, 0), new(3, 3)]);
        Assert.Equal("M0.00 0.00C1.00 1.00 2.00 2.00 3.00 3.00", p);
    }

    [Theory]
    [InlineData(5.3, "5.3s")]
    [InlineData(45, "45s")]
    [InlineData(90, "1m 30s")]
    [InlineData(5400, "1h 30m")]
    public void FmtDur_FormatsDurations(double seconds, string expected) =>
        Assert.Equal(expected, HealthFormat.Dur(seconds));

    [Fact]
    public void FmtDur_Null_IsADash() => Assert.Equal("—", HealthFormat.Dur(null));

    [Fact]
    public void RangePills_Are7_30_90_180_With7DTheDefault()
    {
        Assert.Equal([7, 30, 90, 180], ActivityRanges.Pills.Select(p => p.Days));
        Assert.Equal(["7D", "1M", "3M", "6M"], ActivityRanges.Pills.Select(p => p.Label));
        Assert.Equal(7, ActivityRanges.Default);
    }

    [Fact]
    public void Layout_Empty_HasNothingToDraw()
    {
        var layout = ActivityChartLayout.Build([], v => "", v => "");
        Assert.Equal(ChartMode.Empty, layout.Mode);
        Assert.Empty(layout.Dots);
        Assert.Empty(layout.GridLines);
        Assert.Null(layout.Curve);
    }

    [Fact]
    public void Layout_SinglePoint_IsOneCentredDot_WithoutACurve()
    {
        var layout = ActivityChartLayout.Build([new ActivityChartPoint(1000, 30, "one")], v => HealthFormat.Dur(v), x => "Jul 20");
        Assert.Equal(ChartMode.Dots, layout.Mode);
        Assert.Null(layout.Curve);
        var dot = Assert.Single(layout.Dots);
        Assert.Equal(46 + 700 / 2.0, dot.X, 6);
        // y max is 1.12 x the value, so the dot sits at 1/1.12 of the inner height.
        Assert.Equal(14 + 150 - 150 / 1.12, dot.Y, 6);
        Assert.Equal("one", dot.Label);
        Assert.Equal(["Jul 20"], layout.Ticks.Select(t => t.Label));
    }

    [Fact]
    public void Layout_Curve_HasFourGridLines_FiveTicks_AndADotPerPoint()
    {
        var points = Enumerable.Range(0, 9).Select(i => new ActivityChartPoint(i * 86_400_000d, i % 3, "p" + i)).ToList();
        var layout = ActivityChartLayout.Build(points, v => v.ToString("F2", CultureInfo.InvariantCulture), x => (x / 86_400_000d).ToString(CultureInfo.InvariantCulture));
        Assert.Equal(ChartMode.Curve, layout.Mode);
        Assert.NotNull(layout.Curve);
        Assert.Equal(9, layout.Dots.Count);
        // Gridlines top to bottom: 1.12 x max, two thirds, one third, 0.
        Assert.Equal([14, 64, 114, 164], layout.GridLines.Select(g => Math.Round(g.Y, 6)));
        Assert.Equal(["2.24", "1.49", "0.75", "0.00"], layout.GridLines.Select(g => g.Label));
        // Five ticks spread over the nine points: indices 0, 2, 4, 6, 8.
        Assert.Equal(["0", "2", "4", "6", "8"], layout.Ticks.Select(t => t.Label));
        Assert.Equal(46, layout.Ticks[0].X, 6);
        Assert.Equal(746, layout.Ticks[^1].X, 6);
        // The curve runs from the first dot to the last.
        Assert.Equal(layout.Dots[0].X, layout.Curve!.Start.X, 6);
        Assert.Equal(layout.Dots[^1].Y, layout.Curve.Segments[^1].End.Y, 6);
    }

    [Fact]
    public void Layout_AllZero_UsesAUnitAxis()
    {
        var layout = ActivityChartLayout.Build([new(0, 0, "a"), new(1, 0, "b")], v => HealthFormat.HoursShort(v), x => "");
        Assert.Equal("1.1h", layout.GridLines[0].Label);
        Assert.Equal(164, layout.Dots[0].Y, 6);
    }

    /// <summary>Gridline labels for a chart whose largest value is <paramref name="top"/>.</summary>
    private static string[] HourLabels(double top) =>
        ActivityChartLayout.Build([new(0, top, ""), new(1, 0, "")], HealthFormat.HoursAxis, x => "")
            .GridLines.Select(g => g.Label).ToArray();

    private static string[] DurLabels(double top) =>
        ActivityChartLayout.Build([new(0, top, ""), new(1, 0, "")], HealthFormat.DurAxis, x => "")
            .GridLines.Select(g => g.Label).ToArray();

    [Theory]
    // Large enough: fmtHoursShort as the HTML drew it.
    [InlineData(7.2, "8.1h", "5.4h", "2.7h", "0h")]
    [InlineData(0.5, "0.6h", "0.4h", "0.2h", "0h")]
    // The VM's "0.1h, 0.1h, 0h, 0h": whole minutes instead.
    [InlineData(0.1, "7m", "4m", "2m", "0m")]
    // The VM's "0h" four times (a few minutes, so minutes collide too): whole seconds.
    [InlineData(0.01, "40s", "27s", "13s", "0s")]
    [InlineData(0.001, "4s", "3s", "1s", "0s")]
    // Under a second in total: tenths.
    [InlineData(0.0001, "0.4s", "0.3s", "0.1s", "0.0s")]
    public void HoursAxis_LabelsStayDistinct_ForSmallValues(double top, string a, string b, string c, string d) =>
        Assert.Equal(new[] { a, b, c, d }, HourLabels(top));

    [Theory]
    [InlineData(64, "72s", "48s", "24s", "0.0s")]
    [InlineData(5400, "1h 40m", "67m 12s", "33m 36s", "0.0s")]
    [InlineData(0.04, "0.04s", "0.03s", "0.01s", "0.00s")]
    [InlineData(0.004, "0.004s", "0.003s", "0.001s", "0.000s")]
    public void DurAxis_LabelsStayDistinct_ForSmallValues(double top, string a, string b, string c, string d) =>
        Assert.Equal(new[] { a, b, c, d }, DurLabels(top));

    [Theory]
    [InlineData(0.15)]
    [InlineData(0.07)]
    [InlineData(0.02)]
    [InlineData(0.003)]
    [InlineData(1e-6)]
    [InlineData(12.0)]
    [InlineData(100.0)]
    public void AxisLabels_AreAlwaysDistinct(double top)
    {
        Assert.Equal(4, HourLabels(top).Distinct().Count());
        Assert.Equal(4, DurLabels(top).Distinct().Count());
    }

    [Fact]
    public void AxisLabels_DoNotChangeTheCurve()
    {
        List<ActivityChartPoint> points = [new(0, 0.02, ""), new(1, 0.1, ""), new(2, 0.05, "")];
        var perValue = ActivityChartLayout.Build(points, v => HealthFormat.HoursShort(v), x => "");
        var distinct = ActivityChartLayout.Build(points, HealthFormat.HoursAxis, x => "");
        Assert.Equal(perValue.Curve!.Segments, distinct.Curve!.Segments);
        Assert.Equal(perValue.Dots, distinct.Dots);
        Assert.Equal(perValue.GridLines.Select(g => g.Y), distinct.GridLines.Select(g => g.Y));
    }

    [Fact]
    public void Layout_SevenPoints_TickIndicesRoundHalvesUp()
    {
        // JS Math.round(t * 6 / 4): 0, 1.5 → 2, 3, 4.5 → 5, 6. Math.Round (banker's) would give 0, 2, 3, 4, 6.
        var points = Enumerable.Range(0, 7).Select(i => new ActivityChartPoint(i, 1, "")).ToList();
        var layout = ActivityChartLayout.Build(points, v => "", x => x.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(["0", "2", "3", "5", "6"], layout.Ticks.Select(t => t.Label));
    }

    [Fact]
    public void TickDate_UsesTheCulturesMonthDayOrder_WithTheShortMonth()
    {
        var day = new DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal("Oct 28", HealthFormat.ShortMonthDay(day, CultureInfo.GetCultureInfo("en-US")));
        var de = HealthFormat.ShortMonthDay(day, CultureInfo.GetCultureInfo("de-DE"));
        Assert.StartsWith("28.", de, StringComparison.Ordinal);
        Assert.Contains("Okt", de, StringComparison.Ordinal);
    }

    [Fact]
    public void Layout_ThreePoints_TickEveryPoint()
    {
        var layout = ActivityChartLayout.Build([new(0, 1, "a"), new(10, 2, "b"), new(20, 3, "c")], v => "", x => x.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(["0", "10", "20"], layout.Ticks.Select(t => t.Label));
    }
}
