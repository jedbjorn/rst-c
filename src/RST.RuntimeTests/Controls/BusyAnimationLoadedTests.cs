// rst-c's own (not vendored). SC-045 (issue #163): Health crashed Revit when the Scan button's
// BusySpinner (collapsed while no scan runs) was Loaded before its template was built. The old styles
// started their storyboard from a ControlTemplate Loaded trigger with Storyboard.TargetName="Arc"/"Bar";
// without the template applied there is no Arc or Bar to name, and Storyboard threw
// InvalidOperationException inside ShowDialog. The animation now starts from the part's own Loaded,
// so it can only run once the part exists. These tests reproduce the early Loaded deterministically.

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShapePath = System.Windows.Shapes.Path;
using ShapeRectangle = System.Windows.Shapes.Rectangle;
using System.Windows.Threading;
using RST.Core.Theming;
using RST.RuntimeTests.Support;
using RST.UI.Theming;
using Xunit;

namespace RST.RuntimeTests.Controls;

[Collection(WpfThemeCollection.Name)]
public class BusyAnimationLoadedTests
{
    /// <summary>Each busy style and the template part its animation moves.</summary>
    public static TheoryData<string, string> Styles => new()
    {
        { "BusySpinner", "Arc" },
        { "BusyIndicator", "Bar" },
    };

    private static void Sta(Action action) => Screenshots.RunSta(() =>
    {
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            RstTheme.Apply(UiTheme.Light);
            action();
        }
        finally
        {
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    });

    private static Window ThemedWindow()
    {
        var window = new Window { Width = 200, Height = 120 };
        window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/RST.UI;component/Theme.xaml", UriKind.Relative) });
        return window;
    }

    private static ProgressBar Busy(Window window, string style) => new() { Style = (Style)window.FindResource(style) };

    [Theory]
    [MemberData(nameof(Styles))]
    public void Loaded_BeforeTheTemplateIsBuilt_DoesNotThrow(string style, string part) => Sta(() =>
    {
        var window = ThemedWindow();
        var bar = Busy(window, style);
        window.Content = bar;

        // No layout has run: the template is not applied, so the part does not exist yet.
        Assert.Null(bar.Template.FindName(part, bar));

        var thrown = Record.Exception(() => bar.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)));

        Assert.Null(thrown);
        window.Close();
    });

    [Theory]
    [MemberData(nameof(Styles))]
    public void Collapsed_InAShownWindow_DoesNotThrow_AndAnimatesOnceVisible(string style, string part) => Sta(() =>
    {
        var faults = new List<Exception>();
        Dispatcher.CurrentDispatcher.UnhandledException += (_, e) =>
        {
            faults.Add(e.Exception);
            e.Handled = true; // reported by the assertion below rather than taking the test host down
        };

        var window = ThemedWindow();
        var bar = Busy(window, style);
        bar.Visibility = Visibility.Collapsed;
        window.Content = new StackPanel { Children = { bar } };

        var thrown = Record.Exception(() => WindowCapture.ShowOffScreen(window));

        Assert.Null(thrown);
        Assert.Empty(faults);
        Assert.True(bar.IsLoaded);
        Assert.Null(bar.Template.FindName(part, bar)); // collapsed: still no template, the case that crashed

        bar.Visibility = Visibility.Visible;
        WindowCapture.DoEvents();
        window.UpdateLayout();
        WindowCapture.DoEvents();

        var element = Assert.IsAssignableFrom<FrameworkElement>(bar.Template.FindName(part, bar));
        var first = AnimatedValue(element, out var animated);
        Assert.True(animated, $"{style}: no animation is applied to {part}");
        Assert.True(PumpUntil(() => AnimatedValue(element, out _) != first, TimeSpan.FromSeconds(5)),
            $"{style}: {part} did not move (still {first})");
        Assert.Empty(faults);
        window.Close();
    });

    /// <summary>The animated value (the Arc's angle, the Bar's mask offset) and whether a clock drives it.</summary>
    private static double AnimatedValue(FrameworkElement element, out bool animated)
    {
        switch (element)
        {
            case ShapePath arc when arc.RenderTransform is RotateTransform rotate:
                animated = rotate.HasAnimatedProperties;
                return rotate.Angle;
            case ShapeRectangle rect when rect.OpacityMask is Brush { RelativeTransform: TranslateTransform slide }:
                animated = slide.HasAnimatedProperties;
                return slide.X;
            default:
                throw new InvalidOperationException($"{element.GetType().Name} is not a busy part this test knows");
        }
    }

    /// <summary>Lets the dispatcher (and the animation clock) run until <paramref name="condition"/> holds.</summary>
    private static bool PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (condition()) return true;
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Background,
                (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
            timer.Start();
            Dispatcher.PushFrame(frame);
            timer.Stop();
        }
        return condition();
    }
}
