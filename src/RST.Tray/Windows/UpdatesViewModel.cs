// rst-c's own (not vendored): the Updates and Apps pages' view-model (spec #20 §4, §5, §6; F17 WU3, WU4). It turns
// the checker's immutable UpdateSnapshot and the installer's InstallProgress into rows and lines; it never talks to
// the network or Windows Installer itself. Install and Update run through the host (InstallCoordinator): while
// Revit runs they read "… when Revit closes"; a product installed for everyone on this PC gets no button, only who
// can update it; after a failed companion upgrade that left nothing installed, "Reinstall previous version (x.y.z)".
// Every failure is said in plain words (TrayStrings.Problem); the detail is in the logs (WU3 review D3).

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

    /// <summary>The snapshot or an install's state changed (raised on the UI thread).</summary>
    event EventHandler? Changed;

    /// <summary>The clock, for "today" and "yesterday".</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>The time zone the last-checked time is shown in (the PC's).</summary>
    TimeZoneInfo TimeZone { get; }

    /// <summary>Checks now (Check now). Never blocks.</summary>
    void CheckNow();

    /// <summary>Opens <c>https://github.com/&lt;repo&gt;/releases/latest</c> in the browser (a URL built from the catalog).</summary>
    void OpenReleasePage(ProductInfo product);

    /// <summary>Each product's install state (F17 WU4); absent is idle.</summary>
    IReadOnlyDictionary<string, InstallProgress> Installs => EmptyInstalls;

    /// <summary>A Revit.exe runs in this session: the buttons say "… when Revit closes".</summary>
    bool RevitRunning => false;

    /// <summary>This tray runs from a per-machine install: RST is never installed from it.</summary>
    bool PerMachineTray => false;

    /// <summary>Install or Update the product's latest version. Never blocks.</summary>
    void Install(ProductInfo product) { }

    /// <summary>Reinstalls the version a failed upgrade removed. Never blocks.</summary>
    void ReinstallPrevious(ProductInfo product) { }

    /// <summary>Stops an install that is downloading, waiting for Revit or waiting to retry.</summary>
    void CancelInstall(ProductInfo product) { }

    /// <summary>Opens an msiexec log in Notepad.</summary>
    void OpenLog(string path) { }

    internal static readonly IReadOnlyDictionary<string, InstallProgress> EmptyInstalls = new Dictionary<string, InstallProgress>();
}

/// <summary>The commands a row's buttons and links run.</summary>
internal sealed record RowCommands(ICommand ReleasePage, ICommand Act, ICommand Cancel, ICommand OpenLog)
{
    private static readonly ICommand None = new RelayCommand(() => { }, () => false);

    public static RowCommands ReleasePageOnly(ICommand releasePage) => new(releasePage, None, None, None);
}

/// <summary>How a row's status line looks.</summary>
internal enum RowNoteKind
{
    None,
    Progress,
    Success,
    Warning,
    Error,
}

/// <summary>One product's row (Apps page) or line (Updates page). Immutable: a new snapshot makes new rows.</summary>
internal sealed class ProductRowViewModel
{
    /// <param name="detected">Detection has run. Before it has, the row claims no state: no pill, no button.</param>
    /// <param name="progress">Its install state (null: idle).</param>
    /// <param name="revitRunning">A Revit.exe runs now.</param>
    /// <param name="perMachineTray">The tray runs from a per-machine install (RST is then never installed from it).</param>
    public ProductRowViewModel(ProductStatus status, ICommand releasePage, bool detected = true)
        : this(status, RowCommands.ReleasePageOnly(releasePage), detected, null, false, false)
    {
    }

    public ProductRowViewModel(ProductStatus status, RowCommands commands, bool detected, InstallProgress? progress,
                               bool revitRunning, bool perMachineTray)
    {
        Status = status ?? throw new ArgumentNullException(nameof(status));
        Commands = commands ?? throw new ArgumentNullException(nameof(commands));
        State = status.State;
        IsKnown = detected;
        Progress = progress ?? InstallProgress.Idle;
        RevitRunning = revitRunning;
        IsForEveryone = status.Installation.Kind == InstallKind.Installer
                        && (status.Installation.Installed!.Context == InstallContext.Machine || (status.Product.IsRst && perMachineTray));
    }

    public ProductStatus Status { get; }

    public ProductState State { get; }

    public InstallProgress Progress { get; }

    public bool RevitRunning { get; }

    /// <summary>Installed for everyone on this PC: this per-user tray never installs over it.</summary>
    public bool IsForEveryone { get; }

