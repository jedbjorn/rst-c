// rst-c's own (not vendored): the Activity tab's chart (spec #16, Shared controls: ActivityChart),
// replacing buildChartSvg's inline SVG in the retired health_viewer.html. It draws an
// RST.Core ActivityChartLayout in the same 760 x 190 logical box: gridlines with y labels, date
// ticks, the monotone curve as a WPF Path of BezierSegments, and dots with hover tooltips. The box
// scales to the card's width, as the SVG did (width 100 %, height auto). A dot with a Tone (the
// Health trend chart, spec #24) is filled with that severity's brush; a gridline with no label
// (the trend chart's band edges) draws only its line.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using RST.Core.Health;

namespace RST.UI.Controls;

public sealed class ActivityChart : Viewbox
{
    public const double DotRadius = 3;

    /// <summary>A toned dot (the Health trend chart) is larger: its colour is the reading.</summary>
    public const double TonedDotRadius = 5;

    /// <summary>A hovered dot grows from r = 3 to r = 5, as <c>.act-dot:hover</c> did.</summary>
    public const double DotHoverScale = 5.0 / 3.0;

    public static readonly DependencyProperty LayoutProperty = Register(nameof(Layout), typeof(ActivityChartLayout));
    public static readonly DependencyProperty LineBrushProperty = Register(nameof(LineBrush), typeof(Brush));
    public static readonly DependencyProperty GridBrushProperty = Register(nameof(GridBrush), typeof(Brush));
    public static readonly DependencyProperty LabelBrushProperty = Register(nameof(LabelBrush), typeof(Brush));
    public static readonly DependencyProperty DotStrokeBrushProperty = Register(nameof(DotStrokeBrush), typeof(Brush));
    public static readonly DependencyProperty OkBrushProperty = Register(nameof(OkBrush), typeof(Brush));
    public static readonly DependencyProperty WarnSoftBrushProperty = Register(nameof(WarnSoftBrush), typeof(Brush));
    public static readonly DependencyProperty WarnBrushProperty = Register(nameof(WarnBrush), typeof(Brush));
    public static readonly DependencyProperty DangerBrushProperty = Register(nameof(DangerBrush), typeof(Brush));
    public static readonly DependencyProperty NoDataBrushProperty = Register(nameof(NoDataBrush), typeof(Brush));

    private readonly Canvas _canvas = new()
    {
        Width = ActivityChartLayout.Width,
        Height = ActivityChartLayout.Height,
        ClipToBounds = false, // the end ticks' labels may run a few pixels past the box, as the SVG text could
        SnapsToDevicePixels = true,
    };

    public ActivityChart()
    {
        Stretch = Stretch.Uniform;
        StretchDirection = StretchDirection.Both;
        Child = _canvas;
    }

