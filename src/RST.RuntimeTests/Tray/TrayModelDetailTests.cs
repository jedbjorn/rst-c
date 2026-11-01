// rst-c's own (not vendored): the tray's model detail (spec #25 Model Detail; F19 #25 WU4), over a fake read and fake
// thumbnails. The view-model: the range it opens on, the off-thread read and its loading state, a stale read dropped,
// the 1Y pill, Back, a gone model and a failed read, the header facts. AC 4 (unit level): for the same model, range and
// data, the tray's cards and chart points equal Revit's Activity tab's (the add-in's HealthViewModel), 1Y against the
// same panel with Revit's Activity (Session). The window (AC 3): the detail in place of the list, light and dark
// (tray-health-detail-*); Back and Escape return to the list as it was, and Escape does not close the tray window.

using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RST.Core.Health;
using RST.Core.Telemetry.Rollups;
using RST.Core.Theming;
using RST.Core.Tray;
using RST.RuntimeTests.Health;
using RST.RuntimeTests.Support;
using RST.Tray.Windows;
using RST.UI.Health;
using RST.UI.Theming;
using Xunit;

namespace RST.RuntimeTests.Tray;

/// <summary>Riverside Tower's history the way ReadModel returns it: the Health window's six-month sample, the last ten days in Revit 2027.1.</summary>
internal static class DetailSamples
{
    public static readonly DateTimeOffset Now = new(2026, 10, 31, 15, 0, 0, TimeSpan.Zero);

    public static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");

    public static ModelActivity Tower() => Copy(HealthSamples.Activity(Now).Model!, "riverside-tower",
        @"C:\Users\jb\Documents\Riverside Tower_jb.rvt", @"\\fs01\projects\RT\Riverside Tower.rvt", cloud: false);

    /// <summary><paramref name="m"/> as the tray reads it: no live session; identity and last known stats from the rollup.</summary>
    public static ModelActivity Copy(ModelActivity m, string id, string? local, string? central, bool cloud)
    {
        foreach (var d in m.Days.Where(d => d.Date > ModelsSamples.Today.AddDays(-10)))
        {
            d.RevitVersions = ["2027"];
            d.RevitSubversions = ["2027.1"];
        }
        return new ModelActivity
        {
            MatchedKeyKind = m.MatchedKeyKind,
            ModelIds = [id],
            Days = m.Days,
            Opens = m.Opens,
            Syncs = m.Syncs,
            LastStats = new ModelStatsPoint(new DateTimeOffset(2026, 10, 28, 16, 40, 0, TimeSpan.Zero), 612, (long)(1740.5 * 1024 * 1024)),
            CurrentHealth = m.Days.Last(d => d.Health is not null).Health,
            Identity = new RollupIdentity { LocalPath = local, CentralPath = central, IsWorkshared = true, IsCloud = cloud },
            Zone = TimeZoneInfo.Utc,
        };
    }

    /// <summary>The sample model's card as the list builds it (found by an all-time search on its title's first word).</summary>
    public static ModelCard Card(string id) => ModelListIndex.From(ModelsSamples.Rows())
        .Query(ModelListQuery.Default with { Search = id.Split('-')[0] }, ModelsSamples.Today).Cards
        .First(c => c.ModelId == id);
}

internal sealed class FakeActivitySource : IModelActivitySource
{
    public Dictionary<string, Func<ModelActivity>> Models { get; } = new(StringComparer.Ordinal);

    public List<string> Reads { get; } = new();

    public Exception? Throw { get; set; }

    public ModelActivity Read(string modelId)
    {
        Reads.Add(modelId);
        if (Throw is { } ex) throw ex;
        return Models.TryGetValue(modelId, out var m) ? m() : ModelActivity.Empty;
    }

    public static FakeActivitySource Samples()
    {
        var s = new FakeActivitySource();
        s.Models["riverside-tower"] = DetailSamples.Tower;
        s.Models["civic-center"] = () => DetailSamples.Copy(HealthSamples.SmallActivity(DetailSamples.Now).Model!, "civic-center",
            @"C:\Users\jb\AppData\Local\Autodesk\Revit\PacCache\civic.rvt", "Autodesk Docs://Civic/Phase 2.rvt", cloud: true);
        return s;
    }
}