    /// <summary>Detection has run, so the state is real (false: "Looking for installed apps…").</summary>
    public bool IsKnown { get; }

    public RowCommands Commands { get; }

    public string Id => Status.Product.Id;

    public string Name => Status.Product.Name;

    public string Description => Status.Product.Description;

    public string InstalledText => Status.Installation.Kind switch
    {
        InstallKind.NotInstalled => TrayStrings.App_NotInstalled,
        InstallKind.Manual => TrayStrings.App_InstalledManually,
        InstallKind.Unknown => TrayStrings.App_Unknown,
        _ => InstalledVersion(Status.Installation.Installed!),
    };

    /// <summary>Windows Installer has it: the one-line layouts show its version (otherwise the pill says it all).</summary>
    public bool ShowInstalledVersion => Status.Installation.Kind == InstallKind.Installer;

    /// <summary>
    /// The latest version; after a failed check the last known one marked "(couldn't check)", or "Couldn't check";
    /// "No release yet" when the last good check found none; "Not checked yet" before any check.
    /// </summary>
    public string LatestText => Status switch
    {
        { LatestFailed: true, Latest: { } known } => TrayStrings.App_LatestFailedKnown(known.ToString()),
        { LatestFailed: true } => TrayStrings.App_LatestFailed,
        { Latest: { } latest } => latest.ToString(),
        { NoRelease: true } => TrayStrings.App_LatestNone,
        _ => TrayStrings.App_LatestNotChecked,
    };

    public string StateText => State switch
    {
        ProductState.UpToDate => TrayStrings.State_UpToDate,
        ProductState.UpdateAvailable => TrayStrings.State_UpdateAvailable,
        ProductState.InstalledManually => TrayStrings.State_InstalledManually,
        ProductState.Installed => TrayStrings.State_Installed,
        ProductState.Unknown => TrayStrings.State_Unknown,
        _ => TrayStrings.State_NotInstalled,
    };

    public bool IsUpToDate => IsKnown && State == ProductState.UpToDate;

    public bool IsUpdateAvailable => IsKnown && State == ProductState.UpdateAvailable;

    public bool IsManual => IsKnown && State == ProductState.InstalledManually;

    /// <summary>Not installed, installed with the latest not known, or unknown: the neutral pill.</summary>
    public bool IsNeutral => IsKnown && State is ProductState.NotInstalled or ProductState.Installed or ProductState.Unknown;

    /// <summary>A failed upgrade removed it: the version to reinstall (only while it is not installed).</summary>
    public ReleaseVersion? ReinstallVersion =>
        IsKnown && State == ProductState.NotInstalled && !Progress.IsBusy && Progress.Previous is { } previous ? previous : null;

    /// <summary>
    /// The one button: Update (an update is out), Install (a companion that is not installed), each "… when Revit
    /// closes" while Revit runs, or "Reinstall previous version (x.y.z)". Null: no button (unknown state, installed
    /// manually, for everyone on this PC, not installable, or an install under way).
    /// </summary>
    public string? ActionText
    {
        get
        {
            if (!IsKnown || Progress.IsBusy || !Status.Product.IsInstallable) return null;
            if (ReinstallVersion is { } previous)
                return RevitRunning ? TrayStrings.App_ReinstallPreviousWhenRevitCloses(previous.ToString())
                                    : TrayStrings.App_ReinstallPrevious(previous.ToString());
            return State switch
            {
                ProductState.UpdateAvailable when !IsForEveryone =>
                    RevitRunning ? TrayStrings.App_UpdateWhenRevitCloses : TrayStrings.App_Update,
                ProductState.NotInstalled when !Status.Product.IsRst && Status.Latest is not null =>
                    RevitRunning ? TrayStrings.App_InstallWhenRevitCloses : TrayStrings.App_Install,
                _ => null,
            };
        }
    }

    public bool HasAction => ActionText is not null;

    /// <summary>The button acts (it is shown only when it can).</summary>
    public bool CanAct => HasAction;

    /// <summary>Cancel: while it downloads, waits for Revit, or waits to retry (never once msiexec runs).</summary>
    public bool CanCancel => Progress.Phase is InstallPhase.Downloading or InstallPhase.WaitingForRevit or InstallPhase.Queued
                             or InstallPhase.RetryLater;

    /// <summary>The right-hand column shows a button.</summary>
    public bool HasButtons => HasAction || CanCancel;

    public bool ShowManualNote => IsManual;

