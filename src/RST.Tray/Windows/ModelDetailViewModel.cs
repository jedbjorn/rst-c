// rst-c's own (not vendored): the tray's model detail (spec #25 Model Detail; F19 #25 WU4). Opening a card on the
// Models list replaces the list with this; Back (or Escape) returns to the list as it was (ModelsViewModel keeps the
// list's state and restores it in ReturnFromDetail).
//
//   - The model is read off the UI thread through spec #24's shared read path, ModelActivityReader.ReadModel (the
//     rollups plus closed outbox files not folded yet, no live session), so for the same range the numbers are the
//     ones Revit's Activity tab shows (spec #25 AC 4). A loading state shows meanwhile; a read for a model the user has
//     since left is dropped.
//   - It renders through RST.UI's ModelActivityPanel without Activity (Session): Activity (Total), Longest Sync,
//     Longest Open Time, and Warning Count and File Size as last known ("As of <date>"); the four charts in Revit's
//     order. Range pills 7D · 1M · 3M · 6M · 1Y; it opens on the range the list hands over. A pill re-renders the same
//     read (the panel slices it per range).
//   - A model whose history is gone (its rollup deleted or unreadable, nothing unfolded) shows a clear message on the
//     cards and charts (the panel's RenderError), as does a failed read.
//   - The header: the card's thumbnail (the Models list's Windows thumbnail, asked for again each time a detail opens:
//     through the list for its card, so the list keeps the answer too),
//     title, file name, local and central path, the Revit versions it was opened in, Workshared or Cloud, and the
//     health colour with its name.

using System.Globalization;
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

/// <summary>Where the detail's model comes from: spec #24's shared read path in the tray, a fake in RuntimeTests.</summary>
internal interface IModelActivitySource
{
    /// <summary>Reads one model by id. Runs off the UI thread. A model with nothing recorded reads empty.</summary>
    ModelActivity Read(string modelId);
}

/// <summary>%LocalAppData%\RST\telemetry: the model's rollup plus the outbox files not folded yet (ReadModel, which writes nothing).</summary>
internal sealed class RollupModelActivitySource : IModelActivitySource
{
    public ModelActivity Read(string modelId) => ModelActivityReader.ReadModel(modelId, new ActivityReadOptions
    {
        NowUtc = DateTimeOffset.UtcNow,
        Zone = TimeZoneInfo.Local,
        Log = m => Log.Warning("{Message}", m),
    });
}

/// <summary>No read (a Health page built without one, in tests): every model reads empty, "no longer on this PC".</summary>
internal sealed class EmptyModelActivitySource : IModelActivitySource
{
    public static readonly EmptyModelActivitySource Instance = new();

    public ModelActivity Read(string modelId) => ModelActivity.Empty;
}

/// <summary>The detail's seams.</summary>
internal sealed class ModelDetailViewModelOptions
{
    /// <summary>The model's activity (<see cref="RollupModelActivitySource"/> in the tray). Default: none (<see cref="EmptyModelActivitySource"/>).</summary>
    public IModelActivitySource? Source { get; init; }

    /// <summary>The Models list's thumbnails (the same instance; the list owns and disposes it); null: the card's image only.</summary>
    public IModelThumbnails? Thumbnails { get; init; }

    /// <summary>Runs the read off the UI thread. Default: <see cref="Task.Run(Action)"/>.</summary>
    public Func<Action, Task>? RunInBackground { get; init; }

    /// <summary>Runs an action on the UI thread later. Default: the dispatcher the view-model was built on, at Background priority.</summary>
    public Action<Action>? Post { get; init; }

    /// <summary>Today in the user's calendar (the ranges' last day). Default: the local zone's today.</summary>
    public Func<DateOnly>? Today { get; init; }

    /// <summary>Formats the charts' dates and counts. Default: the current culture.</summary>
    public CultureInfo? Culture { get; init; }

    /// <summary>The zone the charts' dates are shown in. Default: the local zone.</summary>
    public TimeZoneInfo? Zone { get; init; }
}

