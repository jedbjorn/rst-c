// rst-c's own (not vendored): the tray's Health → Models sub-tab (spec #25 Models, Thumbnails, Freshness; F19 #25 WU3),
// without a window: the view-model over a fake index, fold worker and thumbnails (filters, search, empty states, rows,
// the detail request, the freshness pass and reload), and the thumbnail cache's rules over a fake shell (local only,
// last-write refresh, a missing file keeps its image, a network or cloud path is never touched). The window's
// screenshots, timing, keyboard and reflow are TrayModelsScreenshotTests.

using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RST.Core.Health;
using RST.Core.Telemetry.Rollups;
using RST.Core.Tray;
using RST.RuntimeTests.Support;
using RST.Tray.Thumbnails;
using RST.Tray.Windows;
using Xunit;

namespace RST.RuntimeTests.Tray;

/// <summary>A user's models on 2026-10-31: three Revit majors, workshared, cloud and plain files, every health colour.</summary>
internal static class ModelsSamples
{
    public static readonly DateOnly Today = new(2026, 10, 31);

    // Open medians that score Green, Yellow, Amber and Red on their own (decision #55); null is Grey.
    private const double Green = 20, Yellow = 138, Amber = 205, Red = 320;

    public static List<ModelIndexRow> Rows() =>
    [
        Row("riverside-tower", "Riverside Tower", @"C:\Users\jb\Documents\Riverside Tower_jb.rvt", @"\\fs01\projects\RT\Riverside Tower.rvt",
            workshared: true, cloud: false, Green, [1, 2, 3, 5, 8, 9, 12, 40, 75], ("2027.1", 1), ("2026.4", 40)),
        Row("harbor-library", "Harbor Library", @"C:\Projects\Harbor Library.rvt", null,
            workshared: false, cloud: false, Yellow, [2, 6, 7, 30], ("2026.4", 2)),
        Row("civic-center", "Civic Center — Phase 2", @"C:\Users\jb\AppData\Local\Autodesk\Revit\PacCache\civic.rvt", "Autodesk Docs://Civic/Phase 2.rvt",
            workshared: true, cloud: true, Amber, [3, 4, 10, 11, 13], ("2027.1", 3)),
        Row("elm-street-school", "Elm Street School", @"D:\Work\ESS_Main_v3.rvt", null,
            workshared: false, cloud: false, Red, [6, 120], ("2026.3", 6)),
        Row("north-clinic", "North Clinic", @"C:\Users\jb\Documents\North Clinic_jb.rvt", @"\\fs01\projects\NC\North Clinic.rvt",
            workshared: true, cloud: false, null, [9, 10], ("2026", 9)),
        Row("maple-housing", "Maple Housing Block B", @"C:\Projects\Maple B.rvt", null,
            workshared: false, cloud: false, Green, [11, 200], ("2025.4", 11)),
        Row("station-plaza", "Station Plaza", @"C:\Projects\Station Plaza.rvt", null,
            workshared: false, cloud: false, Green, [12, 13], ("2027.1", 12)),
        Row("lakeside-pavilion", "Lakeside Pavilion", @"C:\Projects\Lakeside Pavilion.rvt", null,
            workshared: false, cloud: false, Yellow, [51, 60, 64], ("2026.4", 51)),
        Row("depot-archive", "Archive — Depot 2019", @"C:\Archive\Depot 2019.rvt", null,
            workshared: false, cloud: false, null, [820, 830], ("2025.0", 820)),
    ];

    /// <summary>A model active on <paramref name="days"/> (days ago), with its versions' last days (days ago).</summary>
    public static ModelIndexRow Row(string id, string title, string? local, string? central, bool workshared, bool cloud, double? openMedian,
                                    int[] days, params (string Version, int Ago)[] versions)
    {
        var row = new ModelIndexRow
        {
            ModelId = id,
            KeyKind = "central_path",
            Title = title,
            LocalPath = local,
            CentralPath = central,
            IsWorkshared = workshared,
            IsCloud = cloud,
            Health = openMedian is { } m ? new HealthInputs(null, null, m, null) : null,
        };
        foreach (var (v, ago) in versions)
        {
            var day = Today.AddDays(-ago);
            if (v.Contains('.')) row.RevitSubversions[v] = day;
            row.RevitVersions[ModelList.MajorOf(v)] = day;
        }
        AddDays(row, days.Select(d => Today.AddDays(-d)), seed: id.Length);
        return row;
    }

