// rst-c's own (not vendored): the tray's Health page (spec #25 Scope 1; F19 WU2), between Add-ins and Settings, with
// two sub-tabs. Device is the add-in's own Health view-model (HealthViewModel) over DeviceHealthService, in the tray
// scope: the same sections, rows, severity dots, Scan System, Captured time and Clean Junk Files, with the installed
// Revit years in the Revit section (decision #59) and every Revit year in the cleanup (decision #56). Models is
// ModelsViewModel (spec #25 WU3): the list of every model, its filters and thumbnails, freshened by a fold pass each
// time it is shown. The page opens on Models. Opening a card shows its detail in place of the list (ModelDetailViewModel,
// spec #25 WU4); Back returns to the list as it was.

using RST.Core.Health;
using RST.Core.Profiles;
using RST.UI.Dialogs;
using RST.UI.Health;
using RST.UI.Mvvm;

namespace RST.Tray.Windows;

/// <summary>The Health page's sub-tabs, in tab order.</summary>
public enum HealthSubPage
{
    Device,
    Models,
}

internal sealed class TrayHealthViewModel : ObservableObject, IDisposable
{
    private HealthSubPage _subPage = HealthSubPage.Models;
    private bool _deviceLoaded;

    /// <param name="device">The Device page's snapshot, scan and cleanup (DeviceHealthService in the app, a fake in tests).</param>
    /// <param name="dialogs">"Delete selected junk?" and its kin, owned by the tray window.</param>
    /// <param name="options">The Health view-model's seams (tests); its <see cref="HealthViewModelOptions.Scope"/> is always the tray's.</param>
    /// <param name="models">The Models page (spec #25 WU3); null: one with no index ("No model activity yet").</param>
    /// <param name="detail">A model's detail (spec #25 WU4); null: one with no read (the tray passes spec #24's read path).</param>
    public TrayHealthViewModel(IHealthService device, IDialogService dialogs, HealthViewModelOptions? options = null, ModelsViewModel? models = null,
                               ModelDetailViewModel? detail = null)
    {
        Device = new HealthViewModel(device ?? throw new ArgumentNullException(nameof(device)), dialogs, InTrayScope(options));
        Models = models ?? new ModelsViewModel();
        Detail = detail ?? new ModelDetailViewModel();
        Models.DetailRequested += OnDetailRequested;
        Detail.BackRequested += OnDetailBack;
    }

    /// <summary>Health → Device: the add-in's Health tab, outside Revit.</summary>
    public HealthViewModel Device { get; }

    /// <summary>Health → Models (spec #25 WU3).</summary>
    public ModelsViewModel Models { get; }

    /// <summary>The detail of the model opened from Models (spec #25 WU4); shown in place of the list while open.</summary>
    public ModelDetailViewModel Detail { get; }

    /// <summary>The selected sub-tab; Models when the page first opens (spec #25 Scope 1).</summary>
    public HealthSubPage SubPage
    {
        get => _subPage;
        set
        {
            if (!SetField(ref _subPage, value)) return;
            OnPropertyChanged(nameof(SubPageIndex));
            OnPropertyChanged(nameof(IsDevicePage));
            OnPropertyChanged(nameof(IsModelsPage));
            UiGuard.Run("tray: Health sub-tab", OnSubPageShown);
        }
    }

    /// <summary>The sub-tab strip's selected index.</summary>
    public int SubPageIndex
    {
        get => (int)_subPage;
        set
        {
            if (Enum.IsDefined(typeof(HealthSubPage), value)) SubPage = (HealthSubPage)value;
        }
    }

    public bool IsDevicePage => _subPage == HealthSubPage.Device;

    public bool IsModelsPage => _subPage == HealthSubPage.Models;

    /// <summary>Models shows a model's detail in place of the list (Escape goes Back).</summary>
    public bool IsDetailShowing => IsModelsPage && Detail.IsOpen;

    /// <summary>The window shows the Health page (the tray view-model calls this each time it is selected).</summary>
    public void Shown() => OnSubPageShown();

    public void Dispose()
    {
        Models.DetailRequested -= OnDetailRequested;
        Detail.BackRequested -= OnDetailBack;
        Device.Dispose();
        Detail.Dispose();
        Models.Dispose();
    }

    /// <summary>A card was opened: its detail replaces the list, on the range the list hands over, with the card's thumbnail
    /// at once (the detail asks the worker again with its own generation).</summary>
    private void OnDetailRequested(object? sender, ModelDetailRequestedEventArgs e) => UiGuard.Run("tray Health: open a model's detail", () =>
        Detail.Open(e.Card, e.StartRange, Models.CardFor(e.ModelId)?.Thumbnail));

    /// <summary>Back: the list shows again, scrolled where it was, the opened card focused.</summary>
    private void OnDetailBack(object? sender, EventArgs e) => UiGuard.Run("tray Health: back to the list", Models.ReturnFromDetail);

    /// <summary>Device reads its last snapshot the first time it is shown; Models is told each time it is shown.</summary>
    private void OnSubPageShown()
    {
        if (_subPage == HealthSubPage.Models)
        {
            Models.Shown();
            return;
        }
        if (_deviceLoaded) return;
        _deviceLoaded = true;
        Device.Load();
    }

    private static HealthViewModelOptions InTrayScope(HealthViewModelOptions? o) => o is null
        ? new HealthViewModelOptions { Scope = HealthScope.Tray }
        : new HealthViewModelOptions
        {
            RunInBackground = o.RunInBackground,
            Now = o.Now,
            Culture = o.Culture,
            Zone = o.Zone,
            ToastSchedule = o.ToastSchedule,
            Scope = HealthScope.Tray,
        };
}

/// <summary>No Device service (a tray view-model built without one, in tests): no snapshot, no targets, nothing cleaned.</summary>
internal sealed class NoHealthService : IHealthService
{
    public static readonly NoHealthService Instance = new();

    public HealthSnapshot? LoadSnapshot() => null;

    public HealthSnapshot RunScan() => throw new InvalidOperationException("No health service.");

    public IReadOnlyList<CleanupTarget> GetCleanupTargets() => [];

    public CleanResult Clean(IReadOnlyCollection<string> targetIds) => new();

    public ActivityData GetActivity() => throw new NotSupportedException();

    public TelemetryToggleResult SetTelemetryEnabled(bool enabled) => new(false, null, null);

    public bool IsWorkRunning => false;

    public event EventHandler? WorkFinished
    {
        add { }
        remove { }
    }
}
