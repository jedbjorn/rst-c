// rst-c's own (not vendored): the Health page's Models sub-tab (spec #25 Models, Thumbnails, Freshness; F19 #25 WU3),
// over RST.Core's list logic (ModelListIndex, WU1).
//
//   - The index is read off the UI thread (IModelListSource), once when the page is first shown and again whenever a
//     fold pass ends with the store changed. The result is applied on the UI thread; the user's search, chip, version
//     filter, Group by, collapsed groups and scroll position are kept.
//   - Each time the page is shown, the fold worker is asked for a pass (spec #25 Freshness: it starts one unless one ran
//     in the last minute; decision #29: the tray owns the worker, no IPC). Never blocks: the pass runs on the worker's
//     own thread and its end is marshalled here.
//   - A chip, filter, Group by or search change re-queries the prepared index on the UI thread (a few ms for 500 models;
//     spec #25 AC 9) and rebuilds the rows: the aggregate cards, then per group a header row and rows of cards, as many
//     per row as fit (Columns, set by the view from its width). ModelsView virtualizes the rows.
//   - A card asks for its Windows thumbnail when the list realizes it (IModelThumbnails), and cancels when it scrolls away.
//   - Activating a card raises DetailRequested with the range the detail opens on (spec #25 Model Detail; WU4 shows it).
//     Everything Back needs is kept here: the query, the collapsed groups, the scroll offset and the card that was opened.

using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RST.Core.Health;
using RST.Core.Logging;
using RST.Core.Telemetry.Rollups;
using RST.Core.Tray;
using RST.Tray.Resources;
using RST.Tray.Thumbnails;
using RST.UI.Health;
using RST.UI.Mvvm;

namespace RST.Tray.Windows;

/// <summary>Where the Models list's index comes from: spec #24's index.json in the tray, a fake in RuntimeTests.</summary>
internal interface IModelListSource
{
    /// <summary>Reads and prepares the index. Runs off the UI thread. An unreadable index is an empty list (Core never throws).</summary>
    ModelListIndex Load();
}

/// <summary>%LocalAppData%\RST\telemetry\rollups\index.json, through spec #24's reader (bad rows skipped and logged).</summary>
internal sealed class IndexModelListSource : IModelListSource
{
    public ModelListIndex Load() => ModelListIndex.Load(new ActivityReadOptions
    {
        NowUtc = DateTimeOffset.UtcNow,
        Zone = TimeZoneInfo.Local,
        Log = m => Log.Warning("{Message}", m),
    });
}

/// <summary>No index (a Health page built without one, in tests): "No model activity yet".</summary>
internal sealed class EmptyModelListSource : IModelListSource
{
    public static readonly EmptyModelListSource Instance = new();

    public ModelListIndex Load() => ModelListIndex.Empty;
}

/// <summary>The Models page's seams.</summary>
internal sealed class ModelsViewModelOptions
{
    /// <summary>The index; null: none ("No model activity yet").</summary>
    public IModelListSource? Source { get; init; }

    /// <summary>The fold worker (spec #24 WU3); null: no freshness pass (tests, a tray whose worker did not start).</summary>
    public IActivityFoldService? Fold { get; init; }

    /// <summary>The cards' thumbnails; null: every card shows the placeholder.</summary>
    public IModelThumbnails? Thumbnails { get; init; }

    /// <summary>Runs the index read off the UI thread. Default: <see cref="Task.Run(Action)"/>.</summary>
    public Func<Action, Task>? RunInBackground { get; init; }

    /// <summary>Runs an action on the UI thread later (the read's result, a pass's end, a thumbnail). Default: the
    /// dispatcher the view-model was built on, at Background priority.</summary>
    public Action<Action>? Post { get; init; }

    /// <summary>Today in the user's calendar (the window's last day). Default: the local zone's today.</summary>
    public Func<DateOnly>? Today { get; init; }
}

internal sealed class ModelsViewModel : ObservableObject, IDisposable
{
    /// <summary>A card's width before it stretches to share its row (ModelsView lays the cards out on this grid).</summary>
    public const double CardWidth = 260;