    /// <summary>Daily, weekly and all-time totals for the active days (the index's three shapes).</summary>
    public static void AddDays(ModelIndexRow row, IEnumerable<DateOnly> activeDays, int seed)
    {
        var rng = new Random(seed);
        var days = activeDays.OrderBy(d => d).ToList();
        var weeks = new Dictionary<DateOnly, ActivityTotals>();
        foreach (var day in days)
        {
            var t = new ActivityTotals
            {
                Date = day,
                ActiveMs = rng.Next(20, 300) * 60_000L,
                OpenMs = rng.Next(300, 500) * 60_000L,
                Opens = rng.Next(1, 4),
                OpenSumMs = rng.Next(20, 200) * 1000L,
                Syncs = rng.Next(0, 9),
                SyncSumMs = rng.Next(10, 90) * 1000L,
            };
            if (t.Syncs == 0) t.SyncSumMs = 0;
            if (day > Today.AddDays(-ModelIndexRow.RecentDays)) row.RecentDaysTotals.Add(t);
            var monday = ActivityWeeks.WeekStart(day);
            if (monday > ActivityWeeks.WeekStart(Today).AddDays(-7 * ModelIndexRow.RecentWeeks))
            {
                if (!weeks.TryGetValue(monday, out var w)) weeks[monday] = w = new ActivityTotals { Date = monday };
                Add(w, t);
            }
            Add(row.Totals, t);
        }
        row.RecentWeeksTotals.AddRange(weeks.Values.OrderBy(w => w.Date));
        row.FirstActiveDay = days.Count > 0 ? days[0] : null;
        row.LastActiveDay = days.Count > 0 ? days[^1] : null;
    }

    private static void Add(ActivityTotals into, ActivityTotals t)
    {
        into.ActiveMs += t.ActiveMs;
        into.OpenMs += t.OpenMs;
        into.Opens += t.Opens;
        into.OpenSumMs += t.OpenSumMs;
        into.Syncs += t.Syncs;
        into.SyncSumMs += t.SyncSumMs;
    }

    /// <summary><paramref name="count"/> models spread over two years and three majors (AC 9's synthetic index).</summary>
    public static List<ModelIndexRow> Many(int count)
    {
        var rng = new Random(500);
        var rows = new List<ModelIndexRow>(count);
        for (var i = 0; i < count; i++)
        {
            var last = rng.Next(0, 400);
            var minor = rng.Next(0, 5);
            var major = 2025 + i % 3;
            var days = Enumerable.Range(0, rng.Next(1, 12)).Select(_ => last + rng.Next(0, 90)).Distinct().ToArray();
            rows.Add(Row("model-" + i.ToString("D3"), $"Project {i:D3} - Building {(char)('A' + i % 26)}",
                $@"C:\Users\someone\Documents\Project {i:D3}_someone.rvt", $@"\\fileserver{i % 7}\projects\{i:D3}\Project {i:D3}.rvt",
                workshared: i % 2 == 0, cloud: i % 11 == 0, i % 5 == 4 ? null : 20 + i % 4 * 80,
                days, ($"{major}.{minor}", last)));
        }
        return rows;
    }

    /// <summary>A stand-in Revit preview: a grey elevation on a light sky, so a card with a thumbnail is told apart from the placeholder.</summary>
    public static BitmapSource Picture(int seed)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(236, 241, 247)), null, new Rect(0, 0, 256, 256));
            var rng = new Random(seed);
            for (var i = 0; i < 4; i++)
            {
                var h = rng.Next(60, 200);
                var shade = (byte)rng.Next(90, 170);
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(shade, shade, (byte)(shade + 12))), new Pen(Brushes.DimGray, 2),
                    new Rect(20 + i * 56, 236 - h, 50, h));
            }
            dc.DrawLine(new Pen(Brushes.DimGray, 3), new Point(8, 237), new Point(248, 237));
        }
        var bitmap = new RenderTargetBitmap(256, 256, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}

internal sealed class FakeModelSource(Func<IEnumerable<ModelIndexRow>> rows) : IModelListSource
{
    public int Loads { get; private set; }

    public Exception? Throw { get; set; }

    public ModelListIndex Load()
    {
        Loads++;
        if (Throw is { } ex) throw ex;
        return ModelListIndex.From(rows());
    }
}

internal sealed class FakeFold : IActivityFoldService
{
    public int Requests { get; private set; }

    public FoldRequestOutcome Answer { get; set; } = FoldRequestOutcome.Queued;

    public event EventHandler<FoldPassCompletedEventArgs>? PassCompleted;

    public bool IsRunning => false;

    public DateTimeOffset? LastPassEndedUtc => null;

    public FoldRequestOutcome RequestPass()
    {
        Requests++;
        return Answer;
    }

    /// <summary>A pass ends, with the rollups changed or not.</summary>
    public void Complete(bool changed) => PassCompleted?.Invoke(this, new FoldPassCompletedEventArgs(
        FoldTrigger.Requested, new FoldPassResult(false, !changed, changed ? 1 : 0, 0, 0, 0), 0, TimeSpan.FromSeconds(1), DateTimeOffset.UtcNow));
}

/// <summary>Thumbnails the test hands out: <see cref="Images"/> answer at once (on the calling thread), the rest stay pending.</summary>
internal sealed class FakeThumbnails : IModelThumbnails
{
    public Dictionary<string, ImageSource?> Images { get; } = new();

