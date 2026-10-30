// rst-c's own (not vendored): the RST tray's Add-ins page (spec #20 §3; F17 WU2). It shows the active profile and,
// per installed Revit year, what RST switched off and what it cannot switch back on, and runs Apply profile and
// Restore All through the Profile Loader's own code (RST.Core's TrayAddinService) and the Loader's own dialogs:
//   Apply profile    the Loader's Load Profile: the plan, Confirm Add-in Changes when add-ins would change (the
//                    Loader's rule: Disable unused is on, or the plan changes something), then the Add-in Check;
//   Restore All      the Loader's Restore All Add-ins and its report.
// Every report ends with the Loader's "Add-in changes apply the next time Revit starts.", plus "Revit <year> is
// open: restart it to see the change." for each Revit open now. The files and the plans run off the UI thread;
// the dialogs are modal on it. Built lazily: the page reads the disk when it is first shown, after each action and
// on Refresh, never on a timer.

using System.Windows.Input;
using RST.Core.Activation;
using RST.Core.Configuration;
using RST.Core.Loader;
using RST.Core.Profiles;
using RST.Core.Tray;
using RST.Tray.AddIns;
using RST.Tray.Resources;
using RST.UI.Loader;
using RST.UI.Mvvm;
using RST.UI.Resources;
using Serilog;

namespace RST.Tray.Windows;

/// <summary>One profile in the picker.</summary>
internal sealed record ProfileOption(ProfileEntry Entry)
{
    public string Name => Entry.Profile.ProfileName;

    public string? Id => Entry.Profile.Id;

    public override string ToString() => Name;
}

/// <summary>One installed Revit year on the page.</summary>
internal sealed class AddInsYearViewModel(TrayAddinYear year)
{
    public TrayAddinYear Year { get; } = year;

    public string Label => Year.Label;

    public bool IsRunning => Year.Running;

    public IReadOnlyList<string> SwitchedOff => Year.SwitchedOff;

    public IReadOnlyList<string> CouldNotChange => Year.CouldNotChange;

    public IReadOnlyList<string> Notes => Year.Notes;

    public bool HasSwitchedOff => SwitchedOff.Count > 0;

    public bool HasCouldNotChange => CouldNotChange.Count > 0;

    public bool HasNotes => Notes.Count > 0;

    public bool IsClear => Year.IsClear;