internal static class DetailHarness
{
    /// <summary>A detail over <paramref name="source"/>, posts inline, today fixed; reads inline unless <paramref name="queue"/> holds them.</summary>
    public static ModelDetailViewModel Detail(IModelActivitySource source, List<Action>? queue = null, FakeThumbnails? thumbnails = null) =>
        new(new ModelDetailViewModelOptions
        {
            Source = source,
            Thumbnails = thumbnails,
            RunInBackground = work =>
            {
                if (queue is null) work();
                else queue.Add(work);
                return Task.CompletedTask;
            },
            Post = a => a(),
            Today = () => ModelsSamples.Today,
            Culture = DetailSamples.EnUs,
            Zone = TimeZoneInfo.Utc,
        });

    /// <summary>A chart card as comparable text: its empty state, then its grid, ticks and dots.</summary>
    public static string Signature(ChartCard c) =>
        c.EmptyTitle + "|" + c.EmptyHint + "|" + (c.Chart is not { } l ? "none" :
            l.Mode + "|" + string.Join(";", l.GridLines) + "|" + string.Join(";", l.Ticks) + "|" + string.Join(";", l.Dots));
}

public class TrayModelDetailViewModelTests
{
    private static ModelCard Tower => DetailSamples.Card("riverside-tower");

    [Fact]
    public void Opens_on_the_lists_range_reads_off_the_UI_thread_then_shows_five_cards_without_Activity_Session()
    {
        var source = FakeActivitySource.Samples();
        var queue = new List<Action>();
        var detail = DetailHarness.Detail(source, queue);

        detail.Open(Tower, ModelDetailRange.OneMonth, null);

        Assert.True(detail.IsOpen);
        Assert.True(detail.IsLoading);
        Assert.False(detail.HasContent);
        Assert.Empty(source.Reads);   // queued for the background
        Assert.Equal("1M", detail.Range.Label);
        Assert.True(detail.Ranges.Single(r => r.Label == "1M").IsSelected);
        Assert.Single(detail.Ranges, r => r.IsSelected);

        queue.Single()();

        Assert.False(detail.IsLoading);
        Assert.Equal(new[] { "riverside-tower" }, source.Reads);
        Assert.Null(detail.Panel.ActivitySession);
        Assert.Equal(new[] { "Health.Stat.ActivityTotal", "Health.Stat.LongestSync", "Health.Stat.LongestOpen", "Health.Stat.Warnings", "Health.Stat.FileSize" },
            detail.Panel.StatCards.Select(c => c.AutomationKey));
        Assert.NotEqual(HealthFormat.Missing, detail.Panel.ActivityTotal.Value);
        Assert.Equal("612", detail.Panel.WarningCount.Value);
        Assert.Equal("As of Oct 28, 2026", detail.Panel.WarningCount.Sub);
        Assert.Equal("As of Oct 28, 2026", detail.Panel.FileSize.Sub);
        Assert.Equal(new[] { "Health.Chart.Active", "Health.Chart.Open", "Health.Chart.Sync", "Health.Chart.Health" },
            detail.Panel.Charts.Select(c => c.AutomationKey));
        Assert.All(detail.Panel.Charts, c => Assert.True(c.HasChart, c.Title));
    }

    [Theory]
    [InlineData(ModelDetailRange.OneMonth, "1M")]
    [InlineData(ModelDetailRange.ThreeMonths, "3M")]
    [InlineData(ModelDetailRange.SixMonths, "6M")]
    [InlineData(ModelDetailRange.OneYear, "1Y")]
    public void The_start_range_selects_its_pill(ModelDetailRange start, string pill)
    {
        var detail = DetailHarness.Detail(FakeActivitySource.Samples());
        detail.Open(Tower, start, null);
        Assert.Equal(pill, detail.Range.Label);
        Assert.Equal(pill, detail.Ranges.Single(r => r.IsSelected).Label);
    }