    public List<string> Requested { get; } = new();

    public List<string> Cancelled { get; } = new();

    public bool Disposed { get; private set; }

    public event EventHandler<ThumbnailReadyEventArgs>? Ready;

    public void Request(ThumbnailRequest request)
    {
        Requested.Add(request.ModelId);
        if (Images.TryGetValue(request.ModelId, out var image)) Complete(request.ModelId, image);
    }

    public void Cancel(string modelId) => Cancelled.Add(modelId);

    public void Complete(string modelId, ImageSource? image) =>
        Ready?.Invoke(this, new ThumbnailReadyEventArgs(modelId, image, image is null ? ThumbnailOutcome.NoShellThumbnail : ThumbnailOutcome.ShellOk));

    public void Dispose() => Disposed = true;
}

internal static class ModelsHarness
{
    /// <summary>A Models page over <paramref name="source"/>, everything inline (the read, the posts), today fixed.</summary>
    public static ModelsViewModel Model(IModelListSource source, FakeFold? fold = null, IModelThumbnails? thumbnails = null,
                                        Func<Action, Task>? run = null) => new(new ModelsViewModelOptions
    {
        Source = source,
        Fold = fold,
        Thumbnails = thumbnails,
        RunInBackground = run ?? (work =>
        {
            work();
            return Task.CompletedTask;
        }),
        Post = a => a(),
        Today = () => ModelsSamples.Today,
    });

    public static ModelsViewModel Shown(IModelListSource source, FakeFold? fold = null, IModelThumbnails? thumbnails = null)
    {
        var model = Model(source, fold, thumbnails);
        model.Shown();
        return model;
    }

    public static IEnumerable<ModelCardItem> Cards(ModelsViewModel model) =>
        model.Rows.OfType<ModelsCardRow>().SelectMany(r => r.Cards);
}

public class TrayModelsViewModelTests
{
    private static FakeModelSource Samples() => new(ModelsSamples.Rows);

    [Fact]
    public void Shown_reads_the_index_once_and_asks_the_fold_worker_for_a_pass_each_time()
    {
        var source = Samples();
        var fold = new FakeFold();
        var model = ModelsHarness.Model(source, fold);
        Assert.False(model.HasLoaded);
        Assert.Equal(0, source.Loads);

        model.Shown();
        Assert.True(model.HasLoaded);
        Assert.Equal(1, source.Loads);
        Assert.Equal(1, fold.Requests);

        fold.Answer = FoldRequestOutcome.RanRecently;
        model.Shown();
        model.Shown();
        Assert.Equal(1, source.Loads); // the list is fresh until a pass changes the store
        Assert.Equal(3, fold.Requests);
        Assert.Equal(3, model.TimesShown);
    }

    [Fact]
    public void A_pass_that_changed_the_store_reloads_and_keeps_the_search_filters_groups_and_scroll()
    {
        var rows = ModelsSamples.Rows();
        var source = new FakeModelSource(() => rows);
        var fold = new FakeFold();
        var model = ModelsHarness.Shown(source, fold);
        model.Chip = ModelTimeChip.ThreeMonths;
        model.GroupBy = ModelGroupBy.MinorVersion;
        model.Versions.Single(v => v.Version == "2026").IsChecked = true;
        var first = model.Rows.OfType<ModelsGroupHeaderRow>().First();
        first.IsExpanded = false;
        model.ScrollOffset = 120;
        double? restored = null;
        model.ScrollRequested += (_, offset) => restored = offset;

        fold.Complete(changed: false);
        Assert.Equal(1, source.Loads);
        Assert.Null(restored);

        // The worker folded Revit's last session: a new model appears, the user's choices stay.
        rows.Add(ModelsSamples.Row("new-model", "Newly Closed", @"C:\Projects\New.rvt", null, workshared: false, cloud: false, 20.0,
            [0], ("2026.4", 0)));
        fold.Complete(changed: true);
        Assert.Equal(2, source.Loads);
        Assert.Equal(ModelTimeChip.ThreeMonths, model.Chip);
        Assert.Equal(ModelGroupBy.MinorVersion, model.GroupBy);
        Assert.Equal(new[] { "2026" }, model.SelectedVersions);
        Assert.Contains(first.Key, model.CollapsedGroups);
        Assert.False(model.Rows.OfType<ModelsGroupHeaderRow>().Single(h => h.Key == first.Key).IsExpanded);
        Assert.Equal(120, restored);
        Assert.Contains(model.Result.Cards, c => c.ModelId == "new-model");
        Assert.True(model.Chips.Single(c => c.Value == ModelTimeChip.ThreeMonths).IsSelected);
    }

