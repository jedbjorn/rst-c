// rst-c's own (not vendored): the Health window's view-model, porting the flows of the retired
// src/RST.UI/Assets/health_viewer.html (spec #16, WU2): open, Scan System, Clean Junk Files (the
// Junk modal, confirm, Cleaning, the result), the Activity tab (stat cards, live session timer,
// range pills, three charts) and the collection toggle. Scan and Clean run off the UI thread
// (Planner default 8); while either runs, every command and input is disabled.

using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Threading;
using RST.Core.Health;
using RST.Core.Telemetry;
using RST.UI.Controls;
using RST.UI.Dialogs;
using RST.UI.Mvvm;
using RST.UI.Resources;
using RST.Core.Logging;

namespace RST.UI.Health;

/// <summary>The seams a test replaces: the background runner, the one-second ticker, the clock, culture and zone.</summary>
public sealed class HealthViewModelOptions
{
    /// <summary>Runs slow work (scan, clean) off the UI thread. Default: <see cref="Task.Run(Action)"/>.</summary>
    public Func<Action, Task> RunInBackground { get; init; } = Task.Run;

    /// <summary>Starts a repeating tick and returns a handle that stops it. Default: a <see cref="DispatcherTimer"/>.</summary>
    public Func<TimeSpan, Action, IDisposable> StartTimer { get; init; } = DispatcherRepeat;

    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    public CultureInfo? Culture { get; init; }

    public TimeZoneInfo? Zone { get; init; }

    /// <summary>The toast's one-shot timer (see <see cref="TransientMessage"/>); null: a dispatcher timer.</summary>
    public Func<TimeSpan, Action, IDisposable>? ToastSchedule { get; init; }

    private static IDisposable DispatcherRepeat(TimeSpan interval, Action tick)
    {
        var timer = new DispatcherTimer { Interval = interval };
        timer.Tick += (_, _) => tick();
        timer.Start();
        return new Stop(timer.Stop);
    }

    private sealed class Stop(Action stop) : IDisposable
    {
        public void Dispose() => stop();
    }
}

public sealed class HealthViewModel : ObservableObject, IHasToast, IDisposable
{
    public const int HealthTab = 0;
    public const int ActivityTab = 1;
    public const string ConfirmCleanId = "ConfirmCleanDialog";

    private readonly IHealthService _service;
    private readonly IDialogService _dialogs;
    private readonly HealthViewModelOptions _options;
    private readonly CultureInfo _culture;
    private readonly TimeZoneInfo _zone;

    private string _device = HealthFormat.Missing;
    private string _captured = HealthFormat.Missing;
    private IReadOnlyList<HealthSection> _sections = [];
    private int _selectedTab;
    private bool _isScanning;
    private bool _isJunkOpen;
    private IReadOnlyList<JunkTargetItem> _junkTargets = [];
    private int _rangeDays = ActivityRanges.Default;
    private bool _telemetryEnabled;
    private string _telemetryStatusLine = HealthFormat.Missing;
    private readonly SynchronizationContext? _context;
    private IDisposable? _ticker;
    private int _tickerGeneration;

