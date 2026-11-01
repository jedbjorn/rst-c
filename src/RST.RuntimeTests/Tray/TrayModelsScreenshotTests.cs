// rst-c's own (not vendored): the tray's Health → Models sub-tab in the real tray window (spec #25 Models, Look and
// Feel; F19 #25 WU3), over a fake index (ModelsSamples) and fake thumbnails. Screenshots (AC 3): grouped by major and
// by minor, flat, searching, and the three empty states, light and dark: tray-health-models-*. AC 9 (render): a
// 500-model index renders in at most 1 s with the cards virtualized, and a chip change takes at most 100 ms (inferred
// from synthetic timing: CI is a guard, not a lab). Keyboard: Tab reaches the search box, the filters and the cards;
// Enter on a card asks for its detail. Cards stay in one column as the window resizes; a reload keeps the scroll position.

using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RST.Core.Telemetry.Rollups;
using RST.Core.Theming;
using RST.Core.Tray;
using RST.RuntimeTests.Support;
using RST.Tray.Windows;
using RST.UI.Theming;
using Xunit;
using Xunit.Abstractions;

namespace RST.RuntimeTests.Tray;

[Collection(WpfThemeCollection.Name)]
public class TrayModelsScreenshotTests
{
    private static readonly Size WindowSize = new(850, 650);
    private readonly ITestOutputHelper _out;

    public TrayModelsScreenshotTests(ITestOutputHelper output) => _out = output;

    public static TheoryData<UiTheme> Themes => new() { UiTheme.Light, UiTheme.Dark };

    private static string Name(string what, UiTheme theme) => $"tray-health-models-{what}-{theme.ToString().ToLowerInvariant()}";