    [Fact]
    public void The_index_is_read_off_the_UI_thread_and_a_pass_ending_before_the_first_read_does_not_read_twice()
    {
        Action? pending = null;
        var source = Samples();
        var fold = new FakeFold();
        var model = ModelsHarness.Model(source, fold, run: work =>
        {
            pending = work;
            return Task.CompletedTask;
        });
        model.Shown();
        Assert.NotNull(pending);
        Assert.Equal(0, source.Loads); // Shown returned before the read ran
        Assert.True(model.IsLoading);
        Assert.False(model.IsEmpty);
        Assert.False(model.HasCards);

        fold.Complete(changed: true); // the first read is still to come and will see the new index
        pending!();
        Assert.Equal(1, source.Loads);
        Assert.False(model.IsLoading);
        Assert.True(model.HasCards);
    }

    [Fact]
    public void An_unreadable_index_shows_No_model_activity_yet_and_never_throws()
    {
        var source = Samples();
        source.Throw = new IOException("disk gone");
        var model = ModelsHarness.Shown(source);
        Assert.True(model.IsEmpty);
        Assert.Equal(ModelListEmptyState.NoModels, model.Result.EmptyState);
        Assert.Equal("No model activity yet. Open a model in Revit and it appears here after you close Revit.", model.EmptyText);
        Assert.Empty(model.Rows);

        var none = ModelsHarness.Shown(new FakeModelSource(() => Array.Empty<ModelIndexRow>()));
        Assert.Equal(model.EmptyText, none.EmptyText);
        Assert.False(none.ShowOneYearLink);

        // A page built with nothing at all (TrayHealthViewModel's default) answers at once.
        var bare = new ModelsViewModel();
        bare.Shown();
        Assert.True(bare.IsEmpty);
        Assert.False(bare.IsLoading);
    }

    [Fact]
    public void The_empty_time_frame_offers_1Y_and_a_search_that_finds_nothing_says_so()
    {
        // Only a model last open 200 days ago: nothing in Recent, 3M or 6M; 1Y finds it.
        var source = new FakeModelSource(() => [ModelsSamples.Row("old", "Old Model", @"C:\Old.rvt", null, false, false, 20.0, [200], ("2026.1", 200))]);
        var model = ModelsHarness.Shown(source);
        Assert.Equal("No models in this time frame", model.EmptyText);
        Assert.True(model.ShowOneYearLink);
        model.SwitchToOneYearCommand.Execute(null);
        Assert.Equal(ModelTimeChip.OneYear, model.Chip);
        Assert.True(model.Chips.Single(c => c.Value == ModelTimeChip.OneYear).IsSelected);
        Assert.False(model.Chips.Single(c => c.Value == ModelTimeChip.Recent).IsSelected);
        Assert.True(model.HasCards);
        Assert.False(model.ShowOneYearLink);

        model.SearchText = "nothing like this";
        Assert.Equal("No models match", model.EmptyText);
        Assert.False(model.ShowOneYearLink);
    }

    [Fact]
    public void Search_finds_any_model_all_time_disables_the_filters_and_clearing_restores_them()
    {
        var model = ModelsHarness.Shown(Samples());
        model.Versions.Single(v => v.Version == "2027").IsChecked = true;
        var filtered = model.Result.Cards.Select(c => c.ModelId).ToList();
        Assert.DoesNotContain("depot-archive", filtered);
        Assert.True(model.FiltersEnabled);

        model.SearchText = "depot";
        Assert.True(model.IsSearching);
        Assert.False(model.FiltersEnabled);
        Assert.Equal(new[] { "depot-archive" }, model.Result.Cards.Select(c => c.ModelId));
        Assert.Equal(ModelDetailRange.OneYear, model.Result.DetailRange);
        // By path, case-insensitive, still all time.
        model.SearchText = @"D:\WORK";
        Assert.Equal(new[] { "elm-street-school" }, model.Result.Cards.Select(c => c.ModelId));

        model.ClearSearchCommand.Execute(null);
        Assert.Equal("", model.SearchText);
        Assert.True(model.FiltersEnabled);
        Assert.Equal(filtered, model.Result.Cards.Select(c => c.ModelId));
        Assert.Equal(new[] { "2027" }, model.SelectedVersions);
    }

    [Fact]
    public void The_version_filter_is_All_or_any_set_of_majors()
    {
        var model = ModelsHarness.Shown(Samples());
        Assert.Equal(new string?[] { null, "2027", "2026", "2025" }, model.Versions.Select(v => v.Version));
        Assert.Equal("All versions", model.VersionSummary);
        Assert.True(model.Versions[0].IsChecked);

        model.Versions.Single(v => v.Version == "2025").IsChecked = true;
        model.Versions.Single(v => v.Version == "2027").IsChecked = true;
        Assert.False(model.Versions[0].IsChecked);
        Assert.Equal(new[] { "2027", "2025" }, model.SelectedVersions);
        Assert.Equal("Revit 2027, 2025", model.VersionSummary);
        Assert.All(model.Rows.OfType<ModelsGroupHeaderRow>(), h => Assert.Contains(h.Group.Major, new[] { "2027", "2025" }));

        model.Versions[0].IsChecked = true;
        Assert.Empty(model.SelectedVersions);
        Assert.All(model.Versions.Skip(1), v => Assert.False(v.IsChecked));

        model.Versions[0].IsChecked = false; // All cannot be unchecked on its own
        Assert.True(model.Versions[0].IsChecked);

        model.Versions.Single(v => v.Version == "2026").IsChecked = true;
        model.Versions.Single(v => v.Version == "2026").IsChecked = false;
        Assert.True(model.Versions[0].IsChecked);
        Assert.Equal("All versions", model.VersionSummary);
    }

