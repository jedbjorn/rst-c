// rst-c's own (not vendored): the tray's Health page (spec #25 Scope 1; F19 WU2), between Add-ins and Settings, with
// two sub-tabs. Device is the add-in's own Health view-model (HealthViewModel) over DeviceHealthService, in the tray
// scope: the same sections, rows, severity dots, Scan System, Captured time and Clean Junk Files, with the installed
// Revit years in the Revit section (decision #59) and every Revit year in the cleanup (decision #56). Models is
// spec #25 WU3's; until it lands, ModelsViewModel is a placeholder. The page opens on Models.

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
    /// <param name="models">The Models page (spec #25 WU3); null: the placeholder.</param>
    public TrayHealthViewModel(IHealthService device, IDialogService dialogs, HealthViewModelOptions? options = null, ModelsViewModel? models = null)
    {
        Device = new HealthViewModel(device ?? throw new ArgumentNullException(nameof(device)), dialogs, InTrayScope(options));
        Models = models ?? new ModelsViewModel();
    }

    /// <summary>Health → Device: the add-in's Health tab, outside Revit.</summary>
    public HealthViewModel Device { get; }

    /// <summary>Health → Models (spec #25 WU3).</summary>
    public ModelsViewModel Models { get; }

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

    /// <summary>The window shows the Health page (the tray view-model calls this each time it is selected).</summary>
    public void Shown() => OnSubPageShown();

    public void Dispose()
    {
        Device.Dispose();
        Models.Dispose();
    }

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

/// <summary>
/// The Health page's Models sub-tab (spec #25 Models). A placeholder until spec #25 WU3 builds the list here: WU3
/// replaces this class's body (and ModelsView's content), keeping its name, <see cref="Shown"/> and <see cref="Dispose"/>.
/// </summary>
internal sealed class ModelsViewModel : ObservableObject, IDisposable
{
    /// <summary>How many times the Models sub-tab has been shown (WU3 starts its freshness fold pass here).</summary>
    public int TimesShown { get; private set; }

    /// <summary>The Models sub-tab is shown: the Health page opened on it, or the user switched to it.</summary>
    public void Shown() => TimesShown++;

    public void Dispose()
    {
    }
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