    private static void Sta(UiTheme theme, Action action) => Screenshots.RunSta(() =>
    {
        var culture = CultureInfo.GetCultureInfo("en-US");
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
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

    /// <summary>Thumbnails for most of the sample models (a stand-in elevation); the rest keep the placeholder.</summary>
    private static FakeThumbnails Pictures()
    {
        var t = new FakeThumbnails();
        var seed = 0;
        foreach (var id in new[] { "riverside-tower", "harbor-library", "elm-street-school", "station-plaza", "lakeside-pavilion", "depot-archive" })
            t.Images[id] = ModelsSamples.Picture(seed++);
        return t;
    }

    private sealed record Page(TrayWindow Window, ModelsViewModel Model, ModelsView View);

    /// <summary>The tray window on Health → Models over <paramref name="model"/>.</summary>
    /// <summary>The tray window on Health → Models over <paramref name="model"/>; <paramref name="detail"/> opens a card (WU4).</summary>
    private static Page Open(ModelsViewModel model, double width = 850, double height = 650, ModelDetailViewModel? detail = null)
    {
        var health = new TrayHealthViewModel(NoHealthService.Instance, new FakeDialogs(), TrayHealthSamples.Options(), model, detail);
        var window = new TrayWindow(new TrayViewModel(new FakeTrayHost(), TrayPage.Health, health: health)) { Width = width, Height = height };
        WindowCapture.ShowOffScreen(window);
        Settle();
        return new Page(window, model, window.HealthPage.ModelsPage);
    }

    private static void Settle()
    {
        WindowCapture.DoEvents();
        WindowCapture.DoEvents();
    }

    private static BitmapSource Capture(Page page, string name, Size? size = null)
    {
        Settle();
        var bitmap = WindowCapture.Content(page.Window, size ?? WindowSize);
        Screenshots.Save(bitmap, name);
        Assert.True(Screenshots.DistinctPixels(bitmap) > 40, $"{name} looks blank");
        return bitmap;
    }

    private static string Id(DependencyObject d) => AutomationProperties.GetAutomationId(d);

    private static List<FrameworkElement> ById(DependencyObject root, string id) =>
        WindowCapture.Descendants<FrameworkElement>(root).Where(e => Id(e) == id && e.IsVisible).ToList();

    private static List<Button> Cards(Page page) => WindowCapture.Descendants<Button>(page.View.List)
        .Where(b => Id(b) == "Tray.Health.Models.Card" && b.IsVisible).ToList();

    [Theory]
    [MemberData(nameof(Themes))]
    public void Grouped_by_major_version_with_totals_thumbnails_and_health_names(UiTheme theme) => Sta(theme, () =>
    {
        var page = Open(ModelsHarness.Model(new FakeModelSource(ModelsSamples.Rows), thumbnails: Pictures()));
        try
        {
            Capture(page, Name("grouped", theme));
            // The whole list, as tall as its content (the window scrolls), for the review and the checks below.
            Capture(page, Name("grouped-full", theme), new Size(850, 1500));
            Assert.True(page.Window.HealthPage.ModelsPage.IsVisible);
            var headers = ById(page.View, "Tray.Health.Models.GroupTitle").Cast<TextBlock>().Select(t => t.Text).ToList();
            Assert.Equal(new[] { "Revit 2027", "Revit 2026", "Revit 2025" }, headers);
            Assert.Contains(ById(page.View, "Tray.Health.Models.GroupCount").Cast<TextBlock>(), t => t.Text == "3 models");
            var cards = Cards(page);
            Assert.Equal(7, cards.Count);
            // Every card's health colour has its name beside the dot; a thumbnail or the placeholder shows.
            Assert.Equal(new[] { "Amber", "Green", "Not enough data", "Red", "Yellow" },
                cards.SelectMany(c => ById(c, "Tray.Health.Models.CardHealth")).Cast<TextBlock>().Select(t => t.Text).Distinct().Order());
            var tower = cards.Single(c => ((ModelCardItem)c.DataContext).ModelId == "riverside-tower");
            Assert.Single(ById(tower, "Tray.Health.Models.ThumbnailImage"));
            Assert.Empty(ById(tower, "Tray.Health.Models.ThumbnailPlaceholder"));
            Assert.Contains(ById(tower, "Tray.Health.Models.CardFileName").Cast<TextBlock>(), t => t.Text == "Riverside Tower_jb.rvt");
            Assert.Contains(ById(tower, "Tray.Health.Models.CardMarker").Cast<ContentControl>(), c => (string)c.Content == "Workshared");
            var civic = cards.Single(c => ((ModelCardItem)c.DataContext).ModelId == "civic-center");
            Assert.Single(ById(civic, "Tray.Health.Models.ThumbnailPlaceholder"));
            // The aggregate cards and the filters.
            Assert.Equal(6, WindowCapture.Descendants<FrameworkElement>(page.View).Count(e => Id(e) == "Health.StatValue" && e.IsVisible));
            foreach (var id in new[] { "Tray.Health.Models.Search", "Tray.Health.Models.Chip.Recent", "Tray.Health.Models.Chip.1Y",
                                       "Tray.Health.Models.Versions", "Tray.Health.Models.GroupBy.Major", "Tray.Health.Models.Aggregates",
                                       "Tray.Health.Models.List" })
                Assert.True(ById(page.Window, id).Count > 0, id);
            Assert.Empty(ById(page.View, "Tray.Health.Models.SearchingAllTime"));
            Assert.Empty(ById(page.View, "Tray.Health.Models.Empty"));
        }
        finally
        {
            page.Window.Close();
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void Grouped_by_minor_version_over_6M_with_one_group_closed(UiTheme theme) => Sta(theme, () =>
    {
        var model = ModelsHarness.Model(new FakeModelSource(ModelsSamples.Rows), thumbnails: Pictures());
        var page = Open(model);
        try
        {
            model.Chip = ModelTimeChip.SixMonths;
            model.GroupBy = ModelGroupBy.MinorVersion;
            Settle();
            model.Rows.OfType<ModelsGroupHeaderRow>().Single(h => h.Title == "Revit 2026.3").IsExpanded = false;
            Settle();
            // As tall as the content, so every group (the closed one too) is in the picture.
            Capture(page, Name("minor", theme), new Size(850, 1500));
            Assert.Contains(ById(page.View, "Tray.Health.Models.GroupTitle").Cast<TextBlock>(), t => t.Text == "Revit 2026.3");
            Assert.Contains(ById(page.View, "Tray.Health.Models.GroupTitle").Cast<TextBlock>(), t => t.Text == "Revit 2025.4");
            // The rows (the list virtualizes, so not every header is realized in the capture).
            var headers = model.Rows.OfType<ModelsGroupHeaderRow>().Select(h => h.Title).ToList();
            Assert.Equal(new[] { "Revit 2027.1", "Revit 2026.4", "Revit 2026.3", "Revit 2026 — unknown minor", "Revit 2025.4" }, headers);
            Assert.Equal("Revit 2027.1", ById(page.View, "Tray.Health.Models.GroupTitle").Cast<TextBlock>().First().Text);
        }
        finally
        {
            page.Window.Close();
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void Flat_with_Group_by_None_keeps_one_column_on_a_wider_window(UiTheme theme) => Sta(theme, () =>
    {
        var model = ModelsHarness.Model(new FakeModelSource(ModelsSamples.Rows), thumbnails: Pictures());
        var page = Open(model, width: 980, height: 600);
        try
        {
            model.GroupBy = ModelGroupBy.None;
            model.Chip = ModelTimeChip.OneYear;
            Settle();
            Assert.All(model.Rows.OfType<ModelsCardRow>(), r => Assert.Single(r.Cards));
            Capture(page, Name("flat", theme), new Size(980, 600));
            Assert.Empty(ById(page.View, "Tray.Health.Models.Group"));
            // Consecutive cards occupy different rows, even on a wide window.
            var cards = Cards(page);
            Assert.True(cards.Count >= 3);
            var tops = cards.Take(3).Select(c => Math.Round(c.TranslatePoint(new Point(), page.View).Y)).Distinct();
            Assert.Equal(3, tops.Count());
        }
        finally
        {
            page.Window.Close();
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void Searching_ignores_the_filters_and_says_so(UiTheme theme) => Sta(theme, () =>
    {
        var model = ModelsHarness.Model(new FakeModelSource(ModelsSamples.Rows), thumbnails: Pictures());
        var page = Open(model);
        try
        {
            model.SearchText = "archive";
            Capture(page, Name("searching", theme));
            Assert.Single(ById(page.View, "Tray.Health.Models.SearchingAllTime"));
            Assert.Single(Cards(page));
            Assert.False(ById(page.View, "Tray.Health.Models.Chip.Recent").Single().IsEnabled);
            Assert.False(ById(page.View, "Tray.Health.Models.Versions").Single().IsEnabled);
            Assert.True(ById(page.View, "Tray.Health.Models.GroupBy.Major").Single().IsEnabled);
            Assert.Single(ById(page.View, "Tray.Health.Models.ClearSearch"));
        }
        finally
        {
            page.Window.Close();
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_three_empty_states_read_as_the_spec_words_them(UiTheme theme) => Sta(theme, () =>
    {
        var none = Open(ModelsHarness.Model(new FakeModelSource(() => Array.Empty<ModelIndexRow>())));
        try
        {
            Capture(none, Name("empty", theme));
            Assert.Equal("No model activity yet. Open a model in Revit and it appears here after you close Revit.",
                ((TextBlock)ById(none.View, "Tray.Health.Models.EmptyText").Single()).Text);
            Assert.Empty(ById(none.View, "Tray.Health.Models.List"));
        }
        finally
        {
            none.Window.Close();
        }

        var old = ModelsHarness.Model(new FakeModelSource(() =>
            [ModelsSamples.Row("old", "Old Model", @"C:\Old.rvt", null, false, false, 20.0, [200], ("2026.1", 200))]));
        var frame = Open(old);
        try
        {
            Capture(frame, Name("empty-timeframe", theme));
            Assert.Equal("No models in this time frame", ((TextBlock)ById(frame.View, "Tray.Health.Models.EmptyText").Single()).Text);
            var link = WindowCapture.Descendants<TextBlock>(frame.View).SelectMany(t => t.Inlines.OfType<Hyperlink>())
                .Single(h => AutomationProperties.GetAutomationId(h) == "Tray.Health.Models.SwitchToOneYear");
            Assert.Equal("Switch to 1Y", new TextRange(link.ContentStart, link.ContentEnd).Text);
            link.Command.Execute(null);
            Settle();
            Assert.Equal(ModelTimeChip.OneYear, old.Chip);
            Assert.Single(Cards(frame));
            Assert.True(((RadioButton)ById(frame.View, "Tray.Health.Models.Chip.1Y").Single()).IsChecked);

            old.SearchText = "nothing like this";
            Capture(frame, Name("empty-nomatch", theme));
            Assert.Equal("No models match", ((TextBlock)ById(frame.View, "Tray.Health.Models.EmptyText").Single()).Text);
        }
        finally
        {
            frame.Window.Close();
        }
    });

    [Fact]
    public void With_500_models_the_page_renders_within_1_s_virtualized_and_a_chip_change_within_100_ms() => Sta(UiTheme.Light, () =>
    {
        // Warm-up (JIT, templates, the theme) on a separate small page.
        var warm = Open(ModelsHarness.Model(new FakeModelSource(() => ModelsSamples.Many(30))));
        warm.Model.Chip = ModelTimeChip.OneYear;
        Settle();
        warm.Window.Close();

        Action? read = null;
        var model = ModelsHarness.Model(new FakeModelSource(() => ModelsSamples.Many(500)), run: work =>
        {
            read = work;
            return Task.CompletedTask;
        });
        var page = Open(model);
        try
        {
            Assert.NotNull(read);
            model.Chip = ModelTimeChip.OneYear; // set before the read: the page renders the widest visible set
            var watch = Stopwatch.StartNew();
            read!();
            page.Window.UpdateLayout();
            var render = watch.Elapsed;
            var visible = model.Result.Cards.Count;
            var realized = Cards(page).Count;
            _out.WriteLine($"render: {render.TotalMilliseconds:F0} ms for {visible} visible models; {realized} cards realized");
            Assert.True(visible >= 300, $"the synthetic index shows {visible} models in 1Y");
            Assert.True(render <= TimeSpan.FromSeconds(1), $"rendering {visible} models took {render.TotalMilliseconds:F0} ms");
            Assert.InRange(realized, 1, 60); // virtualized: the rows on screen and its small cache, not every card

            var chip = TimeSpan.Zero;
            foreach (var next in new[] { ModelTimeChip.SixMonths, ModelTimeChip.ThreeMonths, ModelTimeChip.Recent, ModelTimeChip.OneYear })
            {
                watch.Restart();
                model.Chip = next;
                page.Window.UpdateLayout();
                var took = watch.Elapsed;
                _out.WriteLine($"chip {next}: {took.TotalMilliseconds:F1} ms, {model.Result.Cards.Count} models");
                if (took > chip) chip = took;
            }
            Assert.True(chip <= TimeSpan.FromMilliseconds(100), $"the slowest chip change took {chip.TotalMilliseconds:F1} ms");

            // Scrolled to the end, still only a screenful is realized.
            page.View.Scroller!.ScrollToEnd();
            Settle();
            Assert.InRange(Cards(page).Count, 1, 60);
            Capture(page, "tray-health-models-500-light");
        }
        finally
        {
            page.Window.Close();
        }
    });

    [Fact]
    public void Tab_reaches_the_search_box_the_filters_and_the_cards_and_Enter_opens_a_cards_detail() => Sta(UiTheme.Light, () =>
    {
        var model = ModelsHarness.Model(new FakeModelSource(ModelsSamples.Rows));
        var detail = DetailHarness.Detail(FakeActivitySource.Samples());
        var page = Open(model, detail: detail);
        try
        {
            page.Window.Activate();
            Settle();
            var search = (TextBox)page.View.FindName("SearchBox");
            Assert.True(search.Focus(), "the search box takes the keyboard");
            var seen = new List<string> { Id(search) };
            for (var i = 0; i < 40 && !seen.Contains("Tray.Health.Models.Card"); i++)
            {
                PressKey(page.Window, Key.Tab);
                seen.Add(Keyboard.FocusedElement is DependencyObject d ? Id(d) : "");
            }
            _out.WriteLine(string.Join(" → ", seen));
            Assert.Contains(seen, s => s.StartsWith("Tray.Health.Models.Chip.", StringComparison.Ordinal));
            Assert.Contains("Tray.Health.Models.Versions", seen);
            Assert.Contains(seen, s => s.StartsWith("Tray.Health.Models.GroupBy.", StringComparison.Ordinal));
            Assert.Contains("Tray.Health.Models.Group", seen);
            Assert.Contains("Tray.Health.Models.Card", seen);
            Assert.True(seen.IndexOf("Tray.Health.Models.Versions") < seen.IndexOf("Tray.Health.Models.Card"));

            var card = (Button)Keyboard.FocusedElement;
            var asked = new List<ModelDetailRequestedEventArgs>();
            model.DetailRequested += (_, e) => asked.Add(e);
            PressKey(page.Window, Key.Enter);
            Assert.Equal(((ModelCardItem)card.DataContext).ModelId, asked.Single().ModelId);
            Assert.Equal(ModelDetailRange.OneMonth, asked.Single().StartRange);

            // The detail opened in place of the list (WU4); Back: the list again, and the opened card has the keyboard.
            Assert.True(detail.IsOpen);
            Assert.False(page.View.IsVisible);
            detail.Back();
            Settle();
            Assert.True(page.View.IsVisible);
            Assert.Same(card, Keyboard.FocusedElement);
        }
        finally
        {
            page.Window.Close();
        }
    });

    [Fact]
    public void The_version_filter_opens_its_menu_from_the_keyboard_and_a_tick_filters_the_list() => Sta(UiTheme.Light, () =>
    {
        var model = ModelsHarness.Model(new FakeModelSource(ModelsSamples.Rows));
        var page = Open(model);
        try
        {
            page.Window.Activate();
            var toggle = (ToggleButton)ById(page.View, "Tray.Health.Models.Versions").Single();
            Assert.True(toggle.Focus());
            PressKey(page.Window, Key.Space);
            var menu = toggle.ContextMenu!;
            Assert.True(menu.IsOpen);
            Assert.True(toggle.IsChecked);
            var items = WindowCapture.Descendants<MenuItem>(menu).ToList();
            Assert.Equal(new[] { "Tray.Health.Models.Version.All", "Tray.Health.Models.Version.2027", "Tray.Health.Models.Version.2026",
                                 "Tray.Health.Models.Version.2025" }, items.Select(Id));
            Assert.True(items[0].IsChecked);
            items[2].IsChecked = true; // a tick on 2026
            Settle();
            Assert.True(menu.IsOpen); // stays open for another tick
            Assert.False(items[0].IsChecked);
            Assert.Equal(new[] { "2026" }, model.SelectedVersions);
            Assert.Equal("Revit 2026", model.VersionSummary);
            Assert.Equal(new[] { "Revit 2026" }, ById(page.View, "Tray.Health.Models.GroupTitle").Cast<TextBlock>().Select(t => t.Text));
            menu.IsOpen = false;
            Settle();
            Assert.False(toggle.IsChecked);
        }
        finally
        {
            page.Window.Close();
        }
    });

    [Fact]
    public void A_fold_pass_that_changed_the_store_reloads_the_list_where_the_user_left_it() => Sta(UiTheme.Light, () =>
    {
        var fold = new FakeFold();
        var source = new FakeModelSource(() => ModelsSamples.Many(120));
        var model = ModelsHarness.Model(source, fold);
        var page = Open(model);
        try
        {
            Assert.Equal(1, fold.Requests); // the page opened on Models: one pass asked for
            model.Chip = ModelTimeChip.OneYear;
            Settle();
            page.View.Scroller!.ScrollToVerticalOffset(600);
            Settle();
            var offset = page.View.Scroller.VerticalOffset;
            Assert.True(offset > 300, $"scrolled to {offset}");
            Assert.Equal(offset, model.ScrollOffset, 0.5);

            var before = page.View.TopRow();
            Assert.NotNull(before);
            Assert.Equal(before!.Value.Key, model.ScrollAnchorKey);

            fold.Complete(changed: true);
            Settle();
            Assert.Equal(2, source.Loads);
            Assert.Equal(ModelTimeChip.OneYear, model.Chip);
            // Where the user was: the same row at the top of the viewport, as far scrolled past as it was (the panel's
            // offset alone can read a few pixels apart on identical data, so the anchor is what is kept).
            var after = page.View.TopRow();
            Assert.NotNull(after);
            Assert.Equal(before.Value.Key, after!.Value.Key);
            Assert.Equal(before.Value.Delta, after.Value.Delta, 2.0);

            // Shown again (the page re-queries against today): still the same row.
            model.Shown();
            Settle();
            Assert.Equal(before.Value.Key, page.View.TopRow()!.Value.Key);
            Assert.Equal(before.Value.Delta, page.View.TopRow()!.Value.Delta, 2.0);

            // A chip change starts at the top.
            model.Chip = ModelTimeChip.SixMonths;
            Settle();
            Assert.Equal(0, page.View.Scroller.VerticalOffset, 0.5);
        }
        finally
        {
            page.Window.Close();
        }
    });

    [Theory]
    [InlineData(ModelGroupBy.MajorVersion)]
    [InlineData(ModelGroupBy.MinorVersion)]
    [InlineData(ModelGroupBy.None)]
    public void Cards_stay_in_one_column_capped_at_700_and_shrink_to_fit(ModelGroupBy groupBy) => Sta(UiTheme.Light, () =>
    {
        var model = ModelsHarness.Model(new FakeModelSource(ModelsSamples.Rows));
        var page = Open(model);
        try
        {
            model.GroupBy = groupBy;
            foreach (var width in new[] { 850d, 980d, 560d })
            {
                page.Window.Width = width;
                Settle();
                Assert.All(model.Rows.OfType<ModelsCardRow>(), r => Assert.Single(r.Cards));
                var cards = Cards(page);
                Assert.NotEmpty(cards);
                Assert.All(cards, card => Assert.InRange(card.ActualWidth, 1, 700));
                if (width >= 850) Assert.Equal(700, cards[0].ActualWidth, 1);
                else Assert.True(cards[0].ActualWidth < 560);
                Assert.Equal(0, page.View.Scroller!.ScrollableWidth);
            }
        }
        finally
        {
            page.Window.Close();
        }
    });

    /// <summary>A key press as Windows delivers it (FocusReturnTests' helper): Preview then, unhandled, the key down; then its release.</summary>
    private static void PressKey(Window window, Key key)
    {
        var source = PresentationSource.FromVisual(window) ?? throw new InvalidOperationException("the window is not showing");
        InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyUpEvent });
        Settle();
    }
}
