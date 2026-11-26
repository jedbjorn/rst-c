// rst-c's own (not vendored): the RST tray's Add-ins page (spec #20 §3; F17 WU2). It shows the active profile and,
// per installed Revit year, what RST switched off and what it cannot switch back on, and runs Apply profile and the
// two launches through the Profile Loader's own code (RST.Core's TrayAddinService) and the Loader's own dialogs:
//   Apply profile    the Loader's Load Profile: "Adopt RSTify Presets?" when the Loader would ask it, the plan,
//                    Confirm Add-in Changes when add-ins would change (the Loader's rule: Disable unused is on,
//                    or the plan changes something), then the Add-in Check;
//   Launch with Profile   Apply profile, then a new Revit of that year;
//   Launch           (spec #38) the Loader's Restore All in every year, its report only when an add-in was switched
//                    back on or could not be, the blank active profile, then a new Revit of that year.
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
using RST.UI.Dialogs;
using RST.UI.Loader;
using RST.UI.Mvvm;
using RST.UI.Resources;
using RST.Core.Logging;

namespace RST.Tray.Windows;

/// <summary>One profile in the picker.</summary>
internal sealed record ProfileOption(ProfileEntry Entry)
{
    public string Name => Entry.Profile.ProfileName;

    public string? Id => Entry.Profile.Id;

    public override string ToString() => Name;
}

/// <summary>One installed Revit year on the page.</summary>
internal sealed class AddInsYearViewModel(TrayAddinYear year, AddInsViewModel owner)
{
    public TrayAddinYear Year { get; } = year;

    public ICommand LaunchCommand { get; } = new AsyncRelayCommand(() => owner.LaunchAsync(year.Year), () => owner.CanApply);

    /// <summary>Launch (clean): needs no profile (spec #38).</summary>
    public ICommand CleanLaunchCommand { get; } = new AsyncRelayCommand(() => owner.CleanLaunchAsync(year.Year), () => owner.CanLaunchClean);

    public string CleanLaunchAutomationId => "Tray.AddIns.CleanLaunch" + Year.Year.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string CleanLaunchHint => AddInsStrings.CleanLaunchHint(Year.Year);

    public string LaunchAutomationId => "Tray.AddIns.Launch" + Year.Year.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string LaunchHint => AddInsStrings.LaunchHint(Year.Year);

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
    private readonly IDialogService _messages;
    private readonly AddinsWork? _work;
    private readonly bool _inline;
    private UserProfilePrefs _prefs = new();
    private bool _isLoaded;
    private bool _isBusy;
    private string? _busyText;
    private string? _activeProfile;
    private bool _activeUnreadable;
    private IReadOnlyList<AddInsYearViewModel> _years = [];
    private IReadOnlyList<ProfileOption> _profiles = [];
    private ProfileOption? _selected;
    private bool _disableUnused;
    private string? _readError;
    private string? _actionError;
    private string? _result;
    private bool _launchSucceeded;
    private Task? _refreshing;
    private bool _replacingProfiles;

