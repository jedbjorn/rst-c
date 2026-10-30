// rst-c's own (not vendored): the Updates and Apps pages' view-model (spec #20 §4, §6; F17 WU3). It turns the
// checker's immutable UpdateSnapshot into rows and lines; it never talks to the network or Windows Installer
// itself. The Install and Update buttons are shown disabled with "Coming soon": installs are F17 WU4.

using System.Globalization;
using System.Windows.Input;
using RST.Core.Tray;
using RST.Core.Updates;
using RST.Tray.Resources;
using RST.UI.Mvvm;

namespace RST.Tray.Windows;

/// <summary>What the Updates and Apps pages need from the tray.</summary>
internal interface ITrayUpdatesHost
{
    /// <summary>The checker's current snapshot.</summary>
    UpdateSnapshot Current { get; }

    /// <summary>The snapshot changed (raised on the UI thread).</summary>
    event EventHandler? Changed;

    /// <summary>The clock, for "today" and "yesterday".</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>The time zone the last-checked time is shown in (the PC's).</summary>
    TimeZoneInfo TimeZone { get; }

    /// <summary>Checks now (Check now). Never blocks.</summary>
    void CheckNow();

    /// <summary>Opens <c>https://github.com/&lt;repo&gt;/releases/latest</c> in the browser (a URL built from the catalog).</summary>
    void OpenReleasePage(ProductInfo product);
}

/// <summary>One product's row (Apps page) or line (Updates page). Immutable: a new snapshot makes new rows.</summary>
internal sealed class ProductRowViewModel
{
    /// <param name="detected">Detection has run. Before it has, the row claims no state: no pill, no button.</param>
    public ProductRowViewModel(ProductStatus status, ICommand releasePage, bool detected = true)
    {
        Status = status ?? throw new ArgumentNullException(nameof(status));
        ReleasePageCommand = releasePage;
        State = status.State;
        IsKnown = detected;
    }

    public ProductStatus Status { get; }

    public ProductState State { get; }

    /// <summary>Detection has run, so the state is real (false: "Looking for installed apps…").</summary>
    public bool IsKnown { get; }

    public string Id => Status.Product.Id;

    public string Name => Status.Product.Name;

    public string Description => Status.Product.Description;

    public string InstalledText => Status.Installation.Kind switch
    {
        InstallKind.NotInstalled => TrayStrings.App_NotInstalled,
        InstallKind.Manual => TrayStrings.App_InstalledManually,
        _ => InstalledVersion(Status.Installation.Installed!),
    };

    /// <summary>Windows Installer has it: the one-line layouts show its version (otherwise the pill says it all).</summary>
    public bool ShowInstalledVersion => Status.Installation.Kind == InstallKind.Installer;

    public string LatestText =>
        Status.Latest is { } latest ? latest.ToString()
        : Status.LatestFailed ? TrayStrings.App_LatestFailed
        : TrayStrings.App_LatestNotChecked;

    public string StateText => State switch
    {
        ProductState.UpToDate => TrayStrings.State_UpToDate,
        ProductState.UpdateAvailable => TrayStrings.State_UpdateAvailable,
        ProductState.InstalledManually => TrayStrings.State_InstalledManually,
        ProductState.Installed => TrayStrings.State_Installed,
        _ => TrayStrings.State_NotInstalled,
    };

    public bool IsUpToDate => IsKnown && State == ProductState.UpToDate;

    public bool IsUpdateAvailable => IsKnown && State == ProductState.UpdateAvailable;

    public bool IsManual => IsKnown && State == ProductState.InstalledManually;

    /// <summary>Not installed, or installed with the latest not known: the neutral pill.</summary>
    public bool IsNeutral => IsKnown && State is ProductState.NotInstalled or ProductState.Installed;

    /// <summary>Update (an update is out) or Install (a companion that is not installed); null: no button.</summary>
    public string? ActionText => !IsKnown ? null : State switch
    {
        ProductState.UpdateAvailable => TrayStrings.App_Update,
        ProductState.NotInstalled when !Status.Product.IsRst => TrayStrings.App_Install,
        _ => null,
    };

    public bool HasAction => ActionText is not null;

    /// <summary>The buttons arrive with F17 WU4; until then they are shown disabled.</summary>
    public bool CanAct => false;

    public bool ShowManualNote => IsManual;

    public ICommand ReleasePageCommand { get; }

    private static string InstalledVersion(InstalledProduct installed)
    {
        var version = installed.Version?.ToString() ?? (string.IsNullOrWhiteSpace(installed.VersionText)
            ? TrayStrings.App_VersionUnknown
            : installed.VersionText);
        return installed.Context switch
        {
            InstallContext.Machine => TrayStrings.App_InstalledForEveryone(version),
            InstallContext.UserManaged => TrayStrings.App_InstalledForYouManaged(version),
            _ => TrayStrings.App_InstalledForYou(version),
        };
    }
}