    [Fact]
    public void Rows_are_the_aggregates_then_each_groups_header_and_card_rows_and_flat_has_no_headers()
    {
        var model = ModelsHarness.Shown(Samples());
        model.Columns = 2;
        Assert.IsType<ModelsAggregatesRow>(model.Rows[0]);
        var headers = model.Rows.OfType<ModelsGroupHeaderRow>().ToList();
        Assert.Equal(new[] { "Revit 2027", "Revit 2026", "Revit 2025" }, headers.Select(h => h.Title));
        Assert.Equal("3 models", headers[0].Count);
        Assert.Equal("1 model", headers[2].Count);
        Assert.EndsWith(" active", headers[0].Active);
        var rows2027 = model.Rows.SkipWhile(r => r != headers[0]).Skip(1).TakeWhile(r => r is ModelsCardRow).Cast<ModelsCardRow>().ToList();
        Assert.Equal(new[] { 2, 1 }, rows2027.Select(r => r.Cards.Count));
        Assert.Equal(new[] { false, true }, rows2027.Select(r => r.IsLastInGroup));
        Assert.All(rows2027, r => Assert.True(r.InGroup));

        // The aggregate cards over the visible cards.
        Assert.Equal(new[] { "Models", "Active time", "Opens", "Avg open time", "Syncs", "Avg sync time" }, model.StatCards.Select(s => s.Label));
        Assert.Equal(model.Result.Aggregates.Models.ToString(), model.StatCards[0].Value);
        Assert.Equal(HealthFormat.Dur(model.Result.Aggregates.ActiveSeconds), model.StatCards[1].Value);

        // A closed group keeps only its header.
        headers[0].IsExpanded = false;
        var rows = model.Rows.ToList();
        Assert.Equal(rows.IndexOf(rows.OfType<ModelsGroupHeaderRow>().First()) + 1, rows.IndexOf(rows.OfType<ModelsGroupHeaderRow>().ElementAt(1)));

        // Minor: an unknown minor groups under its major.
        model.GroupBy = ModelGroupBy.MinorVersion;
        Assert.Contains("Revit 2026 — unknown minor", model.Rows.OfType<ModelsGroupHeaderRow>().Select(h => h.Title));
        Assert.True(model.GroupByOptions.Single(g => g.Value == ModelGroupBy.MinorVersion).IsSelected);

        model.GroupBy = ModelGroupBy.None;
        Assert.Empty(model.Rows.OfType<ModelsGroupHeaderRow>());
        Assert.All(model.Rows.OfType<ModelsCardRow>(), r => Assert.False(r.InGroup));
        Assert.Equal(model.Result.Cards.Select(c => c.ModelId), ModelsHarness.Cards(model).Select(c => c.ModelId));

        model.Columns = 3;
        Assert.All(model.Rows.OfType<ModelsCardRow>().SkipLast(1), r => Assert.Equal(3, r.Cards.Count));
    }

    [Fact]
    public void A_card_shows_its_title_file_name_health_name_version_last_active_and_marker()
    {
        var model = ModelsHarness.Shown(Samples());
        var cards = ModelsHarness.Cards(model).ToDictionary(c => c.ModelId);

        var tower = cards["riverside-tower"];
        Assert.Equal("Riverside Tower", tower.Title);
        Assert.Equal("Riverside Tower_jb.rvt", tower.FileName); // the local file's name differs from the title
        Assert.Equal("Green", tower.HealthName);
        Assert.Equal(HealthSeverity.Ok, tower.Dot);
        Assert.Equal("Revit 2027.1", tower.Version);
        Assert.Equal("Last active Oct 30, 2026", tower.LastActive);
        Assert.Equal("Workshared", tower.Marker);
        Assert.EndsWith(" active", tower.Active);

        Assert.Equal("Yellow", cards["harbor-library"].HealthName);
        Assert.False(cards["harbor-library"].ShowFileName); // "Harbor Library.rvt" is the title
        Assert.Null(cards["harbor-library"].Marker);
        Assert.Equal("Amber", cards["civic-center"].HealthName);
        Assert.Equal("Cloud", cards["civic-center"].Marker);
        Assert.False(cards["civic-center"].ShowFileName); // a cloud model's local file is Revit's cache
        Assert.Equal("Red", cards["elm-street-school"].HealthName);
        Assert.Equal("ESS_Main_v3.rvt", cards["elm-street-school"].FileName);
        Assert.Equal("Not enough data", cards["north-clinic"].HealthName);
        Assert.Equal(HealthSeverity.None, cards["north-clinic"].Dot);
        Assert.Equal("Revit 2026", cards["north-clinic"].Version); // no subversion recorded: the major
        Assert.Contains("Riverside Tower, Riverside Tower_jb.rvt, Green, Revit 2027.1", tower.AutomationName);
    }