    /// <summary>The chart to draw; null draws nothing.</summary>
    public ActivityChartLayout? Layout
    {
        get => (ActivityChartLayout?)GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    /// <summary>The curve and the dots' fill.</summary>
    public Brush? LineBrush
    {
        get => (Brush?)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public Brush? GridBrush
    {
        get => (Brush?)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    /// <summary>The axis labels.</summary>
    public Brush? LabelBrush
    {
        get => (Brush?)GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    /// <summary>The dots' outline: the card's background, so dots stand off the curve.</summary>
    public Brush? DotStrokeBrush
    {
        get => (Brush?)GetValue(DotStrokeBrushProperty);
        set => SetValue(DotStrokeBrushProperty, value);
    }

    /// <summary>A toned dot's fill for <see cref="HealthSeverity.Ok"/> (green); Health's <c>SuccessBrush</c>.</summary>
    public Brush? OkBrush
    {
        get => (Brush?)GetValue(OkBrushProperty);
        set => SetValue(OkBrushProperty, value);
    }

    /// <summary>A toned dot's fill for <see cref="HealthSeverity.WarnSoft"/> (yellow); <c>CautionTextBrush</c>.</summary>
    public Brush? WarnSoftBrush
    {
        get => (Brush?)GetValue(WarnSoftBrushProperty);
        set => SetValue(WarnSoftBrushProperty, value);
    }

    /// <summary>A toned dot's fill for <see cref="HealthSeverity.Warn"/> (amber); <c>WarningTextBrush</c>.</summary>
    public Brush? WarnBrush
    {
        get => (Brush?)GetValue(WarnBrushProperty);
        set => SetValue(WarnBrushProperty, value);
    }

    /// <summary>A toned dot's fill for <see cref="HealthSeverity.Danger"/> (red); <c>ErrorBrush</c>.</summary>
    public Brush? DangerBrush
    {
        get => (Brush?)GetValue(DangerBrushProperty);
        set => SetValue(DangerBrushProperty, value);
    }

    /// <summary>A toned dot's fill for any other tone (Grey, "not enough data"); <c>MutedTextBrush</c>.</summary>
    public Brush? NoDataBrush
    {
        get => (Brush?)GetValue(NoDataBrushProperty);
        set => SetValue(NoDataBrushProperty, value);
    }

    /// <summary>The drawing surface (for tests): gridlines, labels, the curve Path and the dot Ellipses.</summary>
    public Canvas Surface => _canvas;

    private static DependencyProperty Register(string name, Type type) =>
        DependencyProperty.Register(name, type, typeof(ActivityChart),
            new FrameworkPropertyMetadata(null, (d, _) => ((ActivityChart)d).Redraw()));

    private void Redraw()
    {
        _canvas.Children.Clear();
        if (Layout is not { } layout || layout.Mode == ChartMode.Empty) return;

        foreach (var g in layout.GridLines)
        {
            var y = Math.Round(g.Y) + 0.5;
            _canvas.Children.Add(new Line
            {
                X1 = ActivityChartLayout.PadLeft, X2 = ActivityChartLayout.Width - ActivityChartLayout.PadRight,
                Y1 = y, Y2 = y, Stroke = GridBrush, StrokeThickness = 1,
            });
            if (g.Label.Length == 0) continue;
            // Right-aligned 6 px left of the plot, centred on the line.
            var label = Label(g.Label, TextAlignment.Right);
            label.Width = ActivityChartLayout.PadLeft - 6;
            Canvas.SetLeft(label, 0);
            Canvas.SetTop(label, g.Y - 7);
            _canvas.Children.Add(label);
        }

        foreach (var t in layout.Ticks)
        {
            var label = Label(t.Label, TextAlignment.Center);
            label.Width = 80;
            Canvas.SetLeft(label, t.X - 40);
            Canvas.SetTop(label, ActivityChartLayout.Height - 20);
            _canvas.Children.Add(label);
        }

        if (layout.Curve is { } curve)
        {
            var figure = new PathFigure { StartPoint = new Point(curve.Start.X, curve.Start.Y), IsFilled = false };
            foreach (var s in curve.Segments)
                figure.Segments.Add(new BezierSegment(
                    new Point(s.Control1.X, s.Control1.Y), new Point(s.Control2.X, s.Control2.Y), new Point(s.End.X, s.End.Y), true));
            var geometry = new PathGeometry([figure]);
            geometry.Freeze();
            _canvas.Children.Add(new Path
            {
                Data = geometry, Stroke = LineBrush, StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false,
            });
        }

        foreach (var d in layout.Dots)
        {
            var scale = new ScaleTransform(1, 1);
            var r = d.Tone is null ? DotRadius : TonedDotRadius;
            var dot = new Ellipse
            {
                Width = r * 2, Height = r * 2, Fill = DotFill(d.Tone), Stroke = DotStrokeBrush, StrokeThickness = 1,
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = scale, ToolTip = d.Label, Cursor = System.Windows.Input.Cursors.Hand,
            };
            ToolTipService.SetInitialShowDelay(dot, 0);
            dot.MouseEnter += (_, _) => { scale.ScaleX = DotHoverScale; scale.ScaleY = DotHoverScale; };
            dot.MouseLeave += (_, _) => { scale.ScaleX = 1; scale.ScaleY = 1; };
            Canvas.SetLeft(dot, d.X - r);
            Canvas.SetTop(dot, d.Y - r);
            _canvas.Children.Add(dot);
        }
    }

    private Brush? DotFill(HealthSeverity? tone) => tone switch
    {
        null => LineBrush,
        HealthSeverity.Ok => OkBrush,
        HealthSeverity.WarnSoft => WarnSoftBrush,
        HealthSeverity.Warn => WarnBrush,
        HealthSeverity.Danger => DangerBrush,
        _ => NoDataBrush,
    };

    private TextBlock Label(string text, TextAlignment alignment) => new()
    {
        Text = text,
        FontSize = 10,
        Foreground = LabelBrush,
        TextAlignment = alignment,
        IsHitTestVisible = false,
    };
}