    public string AutomationId => "Tray.AddIns.Year" + Year.Year.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class AddInsViewModel : ObservableObject
{
    private readonly ITrayAddinsBackend _backend;
    private readonly ILoaderDialogs _dialogs;
    private readonly bool _inline;
    private UserProfilePrefs _prefs = new();
    private bool _isLoaded;
    private bool _isBusy;
    private string? _busyText;
    private string? _activeProfile;
    private IReadOnlyList<AddInsYearViewModel> _years = [];
    private IReadOnlyList<ProfileOption> _profiles = [];
    private ProfileOption? _selected;
    private bool _disableUnused;
    private string? _error;
    private string? _result;
    private Task? _refreshing;

    /// <param name="backend">The Loader's services over this user's files.</param>
    /// <param name="dialogs">The Loader's Confirm Add-in Changes and report dialogs.</param>
    /// <param name="runInline">Run the backend on the calling thread (RuntimeTests); the thread pool otherwise.</param>
    public AddInsViewModel(ITrayAddinsBackend backend, ILoaderDialogs dialogs, bool runInline = false)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _inline = runInline;
        ApplyCommand = new RelayCommand(() => _ = ApplyAsync(), () => CanApply);
        RestoreAllCommand = new RelayCommand(() => _ = RestoreAllAsync(), () => IsIdle && _isLoaded);
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => IsIdle);
    }

    public ICommand ApplyCommand { get; }

    public ICommand RestoreAllCommand { get; }

    public ICommand RefreshCommand { get; }

    /// <summary>The first read finished.</summary>
    public bool IsLoaded
    {
        get => _isLoaded;
        private set
        {
            if (!SetField(ref _isLoaded, value)) return;
            OnPropertyChanged(nameof(IsLoading));
            RaiseStateChanged();
        }
    }

    /// <summary>The first read is still running ("Reading your add-ins…").</summary>
    public bool IsLoading => !_isLoaded && _error is null;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetField(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(CanApply));
            RelayCommand.Refresh();
        }
    }

    public bool IsIdle => !_isBusy;

    public string? BusyText
    {
        get => _busyText;
        private set => SetField(ref _busyText, value);
    }

    public string ActiveProfileText => _activeProfile ?? AddInsStrings.ActiveProfileNone;

    public bool HasActiveProfile => _activeProfile is not null;

    public IReadOnlyList<AddInsYearViewModel> Years
    {
        get => _years;
        private set
        {
            if (!SetField(ref _years, value)) return;
            RaiseStateChanged();
        }
    }

    public bool HasYears => _years.Count > 0;

    /// <summary>Read, and no Revit 2025+ is installed.</summary>
    public bool ShowNoRevit => _isLoaded && _years.Count == 0 && _error is null;

    public IReadOnlyList<ProfileOption> Profiles
    {
        get => _profiles;
        private set
        {
            if (!SetField(ref _profiles, value)) return;
            OnPropertyChanged(nameof(HasProfiles));
            OnPropertyChanged(nameof(ShowNoProfiles));
        }
    }

    public bool HasProfiles => _profiles.Count > 0;

    public bool ShowNoProfiles => _isLoaded && _profiles.Count == 0;

    /// <summary>The profile Apply uses; picking one seeds "Disable unused add-ins" from its saved choice, as the Loader does.</summary>
    public ProfileOption? SelectedProfile
    {
        get => _selected;
        set
        {
            if (!SetField(ref _selected, value)) return;
            DisableUnused = value is not null && TrayAddinService.Choices(value.Entry, _prefs).DisableUnused;
            OnPropertyChanged(nameof(CanApply));
            RelayCommand.Refresh();
        }
    }

    /// <summary>The Loader's "Disable unused add-ins" for this Apply.</summary>
    public bool DisableUnused
    {
        get => _disableUnused;
        set => SetField(ref _disableUnused, value);
    }

    public bool CanApply => IsIdle && _isLoaded && _selected is not null;

    /// <summary>Why the add-ins could not be read, or the last action failed (a warning banner).</summary>
    public string? ErrorText
    {
        get => _error;
        private set
        {
            if (!SetField(ref _error, value)) return;
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(ShowNoRevit));
        }
    }

    public bool HasError => _error is not null;

    /// <summary>The last action's one-line result (an info banner).</summary>
    public string? ResultText
    {
        get => _result;
        private set
        {
            if (SetField(ref _result, value)) OnPropertyChanged(nameof(HasResult));
        }
    }

    public bool HasResult => _result is not null;

    public string NextStartNote => ActivationWording.NextStart;

    /// <summary>Reads the page the first time it is shown; later calls are cheap no-ops while a read runs.</summary>
    public Task EnsureLoadedAsync() => _isLoaded || _refreshing is not null ? _refreshing ?? Task.CompletedTask : RefreshAsync();

    /// <summary>Reads the active profile, the years, the profiles and the saved choices again.</summary>
    public Task RefreshAsync()
    {
        if (_refreshing is { IsCompleted: false } running) return running;
        _refreshing = DoRefreshAsync();
        return _refreshing;
    }

    private async Task DoRefreshAsync()
    {
        try
        {
            var snapshot = await Run(() =>
            {
                var running = _backend.RunningYears();
                return (Status: _backend.Status(running), Profiles: _backend.Profiles(), Prefs: _backend.Prefs());
            });
            _prefs = snapshot.Prefs;
            Show(snapshot.Status, snapshot.Profiles);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "add-ins: the page could not read the add-ins");
            ErrorText = AddInsStrings.ReadFailed(ex.Message);
        }
        finally
        {
            _refreshing = null;
        }
    }

    private void Show(TrayAddinStatus status, IReadOnlyList<ProfileEntry> profiles)
    {
        _activeProfile = string.IsNullOrEmpty(status.ActiveProfile) ? null : status.ActiveProfile;
        OnPropertyChanged(nameof(ActiveProfileText));
        OnPropertyChanged(nameof(HasActiveProfile));
        ErrorText = status.Error is null ? null : AddInsStrings.ReadFailed(status.Error);

        var keep = _selected;
        var options = profiles.Select(p => new ProfileOption(p)).ToList();
        IsLoaded = true;
        Years = status.Years.Select(y => new AddInsYearViewModel(y)).ToList();
        Profiles = options;
        // Keep the user's pick; else the active profile; else the first.
        var pick = options.FirstOrDefault(o => keep is not null && Same(o, keep))
                   ?? options.FirstOrDefault(o => _activeProfile is not null && o.Name == _activeProfile)
                   ?? options.FirstOrDefault();
        if (pick is not null && keep is not null && Same(pick, keep))
        {
            _selected = pick;
            OnPropertyChanged(nameof(SelectedProfile));
        }
        else
        {
            SelectedProfile = pick;
        }
        RaiseStateChanged();
        Log.Information("add-ins: page read: active={Active} years={Years} profiles={Profiles}",
            _activeProfile ?? "(none)", string.Join(",", status.Years.Select(y => y.Year + (y.Running ? " (open)" : "") + ":" + y.SwitchedOff.Count + " off, " + y.CouldNotChange.Count + " blocked")),
            options.Count);
    }

    private static bool Same(ProfileOption a, ProfileOption b) =>
        !string.IsNullOrEmpty(a.Id) ? a.Id == b.Id : a.Name == b.Name;

    /// <summary>
    /// Apply profile: the Loader's Load Profile (spec #20 §3). The plan; Confirm Add-in Changes when add-ins would
    /// change; the load; the Add-in Check with the running-Revit notes; then the page again.
    /// </summary>
    public async Task ApplyAsync()
    {
        if (!CanApply || _selected is not { } option) return;
        var entry = option.Entry;
        var disable = DisableUnused;
        var (_, hidden) = TrayAddinService.Choices(entry, _prefs);
        ErrorText = null;
        ResultText = null;
        Log.Information("add-ins: Apply profile {Name} (disable unused {Disable})", option.Name, disable);

        Busy(AddInsStrings.BusyPlanning);
        ActivationPlan? plan = null;
        string? planFailure = null;
        try
        {
            var outcome = await Run(() => _backend.PlanApply(entry, disable));
            if (outcome.Error is not null)
            {
                Idle();
                ErrorText = AddInsStrings.ApplyFailed(outcome.Error);
                return;
            }
            plan = outcome.Plan;
        }
        catch (Exception ex)
        {
            // As the Loader: a plan that cannot be made changes no add-in, and the report says so.
            Log.Error(ex, "add-ins: plan failed; applying without add-in changes");
            planFailure = UiStrings.Loader_PlanFailed(ex.Message);
        }

        Idle();
        if (plan is not null && (disable || plan.HasChanges) && !_dialogs.ConfirmAddinChanges(ConfirmAddinChangesModel.From(plan)))
        {
            Log.Information("add-ins: Apply cancelled at Confirm Add-in Changes");
            return;
        }

        Busy(AddInsStrings.BusyApplying(option.Name));
        LoadResult result;
        IReadOnlyList<int> running;
        try
        {
            result = await Run(() => _backend.Apply(entry, disable, hidden, plan));
            running = await Run(_backend.RunningYears);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "add-ins: Apply threw");
            Idle();
            ErrorText = AddInsStrings.ApplyFailed(ex.Message);
            return;
        }
        Idle();
        if (!result.Ok)
        {
            ErrorText = AddInsStrings.ApplyFailed(string.Join(" ", result.Warnings.DefaultIfEmpty(UiStrings.Loader_LoadFailedText)));
            await RefreshAsync();
            return;
        }

        var report = result.Report;
        if (planFailure is not null) report = report with { Notes = [planFailure, .. report.Notes] };
        report = TrayAddinService.WithRunningNotes(report, running);
        _dialogs.ShowAddinReport(AddinReportModel.ForLoad(option.Name, report, TrayAddinService.QaFor(entry, plan)));
        ResultText = AddInsStrings.Applied(option.Name);
        await RefreshAsync();
    }

    /// <summary>Restore All Add-ins: the Loader's, with its report and the running-Revit notes.</summary>
    public async Task RestoreAllAsync()
    {
        if (!IsIdle || !_isLoaded) return;
        ErrorText = null;
        ResultText = null;
        Log.Information("add-ins: Restore All");
        Busy(AddInsStrings.BusyRestoring);
        RestoreOutcome outcome;
        IReadOnlyList<int> running;
        try
        {
            outcome = await Run(_backend.RestoreAll);
            running = await Run(_backend.RunningYears);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "add-ins: Restore All threw");
            Idle();
            ErrorText = UiStrings.Loader_RestoreError(ex.Message);
            return;
        }
        Idle();
        if (!outcome.Ok || outcome.Addins is null)
        {
            ErrorText = UiStrings.Loader_RestoreFailedText(string.IsNullOrEmpty(outcome.Error) ? UiStrings.Loader_UnknownError : outcome.Error);
            return;
        }
        _dialogs.ShowAddinReport(AddinReportModel.ForRestoreAll(TrayAddinService.WithRunningNotes(outcome.Addins, running)));
        ResultText = AddInsStrings.Restored;
        await RefreshAsync();
    }

    private void Busy(string text)
    {
        BusyText = text;
        IsBusy = true;
    }

    private void Idle()
    {
        IsBusy = false;
        BusyText = null;
    }

    private void RaiseStateChanged()
    {
        OnPropertyChanged(nameof(HasYears));
        OnPropertyChanged(nameof(ShowNoRevit));
        OnPropertyChanged(nameof(ShowNoProfiles));
        OnPropertyChanged(nameof(CanApply));
        RelayCommand.Refresh();
    }

    private Task<T> Run<T>(Func<T> work) => _inline ? Task.FromResult(work()) : Task.Run(work);
}