    [Fact]
    public void Activating_a_card_asks_for_its_detail_on_the_range_nearest_the_chip()
    {
        var model = ModelsHarness.Shown(Samples());
        var asked = new List<ModelDetailRequestedEventArgs>();
        model.DetailRequested += (_, e) => asked.Add(e);
        var card = ModelsHarness.Cards(model).First(c => c.ModelId == "harbor-library");
        Assert.True(model.OpenDetailCommand.CanExecute(card));
        model.OpenDetailCommand.Execute(card);
        Assert.Equal("harbor-library", asked.Single().ModelId);
        Assert.Equal(ModelDetailRange.OneMonth, asked.Single().StartRange); // Recent opens 1M
        Assert.Equal("harbor-library", model.LastOpenedModelId);

        model.Chip = ModelTimeChip.SixMonths;
        model.OpenDetailCommand.Execute(ModelsHarness.Cards(model).First());
        Assert.Equal(ModelDetailRange.SixMonths, asked[1].StartRange);

        model.SearchText = "harbor";
        model.OpenDetailCommand.Execute(ModelsHarness.Cards(model).Single());
        Assert.Equal(ModelDetailRange.OneYear, asked[2].StartRange);

        // Back: the list as it was, the opened card focused.
        model.ScrollOffset = 40;
        double? scrolled = null;
        string? focused = null;
        model.ScrollRequested += (_, o) => scrolled = o;
        model.FocusCardRequested += (_, id) => focused = id;
        model.ReturnFromDetail();
        Assert.Equal(40, scrolled);
        Assert.Equal("harbor-library", focused);
        Assert.Equal("harbor", model.SearchText);
    }

    [Fact]
    public void Cards_ask_for_their_thumbnail_once_cancel_when_they_scroll_away_and_keep_the_image()
    {
        var thumbnails = new FakeThumbnails();
        var model = ModelsHarness.Shown(new FakeModelSource(ModelsSamples.Rows), thumbnails: thumbnails);
        var cards = ModelsHarness.Cards(model).ToList();
        model.ThumbnailWanted(cards[0]);
        model.ThumbnailWanted(cards[0]);
        model.ThumbnailWanted(cards[1]);
        Assert.Equal(new[] { cards[0].ModelId, cards[1].ModelId }, thumbnails.Requested);

        model.ThumbnailNotWanted(cards[1]);
        Assert.Equal(new[] { cards[1].ModelId }, thumbnails.Cancelled);

        var image = ModelsSamples.Picture(1);
        thumbnails.Complete(cards[0].ModelId, image);
        Assert.Same(image, cards[0].Thumbnail);
        Assert.True(cards[0].HasThumbnail);

        // A rebuilt list (a chip change) shows it at once, without asking again.
        model.Chip = ModelTimeChip.OneYear;
        var again = ModelsHarness.Cards(model).Single(c => c.ModelId == cards[0].ModelId);
        Assert.Same(image, again.Thumbnail);
        model.ThumbnailWanted(again);
        Assert.Equal(2, thumbnails.Requested.Count);

        model.Dispose();
        Assert.True(thumbnails.Disposed);
    }
}

public class ModelThumbnailCacheTests : IDisposable
{
    private readonly TempFolder _dir = new();

    public void Dispose() => _dir.Dispose();

    private sealed class FakeShell : IShellThumbnailSource
    {
        public List<string> Calls { get; } = new();

        public BitmapSource? Image { get; set; } = ModelsSamples.Picture(3);

        public BitmapSource? Extract(string path, int size, out string? reason)
        {
            Calls.Add(path);
            Assert.Equal(256, size);
            reason = Image is null ? "0x8004B200" : null;
            return Image;
        }
    }

    private ModelThumbnailCache Cache(FakeShell shell, Dictionary<string, DateTime?> files, Func<string, bool>? isLocal = null) =>
        new(Path.Combine(_dir.Path, "thumbnails"), shell, isLocal ?? (_ => true), p => files.GetValueOrDefault(p));