    /// <param name="backend">The Loader's services over this user's files.</param>
    /// <param name="dialogs">The Loader's Confirm Add-in Changes and report dialogs.</param>
    /// <param name="messages">The family confirm dialog the Loader asks "Adopt RSTify Presets?" with.</param>
    /// <param name="work">Tracks the file work in flight, so the tray waits for it before quitting (review N7).</param>
    /// <param name="runInline">Run the backend on the calling thread (RuntimeTests); the thread pool otherwise.</param>
    public AddInsViewModel(ITrayAddinsBackend backend, ILoaderDialogs dialogs, IDialogService messages, AddinsWork? work = null, bool runInline = false)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _messages = messages ?? throw new ArgumentNullException(nameof(messages));
        _work = work;
        _inline = runInline;
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => CanApply);
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(), () => IsIdle);
    }

    public ICommand ApplyCommand { get; }

    public ICommand RefreshCommand { get; }

    /// <summary>A read succeeded (until then the page claims nothing: no profile, no years).</summary>
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
    public bool IsLoading => !_isLoaded && _readError is null;

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

    public string ActiveProfileText => _activeProfile ?? (_activeUnreadable ? AddInsStrings.ActiveProfileUnreadable : AddInsStrings.ActiveProfileNone);

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
    public bool ShowNoRevit => _isLoaded && _years.Count == 0 && _readError is null;

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
            // The picker's write-back while Show swaps the list is not a pick (SC-019).
            if (_replacingProfiles) return;
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

    /// <summary>Launch (clean) needs only a read page that is idle: no profile at all (spec #38).</summary>
    public bool CanLaunchClean => IsIdle && _isLoaded;

    /// <summary>
    /// The warning banner: the last action's failure, which stays until the next action (a read after it does not
    /// clear it; review B3), else why the add-ins could not be read.
    /// </summary>
    public string? ErrorText => _actionError ?? _readError;

    public bool HasError => ErrorText is not null;

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

    /// <summary>A fresh window must not say it is still starting a Revit from an earlier open. Errors stay.</summary>
    public void ClearLaunchSuccess()
    {
        if (!_launchSucceeded) return;
        ResultText = null;
        _launchSucceeded = false;
    }

    public string NextStartNote => ActivationWording.NextStart;

    /// <summary>Reads the page the first time it is shown; later calls are cheap no-ops while a read runs.</summary>
    public Task EnsureLoadedAsync() => _isLoaded || _refreshing is not null ? _refreshing ?? Task.CompletedTask : RefreshAsync();

    /// <summary>
    /// Reads the active profile, the years, the profiles and the saved choices again. While a read runs, it is
    /// joined; with <paramref name="fresh"/> (after an action) a new read follows it, so what the page shows is read
    /// after the action (review N6).
    /// </summary>
    public Task RefreshAsync(bool fresh = false)
    {
        if (_refreshing is { IsCompleted: false } running)
        {
            if (!fresh) return running;
            _refreshing = ThenRefresh(running);
            return _refreshing;
        }
        _refreshing = DoRefreshAsync();
        return _refreshing;
    }

    private async Task ThenRefresh(Task running)
    {
        try
        {
            await running;
        }
        catch (Exception)
        {
            // Reported by that read.
        }
        await DoRefreshAsync();
    }

    private async Task DoRefreshAsync()
    {
        try
        {
            var snapshot = await Run(() =>
            {
                var running = _backend.RunningYears();
                var status = _backend.Status(running);
                // A profile loaded anywhere, the Loader in Revit included, is the one Launch with Profile offers next.
                if (status.Error is null && !string.IsNullOrEmpty(status.ActiveProfile)) Remember(status.ActiveProfile, status.ActiveProfileId);
                return (Status: status, Profiles: _backend.Profiles(), Prefs: _backend.Prefs(), Remembered: _backend.RememberedProfile());
            });
            _prefs = snapshot.Prefs;
            Show(snapshot.Status, snapshot.Profiles, snapshot.Remembered);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "add-ins: the page could not read the add-ins");
            SetReadError(AddInsStrings.ReadFailed(ex.Message));
        }
    }

    /// <summary>Remembers the last loaded profile. Off the UI thread. A failed write is logged: it only costs the default pick.</summary>
    private void Remember(string name, string? id)
    {
        try
        {
            _backend.RememberLoadedProfile(name, id);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "profiles: the last loaded profile {Name} was not remembered", name);
        }
    }

    private void Show(TrayAddinStatus status, IReadOnlyList<ProfileEntry> profiles, LastLoadedProfile? remembered)
    {
        if (status.Error is not null)
        {
            // Nothing new is known: keep what was shown before (if anything), and say why (review N8).
            Log.Warning("add-ins: the page could not read the add-ins: {Error}", status.Error);
            SetReadError(AddInsStrings.ReadFailed(status.Error));
            return;
        }
        SetReadError(null);
        _activeProfile = string.IsNullOrEmpty(status.ActiveProfile) ? null : status.ActiveProfile;
        _activeUnreadable = status.ActiveProfileUnreadable;
        OnPropertyChanged(nameof(ActiveProfileText));
        OnPropertyChanged(nameof(HasActiveProfile));

        var keep = _selected;
        var options = profiles.Select(p => new ProfileOption(p)).ToList();
        // Keep the user's pick; else the active profile; else the last loaded one (gone: skipped); else the first.
        var pick = options.FirstOrDefault(o => keep is not null && Same(o, keep))
                   ?? options.FirstOrDefault(o => _activeProfile is not null && o.Name == _activeProfile)
                   ?? RememberedOption(options, remembered)
                   ?? options.FirstOrDefault();
        var kept = pick is not null && keep is not null && Same(pick, keep);
        IsLoaded = true;
        Years = status.Years.Select(y => new AddInsYearViewModel(y, this)).ToList();

        // Every read makes new options (Profile is a class), so the picker's selected item is never in the new list.
        // Left to itself, the picker clears its selection when the list is swapped and writes null back through its
        // two-way binding. SC-019: taken as a pick, that null re-seeded "Disable unused add-ins" to off. SC-048:
        // ignored, the binding read the pick back into SelectedItem while the ComboBox was still inside its own
        // selection change, so SelectedItem was right but the box showed nothing, and the later SelectedProfile
        // change, the same value, changed nothing.
        // So the page swaps the list in three steps the picker follows: it lets go of its item first, then gets the
        // new list, then the pick, which is in that list. The picker never holds an item the list lacks, and none of
        // these is a pick (the switch is not re-seeded).
        _replacingProfiles = true;
        try
        {
            _selected = null;
            OnPropertyChanged(nameof(SelectedProfile));
            Profiles = options;
            _selected = pick;
            OnPropertyChanged(nameof(SelectedProfile));
        }
        finally
        {
            _replacingProfiles = false;
        }
        // The same profile keeps the page's switch (after Apply it is what was just saved); a different one is
        // seeded from its saved choice, as picking it does.
        if (!kept) DisableUnused = pick is not null && TrayAddinService.Choices(pick.Entry, _prefs).DisableUnused;
        RaiseStateChanged();
        Log.Information("add-ins: page read: active={Active} years={Years} profiles={Profiles}",
            _activeProfile ?? "(none)", string.Join(",", status.Years.Select(y => y.Year + (y.Running ? " (open)" : "") + ":" + y.SwitchedOff.Count + " off, " + y.CouldNotChange.Count + " blocked")),
            options.Count);
    }

    /// <summary>The remembered profile among <paramref name="options"/>, found as the Loader finds one (ProfileStore.Resolve): id first, then name.</summary>
    private static ProfileOption? RememberedOption(IReadOnlyList<ProfileOption> options, LastLoadedProfile? remembered)
    {
        if (remembered is null) return null;
        return (string.IsNullOrEmpty(remembered.Id) ? null : options.FirstOrDefault(o => string.Equals(o.Id, remembered.Id, StringComparison.Ordinal)))
               ?? options.FirstOrDefault(o => string.Equals(o.Name, remembered.Name, StringComparison.Ordinal));
    }

    private static bool Same(ProfileOption a, ProfileOption b) =>
        !string.IsNullOrEmpty(a.Id) ? a.Id == b.Id : a.Name == b.Name;

    /// <summary>
    /// Apply profile: the Loader's Load Profile (spec #20 §3). The Loader's "Adopt RSTify Presets?" when it would
    /// ask (review B2); the plan; Confirm Add-in Changes when add-ins would change; the load; the Add-in Check with
    /// the running-Revit notes; then the page again. A failure stays on the page (review B3).
    /// </summary>
    public Task ApplyAsync() => ApplyProfileAsync(null);

    /// <summary>Apply the pick through the Loader, then start a new instance of the chosen installed year.</summary>
    public Task LaunchAsync(int year) => ApplyProfileAsync(year);

    private async Task ApplyProfileAsync(int? launchYear)
    {
        if (!CanApply || _selected is not { } option) return;
        if (launchYear is { } requested && !Years.Any(y => y.Year.Year == requested)) return;
        await JoinRefresh();
        try { await ApplySelectedAsync(option, launchYear); }
        finally { Idle(); }
    }

    private async Task ApplySelectedAsync(ProfileOption option, int? launchYear)
    {
        var entry = option.Entry;
        StartAction();
        Busy(AddInsStrings.BusyPlanning);
        Log.Information("add-ins: Apply profile {Name} (disable unused {Disable})", option.Name, DisableUnused);

        // The saved choices as they are on disk now, not as the page last read them: a Loader in an open Revit may
        // have changed this profile's RSTify tabs, or answered the adopt question, since (delta review D1).
        try
        {
            _prefs = await Run(_backend.Prefs);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "add-ins: saved choices not re-read; using the page's last read");
        }

        // The Loader asks once per profile per machine, before its saved choices exist (ProfileLoaderViewModel.Select
        // and AskAdoptPresets): the same rule, the same dialog, the same record.
        var hidden = TrayAddinService.Choices(entry, _prefs).HiddenTabs;
        if (TrayAddinService.AsksToAdoptPresets(entry, _prefs))
        {
            var yes = _messages.Confirm(RstifyRules.AdoptPresetsTitle, RstifyRules.AdoptPresetsMessage(entry.Profile), [],
                UiStrings.Loader_Continue, danger: false, ProfileLoaderViewModel.AdoptPresetsDialogId);
            Log.Information("add-ins: adopt RSTify presets for {Name}: {Answer}", option.Name, yes ? "yes" : "no");
            try
            {
                var (adoptedDisable, adoptedHidden) = await Run(() => _backend.AdoptPresets(entry, yes));
                _prefs.Profiles[entry.Profile.Id!] = new UserProfilePrefsEntry
                {
                    HiddenTabs = [.. adoptedHidden],
                    DisableUnusedAddins = adoptedDisable,
                    PresetsAdopted = true,
                };
                // Yes takes the presets; no keeps what the user set on the page (the Loader asks before they can).
                if (yes)
                {
                    DisableUnused = adoptedDisable;
                    hidden = adoptedHidden;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "add-ins: recording the RSTify presets answer failed");
            }
        }
        var disable = DisableUnused;

        Busy(AddInsStrings.BusyPlanning);
        ActivationPlan? plan = null;
        string? planFailure = null;
        try
        {
            var outcome = await Run(() => _backend.PlanApply(entry, disable));
            if (outcome.Error is not null)
            {
                Idle();
                FailAction(AddInsStrings.ApplyFailed(outcome.Error));
                Busy(AddInsStrings.Loading);
                await RefreshAsync(fresh: true);
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
            FailAction(AddInsStrings.ApplyFailed(ex.Message));
            Busy(AddInsStrings.Loading);
            await RefreshAsync(fresh: true);
            return;
        }
        Idle();
        if (!result.Ok)
        {
            Log.Warning("add-ins: Apply failed: {Warnings}", string.Join(" ", result.Warnings));
            FailAction(AddInsStrings.ApplyFailed(string.Join(" ", result.Warnings.DefaultIfEmpty(UiStrings.Loader_LoadFailedText))));
            Busy(AddInsStrings.Loading);
            await RefreshAsync(fresh: true);
            return;
        }

        await Run(() =>
        {
            Remember(entry.Profile.ProfileName, entry.Profile.Id);
            return true;
        });
        var report = result.Report;
        if (planFailure is not null) report = report with { Notes = [planFailure, .. report.Notes] };
        report = TrayAddinService.WithRunningNotes(report, running);
        _dialogs.ShowAddinReport(AddinReportModel.ForLoad(option.Name, report, TrayAddinService.QaFor(entry, plan)));
        ResultText = AddInsStrings.Applied(option.Name);
        if (launchYear is { } year)
        {
            Busy(AddInsStrings.BusyLaunching(year));
            try
            {
                // The shared profile and add-in settings are saved, and the existing report has been dismissed.
                // Running instances are left alone; every click starts a new instance using those shared files.
                var pid = await Run(() => _backend.LaunchRevit(year));
                Log.Information("profiles: started Revit {Year} with {Profile} (pid {Pid})", year, option.Name, pid);
                _launchSucceeded = true;
                ResultText = AddInsStrings.Launched(year, option.Name);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "profiles: Revit {Year} could not start after Apply", year);
                ResultText = null;
                FailAction(AddInsStrings.LaunchFailed(year, ex.Message));
            }
            finally { Idle(); }
        }
        Busy(AddInsStrings.Loading);
        await RefreshAsync(fresh: true);
    }


    /// <summary>
    /// Launch (clean, spec #38): the Loader's Restore All in every installed year (the blank active profile is shared
    /// by every year, so none is left half-restored); its report, with the running-Revit notes, only when an add-in
    /// was switched back on or could not be; the blank active profile; then a new Revit of that year. A restore or a
    /// blank that fails starts nothing and stays on the page. The remembered last profile is kept.
    /// </summary>
    public async Task CleanLaunchAsync(int year)
    {
        if (!CanLaunchClean || !Years.Any(y => y.Year.Year == year)) return;
        await JoinRefresh();
        try { await CleanLaunchCoreAsync(year); }
        finally { Idle(); }
    }

    private async Task CleanLaunchCoreAsync(int year)
    {
        StartAction();
        Log.Information("profiles: clean launch of Revit {Year}", year);
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
            Log.Error(ex, "profiles: clean launch: Restore All threw; Revit {Year} not started", year);
            Idle();
            FailAction(UiStrings.Loader_RestoreError(ex.Message));
            Busy(AddInsStrings.Loading);
            await RefreshAsync(fresh: true);
            return;
        }
        if (!outcome.Ok || outcome.Addins is null)
        {
            Log.Warning("profiles: clean launch: Restore All failed; Revit {Year} not started", year);
            Idle();
            FailAction(UiStrings.Loader_RestoreFailedText(string.IsNullOrEmpty(outcome.Error) ? UiStrings.Loader_UnknownError : outcome.Error));
            Busy(AddInsStrings.Loading);
            await RefreshAsync(fresh: true);
            return;
        }
        // Something happened: an add-in was switched back on, or one could not be. The report says which; after it
        // the launch goes on. The page stays busy under the report, so no second launch starts meanwhile.
        Busy(AddInsStrings.BusyLaunching(year));
        if (outcome.Addins.Outcomes.Count > 0)
            _dialogs.ShowAddinReport(AddinReportModel.ForRestoreAll(TrayAddinService.WithRunningNotes(outcome.Addins, running)));

        try
        {
            await Run(() =>
            {
                _backend.ClearActiveProfile();
                return true;
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "profiles: clean launch: the blank active profile was not written; Revit {Year} not started", year);
            Idle();
            FailAction(UiStrings.Loader_UnloadError(ex.Message));
            Busy(AddInsStrings.Loading);
            await RefreshAsync(fresh: true);
            return;
        }
        try
        {
            var pid = await Run(() => _backend.LaunchRevit(year));
            Log.Information("profiles: started Revit {Year} with no profile (pid {Pid})", year, pid);
            _launchSucceeded = true;
            ResultText = AddInsStrings.CleanLaunched(year);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "profiles: Revit {Year} could not start after the clean launch's restore", year);
            FailAction(AddInsStrings.CleanLaunchFailed(year, ex.Message));
        }
        Busy(AddInsStrings.Loading);
        await RefreshAsync(fresh: true);
    }

    /// <summary>An action waits for a read in flight, so it acts on, and then shows, what is on disk (review N6).</summary>
    private async Task JoinRefresh()
    {
        if (_refreshing is not { IsCompleted: false } running) return;
        Busy(AddInsStrings.Loading);
        try
        {
            await running;
        }
        catch (Exception)
        {
            // Reported by that read.
        }
        Idle();
    }

    private void StartAction()
    {
        _launchSucceeded = false;
        _actionError = null;
        ResultText = null;
        RaiseError();
    }

    private void FailAction(string message)
    {
        Log.Warning("add-ins: {Failure}", message);
        _actionError = WithoutPaths(message);
        RaiseError();
    }

    /// <summary>
    /// A failure for the banner: a full path Windows quotes in its messages (<c>'C:\Users\…\active_profile.json'</c>)
    /// becomes its file name. The log keeps the full message (delta review D3).
    /// </summary>
    internal static string WithoutPaths(string message) =>
        System.Text.RegularExpressions.Regex.Replace(message, @"'(?:[A-Za-z]:|\\)\\[^']*\\([^'\\]+)'", "'$1'");

    private void SetReadError(string? message)
    {
        if (_readError == message) return;
        _readError = message;
        RaiseError();
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(ShowNoRevit));
    }

    private void RaiseError()
    {
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(HasError));
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

    private Task<T> Run<T>(Func<T> work) =>
        _inline ? Task.FromResult(work()) : _work is not null ? _work.Run(work) : Task.Run(work);
}
