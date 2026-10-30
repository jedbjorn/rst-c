// rst-c's own (not vendored): the Profile Builder's view-model (spec #16, WU4), a port of
// profile_builder.html's script (1361-3453): boot, the profile switch (startFresh /
// loadIntoProfile), the machine-wide branding, the catalogue tree and filter, double-click to add,
// the panels list (add, select, rename, ▲ ▼ ×), the slot rows (rename, ▲ ▼ ×), drag and drop (the
// four kinds, WU5), the icon picker (WU5), the live preview, Save (required add-ins, the auto-reload
// of the active profile), Export ZIP and Exit. Every toast keeps its text.
// Changes are the spec's: the unsaved-changes prompt also guards the title-bar ✕, Escape and the
// profile switch (Planner default 3; SC-010), and the panel delete confirm is the family's
// destructive MessageDialog.
// Feature #18 (spec #19, WU2) reshapes the window by scope: the header's profile menu (switch,
// + New profile, Rename…, Duplicate…; rename-as-copy is retired) and ⚙ menu (Branding…, a dialog);
// the Profile pane's General, Loader defaults and Junk cleanup tabs, which edit the profile in place
// (unsaved changes, saved with Save) where the Properties pane and the Presets and Cleanup modals
// were; an unsaved-changes indicator; and Save enabled only when there are changes. The selected
// panel's colour and name editing and the URL button editor went with the Properties pane; the
// panel cards (WU3) and the Commands pane (WU4) take them over.
// Stacks (feature #15, spec #17): the Builder keeps a profile's stacks as they are and the preview
// draws them; it does not author them (feature #18, decision #19). A stack no panel places (its
// slot deleted) is dropped by the save (ProfileSaveService), and from the editor once that worked.

using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using RST.Core.Builder;
using RST.Core.Colors;
using RST.Core.Loader;
using RST.Core.Profiles;
using RST.Core.Ribbon;
using RST.UI.Controls;
using RST.UI.Dialogs;
using RST.UI.Loader;
using RST.UI.Mvvm;
using RST.UI.Resources;
using RST.Core.Logging;
using ProfilePanel = RST.Core.Profiles.Panel;

namespace RST.UI.Builder;

public sealed class ProfileBuilderViewModel : ObservableObject
{
    public const string UnsavedChangesDialogId = MessageDialog.DiscardChangesId;
    public const string DeletePanelDialogId = "DeletePanelDialog";

    /// <summary>
    /// The color picker's palette: the Builder's default blue and the other panel colors rst-c's
    /// profiles use (the HTML's native color input had no palette).
    /// </summary>
    public static readonly IReadOnlyList<Rgb> PanelPalette =
    [
        Rgb.Parse("#4f8ef7"), Rgb.Parse("#10b981"), Rgb.Parse("#8b5cf6"), Rgb.Parse("#ef4444"),
        Rgb.Parse("#14b8a6"), Rgb.Parse("#f59e0b"), Rgb.Parse("#64748b"), Rgb.Parse("#ec4899"),
    ];

    private readonly IProfileBuilderBackend _backend;
    private readonly IDialogService _dialogs;
    private readonly IBuilderDialogs _builderDialogs;
    private readonly Action<Action> _post;
    private IReadOnlyList<IconPackEntry>? _iconPack;
    private IconPickerViewModel? _iconPicker;
    private BuilderDrag? _drag;

    private IReadOnlyList<CatalogCommand> _catalog = [];
    private CatalogTree _tree = CatalogTree.Build([]);
    private readonly CatalogExpansion _expansion = new();
    private List<CatalogNodeViewModel> _allRoots = [];
    private CatalogFilter? _filter;
    private string _filterText = "";

    private IReadOnlyDictionary<string, AddinLookupEntry> _lookup = new Dictionary<string, AddinLookupEntry>();
    private IReadOnlyList<ProfileEntry> _library = [];
    private string? _activeId;
    // The loaded profile's file when the pointer has no id (a legacy profile with no id is loaded):
    // its first save in the Builder gives it an id, and the pointer and ribbon follow (F6).
    private string? _activeIdlessFile;
    private IReadOnlyList<string> _activeHiddenTabs = [];
    private bool _activeDisable;

    private Profile _profile = ProfileEditing.NewProfile();
    private string _pristine = "";
    private bool _editMode;
    private string _originalName = "";
    private int _selectedPanelIndex = -1;
    private ProfileOptionViewModel? _selectedOption;

    private BrandingState _branding = BrandingState.None;

    private IReadOnlyList<PresetTabViewModel> _presetTabs = [];

    // The file a profile with no id was read from: its first save writes it under a new id and a
    // new file name, and then removes this one (WU1 review F6), so it is not listed twice.
    private string? _supersededFile;

    private int _previewRevision;
    private bool _isWorking;
    private bool _closeApproved;

    /// <param name="backend">The Builder's data and writes (a fake in tests).</param>
    /// <param name="dialogs">The family dialogs: confirm, the three-way prompt, the color picker, the file dialogs.</param>
    /// <param name="schedule">Runs an action once after a delay (the toasts); null uses the dispatcher. Tests pass a fake clock.</param>
    /// <param name="post">
    /// Runs an action after the current input event: the switch after the unsaved prompt (the
    /// profile menu's list is rebuilt), and Save &amp; Exit from the title-bar ✕ (after the Closing
    /// event). Null uses the dispatcher.
    /// </param>
    /// <param name="builderDialogs">Rename… / Duplicate… and Branding…; null cancels them (tests that do not open them).</param>
    public ProfileBuilderViewModel(
        IProfileBuilderBackend backend,
        IDialogService dialogs,
        Func<TimeSpan, Action, IDisposable>? schedule = null,
        Action<Action>? post = null,
        IBuilderDialogs? builderDialogs = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _builderDialogs = builderDialogs ?? NoBuilderDialogs.Instance;
        _post = post ?? (a => Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, a));
        Toast = new TransientMessage(schedule);