    /// <summary>The status line under the row: what the install is doing or how it ended; who updates a per-machine install.</summary>
    public string? NoteText
    {
        get
        {
            if (!IsKnown) return null;
            var name = Name;
            var version = Progress.Version?.ToString() ?? Status.Latest?.ToString() ?? "";
            switch (Progress.Phase)
            {
                case InstallPhase.Downloading: return TrayStrings.Install_Downloading(name, version);
                case InstallPhase.WaitingForRevit: return TrayStrings.Install_WaitingForRevit(name, version);
                case InstallPhase.Queued:
                    return Status.Product.IsRst ? TrayStrings.Install_QueuedRst(name, version) : TrayStrings.Install_Queued(name, version);
                case InstallPhase.Installing: return TrayStrings.Install_Installing(name, version);
                case InstallPhase.RetryLater: return TrayStrings.Install_RetryLater(name, version);
                case InstallPhase.HandingOff: return TrayStrings.Install_HandingOff(name, version);
                case InstallPhase.Installed:
                    return Progress.RestartNeeded ? TrayStrings.Install_DoneRestart(name, version) : TrayStrings.Install_Done(name, version);
                case InstallPhase.Refused when Progress.Problem == InstallProblem.PerMachine:
                    return PerMachineText(name, version);
                case InstallPhase.Refused:
                    return TrayStrings.Install_Refused(name, version, TrayStrings.Problem(Progress.Problem, Progress.Net, Progress.Status));
                case InstallPhase.Failed:
                    return Progress.Problem switch
                    {
                        InstallProblem.InstallerBusy => TrayStrings.Install_Busy(name, version),
                        InstallProblem.UpdaterFailed => TrayStrings.Install_UpdaterFailed(name, version),
                        _ when ReinstallVersion is not null => TrayStrings.Install_FailedGone(name, version, Progress.ExitCode ?? 0),
                        _ => TrayStrings.Install_Failed(name, version, Progress.ExitCode ?? 0),
                    };
            }
            // Idle: a product installed for everyone on this PC with an update out says who can update it.
            return IsForEveryone && State == ProductState.UpdateAvailable ? PerMachineText(name, Status.Latest!.Value.ToString()) : null;
        }
    }

    public bool HasNote => NoteText is not null;

    public RowNoteKind NoteKind => !HasNote ? RowNoteKind.None : Progress.Phase switch
    {
        InstallPhase.Installed => RowNoteKind.Success,
        InstallPhase.Failed => RowNoteKind.Error,
        InstallPhase.Refused when Progress.Problem == InstallProblem.PerMachine => RowNoteKind.Warning,
        InstallPhase.Refused => RowNoteKind.Error,
        InstallPhase.Idle => RowNoteKind.Warning,
        _ => RowNoteKind.Progress,
    };

    public bool IsNoteProgress => NoteKind == RowNoteKind.Progress;

    public bool IsNoteSuccess => NoteKind == RowNoteKind.Success;

    public bool IsNoteWarning => NoteKind == RowNoteKind.Warning;

    public bool IsNoteError => NoteKind == RowNoteKind.Error;

    /// <summary>An msiexec log to open (a finished or failed install).</summary>
    public string? LogPath => Progress.Phase is InstallPhase.Failed or InstallPhase.Installed ? Progress.LogPath : null;

    public bool HasLog => !string.IsNullOrEmpty(LogPath);

    /// <summary>The per-machine note carries the Release page link (spec #20 §5: "with the release link").</summary>
    public bool ShowNoteReleasePage => HasNote && IsForEveryone && State == ProductState.UpdateAvailable && !HasAction;

    public bool HasNoteLinks => HasLog || ShowNoteReleasePage;

    /// <summary>The release page link: beside every button, and with the per-machine note.</summary>
    public bool ShowReleasePage => HasAction || (IsForEveryone && State == ProductState.UpdateAvailable);

    public ICommand ReleasePageCommand => Commands.ReleasePage;

    public ICommand ActCommand => Commands.Act;

    public ICommand CancelCommand => Commands.Cancel;

    public ICommand OpenLogCommand => Commands.OpenLog;

    private string PerMachineText(string name, string version) =>
        Status.Product.IsRst ? TrayStrings.Install_PerMachineRst(name, version) : TrayStrings.Install_PerMachine(name, version);

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
    private readonly RowCommands _commands;
    private UpdateSnapshot _snapshot;
    private bool _attached;