    [Fact]
    public void The_pills_are_7D_to_1Y_and_a_pill_re_renders_the_same_read_weekly_at_6M_and_1Y()
    {
        var source = FakeActivitySource.Samples();
        var detail = DetailHarness.Detail(source);
        detail.Open(Tower, ModelDetailRange.OneMonth, null);
        Assert.Equal(new[] { "Health.Range.7D", "Health.Range.1M", "Health.Range.3M", "Health.Range.6M", "Health.Range.1Y" },
            detail.Ranges.Select(r => r.AutomationKey));
        var month = detail.Panel.ActiveChart.Chart!.Dots.Count;

        detail.Ranges.Single(r => r.Label == "1Y").IsSelected = true; // the user's click

        Assert.Equal("1Y", detail.Range.Label);
        Assert.Single(detail.Ranges, r => r.IsSelected);
        Assert.Single(source.Reads);                                  // no second read
        Assert.InRange(detail.Panel.ActiveChart.Chart!.Dots.Count, 50, 53); // one per ISO week
        Assert.NotEqual(month, detail.Panel.ActiveChart.Chart!.Dots.Count);
        Assert.Contains("Week of", detail.Panel.ActiveChart.Chart!.Dots[^1].Label);
    }

    [Fact]
    public void Back_closes_it_and_a_read_that_ends_after_Back_or_after_another_model_opened_is_dropped()
    {
        var source = FakeActivitySource.Samples();
        var queue = new List<Action>();
        var detail = DetailHarness.Detail(source, queue);
        var backs = 0;
        detail.BackRequested += (_, _) => backs++;

        detail.Open(Tower, ModelDetailRange.OneMonth, null);
        detail.BackCommand.Execute(null);
        Assert.False(detail.IsOpen);
        Assert.False(detail.IsLoading);
        Assert.Equal(1, backs);
        queue[0]();
        Assert.Null(detail.Activity);
        detail.Back(); // closed already: nothing
        Assert.Equal(1, backs);

        // Two opens in a row: only the second model's read shows.
        detail.Open(Tower, ModelDetailRange.OneMonth, null);
        detail.Open(DetailSamples.Card("civic-center"), ModelDetailRange.OneMonth, null);
        queue[1]();
        Assert.Null(detail.Activity);
        Assert.True(detail.IsLoading);
        queue[2]();
        Assert.Equal("civic-center", detail.Activity!.ModelIds.Single());
        Assert.Equal("Civic Center — Phase 2", detail.Title);
    }

    [Fact]
    public void A_model_whose_history_is_gone_says_so_on_every_card_and_chart()
    {
        var detail = DetailHarness.Detail(new FakeActivitySource()); // reads Empty: no rollup, nothing unfolded
        detail.Open(Tower, ModelDetailRange.OneMonth, null);

        Assert.False(detail.IsLoading);
        Assert.Null(detail.Activity);
        Assert.All(detail.Panel.StatCards, c => Assert.Equal(HealthFormat.Missing, c.Value));
        Assert.All(detail.Panel.Charts, c =>
        {
            Assert.False(c.HasChart);
            Assert.Equal("This model's history is no longer on this PC: its activity file was deleted or could not be read.", c.EmptyHint);
        });
        Assert.Equal("Riverside Tower", detail.Title); // the header still says which model
    }

    [Fact]
    public void A_failed_read_shows_why_and_does_not_throw()
    {
        var detail = DetailHarness.Detail(new FakeActivitySource { Throw = new IOException("The disk is not ready") });
        detail.Open(Tower, ModelDetailRange.OneMonth, null);

        Assert.False(detail.IsLoading);
        Assert.All(detail.Panel.Charts, c => Assert.Equal("The disk is not ready", c.EmptyHint));
        Assert.All(detail.Panel.StatCards, c => Assert.Equal(HealthFormat.Missing, c.Value));
    }