    /// <summary>Decoded thumbnails kept in memory; the rest are re-read from the disk cache when their card shows again.</summary>
    private const int ThumbnailMemory = 300;

    private readonly IModelListSource _source;
    private readonly IActivityFoldService? _fold;
    private readonly IModelThumbnails? _thumbnails;
    private readonly Func<Action, Task> _run;
    private readonly Action<Action> _post;
    private readonly Func<DateOnly> _today;
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private readonly List<string> _selectedVersions = new();
    private readonly Dictionary<string, ImageSource?> _thumbs = new(StringComparer.Ordinal);
    private readonly Queue<string> _thumbOrder = new();
    private readonly HashSet<string> _thumbsAsked = new(StringComparer.Ordinal);
    // Memory entries checked by the worker since the last refresh, and the paths each was checked with. A refresh (a
    // reload, the page shown again) clears the first, so a realized card shows its remembered image at once and asks the
    // worker again (the last-write check: a saved model gets its new thumbnail; a past failure is retried).
    private readonly HashSet<string> _thumbsChecked = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _thumbKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ModelCardItem> _cards = new(StringComparer.Ordinal);
    private readonly ModelsAggregatesRow _aggregates;
    private ModelListIndex _index = ModelListIndex.Empty;
    private ModelListQuery _query = ModelListQuery.Default;
    private ModelListResult _result;
    private IReadOnlyList<ModelsRow> _rows = Array.Empty<ModelsRow>();
    private IReadOnlyList<VersionOption> _versions = Array.Empty<VersionOption>();
    private string _searchText = "";
    private int _columns = 3;
    private bool _loading;
    private bool _reloadQueued;
    private bool _hasLoaded;
    private bool _disposed;

    /// <summary>A page with no index, worker or thumbnails (a Health page built without them, in tests).</summary>
    public ModelsViewModel()
        : this(null)
    {
    }

    public ModelsViewModel(ModelsViewModelOptions? options)
    {
        var o = options ?? new ModelsViewModelOptions();
        _source = o.Source ?? EmptyModelListSource.Instance;
        _fold = o.Fold;
        _thumbnails = o.Thumbnails;
        _run = o.RunInBackground ?? Task.Run;
        var dispatcher = Dispatcher.CurrentDispatcher;
        _post = o.Post ?? (a => dispatcher.BeginInvoke(DispatcherPriority.Background, a));
        _today = o.Today ?? (() => ModelList.LocalToday(DateTimeOffset.UtcNow, TimeZoneInfo.Local));
        _result = new ModelListResult();

        Chips = new[]
        {
            ChipOption(ModelTimeChip.Recent, TrayStrings.Models_ChipRecent, "Recent", TrayStrings.Models_ChipRecentTip),
            ChipOption(ModelTimeChip.ThreeMonths, TrayStrings.Models_Chip3M, "3M", TrayStrings.Models_Chip3MTip),
            ChipOption(ModelTimeChip.SixMonths, TrayStrings.Models_Chip6M, "6M", TrayStrings.Models_Chip6MTip),
            ChipOption(ModelTimeChip.OneYear, TrayStrings.Models_Chip1Y, "1Y", TrayStrings.Models_Chip1YTip),
        };
        GroupByOptions = new[]
        {
            GroupOption(ModelGroupBy.MajorVersion, TrayStrings.Models_GroupByMajor, "Major"),
            GroupOption(ModelGroupBy.MinorVersion, TrayStrings.Models_GroupByMinor, "Minor"),
            GroupOption(ModelGroupBy.None, TrayStrings.Models_GroupByNone, "None"),
        };
        StatCards = new[]
        {
            new StatCard(TrayStrings.Models_StatModels, "Tray.Health.Models.Stat.Models"),
            new StatCard(TrayStrings.Models_StatActive, "Tray.Health.Models.Stat.ActiveTime"),
            new StatCard(TrayStrings.Models_StatOpens, "Tray.Health.Models.Stat.Opens"),
            new StatCard(TrayStrings.Models_StatAvgOpen, "Tray.Health.Models.Stat.AvgOpenTime"),
            new StatCard(TrayStrings.Models_StatSyncs, "Tray.Health.Models.Stat.Syncs"),
            new StatCard(TrayStrings.Models_StatAvgSync, "Tray.Health.Models.Stat.AvgSyncTime"),
        };
        _aggregates = new ModelsAggregatesRow(StatCards);
        OpenDetailCommand = new RelayCommand<ModelCardItem>(OpenDetail);
        SwitchToOneYearCommand = new RelayCommand(() => Chip = ModelTimeChip.OneYear);
        ClearSearchCommand = new RelayCommand(() => SearchText = "");

        if (_fold is not null) _fold.PassCompleted += OnPassCompleted;
        if (_thumbnails is not null) _thumbnails.Ready += OnThumbnailReady;
    }