        BrandingCommand = new RelayCommand(() => _builderDialogs.EditBranding(this), () => IsIdle);
        PickLogoCommand = new RelayCommand(PickLogo, () => IsIdle);
        ClearLogoCommand = new RelayCommand(ClearLogo, () => IsIdle);
        ExportCommand = new RelayCommand(Export, () => IsIdle);
        SaveCommand = new RelayCommand(() => _ = SaveAsync(), () => IsIdle && IsDirty);
        ExitCommand = new RelayCommand(() => _ = ExitAsync(), () => IsIdle);
        AddPanelCommand = new RelayCommand(AddPanel, () => IsIdle);
        AddCatalogCommand = new RelayCommand<CatalogNodeViewModel>(n => AddToSelectedPanel(n.Command!), n => IsIdle && n.IsCommand);
        PickColorCommand = new RelayCommand(PickColor, () => IsIdle && SelectedPanel is not null);
        NewProfileCommand = new RelayCommand(() => RequestSwitch(ProfileOptionViewModel.New()), () => IsIdle);
        SwitchProfileCommand = new RelayCommand<ProfileOptionViewModel>(o => SelectedProfileOption = o, _ => IsIdle);
        RenameCommand = new RelayCommand(Rename, () => IsIdle);
        DuplicateCommand = new RelayCommand(Duplicate, () => IsIdle && _editMode);
        RestoreCleanupDefaultsCommand = new RelayCommand(RestoreCleanupDefaults, () => IsIdle);
    }

    /// <summary>Raised when the window should close (Exit, or after the unsaved-changes prompt allowed it).</summary>
    public event Action? CloseRequested;

    public TransientMessage Toast { get; }

    /// <summary>The icon pack folder for the slot thumbnails; null is the shipped one.</summary>
    public string? PackDirectory { get; init; }

    /// <summary>False while a save's auto-reload runs: the commands are disabled until it ends.</summary>
    public bool IsIdle => !_isWorking;

    public bool IsWorking
    {
        get => _isWorking;
        private set
        {
            if (!SetField(ref _isWorking, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            RelayCommand.Refresh();
        }
    }

    // ---- boot ---------------------------------------------------------------------------------

    /// <summary>
    /// bootAfterBridge: the machine-wide branding first, then the catalogue, the profiles, the active
    /// profile, the lookup and the tabs, then a fresh profile with one panel.
    /// </summary>
    public void Initialize()
    {
        _branding = Safe(_backend.LoadBranding, BrandingState.None, "load_default_branding");
        RaiseBranding();

        var catalog = _catalog = Safe(() => _backend.Catalog, [], "get_catalog");
        _tree = CatalogTree.Build(catalog);
        _allRoots = _tree.Roots.Select(r => new CatalogNodeViewModel(r, _expansion)).ToList();
        if (catalog.Count == 0) Log.Warning("Builder: get_catalog returned no commands");
        ApplyFilter();
        OnPropertyChanged(nameof(CatalogCount));
        OnPropertyChanged(nameof(CatalogIsEmpty));

        _library = Safe(_backend.GetProfiles, [], "get_profiles");
        var active = Safe<ActiveProfile?>(_backend.GetActiveProfile, null, "get_active_profile");
        _activeId = active is { IsBlank: false } && !string.IsNullOrEmpty(active.ProfileId) ? active.ProfileId : null;
        _activeIdlessFile = active is { IsBlank: false } && _activeId is null ? active.ProfileFile : null;
        _activeHiddenTabs = active is { IsBlank: false } ? active.HiddenTabs ?? [] : [];
        _activeDisable = active is { IsBlank: false } && active.DisableNonRequired;
        _lookup = Safe(_backend.GetAddinLookup, new Dictionary<string, AddinLookupEntry>(), "get_addin_lookup");
        Log.Information("Builder boot: catalog={Catalog} profiles={Profiles} active={Active} lookup={Lookup} tabs={Tabs}",
            catalog.Count, _library.Count, _activeId, _lookup.Count, _backend.AllTabs.Count);

        PopulateOptions();
        StartFresh();
    }

    // ---- the profile menu -----------------------------------------------------------------------

    /// <summary>The saved profiles, as the header's profile menu lists them (the current one ticked).</summary>
    public ObservableCollection<ProfileOptionViewModel> ProfileOptions { get; } = [];

    /// <summary>
    /// The profile in the editor: a saved one, or "+ New profile" (<see cref="ProfileOptionViewModel.IsNew"/>).
    /// Setting another switches the editor, asking first when there are unsaved changes.
    /// </summary>
    public ProfileOptionViewModel? SelectedProfileOption
    {
        get => _selectedOption;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedOption)) return;
            RequestSwitch(value);
        }
    }

    /// <summary>A profile in the menu: switch to it (asking first when there are unsaved changes).</summary>
    public ICommand SwitchProfileCommand { get; }

    /// <summary>+ New profile: a blank profile with one panel (asking first when there are unsaved changes).</summary>
    public ICommand NewProfileCommand { get; }

    /// <summary>"edit" mode: the editor holds a saved profile (switched to, saved once, or duplicated).</summary>
    public bool IsEditMode => _editMode;

    private void PopulateOptions(string? selectKey = null)
    {
        var previous = selectKey ?? _selectedOption?.Key;
        ProfileOptions.Clear();
        foreach (var entry in _library) ProfileOptions.Add(ProfileOptionViewModel.For(entry, _activeId));
        SetCurrent(ProfileOptions.FirstOrDefault(o => o.Key == previous) ?? ProfileOptionViewModel.New());
    }

    private void SetCurrent(ProfileOptionViewModel option)
    {
        _selectedOption = option;
        foreach (var o in ProfileOptions) o.IsCurrent = ReferenceEquals(o, option);
        OnPropertyChanged(nameof(SelectedProfileOption));
    }

    private void RequestSwitch(ProfileOptionViewModel target)
    {
        if (IsWorking) return;
        if (!IsDirty)
        {
            ApplySwitch(target);
            return;
        }
        switch (_dialogs.Choose(UiStrings.Builder_UnsavedTitle, UiStrings.Builder_UnsavedText,
                    UiStrings.Builder_SaveAndSwitch, UiStrings.Builder_DiscardAndSwitch, UnsavedChangesDialogId))
        {
            // Both run after the menu click is over: the switch rebuilds the menu's list.
            case DialogChoice.Secondary:
                Log.Information("Builder: profile switch → discard unsaved changes");
                _post(() => ApplySwitch(target));
                break;
            case DialogChoice.Primary:
                Log.Information("Builder: profile switch → save first");
                _post(() => _ = SaveThenSwitchAsync(target.Key));
                break;
        }
    }

    private async Task SaveThenSwitchAsync(string targetKey)
    {
        if (!await SaveAsync()) return;
        var target = ProfileOptions.FirstOrDefault(o => o.Key == targetKey) ?? ProfileOptionViewModel.New();
        ApplySwitch(target);
    }

    /// <summary>onProfileSelectChange: "+ New profile" starts fresh; a profile loads into the editor.</summary>
    private void ApplySwitch(ProfileOptionViewModel target)
    {
        SetCurrent(target);
        if (target.Entry is null)
        {
            Log.Information("Builder: profile menu → new profile");
            StartFresh();
            return;
        }
        Log.Information("Builder: profile menu → load existing {Name} ({Id})", target.Entry.Profile.ProfileName, target.Entry.Profile.Id);
        LoadIntoEditor(target.Entry.Profile, target.Entry.FileName);
    }

    /// <summary>startFresh: a blank profile with one panel ("Panel 1"), new mode, a clean baseline.</summary>
    private void StartFresh()
    {
        _profile = ProfileEditing.NewProfile();
        _supersededFile = null;
        _selectedPanelIndex = -1;
        _editMode = false;
        _originalName = "";
        AddPanelCore();
        ProfileChanged();
        SnapshotPristine();
    }

    /// <summary>loadIntoProfile + the dropdown handler: edit mode, panel 0 selected, a clean baseline.</summary>
    /// <param name="source">The saved profile.</param>
    /// <param name="fileName">Its file; kept when the profile has no id, for the first save to replace (WU1 review F6).</param>
    private void LoadIntoEditor(Profile source, string? fileName = null)
    {
        _supersededFile = string.IsNullOrEmpty(source.Id) ? fileName : null;
        _profile = ProfileEditing.ForEditing(source);
        _selectedPanelIndex = _profile.Panels.Count > 0 ? 0 : -1;
        _editMode = true;
        _originalName = _profile.ProfileName ?? "";
        ProfileChanged();
        SnapshotPristine();
    }

    private void ProfileChanged()
    {
        IconPicker = null;
        RenderCanvas();
        RebuildPresetTabs();
        RebuildCleanupRows();
        RaiseName();
        OnPropertyChanged(nameof(ProfileTab));
        OnPropertyChanged(nameof(MinVersion));
        OnPropertyChanged(nameof(PanelOpacity));
        OnPropertyChanged(nameof(PanelOpacityText));
        OnPropertyChanged(nameof(DisableUnused));
        OnPropertyChanged(nameof(PreviewProfile));
        OnPropertyChanged(nameof(IsEditMode));
        OnPropertyChanged(nameof(IsLoadedProfile));
    }

    // ---- dirty tracking -----------------------------------------------------------------------

    /// <summary>
    /// The profile differs from the clean baseline (boot, a profile switch, the last save): the
    /// header's unsaved-changes dot and text show, and Save is enabled.
    /// </summary>
    public bool IsDirty => ProfileEditing.Snapshot(_profile) != _pristine;

    private void SnapshotPristine()
    {
        _pristine = ProfileEditing.Snapshot(_profile);
        ProfileEdited();
    }

    /// <summary>After any edit: the unsaved-changes indicator and Save's enabled state follow.</summary>
    internal void ProfileEdited()
    {
        OnPropertyChanged(nameof(IsDirty));
        RelayCommand.Refresh();
    }

    /// <summary>The profile being edited (the preview draws it; tests read it).</summary>
    public Profile Profile => _profile;

    // ---- the name: header, Rename…, Duplicate… -------------------------------------------------

    /// <summary>
    /// The working copy's name. Rename… sets it once its checks pass (required, unique among the
    /// other saved profiles); General shows it read-only. An unsaved change: Save writes it under the
    /// same id, so a user who has the profile loaded keeps it (spec #19).
    /// </summary>
    public string ProfileName
    {
        get => _profile.ProfileName ?? "";
        set
        {
            if (_profile.ProfileName == (value ?? "")) return;
            _profile.ProfileName = value ?? "";
            RaiseName();
            ProfileEdited();
        }
    }

    public bool HasProfileName => !string.IsNullOrWhiteSpace(_profile.ProfileName);

    /// <summary>The header button's and the Profile pane's name: the profile's, or "New profile" before it has one.</summary>
    public string DisplayName => HasProfileName ? _profile.ProfileName! : UiStrings.Builder_UnnamedProfile;

    /// <summary>The Profile pane's header: "· editing &lt;name&gt;".</summary>
    public string EditingText => UiStrings.Builder_EditingName(DisplayName);

    /// <summary>The header button's "(loaded)": the editor holds the profile the Loader has loaded.</summary>
    public bool IsLoadedProfile => _editMode && !string.IsNullOrEmpty(_profile.Id) && _profile.Id == _activeId;

    private void RaiseName()
    {
        OnPropertyChanged(nameof(ProfileName));
        OnPropertyChanged(nameof(HasProfileName));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(EditingText));
    }

    /// <summary>Rename… (the profile menu, or General's button).</summary>
    public ICommand RenameCommand { get; }

    /// <summary>Duplicate… (the profile menu): only for a saved profile.</summary>
    public ICommand DuplicateCommand { get; }

    private List<Profile> SavedProfiles() => _library.Select(e => e.Profile).ToList();

    /// <summary>
    /// Rename…: a required name, unique among the saved profiles other than this one
    /// (case-insensitive). It changes only the working copy's name, so it is an unsaved change.
    /// </summary>
    private void Rename()
    {
        var saved = SavedProfiles();
        NameCheck Check(string? n) => ProfileNaming.Check(n, _profile.Id, saved);
        var dialog = ProfileNameDialogModel.ForRename(ProfileName, Check);
        if (!_builderDialogs.EditName(dialog) || Check(dialog.Result) != NameCheck.Ok) return;
        if (dialog.Result == ProfileName) return;
        Log.Information("Builder: rename {Old} → {New} (id {Id}, unsaved)", ProfileName, dialog.Result, _profile.Id ?? "(new)");
        ProfileName = dialog.Result;
    }

    /// <summary>
    /// Duplicate…: "&lt;name&gt; copy" by default, unique among every saved profile. Writes a new
    /// profile with a new id from the working copy, unsaved edits included, and switches the editor
    /// to it; the original's file stays as last saved.
    /// </summary>
    private void Duplicate()
    {
        if (!_editMode || IsWorking) return;
        var saved = SavedProfiles();
        NameCheck Check(string? n) => ProfileNaming.Check(n, null, saved);
        var dialog = ProfileNameDialogModel.ForDuplicate(ProfileNaming.DefaultDuplicateName(ProfileName, saved), Check);
        if (!_builderDialogs.EditName(dialog) || Check(dialog.Result) != NameCheck.Ok) return;

        var copy = ProfileNaming.DuplicateOf(_profile, dialog.Result);
        copy.RequiredAddins = RequiredAddinDetector.Compute(copy, _catalog, _lookup);
        Log.Information("Builder: duplicate {Name} ({Id}) as {Copy}", ProfileName, _profile.Id, copy.ProfileName);
        var result = Safe(() => _backend.Save(copy), SaveOutcome.Fail("unknown"), "save_profile (duplicate)");
        if (!result.Ok || result.Saved is not { } written)
        {
            ShowToast(UiStrings.Builder_SaveFailed(result.Error ?? "unknown"), BannerKind.Error);
            return;
        }
        _library = Safe(_backend.GetProfiles, _library, "get_profiles");
        PopulateOptions(written.Id);
        LoadIntoEditor(_library.FirstOrDefault(e => e.Profile.Id == written.Id)?.Profile ?? written);
        ShowToast(UiStrings.Builder_Duplicated(copy.ProfileName), BannerKind.Success);
    }

    // ---- the Profile pane: General --------------------------------------------------------------

    /// <summary>The ribbon tab (General is the only place to edit it); the preview's tab pill follows it as you type.</summary>
    public string ProfileTab
    {
        get => _profile.Tab ?? "";
        set
        {
            if (_profile.Tab == (value ?? "")) return;
            _profile.Tab = value ?? "";
            OnPropertyChanged();
            RebuildPresetTabs(); // the profile's own tab is never offered for hiding
            PreviewChanged();
        }
    }

    public string MinVersion
    {
        get => _profile.MinVersion ?? "";
        set
        {
            if (_profile.MinVersion == (value ?? "")) return;
            _profile.MinVersion = value ?? "";
            OnPropertyChanged();
            ProfileEdited();
        }
    }

    /// <summary>Panel colour strength (the profile's panel opacity), 10-100 in steps of 5; the preview follows it.</summary>
    public int PanelOpacity
    {
        get => _profile.PanelOpacity;
        set
        {
            var v = Math.Clamp(value, 10, 100);
            if (_profile.PanelOpacity == v) return;
            _profile.PanelOpacity = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PanelOpacityText));
            PreviewChanged();
        }
    }

    /// <summary>The slider's value as shown: "60%".</summary>
    public string PanelOpacityText => UiStrings.Builder_OpacityPercent(_profile.PanelOpacity);

    // ---- branding (⚙ → Branding…: machine-wide, not part of the profile) -----------------------

    /// <summary>⚙ → Branding…: the dialog with the logo and link controls.</summary>
    public ICommand BrandingCommand { get; }

    public bool HasLogo => _branding.HasLogo;

    /// <summary>The logo row's text: the file name, or "(none)".</summary>
    public string LogoName => _branding.HasLogo ? _branding.FileName ?? BrandingDefaultsFileName : UiStrings.Builder_NoLogo;

    private const string BrandingDefaultsFileName = "branding.png";

    /// <summary>The URL box. Saved when it changes (focus leaves the box, or Enter), not on every keystroke.</summary>
    public string BrandingUrl
    {
        get => _branding.Url ?? "";
        set
        {
            if ((value ?? "") == (_branding.Url ?? "")) return;
            SetBrandingUrl(value ?? "");
        }
    }

    public ICommand PickLogoCommand { get; }

    public ICommand ClearLogoCommand { get; }

    private void SetBrandingUrl(string text)
    {
        var url = BuilderRules.NormalizeUrl(text.Trim());
        var result = Safe(() => _backend.SaveBrandingUrl(url), OpResult.Fail("unknown"), "save_default_branding_url");
        if (!result.Ok)
        {
            ShowToast(UiStrings.Builder_SaveUrlFailed(result.Error ?? "unknown"), BannerKind.Error);
            RaiseBranding();
            return;
        }
        _branding = _branding with { Url = url.Length == 0 ? null : url };
        RaiseBranding();
    }

    private void PickLogo()
    {
        var path = _dialogs.OpenFile(FileDialogTexts.LogoTitle, FileDialogTexts.ImageFilter);
        if (string.IsNullOrEmpty(path)) return; // cancelled: no toast
        var result = Safe(() => _backend.StoreLogo(path), LogoOutcome.Fail("unknown"), "pick_logo_file");
        if (!result.Ok)
        {
            ShowToast(UiStrings.Builder_LogoPickFailed(result.Error ?? "unknown"), BannerKind.Error);
            return;
        }
        _branding = _branding with { HasLogo = true, FileName = result.FileName ?? BrandingDefaultsFileName };
        RaiseBranding();
    }

    private void ClearLogo()
    {
        var result = Safe(_backend.ClearLogo, OpResult.Fail("unknown"), "clear_default_logo");
        if (!result.Ok)
        {
            ShowToast(UiStrings.Builder_ClearLogoFailed(result.Error ?? "unknown"), BannerKind.Error);
            return;
        }
        _branding = _branding with { HasLogo = false, FileName = null };
        RaiseBranding();
    }

    private void RaiseBranding()
    {
        OnPropertyChanged(nameof(HasLogo));
        OnPropertyChanged(nameof(LogoName));
        OnPropertyChanged(nameof(BrandingUrl));
    }

    // ---- the catalogue ------------------------------------------------------------------------

    /// <summary>The Commands count: every command in the catalogue.</summary>
    public int CatalogCount => _tree.CommandCount;

    /// <summary>The tree's top level that holds a match: "Autodesk Native", then the other tabs.</summary>
    public FilteredCollection<CatalogNodeViewModel> CatalogRoots { get; } = [];

    /// <summary>Every top-level node, matched or not (tests).</summary>
    public IReadOnlyList<CatalogNodeViewModel> AllCatalogRoots => _allRoots;

    /// <summary>"No commands available…": the catalogue is empty (the Builder opened outside Revit).</summary>
    public bool CatalogIsEmpty => _tree.CommandCount == 0;

    /// <summary>"No matches for "q".": a filter is active and matches nothing.</summary>
    public bool HasNoMatches => !CatalogIsEmpty && _filter is { IsActive: true, AnyMatch: false };

    public string NoMatchesText => UiStrings.Builder_NoMatches(_filter?.Query ?? "");

    /// <summary>The Filter… box. Each keystroke re-filters the tree in place.</summary>
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (!SetField(ref _filterText, value ?? "")) return;
            ApplyFilter();
        }
    }

    public ICommand AddCatalogCommand { get; }

    private void ApplyFilter()
    {
        _filter = _tree.Filter(_filterText);
        _expansion.FilterActive = _filter.IsActive;
        foreach (var root in _allRoots) root.Apply(_filter);
        CatalogRoots.Sync(_allRoots, r => _filter.IsVisible(r.Node));
        OnPropertyChanged(nameof(HasNoMatches));
        OnPropertyChanged(nameof(NoMatchesText));
    }

    /// <summary>
    /// addToolToSelectedPanel (double-click a command): appends a tool slot to the selected panel,
    /// adding a panel first when none is selected.
    /// </summary>
    public void AddToSelectedPanel(CatalogCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (_selectedPanelIndex < 0 || _selectedPanelIndex >= _profile.Panels.Count) AddPanelCore();
        _profile.Panels[_selectedPanelIndex].Slots.Add(BuilderRules.ToolSlot(command));
        RenderCanvas();
    }

    // ---- panels and slots ---------------------------------------------------------------------

    public ObservableCollection<PanelCardViewModel> Panels { get; } = [];

    public bool HasPanels => Panels.Count > 0;

    /// <summary>The selected panel (double-click adds to it), or null.</summary>
    public PanelCardViewModel? SelectedPanel =>
        _selectedPanelIndex >= 0 && _selectedPanelIndex < Panels.Count ? Panels[_selectedPanelIndex] : null;

    public int SelectedPanelIndex => _selectedPanelIndex;

    public ICommand AddPanelCommand { get; }

    private void AddPanel()
    {
        AddPanelCore();
        RenderCanvas();
    }

    private void AddPanelCore()
    {
        _profile.Panels.Add(BuilderRules.NewPanel(_profile.Panels.Count));
        _selectedPanelIndex = _profile.Panels.Count - 1;
    }

    internal void SelectPanel(int index)
    {
        if (index == _selectedPanelIndex) return;
        _selectedPanelIndex = index;
        RenderCanvas();
    }

    internal void MovePanel(int index, int delta)
    {
        var to = index + delta;
        if (to < 0 || to >= _profile.Panels.Count) return;
        var p = _profile.Panels[index];
        _profile.Panels.RemoveAt(index);
        _profile.Panels.Insert(to, p);
        if (_selectedPanelIndex == index) _selectedPanelIndex = to;
        else if (_selectedPanelIndex == to) _selectedPanelIndex = index;
        RenderCanvas();
    }

    /// <summary>× on a panel card: the destructive confirm "Delete panel "X"?" (Cancel is the default).</summary>
    internal void DeletePanel(int index)
    {
        if (index < 0 || index >= _profile.Panels.Count) return;
        var name = _profile.Panels[index].Name;
        if (!_dialogs.Confirm(UiStrings.Builder_DeletePanelTitle(name), "", [], UiStrings.Builder_Delete, danger: true, DeletePanelDialogId)) return;
        _profile.Panels.RemoveAt(index);
        if (_selectedPanelIndex >= _profile.Panels.Count) _selectedPanelIndex = _profile.Panels.Count - 1;
        RenderCanvas();
    }

    internal void MoveSlot(int panelIndex, int slotIndex, int delta)
    {
        var slots = _profile.Panels[panelIndex].Slots;
        var to = slotIndex + delta;
        if (to < 0 || to >= slots.Count) return;
        var s = slots[slotIndex];
        slots.RemoveAt(slotIndex);
        slots.Insert(to, s);
        RenderCanvas();
    }

    /// <summary>× on a slot row removes it (no confirm, as the page).</summary>
    internal void DeleteSlot(int panelIndex, int slotIndex)
    {
        _profile.Panels[panelIndex].Slots.RemoveAt(slotIndex);
        RenderCanvas();
    }

    /// <summary>The icon picker while it is open (the window shows it over everything), else null.</summary>
    public IconPickerViewModel? IconPicker
    {
        get => _iconPicker;
        private set
        {
            if (SetField(ref _iconPicker, value)) OnPropertyChanged(nameof(IsIconPickerOpen));
        }
    }

    public bool IsIconPickerOpen => _iconPicker is not null;

    /// <summary>
    /// The Pick-icon button: opens the picker on the slot's current value (the pack list is read
    /// once). Only a variant or None changes the slot; Cancel, Escape and an outside click do not.
    /// </summary>
    internal void PickIcon(SlotRowViewModel slot)
    {
        _iconPack ??= Safe(_backend.GetIconPack, [], "list_iconpack");
        IconPicker = new IconPickerViewModel(_iconPack, slot.Model.IconFile, PackDirectory, outcome =>
        {
            IconPicker = null;
            if (!outcome.Committed) return;
            slot.Model.IconFile = outcome.Value;
            Log.Information("Builder: icon for {Slot} → {Icon}", slot.Name, outcome.Value ?? "(none)");
            slot.IconChanged();
            PreviewChanged();
        });
    }

    /// <summary>renderCanvas: the cards and rows again from the profile; the preview redraws.</summary>
    private void RenderCanvas()
    {
        Panels.Clear();
        for (var i = 0; i < _profile.Panels.Count; i++)
        {
            Panels.Add(new PanelCardViewModel(_profile.Panels[i], i, this) { IsSelected = i == _selectedPanelIndex });
        }
        OnPropertyChanged(nameof(HasPanels));
        OnPropertyChanged(nameof(SelectedPanel));
        OnPropertyChanged(nameof(SelectedPanelIndex));
        PreviewChanged();
    }

    /// <summary>
    /// The panel colour picker, starting from the selected panel's colour. Its Properties pane swatch
    /// is retired (spec #19); the panel cards' swatch (WU3) opens it.
    /// </summary>
    public ICommand PickColorCommand { get; }

    private void PickColor()
    {
        if (SelectedPanel is not { } panel) return;
        var current = Rgb.TryParse(panel.ColorHex, out var rgb) ? rgb : Rgb.Parse(BuilderRules.DefaultPanelColor);
        if (_dialogs.PickColor(current, PanelPalette, UiStrings.Builder_ColorDialogTitle) is not { } picked) return;
        panel.SetColor(picked.ToHex().ToLowerInvariant());
    }

    // ---- the Profile pane: Loader defaults --------------------------------------------------------

    /// <summary>
    /// "Hide these tabs (RSTify)": a switch per tab the profile may hide (the ribbon minus the core
    /// tabs and the profile's own tab, then preset tabs that are not on this ribbon). A switch
    /// writes the profile's presets at once, as the RST Presets modal's Apply did.
    /// </summary>
    public IReadOnlyList<PresetTabViewModel> PresetTabs
    {
        get => _presetTabs;
        private set
        {
            _presetTabs = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasPresetTabs));
        }
    }

    public bool HasPresetTabs => _presetTabs.Count > 0;

    /// <summary>"Disable unused add-ins": the Loader switch's starting position for this profile.</summary>
    public bool DisableUnused
    {
        get => _profile.Presets?.DisableUnusedAddins ?? false;
        set
        {
            if (DisableUnused == value) return;
            _profile.Presets ??= new ProfilePresets();
            _profile.Presets.DisableUnusedAddins = value;
            Log.Information("Builder: loader default disableUnused → {Disable}", value);
            OnPropertyChanged();
            ProfileEdited();
        }
    }

    private void RebuildPresetTabs()
    {
        var hidden = new HashSet<string>(_profile.Presets?.HiddenTabs ?? [], StringComparer.Ordinal);
        PresetTabs = BuilderModals.PresetTabs(_backend.AllTabs, _profile.Presets, _profile.Tab)
            .Select(t => new PresetTabViewModel(t, hidden.Contains(t), PresetTabChanged)).ToList();
    }

    private void PresetTabChanged()
    {
        _profile.Presets = BuilderModals.ApplyPresets(_presetTabs.Select(t => (t.Tab, t.IsHidden)), DisableUnused);
        Log.Information("Builder: loader default hiddenTabs → {Hidden}", string.Join(", ", _profile.Presets.HiddenTabs));
        ProfileEdited();
    }

    // ---- the Profile pane: Junk cleanup -----------------------------------------------------------

    /// <summary>The profile's cleanup targets, edited in place: on/off, name, ×.</summary>
    public ObservableCollection<CleanupRowViewModel> CleanupRows { get; } = [];

    public bool HasCleanupTargets => CleanupRows.Count > 0;

    /// <summary>Restore defaults: the list becomes RST's default targets.</summary>
    public ICommand RestoreCleanupDefaultsCommand { get; }

    private void RebuildCleanupRows()
    {
        CleanupRows.Clear();
        foreach (var t in _profile.CleanupTargets ?? []) CleanupRows.Add(new CleanupRowViewModel(t, this));
        OnPropertyChanged(nameof(HasCleanupTargets));
    }

    /// <summary>× on a cleanup row: removed from this profile (Restore defaults brings it back).</summary>
    internal void RemoveCleanupTarget(CleanupRowViewModel row)
    {
        _profile.CleanupTargets?.Remove(row.Target);
        CleanupRows.Remove(row);
        Log.Information("Builder: cleanup target removed {Name}", row.Name);
        OnPropertyChanged(nameof(HasCleanupTargets));
        ProfileEdited();
    }

    private void RestoreCleanupDefaults()
    {
        _profile.CleanupTargets = CleanupDefaults.Build();
        Log.Information("Builder: cleanup targets restored to the defaults");
        RebuildCleanupRows();
        ProfileEdited();
    }

    // ---- drag and drop ------------------------------------------------------------------------

    /// <summary>What is being dragged, or null.</summary>
    public BuilderDrag? CurrentDrag => _drag;

    /// <summary>A drag starts: a catalogue command, a slot row (drawn faded) or a panel header.</summary>
    public void BeginDrag(BuilderDrag drag)
    {
        _drag = drag ?? throw new ArgumentNullException(nameof(drag));
        if (drag.Kind == DragKind.Slot && Slot(drag.PanelIndex, drag.SlotIndex) is { } row) row.IsDragSource = true;
    }

    /// <summary>The drag ended (dropped or cancelled): every marker clears.</summary>
    public void EndDrag()
    {
        _drag = null;
        ClearDropMarkers();
        foreach (var row in Panels.SelectMany(p => p.Slots)) row.IsDragSource = false;
    }

    /// <summary>The pointer is over a panel card (not over one of its slots). True when the card accepts the drag.</summary>
    public bool DragOverPanel(int panelIndex, bool upperHalf)
    {
        var marker = PanelDrops.CardMarker(_drag, panelIndex, upperHalf);
        SetMarker(panelIndex, null, marker);
        return marker != DropMarker.None;
    }

    /// <summary>The pointer is over a slot row. True when the row accepts the drag.</summary>
    public bool DragOverSlot(int panelIndex, int slotIndex, bool upperHalf)
    {
        var marker = PanelDrops.SlotMarker(_drag, upperHalf);
        SetMarker(panelIndex, slotIndex, marker);
        return marker != DropMarker.None;
    }

    /// <summary>The pointer left a target: its marker clears.</summary>
    public void ClearDropMarkers()
    {
        foreach (var card in Panels)
        {
            card.DropMarker = DropMarker.None;
            foreach (var row in card.Slots) row.DropMarker = DropMarker.None;
        }
    }

    /// <summary>A drop on a panel card (panel reorder, command append, slot move to the end).</summary>
    public void DropOnPanel(int panelIndex, bool upperHalf)
    {
        if (_drag is null) return;
        var selected = PanelDrops.DropOnPanel(_profile, _drag, panelIndex, upperHalf);
        AfterDrop(selected);
    }

    /// <summary>A drop on a slot row (command insert, slot reorder or move across panels).</summary>
    public void DropOnSlot(int panelIndex, int slotIndex, bool upperHalf)
    {
        if (_drag is null) return;
        var selected = PanelDrops.DropOnSlot(_profile, _drag, panelIndex, slotIndex, upperHalf);
        AfterDrop(selected);
    }

    private void AfterDrop(int? selected)
    {
        _drag = null;
        if (selected is null)
        {
            EndDrag();
            return;
        }
        _selectedPanelIndex = selected.Value;
        RenderCanvas();
    }

    private void SetMarker(int panelIndex, int? slotIndex, DropMarker marker)
    {
        // One target is marked at a time, as the page's dragleave cleared the previous one.
        foreach (var card in Panels)
        {
            card.DropMarker = slotIndex is null && card.Index == panelIndex ? marker : DropMarker.None;
            foreach (var row in card.Slots)
                row.DropMarker = slotIndex is { } s && card.Index == panelIndex && row.Index == s ? marker : DropMarker.None;
        }
    }

    private SlotRowViewModel? Slot(int panelIndex, int slotIndex) =>
        panelIndex >= 0 && panelIndex < Panels.Count && slotIndex >= 0 && slotIndex < Panels[panelIndex].Slots.Count
            ? Panels[panelIndex].Slots[slotIndex]
            : null;

    // ---- the live preview ---------------------------------------------------------------------

    public Profile PreviewProfile => _profile;

    /// <summary>Bumped after every edit the preview shows (the profile is edited in place).</summary>
    public int PreviewRevision
    {
        get => _previewRevision;
        private set => SetField(ref _previewRevision, value);
    }

    /// <summary>After an edit the preview shows: it redraws, and the unsaved-changes state follows.</summary>
    internal void PreviewChanged()
    {
        PreviewRevision++;
        ProfileEdited();
    }

    // ---- save, export, exit -------------------------------------------------------------------

    public ICommand SaveCommand { get; }

    public ICommand ExportCommand { get; }

    public ICommand ExitCommand { get; }

    /// <summary>
    /// saveAndMaybeReload: name and tab required; always under the open profile's id, so a name
    /// changed by Rename… is a rename (rename-as-copy is retired, spec #19); the required add-ins
    /// recomputed; the save; the profile menu refreshed; a clean baseline; then, when the saved
    /// profile is the active one, the ribbon reloaded with the active hidden tabs and disable flag.
    /// True when the profile was saved.
    /// </summary>
    /// <remarks>
    /// Rename… already refuses a taken name. Save checks again as a safety net, for a name another
    /// profile took since (a second Revit session saving): a rename in edit mode
    /// (<see cref="ProfileEditing.PlanSave"/>), and a new profile's first save (WU1 review F5).
    /// </remarks>
    public async Task<bool> SaveAsync()
    {
        if (IsWorking) return false;
        var library = SavedProfiles();
        var plan = ProfileEditing.PlanSave(_profile, _editMode, _originalName, library);
        if (plan == SavePlanKind.InPlace && !_editMode && ProfileNaming.IsTaken(_profile.ProfileName, _profile.Id, library))
            plan = SavePlanKind.NameCollision;
        switch (plan)
        {
            case SavePlanKind.MissingName:
                ShowToast(UiStrings.Builder_NameRequired, BannerKind.Error);
                return false;
            case SavePlanKind.MissingTab:
                ShowToast(UiStrings.Builder_TabRequired, BannerKind.Error);
                return false;
            case SavePlanKind.NameCollision:
                Log.Warning("Builder: save aborted (name collision) {New} ← {Original}", _profile.ProfileName, _originalName);
                ShowToast(UiStrings.Builder_NameCollision(_profile.ProfileName), BannerKind.Error);
                return false;
        }

        _profile.RequiredAddins = RequiredAddinDetector.Compute(_profile, _catalog, _lookup);
        Log.Information("Builder: save {Name} (edit={Edit}, requiredAddins={Required})",
            _profile.ProfileName, _editMode, _profile.RequiredAddins.Count);
        var result = Safe(() => _backend.Save(_profile), SaveOutcome.Fail("unknown"), "save_profile");
        if (!result.Ok)
        {
            ShowToast(UiStrings.Builder_SaveFailed(result.Error ?? "unknown"), BannerKind.Error);
            return false;
        }
        // The save wrote the profile without its orphan stacks; the editor follows (spec #17).
        var orphans = ProfileEditing.RemoveOrphanStacks(_profile);
        if (orphans.Count > 0) Log.Information("Builder: save removed stacks not on any panel: {Stacks}", string.Join(", ", orphans));
        if (result.Saved is { } saved)
        {
            _profile.Id = saved.Id;
            _profile.ExportDate = saved.ExportDate;
        }
        _editMode = true;
        _originalName = _profile.ProfileName ?? "";
        OnPropertyChanged(nameof(IsEditMode));
        OnPropertyChanged(nameof(IsLoadedProfile));
        // A loaded legacy profile with no id (the pointer names its file) is the active one too: the
        // auto-reload below re-points active_profile.json at the new id and file and rebuilds the tab,
        // so the pointer is never left on the file this save replaces (F6).
        var wasActiveIdless = _supersededFile is { } from && _activeIdlessFile is { } activeFile
                              && string.Equals(from, activeFile, StringComparison.OrdinalIgnoreCase);
        if (wasActiveIdless)
        {
            Log.Information("Builder: the loaded profile had no id; it is now {Id}", _profile.Id);
            _activeId = _profile.Id;
            _activeIdlessFile = null;
        }
        var savedIsActive = !string.IsNullOrEmpty(_profile.Id) && _profile.Id == _activeId;
        var oldFileKept = RemoveSupersededFile(result.FileName);

        _library = Safe(_backend.GetProfiles, _library, "get_profiles");
        PopulateOptions(_profile.Id);
        SnapshotPristine();

        if (oldFileKept is { } kept)
        {
            // The save stands; the user is told the old file is still there (listed twice until removed).
            ShowToast(UiStrings.Builder_SavedOldFileKept(result.FileName, kept.File, kept.Error), BannerKind.Warning);
            if (!savedIsActive) return true;
        }
        else if (!savedIsActive)
        {
            ShowToast(UiStrings.Builder_SavedAs(result.FileName), BannerKind.Success);
            return true;
        }

        Log.Information("Builder: saved profile is active — auto-reloading");
        IsWorking = true;
        try
        {
            var load = await _backend.LoadAsync(new LoadRequest(_profile.ProfileName ?? "", _profile.Id, _activeDisable, _activeHiddenTabs));
            if (!load.Ok) ShowToast(UiStrings.Builder_SavedReloadFailed, BannerKind.Error);
            else if (oldFileKept is null) ShowToast(UiStrings.Builder_SavedReloaded, BannerKind.Success); // else the warning stays
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Builder: auto-reload threw");
            ShowToast(UiStrings.Builder_SavedReloadFailed, BannerKind.Error);
        }
        finally
        {
            IsWorking = false;
        }
        return true;
    }

    /// <summary>Export ZIP: a name is required; the Save dialog (cancel is silent); the path in a toast.</summary>
    private void Export()
    {
        if (string.IsNullOrWhiteSpace(_profile.ProfileName))
        {
            ShowToast(UiStrings.Builder_NameRequired, BannerKind.Error);
            return;
        }
        var path = _dialogs.SaveFile(FileDialogTexts.ExportZipTitle, FileDialogTexts.ZipFilter, ProfileSaveService.SuggestedExportName(_profile));
        if (string.IsNullOrEmpty(path))
        {
            Log.Information("Builder: export cancelled");
            return;
        }
        var result = Safe(() => _backend.Export(_profile, path), OpResult.Fail("unknown"), "export_profile");
        if (result.Ok) ShowToast(UiStrings.Builder_ExportedTo(path), BannerKind.Success);
        else ShowToast(UiStrings.Builder_ExportFailed(result.Error ?? "unknown"), BannerKind.Error);
    }

    /// <summary>Exit: clean closes; dirty asks Save &amp; Exit / Discard &amp; Exit / Cancel (Save &amp; Exit closes only when the save succeeds).</summary>
    public async Task ExitAsync()
    {
        if (IsWorking) return;
        if (!IsDirty)
        {
            Close();
            return;
        }
        switch (AskUnsaved())
        {
            case DialogChoice.Primary:
                if (await SaveAsync()) Close();
                break;
            case DialogChoice.Secondary:
                Close();
                break;
        }
    }

    /// <summary>
    /// The title-bar ✕ (Planner default 3): true when the window may close now. Dirty asks the same
    /// question as Exit; Save &amp; Exit keeps the window open until the save succeeds, then closes it.
    /// </summary>
    public bool CanClose()
    {
        if (_closeApproved) return true;
        if (IsWorking) return false;
        if (!IsDirty) return true;
        switch (AskUnsaved())
        {
            case DialogChoice.Secondary:
                _closeApproved = true;
                return true;
            case DialogChoice.Primary:
                // This close is cancelled; the save runs once the Closing event is over and then
                // closes the window (WPF refuses Close() while a window is still closing).
                _post(() => _ = SaveThenCloseAsync());
                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// After a save: the id-less file the profile was read from is removed (WU1 review F6). A
    /// failure is logged and returned for a warning; it never fails the save. Null when there was
    /// nothing to remove or it was removed.
    /// </summary>
    private (string File, string Error)? RemoveSupersededFile(string? savedFileName)
    {
        if (_supersededFile is not { } old) return null;
        _supersededFile = null;
        if (string.Equals(old, savedFileName, StringComparison.OrdinalIgnoreCase)) return null;
        var removed = Safe(() => _backend.RemoveSupersededFile(old), OpResult.Fail("unknown"), "remove_superseded_file");
        if (removed.Ok)
        {
            Log.Information("Builder: {Old} replaced by {New} (the profile had no id)", old, savedFileName);
            return null;
        }
        Log.Warning("Builder: saved as {New}, but {Old} could not be removed: {Error}", savedFileName, old, removed.Error);
        return (old, removed.Error ?? "unknown");
    }

    private async Task SaveThenCloseAsync()
    {
        if (await SaveAsync()) Close();
    }

    private DialogChoice AskUnsaved() => _dialogs.Choose(UiStrings.Builder_UnsavedTitle, UiStrings.Builder_UnsavedText,
        UiStrings.Builder_SaveAndExit, UiStrings.Builder_DiscardAndExit, UnsavedChangesDialogId);

    private void Close()
    {
        _closeApproved = true;
        CloseRequested?.Invoke();
    }

    // ---- helpers ------------------------------------------------------------------------------

    /// <summary>No dialog host: the name dialogs answer Cancel and Branding opens nothing.</summary>
    private sealed class NoBuilderDialogs : IBuilderDialogs
    {
        public static readonly NoBuilderDialogs Instance = new();

        public bool EditName(ProfileNameDialogModel model) => false;

        public void EditBranding(ProfileBuilderViewModel model)
        {
        }
    }

    private void ShowToast(string text, BannerKind kind) => Toast.Show(text, kind, TransientMessage.ToastDuration);

    private static T Safe<T>(Func<T> read, T fallback, string what)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Builder: {What} threw", what);
            return fallback;
        }
    }
}