    [Fact]
    public void A_local_file_is_taken_from_the_shell_cached_as_a_png_and_served_from_the_cache_until_it_changes() => Screenshots.RunSta(() =>
    {
        var shell = new FakeShell();
        var t0 = new DateTime(2026, 10, 30, 9, 0, 0, DateTimeKind.Utc);
        var files = new Dictionary<string, DateTime?> { [@"C:\Projects\A.rvt"] = t0 };
        var cache = Cache(shell, files);
        var request = new ThumbnailRequest("model-a", @"C:\Projects\A.rvt", null, false);

        var (image, outcome, _) = cache.Resolve(request);
        Assert.Equal(ThumbnailOutcome.ShellOk, outcome);
        Assert.NotNull(image);
        var png = Path.Combine(_dir.Path, "thumbnails", "model-a.png");
        Assert.True(File.Exists(png));
        Assert.Equal(t0, File.GetLastWriteTimeUtc(png));
        var decoded = BitmapFrame.Create(new MemoryStream(File.ReadAllBytes(png)));
        Assert.Equal(256, decoded.PixelWidth);

        Assert.Equal(ThumbnailOutcome.Cached, cache.Resolve(request).Outcome);
        Assert.Single(shell.Calls);

        files[@"C:\Projects\A.rvt"] = t0.AddHours(2); // saved again
        Assert.Equal(ThumbnailOutcome.ShellOk, cache.Resolve(request).Outcome);
        Assert.Equal(2, shell.Calls.Count);
        Assert.Equal(t0.AddHours(2), File.GetLastWriteTimeUtc(png));
    });

    [Fact]
    public void A_missing_or_moved_file_keeps_its_last_cached_image_and_a_shell_failure_keeps_it_too() => Screenshots.RunSta(() =>
    {
        var shell = new FakeShell();
        var files = new Dictionary<string, DateTime?> { [@"C:\Projects\B.rvt"] = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc) };
        var cache = Cache(shell, files);
        var request = new ThumbnailRequest("model-b", @"C:\Projects\B.rvt", null, false);
        Assert.Equal(ThumbnailOutcome.ShellOk, cache.Resolve(request).Outcome);

        files[@"C:\Projects\B.rvt"] = null; // moved
        var kept = cache.Resolve(request);
        Assert.Equal(ThumbnailOutcome.KeptCached, kept.Outcome);
        Assert.NotNull(kept.Image);
        Assert.Single(shell.Calls);

        files[@"C:\Projects\B.rvt"] = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc);
        shell.Image = null; // Windows has none for the new version
        var failed = cache.Resolve(request);
        Assert.Equal(ThumbnailOutcome.NoShellThumbnail, failed.Outcome);
        Assert.Equal("0x8004B200", failed.Reason);
        Assert.NotNull(failed.Image);