    public HealthViewModel(IHealthService service, IDialogService dialogs, HealthViewModelOptions? options = null)
    {
        _service = service;
        _dialogs = dialogs;
        _options = options ?? new HealthViewModelOptions();
        _culture = _options.Culture ?? CultureInfo.CurrentCulture;
        _zone = _options.Zone ?? TimeZoneInfo.Local;

        Toast = new TransientMessage(_options.ToastSchedule);
        Busy.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BusyState.IsBusy)) OnBusyChanged();
        };

        SessionTimer = new StatCard(UiStrings.Health_StatSessionTimer);
        SessionTotal = new StatCard(UiStrings.Health_StatSessionTotal);
        LongestSync = new StatCard(UiStrings.Health_StatLongestSync);
        LongestOpen = new StatCard(UiStrings.Health_StatLongestOpen);
        WarningCount = new StatCard(UiStrings.Health_StatWarnings);
        FileSize = new StatCard(UiStrings.Health_StatFileSize);
        StatCards = [SessionTimer, SessionTotal, LongestSync, LongestOpen, WarningCount, FileSize];

        Ranges = ActivityRanges.Pills
            .Select(p => new RangeOption(p.Days, p.Label, p.Days == ActivityRanges.Default, OnRangeChosen))
            .ToList();

        SessionChart = new ChartCard(UiStrings.Health_ChartSession);
        OpenChart = new ChartCard(UiStrings.Health_ChartOpen);
        SyncChart = new ChartCard(UiStrings.Health_ChartSync);
        Charts = [SessionChart, OpenChart, SyncChart];

        ScanCommand = new AsyncRelayCommand(ScanAsync, () => CanStartWork);
        CleanCommand = new RelayCommand(OpenJunk, () => CanStartWork);
        CloseCommand = new RelayCommand(() => RequestClose?.Invoke(this, EventArgs.Empty), () => IsIdle);
        JunkCancelCommand = new RelayCommand(CancelJunk, () => IsIdle);
        JunkRunCommand = new AsyncRelayCommand(RunJunkAsync, () => CanStartWork);

        // A scan or clean left running by a closed window keeps Scan and Clean disabled here until it ends.
        _context = SynchronizationContext.Current;
        _service.WorkFinished += OnWorkFinished;

        Render(null);
    }

    /// <summary>The window closes itself on this (Close button).</summary>
    public event EventHandler? RequestClose;

    // ── header ──────────────────────────────────────────────────────────

    public string Device
    {
        get => _device;
        private set => SetField(ref _device, value);
    }

    public string Captured
    {
        get => _captured;
        private set => SetField(ref _captured, value);
    }

    /// <summary>0 = Health, 1 = Activity. Switching to Activity loads it; leaving it stops the session timer.</summary>
    public int SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (!SetField(ref _selectedTab, value)) return;
            OnPropertyChanged(nameof(IsHealthTab));
            OnPropertyChanged(nameof(IsActivityTab));
            // A two-way binding's setter: WPF would swallow an exception here without a trace (SC-046, R6).
            UiGuard.Run("Health: switch tab", () =>
            {
                if (IsActivityTab) LoadActivity();
                else StopTicker();
            });
        }
    }

    public bool IsHealthTab => SelectedTab == HealthTab;

    public bool IsActivityTab => SelectedTab == ActivityTab;

    // ── Health tab ──────────────────────────────────────────────────────

    /// <summary>The four sections, rebuilt (collapsed) on each render, as the HTML re-rendered them.</summary>
    public IReadOnlyList<HealthSection> Sections
    {
        get => _sections;
        private set => SetField(ref _sections, value);
    }

    /// <summary>Scan System is running: its button shows the spinner and "Scanning…".</summary>
    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (!SetField(ref _isScanning, value)) return;
            OnPropertyChanged(nameof(ScanLabel));
            OnBusyChanged();
        }
    }

    public string ScanLabel => IsScanning ? UiStrings.Health_Scanning : UiStrings.Health_ScanSystem;

    public ICommand ScanCommand { get; }

    public ICommand CleanCommand { get; }

    public ICommand CloseCommand { get; }

    // ── Junk modal ──────────────────────────────────────────────────────

    public bool IsJunkOpen
    {
        get => _isJunkOpen;
        private set
        {
            if (SetField(ref _isJunkOpen, value)) OnPropertyChanged(nameof(IsMainEnabled));
        }
    }

    public IReadOnlyList<JunkTargetItem> JunkTargets
    {
        get => _junkTargets;
        private set
        {
            if (SetField(ref _junkTargets, value)) OnPropertyChanged(nameof(HasJunkTargets));
        }
    }

    public bool HasJunkTargets => JunkTargets.Count > 0;

    public ICommand JunkCancelCommand { get; }

    public ICommand JunkRunCommand { get; }

    // ── Activity tab ────────────────────────────────────────────────────

    public StatCard SessionTimer { get; }

    public StatCard SessionTotal { get; }

    public StatCard LongestSync { get; }

    public StatCard LongestOpen { get; }

    public StatCard WarningCount { get; }

    public StatCard FileSize { get; }

    /// <summary>The six cards in order: Session Timer, Session Total, Longest Sync, Longest Open Time, Warning Count, File Size.</summary>
    public IReadOnlyList<StatCard> StatCards { get; }

    public IReadOnlyList<RangeOption> Ranges { get; }

    /// <summary>The selected range in days (7 by default).</summary>
    public int RangeDays
    {
        get => _rangeDays;
        private set => SetField(ref _rangeDays, value);
    }

    public ChartCard SessionChart { get; }

    public ChartCard OpenChart { get; }

    public ChartCard SyncChart { get; }

    public IReadOnlyList<ChartCard> Charts { get; }

    /// <summary>The Collection toggle. Setting it saves the choice; a failure puts it back and says why.</summary>
    public bool TelemetryEnabled
    {
        get => _telemetryEnabled;
        set
        {
            if (_telemetryEnabled == value) return;
            _telemetryEnabled = value;
            OnPropertyChanged();
            UiGuard.Run("Health: collection toggle", () => ApplyTelemetry(value));
        }
    }

    /// <summary>"collection on · 12 files · 1.4 MB · since 7/1/2026".</summary>
    public string TelemetryStatusLine
    {
        get => _telemetryStatusLine;
        private set => SetField(ref _telemetryStatusLine, value);
    }

    // ── overlays and state ──────────────────────────────────────────────

    /// <summary>The Cleaning overlay (RSTmsgWait).</summary>
    public BusyState Busy { get; } = new();

    /// <summary>The 3-second toast (RSTmsgInform).</summary>
    public TransientMessage Toast { get; }

    /// <summary>A scan or a clean is running.</summary>
    public bool IsBusy => IsScanning || Busy.IsBusy;

    /// <summary>Nothing is running: commands and inputs are enabled.</summary>
    public bool IsIdle => !IsBusy;

    /// <summary>Scan and Clean may start: this window is idle and no scan or clean runs elsewhere in the process.</summary>
    public bool CanStartWork => IsIdle && !_service.IsWorkRunning;

    /// <summary>The window's content takes input (no modal overlay over it), so the keyboard cannot reach what the overlay covers.</summary>
    public bool IsMainEnabled => !IsJunkOpen && !Busy.IsBusy;

    // ── flows ───────────────────────────────────────────────────────────

    /// <summary>Open: the last saved snapshot (null renders every value as "—").</summary>
    public void Load()
    {
        HealthSnapshot? snap = null;
        try
        {
            snap = _service.LoadSnapshot();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Health: reading the snapshot failed");
        }
        RenderOrFallback(snap);
    }

    /// <summary>Scan System: scans off the UI thread, then re-renders, or toasts "Scan failed".</summary>
    public async Task ScanAsync()
    {
        if (!CanStartWork) return;
        IsScanning = true;
        try
        {
            HealthSnapshot? snap = null;
            await _options.RunInBackground(() => snap = _service.RunScan());
            RenderOrFallback(snap);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Health: Scan System failed");
            Inform(UiStrings.Health_ScanFailedTitle, Message(ex), BannerKind.Error);
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>Clean Junk Files: lists the cleanup targets in the Junk modal, every toggle on.</summary>
    public void OpenJunk()
    {
        if (!CanStartWork) return;
        IReadOnlyList<RST.Core.Profiles.CleanupTarget> targets;
        try
        {
            targets = _service.GetCleanupTargets();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Health: listing the cleanup targets failed");
            targets = [];
        }
        JunkTargets = targets
            .Select(t => new JunkTargetItem(
                string.IsNullOrEmpty(t.Id) ? t.Name ?? "" : t.Id,
                t.Name ?? "",
                t.Path ?? ""))
            .ToList();
        IsJunkOpen = true;
    }

    public void CancelJunk() => IsJunkOpen = false;

    /// <summary>
    /// Clean Selected: closes the modal; nothing selected toasts "Nothing selected"; otherwise asks
    /// "Delete selected junk?" (Cancel by default), then cleans off the UI thread under the Cleaning
    /// overlay and toasts one line per target.
    /// </summary>
    public async Task RunJunkAsync()
    {
        if (!CanStartWork) return;
        var selected = JunkTargets.Where(t => t.IsSelected && t.Id.Length > 0).ToList();
        IsJunkOpen = false;

        if (selected.Count == 0)
        {
            Inform(UiStrings.Health_NothingSelectedTitle, UiStrings.Health_NothingSelected, BannerKind.Info);
            return;
        }

        try
        {
            // Inside the try (SC-046): a confirm that cannot open is a failed clean, said in the toast.
            if (!_dialogs.Confirm(UiStrings.Health_ConfirmCleanTitle, UiStrings.Health_ConfirmClean, [],
                    UiStrings.Health_ConfirmContinue, danger: true, ConfirmCleanId))
                return;

            Busy.Show(UiStrings.Health_CleaningTitle, UiStrings.Health_Cleaning);
            var ids = selected.Select(t => t.Id).ToList();
            CleanResult? result = null;
            await _options.RunInBackground(() => result = _service.Clean(ids));
            Busy.Hide();
            var lines = selected.Select(t =>
            {
                var deleted = result?.Deleted.GetValueOrDefault(t.Id) ?? 0;
                var skipped = result?.Skipped.GetValueOrDefault(t.Id) ?? 0;
                var label = string.IsNullOrEmpty(t.Name) ? t.Id : t.Name;
                return skipped > 0
                    ? UiStrings.Health_CleanupLineLocked(label, HealthFormat.Number(deleted, _culture), HealthFormat.Number(skipped, _culture))
                    : UiStrings.Health_CleanupLine(label, HealthFormat.Number(deleted, _culture));
            });
            Inform(UiStrings.Health_CleanupCompleteTitle, string.Join("\n", lines), BannerKind.Success);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Health: Clean Junk Files failed");
            Busy.Hide();
            Inform(UiStrings.Health_CleanupFailedTitle, Message(ex), BannerKind.Error);
        }
    }

    /// <summary>Reloads the Activity tab for the selected range.</summary>
    public void LoadActivity()
    {
        ActivityData data;
        try
        {
            data = _service.GetActivity(RangeDays);
        }
        catch (Exception ex)
        {
            RenderActivityError(Message(ex));
            return;
        }
        RenderActivity(data);
    }

    /// <summary>Selects a range pill and reloads.</summary>
    public void SelectRange(int days)
    {
        var option = Ranges.FirstOrDefault(r => r.Days == days) ?? Ranges[0];
        if (!option.IsSelected) option.SetSelected(true);
        OnRangeChosen(option);
    }

    public void Dispose()
    {
        _service.WorkFinished -= OnWorkFinished;
        StopTicker();
        Toast.Dismiss();
    }

    // ── rendering ───────────────────────────────────────────────────────

    /// <summary>
    /// Renders <paramref name="snap"/>; a readable but malformed snapshot (a hand-edited or foreign
    /// health_scan.json) renders as no snapshot, every value "—", as the HTML's load() fell back to
    /// render(null), instead of stopping the window opening (spec #16, Beyond Intention).
    /// </summary>
    private void RenderOrFallback(HealthSnapshot? snap)
    {
        try
        {
            Render(snap);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Health: the snapshot could not be shown; showing no snapshot");
            Render(null);
        }
    }

    private void OnWorkFinished(object? sender, EventArgs e)
    {
        if (_context is null) OnBusyChanged();
        else _context.Post(_ => OnBusyChanged(), null);
    }

    private void Render(HealthSnapshot? snap)
    {
        var report = HealthReport.Build(snap, _culture, _zone);
        Device = report.Device;
        Captured = report.Captured;
        Sections = report.Sections;
    }

    // Runs from a range pill's two-way IsSelected setter, where WPF would swallow an exception (SC-046, R6).
    private void OnRangeChosen(RangeOption option) => UiGuard.Run("Health: range pill", () =>
    {
        foreach (var r in Ranges)
            if (!ReferenceEquals(r, option)) r.SetSelected(false);
        RangeDays = option.Days;
        LoadActivity();
    });

    private void RenderActivity(ActivityData data)
    {
        StartTicker(data.SessionStartUtc, data.CurrentFileOpenUtc);
        RenderTelemetryStatus(data.Status);
        RenderStats(ActivityStats.From(data.File));

        var file = data.File;
        if (file is null)
        {
            foreach (var chart in Charts) chart.Set(null, UiStrings.Health_NoModelTitle, UiStrings.Health_NoModelHint);
            return;
        }

        // Session time: one point per calendar day, zeros included; an all-zero range has no history.
        var perDay = file.PerDayOpenHours.Select(p => new ActivityChartPoint(
            LocalMidnightMs(p.Date), p.Hours,
            UiStrings.Health_ChartPoint(p.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), HealthFormat.HoursShort(p.Hours)))).ToList();
        if (perDay.Any(p => p.Y > 0))
            SessionChart.Set(ActivityChartLayout.Build(perDay, HealthFormat.HoursAxis, TickLabel));
        else
            SessionChart.Set(null, UiStrings.Health_NoTimeTitle, file.MatchedKeys is null ? UiStrings.Health_NoKeyHint : "");

        SetDurationChart(OpenChart, file.OpenEvents, UiStrings.Health_NoOpensTitle);

        // Sync history only applies to workshared and cloud models.
        if (file.IsWorkshared != true && file.IsCloud != true)
            SyncChart.Set(null, UiStrings.Health_NotWorksharedTitle, UiStrings.Health_NotWorksharedHint);
        else
            SetDurationChart(SyncChart, file.SyncEvents, UiStrings.Health_NoSyncsTitle);
    }

    private void SetDurationChart(ChartCard card, IReadOnlyList<DurationPoint> events, string emptyTitle)
    {
        var points = events.Select(e => new ActivityChartPoint(
            e.Ts.ToUnixTimeMilliseconds(), e.Seconds,
            UiStrings.Health_ChartPoint(LocalTime(e.Ts).ToString("G", _culture), HealthFormat.Dur(e.Seconds)))).ToList();
        switch (ActivityChartMath.Mode(points.Count))
        {
            case ChartMode.Empty:
                card.Set(null, emptyTitle);
                break;
            case ChartMode.Dots:
                card.Set(ActivityChartLayout.Build(points, HealthFormat.DurAxis, TickLabel), "", UiStrings.Health_NotEnoughHistory);
                break;
            default:
                card.Set(ActivityChartLayout.Build(points, HealthFormat.DurAxis, TickLabel));
                break;
        }
    }

    private void RenderActivityError(string message)
    {
        StopTicker();
        RenderStats(null);
        foreach (var chart in Charts) chart.Set(null, UiStrings.Health_ActivityFailedTitle, message);
    }

    private void RenderStats(ActivityStats? s)
    {
        SessionTotal.Value = s is null ? HealthFormat.Missing : HealthFormat.HoursShort(s.TotalHours);
        LongestSync.Value = s is null ? HealthFormat.Missing : HealthFormat.Dur(s.LongestSyncSeconds);
        LongestOpen.Value = s is null ? HealthFormat.Missing : HealthFormat.Dur(s.LongestOpenSeconds);
        WarningCount.Value = s is null ? HealthFormat.Missing : HealthFormat.Num(s.WarningsCount, _culture);
        FileSize.Value = s is null ? HealthFormat.Missing : HealthFormat.Bytes(s.FileSizeBytes);
    }

    private void RenderTelemetryStatus(TelemetryStatus status)
    {
        SetTelemetryState(status.Enabled);
        var bits = new List<string>
        {
            status.Enabled ? UiStrings.Health_CollectionOn : UiStrings.Health_CollectionOff,
            UiStrings.Health_StatusFiles(HealthFormat.Num(status.OutboxFileCount, _culture)),
            HealthFormat.Bytes(status.OutboxSizeBytes),
        };
        if (status.OldestEventTs is { } oldest)
            bits.Add(UiStrings.Health_StatusSince(LocalTime(oldest).ToString("d", _culture)));
        TelemetryStatusLine = string.Join(" · ", bits);
    }

    private void ApplyTelemetry(bool want)
    {
        try
        {
            var res = _service.SetTelemetryEnabled(want);
            if (res.Ok)
            {
                SetTelemetryState(res.Enabled == true);
                LoadActivity();
            }
            else
            {
                SetTelemetryState(res.Enabled ?? !want);
                Inform(UiStrings.Health_CollectionFailedTitle, res.Error ?? UiStrings.Health_UnknownError, BannerKind.Error);
            }
        }
        catch (Exception ex)
        {
            SetTelemetryState(!want);
            Inform(UiStrings.Health_CollectionFailedTitle, Message(ex), BannerKind.Error);
        }
    }

    private void SetTelemetryState(bool enabled) => SetField(ref _telemetryEnabled, enabled, nameof(TelemetryEnabled));

    // ── session timer ───────────────────────────────────────────────────

    /// <summary>True while the one-second session tick runs.</summary>
    public bool IsTimerRunning => _ticker is not null;

    private void StartTicker(DateTimeOffset? start, DateTimeOffset? fileOpen)
    {
        StopTicker();
        void Tick()
        {
            var now = _options.Now();
            SessionTimer.Value = start is { } s ? HealthFormat.Elapsed((now - s).TotalMilliseconds) : HealthFormat.Missing;
            SessionTimer.Sub = fileOpen is { } f ? UiStrings.Health_FileOpen(HealthFormat.Elapsed((now - f).TotalMilliseconds)) : "";
        }
        Tick();
        if (start is null && fileOpen is null) return;
        var generation = ++_tickerGeneration;
        _ticker = _options.StartTimer(TimeSpan.FromSeconds(1), () =>
        {
            if (generation == _tickerGeneration) Tick();
        });
        OnPropertyChanged(nameof(IsTimerRunning));
    }

    private void StopTicker()
    {
        if (_ticker is null) return;
        _tickerGeneration++;
        _ticker.Dispose();
        _ticker = null;
        OnPropertyChanged(nameof(IsTimerRunning));
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private void OnBusyChanged()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(CanStartWork));
        OnPropertyChanged(nameof(IsMainEnabled));
        CommandManager.InvalidateRequerySuggested();
    }

    private void Inform(string title, string text, BannerKind kind) => Toast.Show(text, kind, title: title);

    private static string Message(Exception ex) => string.IsNullOrEmpty(ex.Message) ? UiStrings.Health_UnknownError : ex.Message;

    private DateTimeOffset LocalTime(DateTimeOffset ts) => TimeZoneInfo.ConvertTime(ts, _zone);

    /// <summary>JS <c>Date.parse(date + 'T00:00:00')</c>: the day's local midnight, in epoch milliseconds.</summary>
    private double LocalMidnightMs(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, _zone.GetUtcOffset(local)).ToUnixTimeMilliseconds();
    }

    /// <summary>A date tick: JS <c>toLocaleDateString(undefined, { month: 'short', day: 'numeric' })</c>.</summary>
    private string TickLabel(double ms) =>
        HealthFormat.ShortMonthDay(LocalTime(DateTimeOffset.FromUnixTimeMilliseconds((long)ms)), _culture);
}