    /// <summary>A card was activated (click or Enter): show its detail (WU4). The list keeps its state for Back.</summary>
    public event EventHandler<ModelDetailRequestedEventArgs>? DetailRequested;

    /// <summary>The view should scroll the list to this offset once the rows are laid out (0 after a filter change; the
    /// kept offset after a reload, a reflow, a group toggle or Back).</summary>
    public event EventHandler<double>? ScrollRequested;

    /// <summary>The view should focus this model's card (Back to the list, for keyboard users).</summary>
    public event EventHandler<string>? FocusCardRequested;

    /// <summary>How many times the Models sub-tab has been shown.</summary>
    public int TimesShown { get; private set; }

    /// <summary>The time chips: Recent (default) · 3M · 6M · 1Y.</summary>
    public IReadOnlyList<PillOption<ModelTimeChip>> Chips { get; }

    /// <summary>Group by: Major (default) · Minor · None.</summary>
    public IReadOnlyList<PillOption<ModelGroupBy>> GroupByOptions { get; }

    /// <summary>The six aggregate cards (RST.UI's Activity stat card).</summary>
    public IReadOnlyList<StatCard> StatCards { get; }

    /// <summary>The Revit version filter: All, then every major in the index, newest first.</summary>
    public IReadOnlyList<VersionOption> Versions
    {
        get => _versions;
        private set => SetField(ref _versions, value);
    }

    /// <summary>The version filter's button: "All versions" or "Revit 2027, 2026".</summary>
    public string VersionSummary => _selectedVersions.Count == 0
        ? TrayStrings.Models_VersionAll
        : TrayStrings.Models_VersionSelected(string.Join(", ", _selectedVersions));