internal sealed class ModelDetailViewModel : ObservableObject, IDisposable
{
    private readonly IModelActivitySource _source;
    private readonly IModelThumbnails? _thumbnails;
    private readonly Func<Action, Task> _run;
    private readonly Action<Action> _post;
    private readonly Func<DateOnly> _today;
    private readonly TimeZoneInfo _zone;
    private ModelCard? _card;
    private ModelActivity? _activity;
    private ActivityRange _range = ActivityRanges.OneMonth;
    private int _generation;
    private bool _isOpen;
    private bool _isLoading;
    private bool _syncingRanges;
    private bool _disposed;
    private string _title = "";
    private string? _fileName;
    private string? _localPath;
    private string? _centralPath;
    private bool _isCloud;
    private string _versions = "";
    private string? _marker;
    private ModelHealthColour _health = ModelHealthColour.Grey;
    private ImageSource? _thumbnail;

    /// <summary>A detail with no read (a Health page built without one, in tests).</summary>
    public ModelDetailViewModel()
        : this(null)
    {
    }

    public ModelDetailViewModel(ModelDetailViewModelOptions? options)
    {
        var o = options ?? new ModelDetailViewModelOptions();
        _source = o.Source ?? EmptyModelActivitySource.Instance;
        _thumbnails = o.Thumbnails;
        _run = o.RunInBackground ?? Task.Run;
        var dispatcher = Dispatcher.CurrentDispatcher;
        _post = o.Post ?? (a => dispatcher.BeginInvoke(DispatcherPriority.Background, a));
        _zone = o.Zone ?? TimeZoneInfo.Local;
        _today = o.Today ?? (() => ModelList.LocalToday(DateTimeOffset.UtcNow, _zone));

        // Activity (Session) is Revit's only: the tray has no live session.
        Panel = new ModelActivityPanel(includeSession: false, o.Culture ?? CultureInfo.CurrentCulture, _zone);
        Ranges = ModelDetail.Ranges
            .Select(r => new RangeOption(r.Days, r.Label, r.Days == _range.Days, OnRangeChosen))
            .ToList();
        BackCommand = new RelayCommand(Back);

        if (_thumbnails is not null) _thumbnails.Ready += OnThumbnailReady;
    }

    /// <summary>Back (the button or Escape): the page shows the Models list again.</summary>
    public event EventHandler? BackRequested;

    /// <summary>The cards and charts (RST.UI's, as in Revit's Activity tab, without Activity (Session)).</summary>
    public ModelActivityPanel Panel { get; }

    /// <summary>7D · 1M · 3M · 6M · 1Y (AutomationIds Health.Range.&lt;Label&gt;).</summary>
    public IReadOnlyList<RangeOption> Ranges { get; }

    /// <summary>The selected range.</summary>
    public ActivityRange Range => _range;

    public ICommand BackCommand { get; }

    /// <summary>The detail shows in place of the Models list.</summary>
    public bool IsOpen
    {
        get => _isOpen;
        private set => SetField(ref _isOpen, value);
    }