    public UpdatesViewModel(ITrayUpdatesHost host, Func<UpdateChecksState> checks)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _checks = checks ?? throw new ArgumentNullException(nameof(checks));
        _commands = new RowCommands(
            new RelayCommand<ProductRowViewModel>(row => _host.OpenReleasePage(row.Status.Product)),
            new RelayCommand<ProductRowViewModel>(Act),
            new RelayCommand<ProductRowViewModel>(row => _host.CancelInstall(row.Status.Product)),
            new RelayCommand<ProductRowViewModel>(row =>
            {
                if (row.LogPath is { } log) _host.OpenLog(log);
            }));
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

    /// <summary>
    /// "Last checked today at 09:14" (… yesterday, … 28 Oct 2026) or "Not checked yet". After a failed check:
    /// "Last successful check …" or "No successful check yet", so a failure never reads as a fresh check.
    /// </summary>
    public string LastCheckedText =>
        FormatLastChecked(_snapshot.LastCheckedUtc, _host.UtcNow, _host.TimeZone, afterFailure: _snapshot.LastCheckFailed);

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

    /// <summary>RST can be updated from here: the note beside the Update button, with the Release page link.</summary>
    public bool ShowRstInstallNote => Rst.IsUpdateAvailable && Rst.HasAction;

    /// <summary>Automatic checks are off (the setting or the preset).</summary>
    public bool ShowChecksOff => !_snapshot.AutomaticChecks;

    public string ChecksOffText => _checks().LockedOff ? TrayStrings.Updates_ChecksOffLocked : TrayStrings.Updates_ChecksOff;

    /// <summary>The last check failed for some product (and none is running now).</summary>
    public bool ShowCheckFailed => _snapshot.LastCheckFailed && !_snapshot.Checking;

    /// <summary>"Couldn't check: &lt;plain reason&gt;. RST tries again at the next daily check. …" (the detail is in the log).</summary>
    public string CheckFailedText => TrayStrings.Updates_CheckFailed(TrayStrings.Net(_snapshot.Failure, _snapshot.FailureStatus));

    /// <summary>Windows Installer could not be read for some product (its state is Unknown).</summary>
    public bool ShowDetectionFailed => _snapshot.Detected && _snapshot.DetectionFailed;

    /// <summary>Detection has not run yet.</summary>
    public bool IsDetecting => !_snapshot.Detected;

    /// <summary>Stops following the checker (the window closed).</summary>
    public void Detach()
    {
        if (!_attached) return;
        _attached = false;
        _host.Changed -= OnChanged;
    }

    /// <summary>Re-reads the checker's snapshot and the installs.</summary>
    public void Refresh()
    {
        _snapshot = _host.Current;
        Rows = Build(_snapshot);
        OnPropertyChanged(string.Empty); // every binding
        RelayCommand.Refresh();
    }

    internal static string FormatLastChecked(DateTimeOffset? checkedUtc, DateTimeOffset nowUtc, TimeZoneInfo zone, bool afterFailure = false)
    {
        if (checkedUtc is not { } at) return afterFailure ? TrayStrings.Updates_NoSuccessYet : TrayStrings.Updates_NeverChecked;
        var local = TimeZoneInfo.ConvertTime(at, zone);
        var today = TimeZoneInfo.ConvertTime(nowUtc, zone).Date;
        var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        var date = local.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        if (local.Date == today)
            return afterFailure ? TrayStrings.Updates_LastSuccessToday(time) : TrayStrings.Updates_LastCheckedToday(time);
        if (local.Date == today.AddDays(-1))
            return afterFailure ? TrayStrings.Updates_LastSuccessYesterday(time) : TrayStrings.Updates_LastCheckedYesterday(time);
        return afterFailure ? TrayStrings.Updates_LastSuccessOn(date, time) : TrayStrings.Updates_LastCheckedOn(date, time);
    }

    private void Act(ProductRowViewModel row)
    {
        if (!row.CanAct) return;
        if (row.ReinstallVersion is not null) _host.ReinstallPrevious(row.Status.Product);
        else _host.Install(row.Status.Product);
    }

    private IReadOnlyList<ProductRowViewModel> Build(UpdateSnapshot snapshot)
    {
        var installs = _host.Installs;
        var revit = snapshot.Detected && _host.RevitRunning;
        var perMachine = _host.PerMachineTray;
        return snapshot.Products.Select(s => new ProductRowViewModel(
            s, _commands, snapshot.Detected, installs.TryGetValue(s.Product.Id, out var p) ? p : null, revit, perMachine)).ToList();
    }

    private void OnChanged(object? sender, EventArgs e) => Refresh();
}