        var never = cache.Resolve(new ThumbnailRequest("model-c", @"C:\Projects\C.rvt", null, false));
        Assert.Equal(ThumbnailOutcome.FileMissing, never.Outcome);
        Assert.Null(never.Image);
    });

    [Fact]
    public void A_network_mapped_or_cloud_model_gets_the_placeholder_and_its_path_is_never_touched() => Screenshots.RunSta(() =>
    {
        var shell = new FakeShell();
        // The real guard: a UNC path fails before any probe. Nothing may ask for a last-write time or the shell.
        var cache = new ModelThumbnailCache(Path.Combine(_dir.Path, "thumbnails"), shell,
            lastWriteUtc: p => throw new InvalidOperationException("read " + p));
        Assert.Equal(ThumbnailOutcome.SkippedNonLocal, cache.Resolve(new ThumbnailRequest("unc", @"\\fs01\projects\A.rvt", null, false)).Outcome);
        Assert.Equal(ThumbnailOutcome.SkippedNonLocal, cache.Resolve(new ThumbnailRequest("unc2", null, @"\\fs01\projects\A.rvt", false)).Outcome);
        Assert.Equal(ThumbnailOutcome.SkippedNonLocal, cache.Resolve(new ThumbnailRequest("docs", "Autodesk Docs://P/A.rvt", null, false)).Outcome);
        Assert.Equal(ThumbnailOutcome.SkippedCloud, cache.Resolve(new ThumbnailRequest("cloud", @"C:\Users\jb\cache.rvt", "Autodesk Docs://P/A.rvt", true)).Outcome);
        Assert.Equal(ThumbnailOutcome.NoPath, cache.Resolve(new ThumbnailRequest("none", null, " ", false)).Outcome);
        Assert.Equal(ThumbnailOutcome.InvalidId, cache.Resolve(new ThumbnailRequest(@"..\escape", @"C:\A.rvt", null, false)).Outcome);

        // A mapped drive: the guard's drive probe answers Network (CI cannot mint one; the seam stands in).
        var mapped = new ModelThumbnailCache(Path.Combine(_dir.Path, "thumbnails"), shell, isLocal: p => !p.StartsWith("Z:", StringComparison.Ordinal),
            lastWriteUtc: p => throw new InvalidOperationException("read " + p));
        Assert.Equal(ThumbnailOutcome.SkippedNonLocal, mapped.Resolve(new ThumbnailRequest("mapped", @"Z:\Projects\A.rvt", null, false)).Outcome);
        Assert.Empty(shell.Calls);
        Assert.False(Directory.Exists(Path.Combine(_dir.Path, "thumbnails")));
    });

    [Fact]
    public void The_worker_serves_requests_off_the_UI_thread_and_logs_each_models_outcome_once()
    {
        var shell = new FakeShell();
        var t = new DateTime(2026, 10, 30, 9, 0, 0, DateTimeKind.Utc);
        var id = "w-" + Guid.NewGuid().ToString("N")[..8];
        var lines = new List<string>();
        var cache = new ModelThumbnailCache(Path.Combine(_dir.Path, "thumbnails"), shell, isLocal: null, lastWriteUtc: _ => t);
        using var worker = new ModelThumbnails(cache, m => { lock (lines) lines.Add(m); });
        var ready = new List<(ThumbnailReadyEventArgs Args, int Thread)>();
        using var done = new SemaphoreSlim(0);
        worker.Ready += (_, e) =>
        {
            lock (ready) ready.Add((e, Environment.CurrentManagedThreadId));
            done.Release();
        };

        worker.Request(new ThumbnailRequest(id, @"C:\A.rvt", null, false));
        Assert.True(done.Wait(TimeSpan.FromSeconds(10)), "the first thumbnail came back");
        worker.Request(new ThumbnailRequest(id, @"C:\A.rvt", null, false));
        Assert.True(done.Wait(TimeSpan.FromSeconds(10)), "the second came back");
        worker.Request(new ThumbnailRequest(id + "-unc", @"\\fs01\A.rvt", null, false));
        Assert.True(done.Wait(TimeSpan.FromSeconds(10)), "the network one came back");

        Assert.Equal(new[] { ThumbnailOutcome.ShellOk, ThumbnailOutcome.Cached, ThumbnailOutcome.SkippedNonLocal }, ready.Select(r => r.Args.Outcome));
        Assert.All(ready, r => Assert.NotEqual(Environment.CurrentManagedThreadId, r.Thread));
        Assert.Null(ready[2].Args.Image);
        lock (lines)
        {
            Assert.Contains($"thumbnail: {id} shell ok", lines);
            Assert.Contains($"thumbnail: {id} cached", lines);
            Assert.Contains($"thumbnail: {id}-unc skipped non-local", lines);
            Assert.DoesNotContain(lines, l => l.Contains("fs01", StringComparison.OrdinalIgnoreCase)); // never the path
        }

        // The same outcome again is not logged again.
        worker.Request(new ThumbnailRequest(id, @"C:\A.rvt", null, false));
        Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
        lock (lines) Assert.Single(lines, l => l == $"thumbnail: {id} cached");
    }

    [Fact]
    public void A_cancelled_request_is_never_served()
    {
        var gate = new ManualResetEventSlim(false);
        var shell = new BlockingShell(gate);
        var cache = new ModelThumbnailCache(Path.Combine(_dir.Path, "thumbnails"), shell, _ => true, _ => DateTime.UtcNow);
        using var worker = new ModelThumbnails(cache, _ => { });
        var served = new List<string>();
        using var done = new SemaphoreSlim(0);
        worker.Ready += (_, e) =>
        {
            lock (served) served.Add(e.ModelId);
            done.Release();
        };
        worker.Request(new ThumbnailRequest("first", @"C:\A.rvt", null, false));
        Assert.True(shell.Started.Wait(TimeSpan.FromSeconds(10)));
        worker.Request(new ThumbnailRequest("scrolled-away", @"C:\B.rvt", null, false));
        worker.Request(new ThumbnailRequest("still-there", @"C:\C.rvt", null, false));
        worker.Cancel("scrolled-away");
        gate.Set();
        Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
        lock (served) Assert.Equal(new[] { "first", "still-there" }, served);
    }

    private sealed class BlockingShell(ManualResetEventSlim gate) : IShellThumbnailSource
    {
        public ManualResetEventSlim Started { get; } = new(false);

        public BitmapSource? Extract(string path, int size, out string? reason)
        {
            Started.Set();
            gate.Wait(TimeSpan.FromSeconds(10));
            reason = "none";
            return null;
        }
    }

    [Fact]
    public void Windows_gives_the_shells_thumbnail_of_a_local_image_file_and_none_for_a_missing_one() => Screenshots.RunSta(() =>
    {
        // The real P/Invoke path (IShellItemImageFactory) on the runner, over a file type Windows always has a
        // thumbnail handler for. An .rvt needs Revit's handler: that is the VM check (spec #25 AC 8).
        var file = _dir.File("elevation.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(ModelsSamples.Picture(7)));
        using (var stream = File.Create(file)) encoder.Save(stream);

        var image = ShellThumbnailSource.Instance.Extract(file, 256, out var reason);
        Assert.True(image is not null, "no shell thumbnail: " + reason);
        Assert.InRange(image!.PixelWidth, 1, 256);
        Assert.InRange(image.PixelHeight, 1, 256);
        Assert.True(image.IsFrozen);

        Assert.Null(ShellThumbnailSource.Instance.Extract(_dir.File("missing.rvt"), 256, out var missing));
        Assert.False(string.IsNullOrEmpty(missing));
    });
}