    [Fact]
    public void The_header_names_the_paths_versions_kind_and_health()
    {
        var detail = DetailHarness.Detail(FakeActivitySource.Samples());
        detail.Open(Tower, ModelDetailRange.OneMonth, null);

        Assert.Equal("Riverside Tower", detail.Title);
        Assert.Equal("Riverside Tower_jb.rvt", detail.FileName);
        Assert.Equal(@"C:\Users\jb\Documents\Riverside Tower_jb.rvt", detail.LocalPath);
        Assert.Equal(@"\\fs01\projects\RT\Riverside Tower.rvt", detail.CentralPath);
        Assert.Equal("Central model", detail.CentralLabel);
        Assert.Equal("Opened in Revit 2027.1, 2026.4", detail.Versions);
        Assert.Equal("Workshared", detail.Marker);
        Assert.Equal(ModelHealthColour.Green, detail.Health);
        Assert.Equal("Green", detail.HealthName);
        Assert.Equal(HealthSeverity.Ok, detail.Dot);

        detail.Open(DetailSamples.Card("civic-center"), ModelDetailRange.OneMonth, null);
        Assert.Equal("Cloud", detail.Marker);
        Assert.Equal("Cloud model", detail.CentralLabel);
        Assert.Null(detail.FileName); // a cloud model's local file is a cache
        Assert.Equal("Autodesk Docs://Civic/Phase 2.rvt", detail.CentralPath);
    }

    [Fact]
    public void The_header_shows_the_cards_thumbnail_then_the_workers_answer_for_this_model_only() => Screenshots.RunSta(() =>
    {
        var thumbnails = new FakeThumbnails();
        var detail = DetailHarness.Detail(FakeActivitySource.Samples(), thumbnails: thumbnails);
        var first = ModelsSamples.Picture(1);
        var second = ModelsSamples.Picture(2);

        detail.Open(Tower, ModelDetailRange.OneMonth, first);
        Assert.Same(first, detail.Thumbnail);
        detail.RequestThumbnail();
        Assert.Equal(new[] { "riverside-tower" }, thumbnails.Requested);

        thumbnails.Complete("harbor-library", second); // another model's answer
        Assert.Same(first, detail.Thumbnail);
        thumbnails.Complete("riverside-tower", null);  // no shell thumbnail: the card's image stays
        Assert.Same(first, detail.Thumbnail);
        thumbnails.Complete("riverside-tower", second);
        Assert.Same(second, detail.Thumbnail);
        Assert.True(detail.HasThumbnail);

        detail.Dispose();
        thumbnails.Complete("riverside-tower", first);
        Assert.Same(second, detail.Thumbnail);
        Assert.False(thumbnails.Disposed); // the Models list owns it
    });

    [Fact]
    public void The_Health_page_opens_a_card_into_the_detail_and_Back_returns_to_the_list()
    {
        var models = ModelsHarness.Shown(new FakeModelSource(ModelsSamples.Rows), thumbnails: new FakeThumbnails());
        var detail = DetailHarness.Detail(FakeActivitySource.Samples());
        var health = new TrayHealthViewModel(NoHealthService.Instance, new FakeDialogs(), TrayHealthSamples.Options(), models, detail);
        var scrolls = new List<double>();
        var focused = new List<string>();
        models.ScrollRequested += (_, o) => scrolls.Add(o);
        models.FocusCardRequested += (_, id) => focused.Add(id);

        models.Chip = ModelTimeChip.SixMonths;
        models.ScrollOffset = 87;
        models.OpenDetailCommand.Execute(ModelsHarness.Cards(models).Single(c => c.ModelId == "riverside-tower"));

        Assert.True(health.Detail.IsOpen);
        Assert.Equal("riverside-tower", health.Detail.ModelId);
        Assert.Equal("6M", health.Detail.Range.Label);

        health.Detail.Back();

        Assert.False(health.Detail.IsOpen);
        Assert.Equal(87, scrolls.Last());
        Assert.Equal("riverside-tower", focused.Single());
        Assert.Equal(ModelTimeChip.SixMonths, models.Chip);
        health.Dispose();
    }
}

/// <summary>AC 4 at unit level: the same model, range and data give the same numbers in the tray and in Revit.</summary>
public class TrayModelDetailMatchesRevitTests
{
    private static HealthViewModel Revit(ModelActivity model) => new(
        new FakeHealthService
        {
            Activity = () => new ActivityData
            {
                Model = model,
                IsWorkshared = true,
                IsCloud = false,
                WarningsCount = model.LastStats!.WarningsCount,
                FileSizeBytes = model.LastStats.FileSizeBytes,
                Status = new TelemetryStatus(true, 0, 0),
            },
        },
        new FakeDialogs(),
        new HealthViewModelOptions
        {
            RunInBackground = work =>
            {
                work();
                return Task.CompletedTask;
            },
            Now = () => DetailSamples.Now,
            Culture = DetailSamples.EnUs,
            Zone = TimeZoneInfo.Utc,
            ToastSchedule = (_, _) => new NoTimer(),
        });

