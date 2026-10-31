// rst-c's own (not vendored): the Health window rendered off-screen in its main states, light and
// dark (spec #16, WU2, AC3/AC4), with a scripted service and realistic data. Each test also checks
// what it shows (the visible tab, the overlay, the severity dot colors, the chart dots), so a
// broken template fails here as well as in review. The Activity captures (spec #24, Revit Health)
// cover the Activity (Session) / (Total) cards, the four charts with the Health trend's coloured
// dots, the 6M weekly view, the single-dot and empty states and "No model open". The PNGs land in TestResults/screenshots; CI
// uploads them as RST-screenshots.

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using RST.Core.Health;
using RST.Core.Theming;
using RST.RuntimeTests.Support;
using RST.UI.Controls;
using RST.UI.Health;
using RST.UI.Theming;
using Xunit;

namespace RST.RuntimeTests.Health;

[Collection(WpfThemeCollection.Name)]
public class HealthWindowScreenshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 14, 0, 0, TimeSpan.Zero);

    /// <summary>The window's logical size (spec #16, Planner default 2).</summary>
    private static readonly Size WindowSize = new(900, 800);

    public static TheoryData<UiTheme> Themes => new() { UiTheme.Light, UiTheme.Dark };

    private static string Name(string what, UiTheme theme) => $"{what}-{theme.ToString().ToLowerInvariant()}";

    private static Color Palette(UiTheme theme, string key)
    {
        var c = UiPalette.For(theme)[key];
        return Color.FromRgb(c.R, c.G, c.B);
    }

    private static void Sta(Action action) => Screenshots.RunSta(() =>
    {
        var culture = CultureInfo.GetCultureInfo("en-US");
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            action();
        }
        finally
        {
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    });

    private sealed class NoTimer : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private static HealthViewModel ViewModel(FakeHealthService service, FakeDialogs? dialogs = null, Func<Action, Task>? runner = null) =>
        new(service, dialogs ?? new FakeDialogs(), new HealthViewModelOptions
        {
            RunInBackground = runner ?? (work =>
            {
                work();
                return Task.CompletedTask;
            }),
            Now = () => Now,
            Culture = CultureInfo.GetCultureInfo("en-US"),
            Zone = TimeZoneInfo.Utc,
            ToastSchedule = (_, _) => new NoTimer(),
        });

    /// <summary>Shows the window off-screen, renders it (at <paramref name="size"/>, or 900 wide and as tall as its content), saves, checks the corners.</summary>
    private static BitmapSource Capture(HealthWindow window, UiTheme theme, string name, Size? size = null)
    {
        WindowCapture.ShowOffScreen(window);
        WindowCapture.DoEvents();
        var target = size ?? FullHeight(window);
        var bitmap = WindowCapture.Content(window, target);
        Screenshots.Save(bitmap, Name(name, theme));
        Assert.True(Screenshots.DistinctPixels(bitmap) > 40, $"{name} looks blank");
        return bitmap;
    }

    /// <summary>900 wide and tall enough that nothing scrolls (the real window is 800 tall and scrolls).</summary>
    private static Size FullHeight(HealthWindow window)
    {
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(WindowSize.Width, double.PositiveInfinity));
        return new Size(WindowSize.Width, Math.Max(WindowSize.Height, Math.Ceiling(root.DesiredSize.Height)));
    }

    private static Color At(BitmapSource bitmap, Visual element, Visual root, Point p)
    {
        var q = element.TransformToAncestor(root).Transform(p);
        return Screenshots.PixelAt(bitmap, (int)Math.Floor(q.X), (int)Math.Floor(q.Y));
    }

    private static int Distance(Color a, Color b) =>
        Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));

    private static void AssertColor(Color expected, Color actual, string what) =>
        Assert.True(Distance(expected, actual) <= 3, $"{what}: expected {expected}, rendered {actual}");

    private static void AssertWindowCorners(BitmapSource bitmap, UiTheme theme)
    {
        // The footer and header paint the surface color across the window's width; the window
        // color shows at the left edge of the content area.
        AssertColor(Palette(theme, UiPalette.Surface), Screenshots.PixelAt(bitmap, 2, 2), "header");
        AssertColor(Palette(theme, UiPalette.Surface), Screenshots.PixelAt(bitmap, 2, bitmap.PixelHeight - 3), "footer");
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void HealthTab_AllSectionsExpanded_MixedSeverities(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var vm = ViewModel(new FakeHealthService { Snapshot = HealthSamples.Snapshot() });
        vm.Load();
        foreach (var s in vm.Sections) s.IsExpanded = true;
        var window = new HealthWindow(vm);
        try
        {
            var bitmap = Capture(window, theme, "health-sections");
            AssertWindowCorners(bitmap, theme);
            Assert.True(bitmap.PixelHeight > 900, $"every row should show: {bitmap.PixelHeight}");

            // The section dots: Hardware danger (RAM 88 %), Display & Network info, Revit warn (612 warnings), OS none.
            var dots = WindowCapture.Descendants<Expander>(window)
                .ToDictionary(e => ((HealthSection)e.DataContext).Title, e => WindowCapture.Descendants<Ellipse>(e).First());
            var root = (Visual)window.Content;
            AssertColor(Palette(theme, UiPalette.Error), At(bitmap, dots["Hardware"], root, new Point(4, 4)), "Hardware dot");
            AssertColor(Palette(theme, UiPalette.AccentForeground), At(bitmap, dots["Display & Network"], root, new Point(4, 4)), "Display dot");
            AssertColor(Palette(theme, UiPalette.WarningText), At(bitmap, dots["Revit"], root, new Point(4, 4)), "Revit dot");
            Assert.Equal(Visibility.Collapsed, dots["Operating System"].Visibility);
            Assert.Equal(4, WindowCapture.Descendants<Expander>(window).Count(e => e.IsExpanded));
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void HealthTab_NoSnapshot_EveryValueADash(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var vm = ViewModel(new FakeHealthService());
        vm.Load();
        foreach (var s in vm.Sections) s.IsExpanded = true;
        var window = new HealthWindow(vm);
        try
        {
            var bitmap = Capture(window, theme, "health-empty");
            AssertWindowCorners(bitmap, theme);
            var values = WindowCapture.Descendants<TextBlock>(window)
                .Where(t => System.Windows.Automation.AutomationProperties.GetAutomationId(t) == "Health.RowValue")
                .Select(t => t.Text).ToList();
            Assert.Equal(16, values.Count);
            Assert.Equal(15, values.Count(v => v == "—"));
            Assert.Contains("Unknown", values);
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void HealthTab_Scanning(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var pending = new TaskCompletionSource();
        var vm = ViewModel(new FakeHealthService { Snapshot = HealthSamples.Healthy() }, runner: _ => pending.Task);
        vm.Load();
        vm.Sections[0].IsExpanded = true;
        _ = vm.ScanAsync();
        var window = new HealthWindow(vm);
        try
        {
            Assert.True(vm.IsScanning);
            var bitmap = Capture(window, theme, "health-scanning", WindowSize);
            AssertWindowCorners(bitmap, theme);
            Assert.False(window.ScanButton.IsEnabled);
            Assert.False(window.CleanButton.IsEnabled);
            Assert.False(window.CloseButton.IsEnabled);
            Assert.False(window.Tabs.IsEnabled);
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void JunkModal_EveryTargetOn(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var vm = ViewModel(new FakeHealthService { Snapshot = HealthSamples.Snapshot(), Targets = HealthSamples.Targets() });
        vm.Load();
        vm.OpenJunk();
        var window = new HealthWindow(vm);
        try
        {
            var bitmap = Capture(window, theme, "junk-modal", WindowSize);
            Assert.Equal(Visibility.Visible, window.JunkOverlay.Visibility);
            Assert.False(window.Main.IsEnabled);
            var toggles = WindowCapture.Descendants<CheckBox>(window.JunkOverlay).ToList();
            Assert.Equal(3, toggles.Count);
            Assert.All(toggles, t => Assert.True(t.IsChecked));
            // The scrim darkens the window behind the card.
            Assert.True(Screenshots.PixelAt(bitmap, 2, 400).R < Palette(theme, UiPalette.Window).R, "no scrim");
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void JunkModal_NoTargets_EmptyState(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var vm = ViewModel(new FakeHealthService { Snapshot = HealthSamples.Snapshot() });
        vm.Load();
        vm.OpenJunk();
        var window = new HealthWindow(vm);
        try
        {
            Capture(window, theme, "junk-modal-empty", WindowSize);
            Assert.Empty(WindowCapture.Descendants<CheckBox>(window.JunkOverlay));
            Assert.Contains(WindowCapture.Descendants<TextBlock>(window.JunkOverlay), t => t.Text == "No cleanup targets configured");
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void Cleaning_BusyOverlay(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var pending = new TaskCompletionSource();
        var vm = ViewModel(new FakeHealthService { Snapshot = HealthSamples.Snapshot(), Targets = HealthSamples.Targets() },
            new FakeDialogs { ConfirmResult = true }, _ => pending.Task);
        vm.Load();
        vm.OpenJunk();
        _ = vm.RunJunkAsync();
        var window = new HealthWindow(vm);
        try
        {
            Assert.True(vm.Busy.IsBusy);
            Capture(window, theme, "health-cleaning", WindowSize);
            Assert.Equal(Visibility.Visible, window.BusyOverlay.Visibility);
            Assert.False(window.Main.IsEnabled);
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void CleanupResult_Toast(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var vm = ViewModel(new FakeHealthService
        {
            Snapshot = HealthSamples.Snapshot(),
            Targets = HealthSamples.Targets(),
            CleanResult = HealthSamples.CleanResult(),
        }, new FakeDialogs { ConfirmResult = true });
        vm.Load();
        vm.OpenJunk();
        vm.RunJunkAsync().GetAwaiter().GetResult();
        var window = new HealthWindow(vm);
        try
        {
            Capture(window, theme, "cleanup-result", WindowSize);
            Assert.Equal(Visibility.Visible, window.Toast.Visibility);
            Assert.Equal("Cleanup complete", vm.Toast.Title);
            Assert.Equal(3, vm.Toast.Text.Split('\n').Length);
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>The window's charts by card: Active time, Opening time, Sync history, Health.</summary>
    private static Dictionary<string, ActivityChart> ChartsByCard(HealthWindow window) =>
        WindowCapture.Descendants<Border>(window)
            .Where(b => b.DataContext is ChartCard && System.Windows.Automation.AutomationProperties.GetAutomationId(b).StartsWith("Health.Chart.", StringComparison.Ordinal))
            .ToDictionary(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b), b => WindowCapture.Descendants<ActivityChart>(b).Single());

    /// <summary>Each Health trend dot is drawn in its band's colour: Health's four severity colours, grey for "not enough data".</summary>
    private static void AssertTrendColours(BitmapSource bitmap, HealthWindow window, ActivityChart trend, UiTheme theme)
    {
        var keys = new Dictionary<HealthSeverity, string>
        {
            [HealthSeverity.Ok] = UiPalette.Success,
            [HealthSeverity.WarnSoft] = UiPalette.CautionText,
            [HealthSeverity.Warn] = UiPalette.WarningText,
            [HealthSeverity.Danger] = UiPalette.Error,
            [HealthSeverity.None] = UiPalette.MutedText,
        };
        var root = (Visual)window.Content;
        var ellipses = trend.Surface.Children.OfType<Ellipse>().ToList();
        Assert.Equal(trend.Layout!.Dots.Count, ellipses.Count);
        for (var i = 0; i < ellipses.Count; i++)
        {
            var tone = trend.Layout.Dots[i].Tone!.Value;
            Assert.Equal(ActivityChart.TonedDotRadius * 2, ellipses[i].Width);
            AssertColor(Palette(theme, keys[tone]), At(bitmap, ellipses[i], root, new Point(ActivityChart.TonedDotRadius, ActivityChart.TonedDotRadius)),
                $"trend dot {i} ({tone})");
            Assert.DoesNotMatch(@"\d+(\.\d+)?\s*(%|points?|score)", (string)ellipses[i].ToolTip);
        }
        // No numeric axis: the band edges are lines without labels; the only text is the date ticks.
        Assert.Equal(3, trend.Surface.Children.OfType<Line>().Count());
        Assert.Equal(trend.Layout.Ticks.Select(t => t.Label), trend.Surface.Children.OfType<TextBlock>().Select(t => t.Text));
        Assert.Empty(trend.Surface.Children.OfType<Path>());
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void ActivityTab_SevenDays_CardsAndFourCharts(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var vm = ViewModel(new FakeHealthService
        {
            Snapshot = HealthSamples.Snapshot(),
            Activity = () => HealthSamples.Activity(Now),
        });
        vm.Load();
        vm.SelectedTab = HealthViewModel.ActivityTab;
        var window = new HealthWindow(vm);
        try
        {
            var bitmap = Capture(window, theme, "activity-curves");
            AssertWindowCorners(bitmap, theme);
            Assert.Equal(Visibility.Collapsed, window.HealthPane.Visibility);
            Assert.Equal(Visibility.Visible, window.ActivityPane.Visibility);
            Assert.True(window.CollectionToggle.IsChecked);

            var charts = ChartsByCard(window);
            Assert.Equal(["Health.Chart.Active", "Health.Chart.Open", "Health.Chart.Sync", "Health.Chart.Health"], charts.Keys);
            var root = (Visual)window.Content;
            foreach (var key in new[] { "Health.Chart.Active", "Health.Chart.Open", "Health.Chart.Sync" })
            {
                var chart = charts[key];
                Assert.Equal(ActivityChartLayout.Width, chart.Surface.Width);
                Assert.Equal(ActivityChartLayout.Height, chart.Surface.Height);
                Assert.Single(chart.Surface.Children.OfType<Path>());
                Assert.Equal(4, chart.Surface.Children.OfType<Line>().Count());
                var dot = chart.Surface.Children.OfType<Ellipse>().First();
                Assert.False(string.IsNullOrEmpty(dot.ToolTip as string));
                AssertColor(Palette(theme, UiPalette.AccentForeground), At(bitmap, dot, root, new Point(3, 3)), $"{key} dot");
            }
            AssertTrendColours(bitmap, window, charts["Health.Chart.Health"], theme);
            Assert.Equal(6, charts["Health.Chart.Health"].Layout!.Dots.Count);

            // Six stat cards with their automation ids, the 7D pill selected.
            Assert.Equal(["Activity (Session)", "Activity (Total)", "Longest Sync", "Longest Open Time", "Warning Count", "File Size"],
                vm.StatCards.Select(c => c.Label));
            var cardIds = WindowCapture.Descendants<Border>(window).Where(b => b.DataContext is StatCard)
                .Select(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b)).Where(id => id.Length > 0).ToList();
            Assert.Equal(["Health.Stat.ActivitySession", "Health.Stat.ActivityTotal", "Health.Stat.LongestSync", "Health.Stat.LongestOpen",
                "Health.Stat.Warnings", "Health.Stat.FileSize"], cardIds);
            var pills = WindowCapture.Descendants<RadioButton>(window).ToList();
            Assert.True(pills[0].IsChecked);
            Assert.Equal("Health.Range.7D", System.Windows.Automation.AutomationProperties.GetAutomationId(pills[0]));
            var texts = WindowCapture.Descendants<TextBlock>(window).Where(t => t.IsVisible).Select(t => t.Text).ToList();
            Assert.Contains("47m", texts);
            Assert.Contains("opened 12:56 PM", texts);
            Assert.Contains("Health — this file", texts);
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void ActivityTab_SixMonths_WeeklyPoints(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var vm = ViewModel(new FakeHealthService
        {
            Snapshot = HealthSamples.Snapshot(),
            Activity = () => HealthSamples.Activity(Now),
        });
        vm.Load();
        vm.SelectedTab = HealthViewModel.ActivityTab;
        vm.SelectRange(180);
        var window = new HealthWindow(vm);
        try
        {
            var bitmap = Capture(window, theme, "activity-weekly");
            AssertWindowCorners(bitmap, theme);
            Assert.True(WindowCapture.Descendants<RadioButton>(window).Last().IsChecked);
            var charts = ChartsByCard(window);
            Assert.Equal(26, charts["Health.Chart.Active"].Layout!.Dots.Count);
            Assert.All(charts["Health.Chart.Active"].Layout!.Dots, d => Assert.StartsWith("Week of ", d.Label, StringComparison.Ordinal));
            Assert.All(charts["Health.Chart.Open"].Layout!.Dots, d => Assert.Contains(" average of ", d.Label, StringComparison.Ordinal));
            AssertTrendColours(bitmap, window, charts["Health.Chart.Health"], theme);
            var tones = charts["Health.Chart.Health"].Layout!.Dots.Select(d => d.Tone).ToList();
            Assert.Equal(HealthSeverity.Danger, tones[0]);
            Assert.Equal(HealthSeverity.Ok, tones[^1]);
            Assert.Contains(WindowCapture.Descendants<TextBlock>(window), t => t.Text == "133h 36m");
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void ActivityTab_SingleDotAndEmptyStates_CollectionOff(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var vm = ViewModel(new FakeHealthService
        {
            Snapshot = HealthSamples.Snapshot(),
            Activity = () => HealthSamples.SparseActivity(Now),
        });
        vm.Load();
        vm.SelectedTab = HealthViewModel.ActivityTab;
        var window = new HealthWindow(vm);
        try
        {
            Capture(window, theme, "activity-sparse", WindowSize);
            Assert.False(window.CollectionToggle.IsChecked);
            var visible = WindowCapture.Descendants<ActivityChart>(window).Where(c => c.IsVisible).ToList();
            Assert.Equal(2, visible.Count); // Opening time and Health: one dot each
            Assert.All(visible, c =>
            {
                Assert.Single(c.Surface.Children.OfType<Ellipse>());
                Assert.Empty(c.Surface.Children.OfType<Path>());
            });
            var texts = WindowCapture.Descendants<TextBlock>(window).Where(t => t.IsVisible).Select(t => t.Text).ToList();
            Assert.Contains("No time recorded in this range", texts);
            Assert.Equal(2, texts.Count(t => t == "not enough history yet"));
            Assert.Contains("Not a workshared model", texts);
            Assert.Contains("collection off · 0 files · 0 B · since 10/30/2026", texts);
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void ActivityTab_NoModelOpen(UiTheme theme) => Sta(() =>
    {
        RstTheme.Apply(theme);
        var vm = ViewModel(new FakeHealthService
        {
            Snapshot = HealthSamples.Snapshot(),
            Activity = HealthSamples.NoModel,
        });
        vm.Load();
        vm.SelectedTab = HealthViewModel.ActivityTab;
        var window = new HealthWindow(vm);
        try
        {
            Capture(window, theme, "activity-no-model", WindowSize);
            Assert.DoesNotContain(WindowCapture.Descendants<ActivityChart>(window), c => c.IsVisible);
            var texts = WindowCapture.Descendants<TextBlock>(window).Where(t => t.IsVisible).Select(t => t.Text).ToList();
            Assert.Equal(4, texts.Count(t => t == "No model open"));
            Assert.Equal(6, WindowCapture.Descendants<TextBlock>(window)
                .Count(t => System.Windows.Automation.AutomationProperties.GetAutomationId(t) == "Health.StatValue" && t.Text == "—"));
            Assert.Contains("collection on · 23 files · 1.4 MB", texts);
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void ActivityTab_SmallValues_DistinctAxisLabels(UiTheme theme) => Sta(() =>
    {
        // VM AC9 finding: small values read "0.1h, 0.1h, 0h, 0h" on the HTML's axis; each label is now distinct.
        RstTheme.Apply(theme);
        var vm = ViewModel(new FakeHealthService
        {
            Snapshot = HealthSamples.Snapshot(),
            Activity = () => HealthSamples.SmallActivity(Now),
        });
        vm.Load();
        vm.SelectedTab = HealthViewModel.ActivityTab;
        var window = new HealthWindow(vm);
        try
        {
            Capture(window, theme, "activity-small");
            var charts = ChartsByCard(window);
            foreach (var key in new[] { "Health.Chart.Active", "Health.Chart.Open", "Health.Chart.Sync" })
            {
                var labels = charts[key].Layout!.GridLines.Select(g => g.Label).ToList();
                Assert.Equal(4, labels.Distinct().Count());
                Assert.Single(charts[key].Surface.Children.OfType<Path>());
            }
            Assert.Equal(["7m", "4m", "2m", "0m"], charts["Health.Chart.Active"].Layout!.GridLines.Select(g => g.Label));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void HealthWindow_HasNoTaskbarEntryOfItsOwn() => Sta(() =>
    {
        // VM AC9 finding: the window is owned by Revit and stays off the taskbar, like MessageDialog and ColorPickerDialog.
        RstTheme.Apply(UiTheme.Light);
        var window = new HealthWindow(ViewModel(new FakeHealthService()));
        try
        {
            Assert.False(window.ShowInTaskbar);
            Assert.False(new RST.UI.Dialogs.MessageDialog(
                new RST.UI.Dialogs.MessageDialogModel("t", "t", "m", []), RST.UI.Dialogs.MessageDialog.MessageId).ShowInTaskbar);
        }
        finally
        {
            window.Close();
        }
    });
}