    /// <summary>The search box. Any text searches every model, all time; the chips and the version filter wait.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            value ??= "";
            if (!SetField(ref _searchText, value)) return;
            var was = _query.IsSearching;
            Requery(_query with { Search = value }, scrollTop: true);
            if (was != _query.IsSearching) OnPropertyChanged(nameof(FiltersEnabled));
        }
    }

    /// <summary>The filter row shows "Searching all time"; every number on the page is all-time.</summary>
    public bool IsSearching => _query.IsSearching;

    /// <summary>The chips and the version filter apply (the box is empty).</summary>
    public bool FiltersEnabled => !_query.IsSearching;

    public ModelTimeChip Chip
    {
        get => _query.Chip;
        set
        {
            if (_query.Chip == value) return;
            Requery(_query with { Chip = value }, scrollTop: true);
            foreach (var c in Chips) c.SetSelected(c.Value == value);
            OnPropertyChanged();
        }
    }

    public ModelGroupBy GroupBy
    {
        get => _query.GroupBy;
        set
        {
            if (_query.GroupBy == value) return;
            Requery(_query with { GroupBy = value }, scrollTop: true);
            foreach (var g in GroupByOptions) g.SetSelected(g.Value == value);
            OnPropertyChanged();
        }
    }

    /// <summary>The selected major versions, newest first; empty = All.</summary>
    public IReadOnlyList<string> SelectedVersions => _selectedVersions;

    /// <summary>What the list shows now (WU1's result).</summary>
    public ModelListResult Result => _result;

    /// <summary>The rows the view virtualizes.</summary>
    public IReadOnlyList<ModelsRow> Rows
    {
        get => _rows;
        private set => SetField(ref _rows, value);
    }

    /// <summary>The index is being read for the first time.</summary>
    public bool IsLoading => _loading && !_hasLoaded;

    public bool HasLoaded => _hasLoaded;

    /// <summary>An empty state shows in place of the list.</summary>
    public bool IsEmpty => _hasLoaded && _result.EmptyState != ModelListEmptyState.None;

    public bool HasCards => _hasLoaded && _result.EmptyState == ModelListEmptyState.None;

    public string EmptyText => _result.EmptyState switch
    {
        ModelListEmptyState.NoModels => TrayStrings.Models_EmptyNoModels,
        ModelListEmptyState.NothingInTimeFrame => TrayStrings.Models_EmptyTimeFrame,
        ModelListEmptyState.NoMatches => TrayStrings.Models_EmptyNoMatches,
        _ => "",
    };

    /// <summary>"No models in this time frame" offers the switch to 1Y.</summary>
    public bool ShowOneYearLink => _hasLoaded && _result.EmptyState == ModelListEmptyState.NothingInTimeFrame && _result.OfferOneYear;

    public ICommand SwitchToOneYearCommand { get; }

    public ICommand ClearSearchCommand { get; }

    /// <summary>Opens a card's detail (the card's command; Enter on a focused card).</summary>
    public ICommand OpenDetailCommand { get; }

    /// <summary>Cards per row, from the view's width (at least 1). A change reflows the rows.</summary>
    public int Columns
    {
        get => _columns;
        set
        {
            value = Math.Max(1, value);
            if (!SetField(ref _columns, value)) return;
            BuildRows(scrollTo: ScrollOffset);
        }
    }

    /// <summary>The list's scroll offset, kept by the view as the user scrolls (the fallback when no anchor is kept).</summary>
    public double ScrollOffset { get; set; }

    /// <summary>The row at the top of the list as the user left it (<see cref="RowKey"/>): restored after a reload, a
    /// reflow, a group toggle and on Back, so the same card stays at the top. Null after a filter change (the top).</summary>
    public string? ScrollAnchorKey { get; set; }

    /// <summary>That row's top relative to the viewport's top (0 or less: how far it is scrolled past).</summary>
    public double ScrollAnchorDelta { get; set; }

    /// <summary>A row's anchor key: "aggregates", "group:&lt;key&gt;", or "card:&lt;model_id&gt;" for its first card.</summary>
    public static string? RowKey(object? row) => row switch
    {
        ModelsAggregatesRow => "aggregates",
        ModelsGroupHeaderRow h => "group:" + h.Key,
        ModelsCardRow { Cards.Count: > 0 } r => "card:" + r.Cards[0].ModelId,
        _ => null,
    };

    /// <summary>The index of the row an anchor names now (a card's row may differ after a reflow); -1 when it is gone.</summary>
    public int RowIndexOf(string key)
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            if (string.Equals(RowKey(_rows[i]), key, StringComparison.Ordinal)) return i;
            if (key.StartsWith("card:", StringComparison.Ordinal) && _rows[i] is ModelsCardRow r &&
                r.Cards.Any(c => string.Equals("card:" + c.ModelId, key, StringComparison.Ordinal))) return i;
        }
        return -1;
    }

    /// <summary>The groups the user collapsed (by <see cref="ModelGroup.Key"/>); every other group is open.</summary>
    public IReadOnlyCollection<string> CollapsedGroups => _collapsed;

    /// <summary>The model whose detail was last opened (Back focuses its card).</summary>
    public string? LastOpenedModelId { get; private set; }

    /// <summary>The card shown for a model, when it is visible.</summary>
    public ModelCardItem? CardFor(string modelId) => _cards.GetValueOrDefault(modelId);

    /// <summary>
    /// The Models sub-tab is shown (the Health page opened on it, or the user switched to it): read the index the first
    /// time, otherwise re-query it against today's local date (the window may have crossed midnight since: Recent's 14
    /// days move on even when no fold changes the store), keeping the user's search, filters, groups and scroll; and ask
    /// the fold worker for a pass (spec #25 Freshness); the list reloads when it ends with changes.
    /// </summary>
    public void Shown()
    {
        TimesShown++;
        if (_disposed) return;
        _thumbsChecked.Clear();
        if (!_hasLoaded && !_loading) Reload();
        else if (_hasLoaded) Requery(_query, scrollTop: false);
        if (_fold is null) return;
        FoldRequestOutcome outcome;
        try
        {
            outcome = _fold.RequestPass();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "models: asking for a fold pass failed");
            return;
        }
        Log.Information("models: Health page shown; fold pass {Outcome}", outcome);
    }

    /// <summary>Re-reads the index off the UI thread; the filters and the scroll position stay. Never blocks.</summary>
    public void Reload()
    {
        if (_disposed) return;
        if (_source is EmptyModelListSource)
        {
            Apply(ModelListIndex.Empty); // nothing to read: answer at once, never "loading"
            return;
        }
        if (_loading)
        {
            _reloadQueued = true;
            return;
        }
        _loading = true;
        OnPropertyChanged(nameof(IsLoading));
        Task started;
        try
        {
            started = _run(ReadIndex);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "models: the index read could not start");
            Apply(ModelListIndex.Empty);
            return;
        }
        started.ContinueWith(t =>
        {
            if (t.Exception is { } ex) Log.Warning(ex, "models: the index read failed");
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>Back from the detail (WU4): the list as it was, scrolled where it was, the opened card focused.</summary>
    public void ReturnFromDetail()
    {
        ScrollRequested?.Invoke(this, ScrollAnchorKey is null ? 0 : ScrollOffset);
        if (LastOpenedModelId is { } id) FocusCardRequested?.Invoke(this, id);
    }

    /// <summary>The list realized this card: show its remembered image at once, and ask the worker unless it already
    /// checked this model, with these paths, since the last refresh.</summary>
    public void ThumbnailWanted(ModelCardItem card)
    {
        if (_thumbnails is null || _disposed) return;
        var c = card.Card;
        var key = ThumbKey(c);
        if (_thumbs.TryGetValue(c.ModelId, out var image)) card.Thumbnail = image;
        if (_thumbsChecked.Contains(c.ModelId) && _thumbKeys.TryGetValue(c.ModelId, out var checkedWith) && checkedWith == key) return;
        if (_thumbsAsked.Contains(c.ModelId) && _thumbKeys.TryGetValue(c.ModelId, out var askedWith) && askedWith == key) return;
        _thumbsAsked.Add(c.ModelId);
        _thumbsChecked.Remove(c.ModelId);
        _thumbKeys[c.ModelId] = key;
        _thumbnails.Request(new ThumbnailRequest(c.ModelId, c.LocalPath, c.CentralPath, c.IsCloud));
    }

    /// <summary>What a thumbnail was checked with: a moved model, or one now in the cloud, is checked again.</summary>
    private static string ThumbKey(ModelCard c) => (c.IsCloud ? "cloud" : "file") + "|" + c.LocalPath + "|" + c.CentralPath;

    /// <summary>The card scrolled away before its thumbnail was ready: drop the request. A card of a list since rebuilt
    /// (a chip change, a reload) changes nothing: its model's current card may be showing and waiting for the same image.</summary>
    public void ThumbnailNotWanted(ModelCardItem card)
    {
        if (_thumbnails is null) return;
        if (!_cards.TryGetValue(card.ModelId, out var current) || !ReferenceEquals(current, card)) return;
        if (!_thumbsAsked.Remove(card.ModelId)) return;
        _thumbnails.Cancel(card.ModelId);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_fold is not null) _fold.PassCompleted -= OnPassCompleted;
        if (_thumbnails is not null)
        {
            _thumbnails.Ready -= OnThumbnailReady;
            _thumbnails.Dispose();
        }
    }

    private PillOption<ModelTimeChip> ChipOption(ModelTimeChip chip, string label, string id, string tip) =>
        new(chip, label, "Tray.Health.Models.Chip." + id, tip, chip == _query.Chip, c => Chip = c);

    private PillOption<ModelGroupBy> GroupOption(ModelGroupBy by, string label, string id) =>
        new(by, label, "Tray.Health.Models.GroupBy." + id, null, by == _query.GroupBy, g => GroupBy = g);

    /// <summary>Off the UI thread: read and prepare the index, then apply it on the UI thread.</summary>
    private void ReadIndex()
    {
        ModelListIndex index;
        try
        {
            index = _source.Load();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "models: reading the index failed");
            index = ModelListIndex.Empty;
        }
        _post(() => UiGuard.Run("Models: show the index", () => Apply(index)));
    }

    private void Apply(ModelListIndex index)
    {
        _loading = false;
        if (_disposed) return;
        var first = !_hasLoaded;
        _index = index;
        _hasLoaded = true;
        _thumbsChecked.Clear(); // a refresh: every card's thumbnail is checked again as it shows
        Log.Information("models: list read ({Models} models)", index.Count);
        Requery(_query, scrollTop: first);
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(HasLoaded));
        if (!_reloadQueued) return;
        _reloadQueued = false;
        Reload();
    }

    private void OnPassCompleted(object? sender, FoldPassCompletedEventArgs e)
    {
        if (!e.StoreChanged || _disposed) return;
        _post(() => UiGuard.Run("Models: reload after a fold pass", () =>
        {
            // Before the page was first shown, nothing is read yet: the first read sees the new index. While that read
            // runs it may already hold the old one, so Reload queues another read after it.
            if (_disposed || (!_hasLoaded && !_loading)) return;
            Reload();
        }));
    }

    private void OnThumbnailReady(object? sender, ThumbnailReadyEventArgs e)
    {
        if (_disposed) return;
        _post(() => UiGuard.Run("Models: show a thumbnail", () =>
        {
            if (_disposed) return;
            _thumbsAsked.Remove(e.ModelId);
            _thumbsChecked.Add(e.ModelId);
            Remember(e.ModelId, e.Image);
            if (_cards.TryGetValue(e.ModelId, out var card)) card.Thumbnail = e.Image;
        }));
    }

    private void Remember(string modelId, ImageSource? image)
    {
        if (!_thumbs.ContainsKey(modelId)) _thumbOrder.Enqueue(modelId);
        _thumbs[modelId] = image;
        while (_thumbOrder.Count > ThumbnailMemory && _thumbOrder.TryDequeue(out var oldest)) _thumbs.Remove(oldest);
    }

    private void VersionChanged(VersionOption option, bool isChecked)
    {
        if (option.IsAll)
        {
            if (isChecked) _selectedVersions.Clear();
            else if (_selectedVersions.Count == 0)
            {
                option.SetChecked(true); // All stays on until a version is chosen
                return;
            }
        }
        else if (isChecked)
        {
            if (!_selectedVersions.Contains(option.Version!, StringComparer.OrdinalIgnoreCase)) _selectedVersions.Add(option.Version!);
        }
        else
        {
            _selectedVersions.RemoveAll(v => string.Equals(v, option.Version, StringComparison.OrdinalIgnoreCase));
        }
        // Newest first, as the list offers them.
        var order = _result.AvailableVersions.ToList();
        _selectedVersions.Sort((a, b) => order.IndexOf(a).CompareTo(order.IndexOf(b)));
        foreach (var v in _versions)
            v.SetChecked(v.IsAll ? _selectedVersions.Count == 0 : _selectedVersions.Contains(v.Version!, StringComparer.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(VersionSummary));
        OnPropertyChanged(nameof(SelectedVersions));
        Requery(_query with { Versions = _selectedVersions.ToArray() }, scrollTop: true);
    }

    /// <summary>Runs the query on the prepared index and rebuilds the page.</summary>
    private void Requery(ModelListQuery query, bool scrollTop)
    {
        _query = query;
        if (!_hasLoaded)
        {
            OnPropertyChanged(nameof(IsSearching));
            return;
        }
        ModelListResult result;
        try
        {
            result = _index.Query(query, _today());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "models: the list query failed");
            result = ModelListIndex.Empty.Query(query, _today());
        }
        _result = result;
        SyncVersions(result.AvailableVersions);
        var a = result.Aggregates;
        StatCards[0].Value = ModelsText.Count(a.Models);
        StatCards[1].Value = HealthFormat.Dur(a.ActiveSeconds);
        StatCards[2].Value = ModelsText.Count(a.Opens);
        StatCards[3].Value = HealthFormat.Dur(a.AverageOpenSeconds);
        StatCards[4].Value = ModelsText.Count(a.Syncs);
        StatCards[5].Value = HealthFormat.Dur(a.AverageSyncSeconds);
        if (scrollTop) ScrollAnchorKey = null;
        BuildRows(scrollTo: scrollTop ? 0 : ScrollOffset);
        OnPropertyChanged(nameof(Result));
        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasCards));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(ShowOneYearLink));
    }

    /// <summary>The version filter's choices follow the index; a chosen version stays chosen.</summary>
    private void SyncVersions(IReadOnlyList<string> available)
    {
        if (_versions.Count == available.Count + 1 && _versions.Skip(1).Select(v => v.Version).SequenceEqual(available)) return;
        var list = new List<VersionOption> { new(null, _selectedVersions.Count == 0, VersionChanged) };
        foreach (var v in available)
            list.Add(new VersionOption(v, _selectedVersions.Contains(v, StringComparer.OrdinalIgnoreCase), VersionChanged));
        Versions = new ReadOnlyCollection<VersionOption>(list);
    }

    /// <summary>The aggregate row, then per group its header and (open) its rows of <see cref="Columns"/> cards.</summary>
    private void BuildRows(double scrollTo)
    {
        if (!_hasLoaded) return;
        var rows = new List<ModelsRow>();
        _cards.Clear();
        if (_result.EmptyState == ModelListEmptyState.None)
        {
            rows.Add(_aggregates);
            var flat = _query.GroupBy == ModelGroupBy.None;
            foreach (var group in _result.Groups)
            {
                var open = flat || !_collapsed.Contains(group.Key);
                if (!flat) rows.Add(new ModelsGroupHeaderRow(group, open, Toggled));
                if (!open) continue;
                for (var i = 0; i < group.Cards.Count; i += _columns)
                {
                    var n = Math.Min(_columns, group.Cards.Count - i);
                    var items = new ModelCardItem[n];
                    for (var k = 0; k < n; k++)
                    {
                        var item = new ModelCardItem(group.Cards[i + k]);
                        if (_thumbs.TryGetValue(item.ModelId, out var image)) item.Thumbnail = image;
                        _cards[item.ModelId] = item;
                        items[k] = item;
                    }
                    rows.Add(new ModelsCardRow(items, _columns, inGroup: !flat, isLast: i + n >= group.Cards.Count));
                }
            }
        }
        Rows = rows;
        ScrollOffset = scrollTo;
        ScrollRequested?.Invoke(this, scrollTo);
    }

    /// <summary>A group opened or closed. The header is one of the rows: the rebuild is posted, so it never re-enters
    /// WPF inside the toggle's own click (docs/DEVELOPING.md, Behavior).</summary>
    private void Toggled(ModelsGroupHeaderRow header)
    {
        if (header.IsExpanded) _collapsed.Remove(header.Key);
        else _collapsed.Add(header.Key);
        _post(() => UiGuard.Run("Models: open or close a group", () =>
        {
            if (!_disposed) BuildRows(scrollTo: ScrollOffset);
        }));
    }

    private void OpenDetail(ModelCardItem card)
    {
        if (_disposed) return;
        LastOpenedModelId = card.ModelId;
        Log.Information("models: open detail for {Model} ({Range})", card.ModelId, _result.DetailRange);
        DetailRequested?.Invoke(this, new ModelDetailRequestedEventArgs(card.Card, _result.DetailRange));
    }
}