    private static void SameNumbers(ModelActivityPanel tray, ModelActivityPanel revit, string range)
    {
        foreach (var (t, r) in new[]
                 {
                     (tray.ActivityTotal, revit.ActivityTotal), (tray.LongestSync, revit.LongestSync), (tray.LongestOpen, revit.LongestOpen),
                     (tray.WarningCount, revit.WarningCount), (tray.FileSize, revit.FileSize),
                 })
            Assert.True(t.Value == r.Value, $"{range} {t.Label}: tray {t.Value}, Revit {r.Value}");
        for (var i = 0; i < tray.Charts.Count; i++)
            Assert.True(DetailHarness.Signature(tray.Charts[i]) == DetailHarness.Signature(revit.Charts[i]), $"{range} {tray.Charts[i].Title}");
    }

    [Fact]
    public void Every_card_value_and_chart_point_equals_Revits_Activity_tab_for_7D_to_6M()
    {
        var model = DetailSamples.Tower();
        var revit = Revit(model);
        revit.LoadActivity();
        var detail = DetailHarness.Detail(new FakeActivitySource { Models = { ["riverside-tower"] = () => model } });
        detail.Open(DetailSamples.Card("riverside-tower"), ModelDetailRange.OneMonth, null);

        foreach (var range in ActivityRanges.Pills)
        {
            revit.SelectRange(range.Days);
            detail.SelectRange(range);
            Assert.Equal(range, detail.Range);
            SameNumbers(detail.Panel, revit.Activity, range.Label);
        }
        Assert.NotNull(revit.Activity.ActivitySession); // Revit's sixth card, not the tray's
        Assert.Null(detail.Panel.ActivitySession);
    }

    [Fact]
    public void At_1Y_the_numbers_equal_Revits_panel_over_the_same_read()
    {
        var model = DetailSamples.Tower();
        var revitPanel = new ModelActivityPanel(includeSession: true, DetailSamples.EnUs, TimeZoneInfo.Utc);
        revitPanel.Render(model, ActivityRanges.OneYear, ModelsSamples.Today,
            new ModelActivityFacts(true, false, model.LastStats!.WarningsCount, model.LastStats.FileSizeBytes));
        var detail = DetailHarness.Detail(new FakeActivitySource { Models = { ["riverside-tower"] = () => model } });
        detail.Open(DetailSamples.Card("riverside-tower"), ModelDetailRange.OneYear, null);

        SameNumbers(detail.Panel, revitPanel, "1Y");
    }

    private sealed class NoTimer : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

[Collection(WpfThemeCollection.Name)]
public class TrayModelDetailScreenshotTests
{
    private static readonly Size WindowSize = new(720, 540);

    public static TheoryData<UiTheme> Themes => new() { UiTheme.Light, UiTheme.Dark };

    private static string Name(string what, UiTheme theme) => $"tray-health-detail{what}-{theme.ToString().ToLowerInvariant()}";