    /// <summary>The model is being read: the cards and charts wait.</summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetField(ref _isLoading, value)) OnPropertyChanged(nameof(HasContent));
        }
    }

    /// <summary>The cards, pills and charts show (the read is in).</summary>
    public bool HasContent => !_isLoading;

    /// <summary>The model shown; null before the first open.</summary>
    public string? ModelId => _card?.ModelId;

    /// <summary>The last read (null while loading, after a failure, or when the model is gone).</summary>
    public ModelActivity? Activity => _activity;

    public string Title
    {
        get => _title;
        private set => SetField(ref _title, value);
    }

    /// <summary>The file name, when it differs from the title (as on the card).</summary>
    public string? FileName
    {
        get => _fileName;
        private set
        {
            if (SetField(ref _fileName, value)) OnPropertyChanged(nameof(ShowFileName));
        }
    }

    public bool ShowFileName => !string.IsNullOrEmpty(_fileName);

    /// <summary>The model's local file (for a cloud model, Revit's local cache of it).</summary>
    public string? LocalPath
    {
        get => _localPath;
        private set
        {
            if (SetField(ref _localPath, value)) OnPropertyChanged(nameof(HasLocalPath));
        }
    }

    public bool HasLocalPath => !string.IsNullOrWhiteSpace(_localPath);

    /// <summary>The central model's path, or the cloud model's.</summary>
    public string? CentralPath
    {
        get => _centralPath;
        private set
        {
            if (SetField(ref _centralPath, value)) OnPropertyChanged(nameof(HasCentralPath));
        }
    }

    public bool HasCentralPath => !string.IsNullOrWhiteSpace(_centralPath);

    /// <summary>"Central model" or, for a cloud model, "Cloud model".</summary>
    public string CentralLabel => _isCloud ? TrayStrings.Detail_CloudPath : TrayStrings.Detail_CentralPath;

    /// <summary>"Opened in Revit 2027.1, 2026.4", newest first.</summary>
    public string Versions
    {
        get => _versions;
        private set => SetField(ref _versions, value);
    }

    /// <summary>"Workshared" or "Cloud"; null for neither.</summary>
    public string? Marker
    {
        get => _marker;
        private set
        {
            if (SetField(ref _marker, value)) OnPropertyChanged(nameof(HasMarker));
        }
    }

    public bool HasMarker => _marker is not null;

    /// <summary>The model's health colour (decision #55).</summary>
    public ModelHealthColour Health => _health;

    /// <summary>The health dot's severity (Health's colours; None is the grey "Not enough data"). Named Dot for Health's SeverityDot style.</summary>
    public HealthSeverity Dot => ModelHealth.ToSeverity(_health);

    /// <summary>Green, Yellow, Amber, Red or "Not enough data", beside the dot.</summary>
    public string HealthName => ModelsText.HealthName(_health);

    /// <summary>The Windows thumbnail; null shows the placeholder.</summary>
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            if (SetField(ref _thumbnail, value)) OnPropertyChanged(nameof(HasThumbnail));
        }
    }

    public bool HasThumbnail => _thumbnail is not null;

    /// <summary>The header's name for screen readers: "Riverside Tower, Green, Workshared".</summary>
    public string AutomationName => string.Join(", ", new[] { Title, HealthName, Marker }.Where(s => !string.IsNullOrEmpty(s)));

    /// <summary>
    /// Shows <paramref name="card"/>'s detail on <paramref name="start"/> (the list's chip mapped to a pill), with the
    /// card's thumbnail when it has one, and reads the model off the UI thread.
    /// </summary>
    public void Open(ModelCard card, ModelDetailRange start, ImageSource? thumbnail)
    {
        if (_disposed) return;
        ArgumentNullException.ThrowIfNull(card);
        _card = card;
        _activity = null;
        OnPropertyChanged(nameof(ModelId));
        OnPropertyChanged(nameof(Activity));
        ShowHeader(card, null);
        // The card's image at once; the host asks the worker again (RequestThumbnail, or the list's own ask for the card),
        // and the answer replaces it (its last-write check: a model saved since the list showed it gets its new image).
        Thumbnail = thumbnail;
        SelectRangeQuietly(ModelDetail.RangeFor(start));
        IsLoading = true;
        IsOpen = true;
        Log.Information("models: detail for {Model} on {Range}", card.ModelId, _range.Label);
        Read(card.ModelId);
    }

    /// <summary>Asks the worker for the open model's thumbnail (when the list has no card to ask for it). The answer shows
    /// in the header; a model with none keeps the placeholder.</summary>
    public void RequestThumbnail()
    {
        if (_disposed || _thumbnails is null || _card is not { } c) return;
        _thumbnails.Request(new ThumbnailRequest(c.ModelId, c.LocalPath, c.CentralPath, c.IsCloud));
    }

    /// <summary>Back to the list. A read still running is dropped.</summary>
    public void Back()
    {
        if (!_isOpen) return;
        _generation++;
        IsLoading = false;
        IsOpen = false;
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Selects a range pill (as the user's click does) and re-renders.</summary>
    public void SelectRange(ActivityRange range)
    {
        var option = Ranges.FirstOrDefault(r => r.Days == range.Days) ?? Ranges[1];
        if (option.IsSelected) return;
        option.IsSelected = true; // OnRangeChosen
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _generation++;
        if (_thumbnails is not null) _thumbnails.Ready -= OnThumbnailReady; // the Models list owns and disposes it
    }

    private void Read(string modelId)
    {
        var generation = ++_generation;
        try
        {
            _run(() =>
            {
                ModelActivity? model = null;
                Exception? failure = null;
                try
                {
                    model = _source.Read(modelId);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                _post(() => UiGuard.Run("Model detail: show the model", () => Apply(generation, model, failure)));
            }).ContinueWith(t =>
            {
                if (t.Exception is { } ex) Log.Warning(ex, "models: the detail read failed");
            }, TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception ex)
        {
            Apply(generation, null, ex);
        }
    }

    private void Apply(int generation, ModelActivity? model, Exception? failure)
    {
        if (_disposed || generation != _generation || !_isOpen || _card is not { } card) return; // left, or another model opened
        IsLoading = false;
        if (failure is not null || model is null)
        {
            Log.Warning(failure, "models: reading {Model} for its detail failed", card.ModelId);
            Panel.RenderError(failure?.Message ?? TrayStrings.Detail_Gone);
            return;
        }
        if (ModelDetail.IsGone(model))
        {
            Log.Warning("models: {Model} has no history left to show (its rollup is missing or unreadable)", card.ModelId);
            Panel.RenderError(TrayStrings.Detail_Gone);
            return;
        }
        _activity = model;
        OnPropertyChanged(nameof(Activity));
        ShowHeader(card, model);
        Render();
    }

    private void Render()
    {
        if (_activity is not { } model || _card is not { } card) return;
        var asOf = ModelDetail.StatsAsOf(model, _zone);
        var facts = ModelActivityFacts.FromRollup(model, asOf is { } d ? TrayStrings.Detail_AsOf(ModelsText.Date(d)) : "");
        // A rollup from before identity capture may not know whether the model syncs: the list's index row does.
        if (facts.IsWorkshared is null) facts = facts with { IsWorkshared = card.IsWorkshared };
        if (facts.IsCloud is null) facts = facts with { IsCloud = card.IsCloud };
        Panel.Render(model, _range, _today(), facts);
    }

    /// <summary>The header from the card, refined by the read (its paths, versions, kind and current health).</summary>
    private void ShowHeader(ModelCard card, ModelActivity? model)
    {
        var identity = model?.Identity;
        Title = card.Title;
        FileName = card.ShowFileName ? card.FileName : null;
        LocalPath = Pick(identity?.LocalPath, card.LocalPath);
        CentralPath = Pick(identity?.CentralPath, card.CentralPath);
        _isCloud = identity?.IsCloud ?? card.IsCloud;
        OnPropertyChanged(nameof(CentralLabel));
        var workshared = identity?.IsWorkshared ?? card.IsWorkshared;
        Marker = _isCloud ? TrayStrings.Models_Cloud : workshared ? TrayStrings.Models_Workshared : null;

        var versions = model is null ? Array.Empty<string>() : ModelDetail.VersionsOpened(model.Days);
        Versions = versions.Count > 0
            ? TrayStrings.Detail_Versions(string.Join(", ", versions))
            : card.LatestVersion is { } v ? TrayStrings.Detail_Versions(v) : TrayStrings.Models_CardNoVersion;

        _health = model is null ? card.Health : ModelDetail.Health(model, card.Health);
        OnPropertyChanged(nameof(Health));
        OnPropertyChanged(nameof(Dot));
        OnPropertyChanged(nameof(HealthName));
        OnPropertyChanged(nameof(AutomationName));
    }

    private static string? Pick(string? read, string? listed) => string.IsNullOrWhiteSpace(read) ? listed : read;

    private void SelectRangeQuietly(ActivityRange range)
    {
        _syncingRanges = true;
        try
        {
            foreach (var r in Ranges) r.IsSelected = r.Days == range.Days;
        }
        finally
        {
            _syncingRanges = false;
        }
        _range = range;
        OnPropertyChanged(nameof(Range));
    }

    // Runs from a pill's two-way IsSelected setter, where WPF would swallow an exception (SC-046, R6).
    private void OnRangeChosen(RangeOption option) => UiGuard.Run("Model detail: range pill", () =>
    {
        if (_syncingRanges) return;
        SelectRangeQuietly(ActivityRanges.For(option.Days));
        Render();
    });

    private void OnThumbnailReady(object? sender, ThumbnailReadyEventArgs e)
    {
        if (_disposed || e.Image is null) return;
        _post(() => UiGuard.Run("Model detail: show the thumbnail", () =>
        {
            if (!_disposed && _card is { } card && card.ModelId == e.ModelId) Thumbnail = e.Image;
        }));
    }
}