internal sealed class UpdatesViewModel : ObservableObject
{
    private readonly ITrayUpdatesHost _host;
    private readonly Func<UpdateChecksState> _checks;
    private readonly RelayCommand<ProductRowViewModel> _releasePage;
    private UpdateSnapshot _snapshot;
    private bool _attached;

    public UpdatesViewModel(ITrayUpdatesHost host, Func<UpdateChecksState> checks)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _checks = checks ?? throw new ArgumentNullException(nameof(checks));
        _releasePage = new RelayCommand<ProductRowViewModel>(row => _host.OpenReleasePage(row.Status.Product));
        CheckNowCommand = new RelayCommand(() =>
        {
            if (!IsChecking) _host.CheckNow();
        }, () => !IsChecking);
        _snapshot = host.Current;
        Rows = Build(_snapshot);
        host.Changed += OnChanged;
        _attached = true;
    }

    /// <summary>Every product, RST first (the Apps page).</summary>
    public IReadOnlyList<ProductRowViewModel> Rows { get; private set; }

    public ProductRowViewModel Rst => Rows.First(r => r.Status.Product.IsRst);

    /// <summary>Gantt-It and Splash-It (the Updates page's second panel).</summary>
    public IReadOnlyList<ProductRowViewModel> Companions => Rows.Where(r => !r.Status.Product.IsRst).ToList();

    public ICommand CheckNowCommand { get; }

    public bool IsChecking => _snapshot.Checking;

    public string CheckNowText => IsChecking ? TrayStrings.Updates_Checking : TrayStrings.Updates_CheckNow;

    /// <summary>"Last checked today at 09:14", "… yesterday at …", "… 28 Oct 2026 at …", or "Not checked yet".</summary>
    public string LastCheckedText => FormatLastChecked(_snapshot.LastCheckedUtc, _host.UtcNow, _host.TimeZone);

    /// <summary>The Updates page's headline about RST.</summary>
    public string RstHeadline
    {
        get
        {
            var rst = Rst;
            return rst.State switch
            {
                ProductState.UpdateAvailable => TrayStrings.Updates_RstAvailable(rst.Status.Latest!.Value.ToString()),
                ProductState.UpToDate => TrayStrings.Updates_RstUpToDate,
                ProductState.Installed when rst.Status.Installation.Version is { } v => TrayStrings.Updates_RstInstalled(v.ToString()),
                _ => TrayStrings.Updates_RstUnknown,
            };
        }
    }

    public bool ShowRstInstallNote => Rst.IsUpdateAvailable;

    /// <summary>Automatic checks are off (the setting or the preset).</summary>
    public bool ShowChecksOff => !_snapshot.AutomaticChecks;

    public string ChecksOffText => _checks().LockedOff ? TrayStrings.Updates_ChecksOffLocked : TrayStrings.Updates_ChecksOff;

    /// <summary>The last check failed for some product (and none is running now).</summary>
    public bool ShowCheckFailed => _snapshot.LastCheckFailed && !_snapshot.Checking;

    /// <summary>Detection has not run yet.</summary>
    public bool IsDetecting => !_snapshot.Detected;

    /// <summary>Stops following the checker (the window closed).</summary>
    public void Detach()
    {
        if (!_attached) return;
        _attached = false;
        _host.Changed -= OnChanged;
    }

    /// <summary>Re-reads the checker's snapshot.</summary>
    public void Refresh()
    {
        _snapshot = _host.Current;
        Rows = Build(_snapshot);
        OnPropertyChanged(string.Empty); // every binding
        RelayCommand.Refresh();
    }

    internal static string FormatLastChecked(DateTimeOffset? checkedUtc, DateTimeOffset nowUtc, TimeZoneInfo zone)
    {
        if (checkedUtc is not { } at) return TrayStrings.Updates_NeverChecked;
        var local = TimeZoneInfo.ConvertTime(at, zone);
        var today = TimeZoneInfo.ConvertTime(nowUtc, zone).Date;
        var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (local.Date == today) return TrayStrings.Updates_LastCheckedToday(time);
        if (local.Date == today.AddDays(-1)) return TrayStrings.Updates_LastCheckedYesterday(time);
        return TrayStrings.Updates_LastCheckedOn(local.ToString("d MMM yyyy", CultureInfo.InvariantCulture), time);
    }

    private IReadOnlyList<ProductRowViewModel> Build(UpdateSnapshot snapshot) =>
        snapshot.Products.Select(s => new ProductRowViewModel(s, _releasePage, snapshot.Detected)).ToList();

    private void OnChanged(object? sender, EventArgs e) => Refresh();
}