    private static void Sta(UiTheme theme, Action action) => Screenshots.RunSta(() =>
    {
        Thread.CurrentThread.CurrentCulture = DetailSamples.EnUs;
        Thread.CurrentThread.CurrentUICulture = DetailSamples.EnUs;
        SynchronizationContext.SetSynchronizationContext(null);
        RstTheme.Apply(theme);
        try
        {
            action();
        }
        finally
        {
            RstTheme.Apply(UiTheme.Light);
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    });

    private sealed record Page(TrayWindow Window, ModelsViewModel Models, ModelDetailViewModel Detail);

    private static Page Open(FakeActivitySource source)
    {
        var thumbnails = new FakeThumbnails();
        thumbnails.Images["riverside-tower"] = ModelsSamples.Picture(0);
        var models = ModelsHarness.Model(new FakeModelSource(ModelsSamples.Rows), thumbnails: thumbnails);
        var detail = DetailHarness.Detail(source, thumbnails: thumbnails);
        var health = new TrayHealthViewModel(NoHealthService.Instance, new FakeDialogs(), TrayHealthSamples.Options(), models, detail);
        var window = new TrayWindow(new TrayViewModel(new FakeTrayHost(), TrayPage.Health, health: health)) { Width = WindowSize.Width, Height = WindowSize.Height };
        WindowCapture.ShowOffScreen(window);
        Settle();
        return new Page(window, models, detail);
    }

    private static void Settle()
    {
        WindowCapture.DoEvents();
        WindowCapture.DoEvents();
    }

    private static void OpenCard(Page page, string id)
    {
        page.Models.OpenDetailCommand.Execute(ModelsHarness.Cards(page.Models).Single(c => c.ModelId == id));
        Settle();
    }

    private static void Capture(Page page, string name, Size? size = null)
    {
        Settle();
        var bitmap = WindowCapture.Content(page.Window, size ?? WindowSize);
        Screenshots.Save(bitmap, name);
        Assert.True(Screenshots.DistinctPixels(bitmap) > 40, $"{name} looks blank");
    }

    private static string Id(DependencyObject d) => AutomationProperties.GetAutomationId(d);

    private static List<FrameworkElement> ById(DependencyObject root, string id) =>
        WindowCapture.Descendants<FrameworkElement>(root).Where(e => Id(e) == id && e.IsVisible).ToList();

    private static string Text(DependencyObject root, string id) => ((TextBlock)ById(root, id).Single()).Text;

    private static void PressKey(Window window, Key key)
    {
        var source = PresentationSource.FromVisual(window) ?? throw new InvalidOperationException("the window is not showing");
        InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyUpEvent });
        Settle();
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_detail_replaces_the_list_with_the_header_cards_pills_and_charts(UiTheme theme) => Sta(theme, () =>
    {
        var page = Open(FakeActivitySource.Samples());
        try
        {
            OpenCard(page, "riverside-tower");
            var view = page.Window.HealthPage.DetailPage;
            Assert.True(view.IsVisible);
            Assert.False(page.Window.HealthPage.ModelsPage.IsVisible);
            Capture(page, Name("", theme));
            Capture(page, Name("-full", theme), new Size(720, 1420));

            Assert.Equal("Riverside Tower", Text(view, "Tray.Health.Detail.Title"));
            Assert.Equal("Green", Text(view, "Tray.Health.Detail.Health"));
            Assert.Equal("Opened in Revit 2027.1, 2026.4", Text(view, "Tray.Health.Detail.Versions"));
            Assert.Equal(@"\\fs01\projects\RT\Riverside Tower.rvt", Text(view, "Tray.Health.Detail.CentralPath"));
            Assert.Single(ById(view, "Tray.Health.Detail.ThumbnailImage"));
            Assert.Single(ById(view, "Tray.Health.Detail.Marker"));
            Assert.Single(ById(view, "Tray.Health.Detail.Back"));
            Assert.Empty(ById(view, "Tray.Health.Detail.Loading"));
            foreach (var id in new[] { "Health.Stat.ActivityTotal", "Health.Stat.LongestSync", "Health.Stat.LongestOpen", "Health.Stat.Warnings",
                                       "Health.Stat.FileSize", "Health.Range.7D", "Health.Range.1Y", "Health.Chart.Active", "Health.Chart.Open",
                                       "Health.Chart.Sync", "Health.Chart.Health" })
                Assert.True(ById(view, id).Count > 0, id);
            Assert.Empty(ById(view, "Health.Stat.ActivitySession"));
            Assert.True(((RadioButton)ById(view, "Health.Range.1M").Single()).IsChecked);

            // 1Y: weekly points over the year.
            ((RadioButton)ById(view, "Health.Range.1Y").Single()).IsChecked = true;
            Capture(page, Name("-1y", theme), new Size(720, 1420));
            Assert.Equal("1Y", page.Detail.Range.Label);
        }
        finally
        {
            page.Window.Close();
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void A_cloud_model_and_a_model_whose_history_is_gone(UiTheme theme) => Sta(theme, () =>
    {
        var page = Open(FakeActivitySource.Samples());
        try
        {
            OpenCard(page, "civic-center");
            Capture(page, Name("-cloud", theme));
            var view = page.Window.HealthPage.DetailPage;
            Assert.Equal("Cloud", (string)((ContentControl)ById(view, "Tray.Health.Detail.Marker").Single()).Content);
            Assert.Single(ById(view, "Tray.Health.Detail.ThumbnailPlaceholder"));

            page.Detail.Back();
            Settle();
            OpenCard(page, "harbor-library"); // no history to read
            Capture(page, Name("-gone", theme));
            Assert.Contains(WindowCapture.Descendants<TextBlock>(view),
                t => t.IsVisible && t.Text.StartsWith("This model's history is no longer on this PC", StringComparison.Ordinal));
        }
        finally
        {
            page.Window.Close();
        }
    });

    [Fact]
    public void Loading_shows_until_the_read_is_in() => Sta(UiTheme.Light, () =>
    {
        var queue = new List<Action>();
        var thumbnails = new FakeThumbnails();
        var models = ModelsHarness.Model(new FakeModelSource(ModelsSamples.Rows), thumbnails: thumbnails);
        var detail = DetailHarness.Detail(FakeActivitySource.Samples(), queue, thumbnails);
        var health = new TrayHealthViewModel(NoHealthService.Instance, new FakeDialogs(), TrayHealthSamples.Options(), models, detail);
        var window = new TrayWindow(new TrayViewModel(new FakeTrayHost(), TrayPage.Health, health: health)) { Width = 720, Height = 540 };
        WindowCapture.ShowOffScreen(window);
        Settle();
        try
        {
            models.OpenDetailCommand.Execute(ModelsHarness.Cards(models).First());
            Settle();
            var view = window.HealthPage.DetailPage;
            Assert.Single(ById(view, "Tray.Health.Detail.Loading"));
            Assert.Empty(ById(view, "Health.Chart.Active"));
            queue.Single()();
            Settle();
            Assert.Empty(ById(view, "Tray.Health.Detail.Loading"));
            Assert.Single(ById(view, "Health.Chart.Active"));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Back_and_Escape_return_to_the_list_as_it_was_and_Escape_leaves_the_window_open() => Sta(UiTheme.Light, () =>
    {
        var page = Open(FakeActivitySource.Samples());
        var closes = 0;
        page.Window.ViewModel.RequestClose += (_, _) => closes++;
        try
        {
            page.Window.Activate();
            var list = page.Window.HealthPage.ModelsPage;
            page.Models.Chip = ModelTimeChip.SixMonths;
            page.Models.Rows.OfType<ModelsGroupHeaderRow>().Single(h => h.Title == "Revit 2025").IsExpanded = false;
            Settle();
            list.Scroller!.ScrollToVerticalOffset(140);
            Settle();
            var offset = list.Scroller.VerticalOffset;
            Assert.True(offset > 0, "the list scrolls");

            // Escape, from the keyboard: back to the list, nothing else.
            OpenCard(page, "station-plaza");
            Assert.Equal("6M", page.Detail.Range.Label);
            var back = (Button)ById(page.Window.HealthPage.DetailPage, "Tray.Health.Detail.Back").Single();
            Assert.Same(back, Keyboard.FocusedElement); // the detail takes the keyboard, so Escape reaches it
            PressKey(page.Window, Key.Escape);

            Assert.False(page.Detail.IsOpen);
            Assert.True(list.IsVisible);
            Assert.True(page.Window.IsVisible);
            Assert.Equal(0, closes);
            Assert.Equal(ModelTimeChip.SixMonths, page.Models.Chip);
            Assert.Contains("2025", page.Models.CollapsedGroups);
            Assert.InRange(list.Scroller.VerticalOffset, offset - 1, offset + 1);

            // Back, by its button: the same.
            OpenCard(page, "station-plaza");
            back.Command.Execute(null);
            Settle();
            Assert.True(list.IsVisible);
            Assert.InRange(list.Scroller.VerticalOffset, offset - 1, offset + 1);
            Assert.Equal(0, closes);
        }
        finally
        {
            page.Window.Close();
        }
    });
}
