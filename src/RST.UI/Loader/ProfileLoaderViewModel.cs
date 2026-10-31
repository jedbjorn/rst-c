// rst-c's own (not vendored): the Profile Loader's view-model (spec #16, WU3), a port of
// profile_loader.html's script (911-1931): boot, card selection with the "Adopt RSTify Presets?"
// prompt, the opacity override, import, load (not-found warnings, add-in plan and confirm, the
// report, "Loading …" for 3 s, close), unload, delete, restore and close. Changes are the spec's
// Planner defaults: the detail badge and the plan go by id (5), the active profile is selected only
// after the tabs and prefs are read (6), and the load's add-in changes run off the UI thread (8).
// Spec #18 (feature #16): the plan the Confirm dialog shows is the plan the load runs; Load, Unload
// and Restore All show the same add-in report; RST 3.0's renamed add-ins are moved once at boot;
// the required add-in pills come from the add-in inventory.

using System.Collections.ObjectModel;
using System.Windows.Input;
using RST.Core.Activation;
using RST.Core.Configuration;
using RST.Core.Loader;
using RST.Core.Profiles;
using RST.Core.Scanning;
using RST.UI.Controls;
using RST.UI.Dialogs;
using RST.UI.Mvvm;
using RST.UI.Resources;
using RST.Core.Logging;

namespace RST.UI.Loader;

public sealed class ProfileLoaderViewModel : ObservableObject, IHasToast
{
    /// <summary>How long "Loading …" / "Unloading" shows before the window closes (showLoadingAndClose: 3000 ms).</summary>
    public static readonly TimeSpan CloseDelay = TimeSpan.FromSeconds(3);

    public const string AdoptPresetsDialogId = "AdoptPresetsDialog";
    public const string DeleteProfileDialogId = "DeleteProfileDialog";

    private readonly IProfileLoaderBackend _backend;
    private readonly IDialogService _dialogs;
    private readonly ILoaderDialogs _loaderDialogs;
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    private readonly Action<string> _openUrl;
    private readonly IReadOnlyList<ScannedCommand> _catalog;

    private AddinSessionContext _session = new();
    private IReadOnlyDictionary<string, AddinLookupEntry> _lookup = new Dictionary<string, AddinLookupEntry>();
    private UserProfilePrefs _prefs = new();
    private string? _activeId;
    private string? _activeName;
    private List<string> _previousHiddenTabs = [];
    private bool _dataLoaded;
    private bool _activeSelected;
    private bool _isWorking;

    private ProfileCardViewModel? _selected;
    private bool _isSelectionLoaded;
    private int _opacityOverride = 100;
    private int _committedOpacity = 100;
    private bool _disableUnused;
    private IReadOnlyList<TabToggleViewModel> _tabToggles = [];
    private bool _tabTogglesEmpty;
    private IReadOnlyList<RequiredAddinRowViewModel> _requiredAddins = [];
    private string _revitVersionDisplay = UiStrings.Loader_NoSelection;

    /// <param name="backend">The Loader services (a fake in tests).</param>
    /// <param name="dialogs">The family message and confirm dialogs, and the import file dialog.</param>
    /// <param name="loaderDialogs">"Confirm Add-in Changes" and "Add-in Check".</param>
    /// <param name="schedule">Runs an action once after a delay (the 3 s close and the toasts); null uses the dispatcher. Tests pass a fake clock.</param>
    /// <param name="openUrl">Opens an add-in link; null opens http(s) links in the default browser.</param>
    /// <param name="catalog">The session catalogue, including each command's ribbon placements.</param>
    public ProfileLoaderViewModel(
        IProfileLoaderBackend backend,
        IDialogService dialogs,
        ILoaderDialogs loaderDialogs,
        Func<TimeSpan, Action, IDisposable>? schedule = null,
        Action<string>? openUrl = null,
        IReadOnlyList<ScannedCommand>? catalog = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _loaderDialogs = loaderDialogs ?? throw new ArgumentNullException(nameof(loaderDialogs));
        _schedule = schedule ?? DispatcherSchedule;
        _openUrl = openUrl ?? ExternalLinks.Open;
        _catalog = catalog ?? [];

        Toast = new TransientMessage(schedule);
        Busy.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(BusyState.IsBusy)) return;
            OnPropertyChanged(nameof(IsIdle));
            RelayCommand.Refresh();
        };

        ImportCommand = new RelayCommand(Import, () => IsIdle);
        LoadCommand = new AsyncRelayCommand(RunLoadAsync, () => IsIdle);
        UnloadCommand = new RelayCommand(Unload, () => IsIdle);
        DeleteCommand = new RelayCommand(Delete, () => IsIdle);
        RestoreCommand = new RelayCommand(RestoreAll, () => IsIdle);
        CloseCommand = new RelayCommand(RequestClose, () => IsIdle);
        OpenUrlCommand = new RelayCommand<string>(url => _openUrl(url));
    }

    /// <summary>Raised when the window should close (Close, or 3 s after a load or unload).</summary>
    public event Action? CloseRequested;

    public ObservableCollection<ProfileCardViewModel> Profiles { get; } = [];

    public BusyState Busy { get; } = new();

    public TransientMessage Toast { get; }

    /// <summary>False while the busy overlay shows: every command and input is disabled (Planner default 8).</summary>
    public bool IsIdle => !Busy.IsBusy;

    public ICommand ImportCommand { get; }

    public ICommand LoadCommand { get; }

    public ICommand UnloadCommand { get; }

    public ICommand DeleteCommand { get; }

    public ICommand RestoreCommand { get; }

    public ICommand CloseCommand { get; }

    public ICommand OpenUrlCommand { get; }

    /// <summary>"Revit 2026", or "Not detected".</summary>
    public string RevitVersionDisplay
    {
        get => _revitVersionDisplay;
        private set => SetField(ref _revitVersionDisplay, value);
    }

    public int ProfileCount => Profiles.Count;

    public bool HasProfiles => Profiles.Count > 0;

    /// <summary>The selected card (the ListBox's SelectedItem). Selecting runs selectCard.</summary>
    public ProfileCardViewModel? SelectedCard
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(value, _selected)) return;
            // The ListBox's two-way binding: WPF would swallow an exception here without a trace (SC-046, R6).
            UiGuard.Run("Loader: select a profile", () =>
            {
                if (value is null) ClearDetail();
                else Select(value);
            });
        }
    }

    public bool HasSelection => _selected is not null;

    /// <summary>The profile the preview draws (null clears it).</summary>
    public Profile? SelectedProfile => _selected?.Profile;

    public string DetailName => _selected?.Name ?? UiStrings.Loader_NoSelection;

    /// <summary>The detail header's "● Loaded" badge (by id, as the cards: Planner default 5).</summary>
    public bool IsSelectionLoaded
    {
        get => _isSelectionLoaded;
        private set => SetField(ref _isSelectionLoaded, value);
    }

    public string DetailTab => _selected?.Tab ?? "";

    public string DetailMinRevit => _selected?.MinRevit ?? "";

    public string DetailUpdated => _selected?.Profile.ExportDate ?? "";

    /// <summary>The "Override Panel Opacity" slider (10-100). Moving it redraws the preview; the value is saved on release.</summary>
    public int OpacityOverride
    {
        get => _opacityOverride;
        set
        {
            var v = LoaderPrefsService.ClampOpacity(value);
            if (SetField(ref _opacityOverride, v)) OnPropertyChanged(nameof(PreviewOpacity));
        }
    }

    /// <summary>The opacity the preview draws with (null with no selection).</summary>
    public int? PreviewOpacity => _selected is null ? null : _opacityOverride;

    /// <summary>The footer's "Disable unused add-ins" switch.</summary>
    public bool DisableUnused
    {
        get => _disableUnused;
        set => SetField(ref _disableUnused, value);
    }

    public IReadOnlyList<TabToggleViewModel> TabToggles
    {
        get => _tabToggles;
        private set
        {
            if (SetField(ref _tabToggles, value)) OnPropertyChanged(nameof(HasTabToggles));
        }
    }

    /// <summary>The RSTify list has rows to show.</summary>
    public bool HasTabToggles => _tabToggles.Count > 0;

    /// <summary>"No tabs available to hide" (a profile is selected, tabs were captured, none can be hidden).</summary>
    public bool TabTogglesEmpty
    {
        get => _tabTogglesEmpty;
        private set => SetField(ref _tabTogglesEmpty, value);
    }

    public IReadOnlyList<RequiredAddinRowViewModel> RequiredAddins
    {
        get => _requiredAddins;
        private set
        {
            if (SetField(ref _requiredAddins, value)) OnPropertyChanged(nameof(RequiredAddinsEmpty));
        }
    }

    /// <summary>"None" under the Required Add-ins heading.</summary>
    public bool RequiredAddinsEmpty => _requiredAddins.Count == 0;

    /// <summary>The hidden tabs Load Profile sends: the switched-on RSTify rows.</summary>
    public IReadOnlyList<string> SelectedHiddenTabs => _tabToggles.Where(t => t.IsHidden).Select(t => t.Tab).ToList();

    /// <summary>The active profile pointer's id and name (null when none is loaded).</summary>
    public (string? Id, string? Name) Active => (_activeId, _activeName);

    /// <summary>
    /// Boot, both steps: read the data, then select the active profile (which may ask the
    /// "Adopt RSTify Presets?" question). Tests and the screenshots call this.
    /// </summary>
    public void Initialize()
    {
        LoadData();
        SelectActiveProfile();
    }

    /// <summary>
    /// Boot step 1 (before the window shows; no dialogs): the lookup, the add-in inventory, the
    /// profiles, the active profile, the ribbon tabs, the prefs and the version.
    /// </summary>
    public void LoadData()
    {
        if (_dataLoaded) return;
        _dataLoaded = true;
        Log.Information("UI: selector boot");

        _lookup = Safe(() => _backend.GetAddinLookup(), new Dictionary<string, AddinLookupEntry>(), "get_addin_lookup");
        RefreshAddinSession();
        _prefs = Safe(() => _backend.GetPrefs(), new UserProfilePrefs(), "get_user_profile_prefs");
        Log.Information("UI: selector: user_profile_prefs loaded {Payload}", _prefs.Profiles.Count);

        var entries = Safe(() => _backend.GetProfiles(), [], "get_profiles");
        foreach (var e in entries) Profiles.Add(new ProfileCardViewModel(e.FileName, e.Profile));
        Log.Information("UI: selector: profiles fetched {Payload}", Profiles.Count);

        var active = Safe<ActiveProfile?>(() => _backend.GetActiveProfile(), null, "get_active_profile");
        if (active is not null && !active.IsBlank && (!string.IsNullOrEmpty(active.ProfileName) || !string.IsNullOrEmpty(active.ProfileId)))
        {
            _activeId = active.ProfileId;
            _activeName = active.ProfileName;
            _previousHiddenTabs = [.. active.HiddenTabs ?? []];
            DisableUnused = active.DisableNonRequired;
        }

        var version = _backend.RevitVersion;
        RevitVersionDisplay = string.IsNullOrEmpty(version) ? UiStrings.Loader_RevitNotDetected : UiStrings.Loader_RevitVersion(version);
        RefreshCards();
    }

    /// <summary>
    /// Boot step 2 (once the window shows): moves RST 3.0's renamed add-ins into the settings file
    /// and reports it (once: the first time there is something to move; spec #18, In Scope 9), then
    /// selects the active profile's card, now that the tabs and prefs are in (Planner default 6).
    /// </summary>
    public void SelectActiveProfile()
    {
        LoadData();
        if (_activeSelected) return;
        _activeSelected = true;
        MigrateLegacyAddins();
        var card = Profiles.FirstOrDefault(IsActive);
        if (card is not null) Select(card);
    }

    private void MigrateLegacyAddins()
    {
        var report = Safe<ActivationReport?>(() => _backend.MigrateLegacy(), null, "migrate_legacy_addins");
        if (report is null) return;
        Log.Information("UI: selector: moved RST 3.0 renames {Payload}", new { done = report.Done.Count, notChanged = report.NotChanged.Count });
        RefreshAddinSession();
        _loaderDialogs.ShowAddinReport(AddinReportModel.ForMigration(report));
    }

    /// <summary>Re-reads the inventory, so the status pills show the add-ins as they are now.</summary>
    private void RefreshAddinSession()
    {
        var inventory = Safe(() => _backend.GetAddinInventory(), AddinInventory.Empty, "get_addin_inventory");
        _session = AddinSessionContext.For(_backend.RevitVersion, inventory, _backend.AllTabs, _lookup);
        if (_selected is not null)
            RequiredAddins = RequiredAddinRules.Rows(_selected.Profile, _session)
                .Select(r => new RequiredAddinRowViewModel(r, OpenUrlCommand)).ToList();
    }

    /// <summary>The slider was released (or moved by keyboard): saves the override when it changed.</summary>
    public void CommitOpacityOverride()
    {
        var card = _selected;
        if (card is null || string.IsNullOrEmpty(card.Id)) return;
        var v = _opacityOverride;
        if (v == _committedOpacity) return;
        _committedOpacity = v;
        try
        {
            _backend.RecordOpacityOverride(card.Id!, v);
            EntryFor(card.Id!).PanelOpacityOverride = v;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "UI: selector: record_panel_opacity_override failed");
        }
    }

    // ── selection ────────────────────────────────────────────────────────────

    private bool IsActive(ProfileCardViewModel card)
    {
        if (!string.IsNullOrEmpty(_activeId) && !string.IsNullOrEmpty(card.Id)) return card.Id == _activeId;
        return card.Name == _activeName;
    }

    private void RefreshCards()
    {
        foreach (var card in Profiles) card.IsLoaded = IsActive(card);
        IsSelectionLoaded = _selected is not null && IsActive(_selected);
        OnPropertyChanged(nameof(ProfileCount));
        OnPropertyChanged(nameof(HasProfiles));
    }

    private void Select(ProfileCardViewModel card)
    {
        _selected = card;
        var profile = card.Profile;
        IsSelectionLoaded = IsActive(card);

        // RST-046: the slider starts at the saved override, else the profile's opacity.
        var entry = string.IsNullOrEmpty(card.Id) ? null : _prefs.Get(card.Id!);
        var effective = RibbonPreviewRules.EffectiveOpacity(profile, entry?.PanelOpacityOverride);
        _opacityOverride = LoaderPrefsService.ClampOpacity(effective);
        _committedOpacity = _opacityOverride;

        RequiredAddins = RequiredAddinRules.Rows(profile, _session)
            .Select(r => new RequiredAddinRowViewModel(r, OpenUrlCommand)).ToList();

        // RST-026: the profile's saved prefs seed the RSTify list and the footer switch; with
        // none, and admin presets on the profile, the adopt prompt asks once.
        var askAdopt = false;
        if (entry is not null)
        {
            _previousHiddenTabs = [.. entry.HiddenTabs ?? []];
            DisableUnused = entry.DisableUnusedAddins;
        }
        else
        {
            _previousHiddenTabs = [];
            DisableUnused = false;
            askAdopt = RstifyRules.HasPresets(profile) && !string.IsNullOrEmpty(card.Id);
        }
        RenderTabToggles(profile);
        RaiseDetailChanged();

        // While "Loading …" / "Unloading" shows, the window is about to close: no question then.
        if (askAdopt && IsIdle) AskAdoptPresets(card);
    }

    private void ClearDetail()
    {
        _selected = null;
        IsSelectionLoaded = false;
        RequiredAddins = [];
        TabToggles = [];
        TabTogglesEmpty = false;
        RaiseDetailChanged();
    }

    private void RaiseDetailChanged()
    {
        OnPropertyChanged(nameof(SelectedCard));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedProfile));
        OnPropertyChanged(nameof(DetailName));
        OnPropertyChanged(nameof(DetailTab));
        OnPropertyChanged(nameof(DetailMinRevit));
        OnPropertyChanged(nameof(DetailUpdated));
        OnPropertyChanged(nameof(OpacityOverride));
        OnPropertyChanged(nameof(PreviewOpacity));
    }

    private void RenderTabToggles(Profile profile)
    {
        var list = RstifyRules.TabsToOffer(profile, _session.AllTabs, _catalog);
        var hidden = RstifyRules.ReconcileHiddenTabs(profile, _session.AllTabs, _catalog, _previousHiddenTabs);
        TabToggles = list.Tabs.Select(t => new TabToggleViewModel(t, hidden.Contains(t))).ToList();
        foreach (var toggle in TabToggles)
            toggle.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TabToggleViewModel.IsHidden)) RefreshTabLocks(profile);
            };
        RefreshTabLocks(profile);
        TabTogglesEmpty = list.ShowsEmptyState;
    }

    private void RefreshTabLocks(Profile profile)
    {
        var hidden = SelectedHiddenTabs;
        foreach (var toggle in TabToggles)
        {
            // A ticked switch must always allow the user to show that tab again.
            string? tool = null;
            var blocked = !toggle.IsHidden
                && RstifyRules.BlockingTool(profile, _session.AllTabs, _catalog, hidden, toggle.Tab, out tool);
            toggle.SetLock(blocked, tool);
        }
    }

    private void AskAdoptPresets(ProfileCardViewModel card)
    {
        var profile = card.Profile;
        var presets = profile.Presets ?? new ProfilePresets();
        var yes = _dialogs.Confirm(RstifyRules.AdoptPresetsTitle, RstifyRules.AdoptPresetsMessage(profile), [],
            UiStrings.Loader_Continue, danger: false, AdoptPresetsDialogId);
        List<string> hidden = yes ? [.. presets.HiddenTabs ?? []] : [];
        var disableUnused = yes && presets.DisableUnusedAddins;
        try
        {
            // Saved with presetsAdopted=true whatever the answer, so the prompt fires once per profile per machine.
            _backend.RecordPrefs(card.Id!, hidden, disableUnused, presetsAdopted: true);
            _prefs.Profiles[card.Id!] = new UserProfilePrefsEntry
            {
                HiddenTabs = [.. hidden],
                DisableUnusedAddins = disableUnused,
                PresetsAdopted = true,
            };
            if (ReferenceEquals(_selected, card))
            {
                _previousHiddenTabs = [.. hidden];
                DisableUnused = disableUnused;
                RenderTabToggles(profile);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "UI: selector: record_user_profile_prefs failed");
        }
    }

    private UserProfilePrefsEntry EntryFor(string id)
    {
        if (!_prefs.Profiles.TryGetValue(id, out var entry))
        {
            entry = new UserProfilePrefsEntry();
            _prefs.Profiles[id] = entry;
        }
        return entry;
    }

    // ── import ───────────────────────────────────────────────────────────────

    private void Import()
    {
        var path = _dialogs.OpenFile(UiStrings.Loader_ImportDialogTitle, FileDialogTexts.ZipFilter);
        if (string.IsNullOrEmpty(path)) return;
        ImportOutcome result;
        try
        {
            result = _backend.Import(path);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "UI: loader: import threw");
            ShowToast(UiStrings.Loader_ImportFailed, UiStrings.Loader_ImportError(ex.Message), BannerKind.Error);
            return;
        }
        if (!result.Ok || result.Profile is null || result.FileName is null)
        {
            Log.Error("UI: loader: import failed {Payload}", result.Error);
            ShowToast(UiStrings.Loader_ImportFailed, result.Error ?? "", BannerKind.Error);
            return;
        }

        // Re-importing an existing profile overwrites its file: replace its card instead of adding a duplicate.
        var existing = Profiles.FirstOrDefault(p => p.FileName == result.FileName
                                                    || (!string.IsNullOrEmpty(p.Id) && p.Id == result.Profile.Id));
        if (existing is not null)
        {
            existing.Replace(result.FileName, result.Profile);
            if (ReferenceEquals(existing, _selected)) Select(existing);
        }
        else
        {
            Profiles.Add(new ProfileCardViewModel(result.FileName, result.Profile));
        }
        RefreshCards();
    }

    // ── load ─────────────────────────────────────────────────────────────────

    /// <summary>True while the load's add-in pass runs: the window does not close then.</summary>
    public bool IsWorking
    {
        get => _isWorking;
        private set => SetField(ref _isWorking, value);
    }

    private async Task RunLoadAsync()
    {
        try
        {
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "UI: selector: load threw");
            IsWorking = false;
            Busy.Hide();
            ShowToast(UiStrings.Loader_Error, UiStrings.Loader_LoadError(ex.Message), BannerKind.Error);
        }
    }

    /// <summary>Load Profile → (the command runs this; tests await it).</summary>
    public async Task LoadAsync()
    {
        var card = _selected;
        if (card is null)
        {
            Log.Warning("UI: selector: Load clicked with no selection");
            return;
        }
        Log.Information("UI: selector: Load attempt {Payload}", card.Name);

        // Check 1: not-found add-ins warn, but do not block.
        var warnings = RequiredAddinRules.NotFound(card.Profile, _session)
            .Select(a => RequiredAddinRules.NotFoundWarning(a.TabName, _backend.RevitVersion))
            .ToList();

        // Check 2: the add-in plan. It is what the Confirm dialog shows and exactly what the load
        // runs (spec #18, AC2). A plan that cannot be made changes no add-in, and the report says so
        // (v3.0 went ahead with Disable on and no confirmation, D15).
        ActivationPlan? plan = null;
        string? planFailure = null;
        try
        {
            var outcome = _backend.PlanLoad(card.Name, card.Id, DisableUnused);
            if (outcome.Error is not null)
            {
                ShowToast(UiStrings.Loader_Error, outcome.Error, BannerKind.Error);
                return;
            }
            plan = outcome.Plan;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "UI: selector: add-in plan failed; loading without add-in changes");
            planFailure = UiStrings.Loader_PlanFailed(ex.Message);
        }

        if (plan is not null && (DisableUnused || plan.HasChanges)
            && !_loaderDialogs.ConfirmAddinChanges(ConfirmAddinChangesModel.From(plan)))
            return;

        await DoLoadAsync(card, warnings, plan, planFailure);
    }

    private async Task DoLoadAsync(ProfileCardViewModel card, IReadOnlyList<string> notFoundWarnings, ActivationPlan? plan, string? planFailure)
    {
        var profile = card.Profile;
        var request = new LoadRequest(card.Name, card.Id, DisableUnused, SelectedHiddenTabs, plan);
        LoadResult result;
        Busy.Show(UiStrings.Loader_Loading(card.Name));
        IsWorking = true;
        try
        {
            result = await _backend.LoadAsync(request);
        }
        catch (Exception ex)
        {
            IsWorking = false;
            Busy.Hide();
            ShowToast(UiStrings.Loader_Error, UiStrings.Loader_LoadError(ex.Message), BannerKind.Error);
            return;
        }
        IsWorking = false;
        Busy.Hide();

        if (!result.Ok)
        {
            Log.Error("UI: selector: load_profile bridge returned ok=false {Payload}", string.Join("; ", result.Warnings));
            ShowToast(UiStrings.Loader_LoadFailed, UiStrings.Loader_LoadFailedText, BannerKind.Error);
            return;
        }
        var report = result.Report;
        if (planFailure is not null) report = report with { Notes = [planFailure, .. report.Notes] };
        Log.Information("UI: selector: load_profile OK {Payload}", new
        {
            name = card.Name, id = card.Id, restart_needed = result.RestartNeeded,
            addins_done = report.Done.Count, addins_not_changed = report.NotChanged.Count,
        });
        _activeId = card.Id;
        _activeName = card.Name;
        RefreshCards();

        var lines = new List<string>();
        if (result.RestartNeeded) lines.Add(UiStrings.Loader_RestartNeeded);
        lines.Add(UiStrings.Loader_TabWillBeBuilt(profile.Tab));
        lines.AddRange(notFoundWarnings);
        lines.AddRange(result.Warnings);

        // The Add-in Check always shows, before anything closes the window: what changed, what
        // could not, and the required add-ins that are missing or switched off.
        var qa = result.Qa ?? QaCheck.For(profile, _session, plan);
        Log.Information("UI: selector: load_profile QA {Payload}", new { notInstalled = qa.NotInstalled.Count, requiredOff = qa.RequiredOff.Count, allClear = qa.AllClear });
        _loaderDialogs.ShowAddinReport(AddinReportModel.ForLoad(card.Name, report, qa, _openUrl));

        ShowLoadingAndClose(UiStrings.Loader_Loading(card.Name), string.Join(" ", lines));
    }

    // ── unload, delete, restore, close ───────────────────────────────────────

    private void Unload()
    {
        if (string.IsNullOrEmpty(_activeName) && string.IsNullOrEmpty(_activeId))
        {
            ShowToast(UiStrings.Loader_NoProfile, UiStrings.Loader_NoProfileText, BannerKind.Info);
            return;
        }
        if (!TryUnload()) return;
        _activeId = null;
        _activeName = null;
        RefreshCards();
        ShowLoadingAndClose(UiStrings.Loader_Unloading, UiStrings.Loader_UnloadingText);
        SelectFirstOrClear();
    }

    /// <summary>Unloads, then reports the add-ins switched back on (decision #16.2), if rst-c had switched any off.</summary>
    private bool TryUnload()
    {
        UnloadOutcome result;
        try
        {
            result = _backend.Unload();
        }
        catch (Exception ex)
        {
            ShowToast(UiStrings.Loader_Error, UiStrings.Loader_UnloadError(ex.Message), BannerKind.Error);
            return false;
        }
        if (!result.Ok)
        {
            ShowToast(UiStrings.Loader_UnloadFailed, UiStrings.Loader_UnloadFailedText, BannerKind.Error);
            return false;
        }
        if (result.Addins.Outcomes.Count > 0 || result.Addins.Notes.Count > 0)
        {
            Log.Information("UI: selector: unload switched add-ins back on {Payload}", new { done = result.Addins.Done.Count, notChanged = result.Addins.NotChanged.Count });
            _loaderDialogs.ShowAddinReport(AddinReportModel.ForUnload(result.Addins));
        }
        else
        {
            // Nothing is silent (spec #18, In Scope 5): a short non-modal note instead of a dialog.
            Toast.Show(UiStrings.Loader_UnloadNoAddinChanges, BannerKind.Info, TransientMessage.InformDuration);
        }
        return true;
    }

    private void Delete()
    {
        var card = _selected;
        if (card is null) return;
        var name = card.Name;
        var id = card.Id;
        var isLoaded = IsActive(card);

        var body = UiStrings.Loader_DeleteText + (isLoaded ? " " + UiStrings.Loader_DeleteLoadedText : "");
        if (!_dialogs.Confirm(UiStrings.Loader_DeleteTitle(name), body, [], UiStrings.Loader_Continue, danger: true, DeleteProfileDialogId))
            return;

        // The loaded profile is unloaded first.
        if (isLoaded && !TryUnload()) return;

        DeleteOutcome result;
        try
        {
            result = _backend.Delete(name, id);
        }
        catch (Exception ex)
        {
            ShowToast(UiStrings.Loader_Error, UiStrings.Loader_DeleteError(ex.Message), BannerKind.Error);
            return;
        }
        if (!result.Ok)
        {
            ShowToast(UiStrings.Loader_DeleteFailed, UiStrings.Loader_DeleteFailedText, BannerKind.Error);
            return;
        }

        var gone = Profiles.FirstOrDefault(p => (!string.IsNullOrEmpty(id) && p.Id == id) || p.Name == name);
        if (gone is not null) Profiles.Remove(gone);
        if (isLoaded)
        {
            _activeId = null;
            _activeName = null;
        }
        ClearDetail();
        RefreshCards();
        if (isLoaded) ShowLoadingAndClose(UiStrings.Loader_Unloading, UiStrings.Loader_UnloadingText);
        SelectFirstOrClear();
    }

    private void SelectFirstOrClear()
    {
        if (Profiles.Count > 0) Select(Profiles[0]);
        else ClearDetail();
    }

    /// <summary>Restore All Add-ins: switches back on everything rst-c switched off, reports it, and redraws the status pills.</summary>
    private void RestoreAll()
    {
        RestoreOutcome result;
        try
        {
            result = _backend.RestoreAll();
        }
        catch (Exception ex)
        {
            ShowToast(UiStrings.Loader_Error, UiStrings.Loader_RestoreError(ex.Message), BannerKind.Error);
            return;
        }
        if (!result.Ok || result.Addins is null)
        {
            ShowToast(UiStrings.Loader_RestoreFailed,
                UiStrings.Loader_RestoreFailedText(string.IsNullOrEmpty(result.Error) ? UiStrings.Loader_UnknownError : result.Error),
                BannerKind.Error);
            return;
        }

        // v3.0 left the add-in rows as they were (investigation B8); they show the new state now.
        RefreshAddinSession();
        _loaderDialogs.ShowAddinReport(AddinReportModel.ForRestoreAll(result.Addins));
    }

    private void RequestClose() => CloseRequested?.Invoke();

    /// <summary>showLoadingAndClose: the busy overlay with this text for 3 s, then close.</summary>
    private void ShowLoadingAndClose(string title, string message)
    {
        Busy.Show(title, message);
        _schedule(CloseDelay, () =>
        {
            Busy.Hide();
            RequestClose();
        });
    }

    private void ShowToast(string title, string text, BannerKind kind) =>
        Toast.Show(text, kind, TransientMessage.InformDuration, title);

    private static T Safe<T>(Func<T> read, T fallback, string what)
    {
        try
        {
            return read() ?? fallback;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "UI: selector boot: {What} failed", what);
            return fallback;
        }
    }

    private static IDisposable DispatcherSchedule(TimeSpan delay, Action action)
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
        return new StopTimer(timer);
    }

    private sealed class StopTimer(System.Windows.Threading.DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }
}
