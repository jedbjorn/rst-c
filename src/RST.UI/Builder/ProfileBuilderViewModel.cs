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
// Feature #18, WU3: the panel cards hold tiles and stacks (decisions #18 and #19). The colour and
// name are edited on the card; tiles are dragged (TileDrops' rules: make a stack, join one, insert
// beside, take out, move a whole stack, append), removed with ×, and grouped with + New Stack, ✎ and
// × on a stack (the stack dialog). Every structural change goes through RST.Core's PanelStacks. A
// profile the Builder opens is normalised first (PanelStacks.Normalize: a shared stack is split, a
// broken one repaired), which counts as an unsaved change; Save refuses a stack of 4+ tools
// (PanelStacks.SaveBlocker). The ▲ ▼ slot buttons and the slot rows' command ids are retired.
// Feature #18, WU4: the Commands pane's Custom Tools (decision #20): the list derived from the saved
// profiles and the working copy, filtered with the catalogue, dragged or double-clicked onto panels
// as fresh slots; + Create new (the Custom Tool dialog); ✎ on a row or a placed custom tool edits it
// everywhere and × on a row removes it everywhere, each after a confirmation that lists the profiles
// that will change (RST.Core CustomToolEditor.Plan). The open profile's clean baseline takes the same
// change when its file was written, and the ribbon reloads when the loaded profile's file changed.

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

public sealed class ProfileBuilderViewModel : ObservableObject, IHasToast
{
    public const string UnsavedChangesDialogId = MessageDialog.DiscardChangesId;
    public const string DeletePanelDialogId = "DeletePanelDialog";
    public const string RemoveCustomToolDialogId = "RemoveCustomToolDialog";
    public const string CustomToolsPartialDialogId = "CustomToolsPartialDialog";

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

    private readonly CustomToolsGroupViewModel _customTools;

    // The file a profile with no id was read from: its first save writes it under a new id and a
    // new file name, and then removes this one (WU1 review F6), so it is not listed twice.
    private string? _supersededFile;

    private int _previewRevision;
    private bool _isWorking;
    private bool _closeApproved;

    // SC-045: set when the window safety net closed the Builder after an error (Abandon).
    private bool _abandoned;

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
        _customTools = new CustomToolsGroupViewModel(this);

        BrandingCommand = new RelayCommand(() => _builderDialogs.EditBranding(this), () => IsIdle);
        PickLogoCommand = new RelayCommand(PickLogo, () => IsIdle);
        ClearLogoCommand = new RelayCommand(ClearLogo, () => IsIdle);
        ExportCommand = new RelayCommand(Export, () => IsIdle);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => IsIdle && IsDirty);
        ExitCommand = new AsyncRelayCommand(ExitAsync, () => IsIdle);
        AddPanelCommand = new RelayCommand(AddPanel, () => IsIdle);
        AddCatalogCommand = new RelayCommand<CatalogNodeViewModel>(n => AddToSelectedPanel(n.Command!), n => IsIdle && n.IsCommand);
        PickColorCommand = new RelayCommand(PickColor, () => IsIdle && SelectedPanel is not null);
        NewProfileCommand = new RelayCommand(() => RequestSwitch(ProfileOptionViewModel.New()), () => IsIdle);
        SwitchProfileCommand = new RelayCommand<ProfileOptionViewModel>(o => SelectedProfileOption = o, _ => IsIdle);
        RenameCommand = new RelayCommand(Rename, () => IsIdle);
        DuplicateCommand = new RelayCommand(Duplicate, () => IsIdle && _editMode);
        RestoreCleanupDefaultsCommand = new RelayCommand(RestoreCleanupDefaults, () => IsIdle);
        CreateCustomToolCommand = new RelayCommand(CreateCustomTool, () => IsIdle);
        EditCustomToolCommand = new RelayCommand<ToolTileViewModel>(EditPlacedCustomTool, t => IsIdle && t is { IsCustom: true });
    }

    /// <summary>Raised when the window should close (Exit, or after the unsaved-changes prompt allowed it).</summary>
    public event Action? CloseRequested;

    /// <summary>True once <see cref="Abandon"/> ran: the Builder is gone after an error.</summary>
    public bool IsAbandoned => _abandoned;

    /// <summary>
    /// SC-045: the window safety net closed the Builder after an error. Unsaved edits are discarded and
    /// nothing more is done from this faulted state: a save, switch or reload that was queued (Save &amp;
    /// Switch, Save &amp; Exit) returns at entry without side effects, and one already in flight stops after
    /// its current await. What already reached disk before the fault stays.
    /// </summary>
    public void Abandon()
    {
        if (_abandoned) return;
        _abandoned = true;
        Log.Information("Builder: abandoned after an error; queued and in-flight saves, switches and reloads stop here");
    }

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
        OnPropertyChanged(nameof(CommandsCount));
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
        // The profile in the editor stays current even when its file is gone from the list (another session removed it).
        var current = ProfileOptions.FirstOrDefault(o => o.Key == previous)
                      ?? (_selectedOption is { } held && held.Key == previous ? held : ProfileOptionViewModel.New());
        SetCurrent(current);
        RefreshCustomTools();
    }

    /// <summary>
    /// Re-reads the saved profiles (another Revit session may have saved, renamed or removed one
    /// since): before the name checks of Save, Rename… and Duplicate…, and when the profile menu opens.
    /// </summary>
    public void RefreshProfiles()
    {
        _library = Safe(_backend.GetProfiles, _library, "get_profiles");
        PopulateOptions();
    }

    private void SetCurrent(ProfileOptionViewModel option)
    {
        _selectedOption = option;
        foreach (var o in ProfileOptions) o.IsCurrent = ReferenceEquals(o, option);
        OnPropertyChanged(nameof(SelectedProfileOption));
    }

    private void RequestSwitch(ProfileOptionViewModel target)
    {
        if (_abandoned || IsWorking) return;
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
                _post(() => UiGuard.Run("Builder: discard and switch", () => ApplySwitch(target)));
                break;
            case DialogChoice.Primary:
                Log.Information("Builder: profile switch → save first");
                _post(() => UiGuard.Start("Builder: save and switch", () => SaveThenSwitchAsync(target.Key), this));
                break;
        }
    }

    private async Task SaveThenSwitchAsync(string targetKey)
    {
        if (_abandoned) return;
        if (!await SaveAsync() || _abandoned) return;
        var target = ProfileOptions.FirstOrDefault(o => o.Key == targetKey) ?? ProfileOptionViewModel.New();
        ApplySwitch(target);
    }

    /// <summary>onProfileSelectChange: "+ New profile" starts fresh; a profile loads into the editor.</summary>
    private void ApplySwitch(ProfileOptionViewModel target)
    {
        if (_abandoned) return;
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
        // The file a first save replaces is set only once the load is complete (SC-046 review N3). A load that
        // fails before the editor changes leaves the previous profile's as it was; one that fails part-way
        // leaves none, so the next Save cannot remove a file that is not the editor's own.
        var superseded = string.IsNullOrEmpty(source.Id) ? fileName : null;
        var editing = ProfileEditing.ForEditing(source);
        _supersededFile = null;
        _profile = editing;
        _selectedPanelIndex = _profile.Panels.Count > 0 ? 0 : -1;
        _editMode = true;
        _originalName = _profile.ProfileName ?? "";

        // The clean baseline is the profile as saved, taken BEFORE the stack normaliser (WU1 review
        // F2, WU2 review F1): a repair then reads as an unsaved change, so Save is enabled and writes it.
        _pristine = ProfileEditing.Snapshot(_profile);
        var repaired = PanelStacks.Normalize(_profile, SerilogLoaderLog.Instance);
        if (repaired.Changed)
            Log.Information("Builder: {Name} normalised on open ({Count} repairs); the editor holds unsaved changes",
                _profile.ProfileName, repaired.Notes.Count);
        ProfileChanged();
        ProfileEdited();
        _supersededFile = superseded;
        if (repaired.Changed) ShowToast(UiStrings.Builder_StacksRepaired(repaired.Notes.Count), BannerKind.Info);
    }

    private void ProfileChanged()
    {
        IconPicker = null;
        RenderCanvas();
        RebuildPresetTabs();
        RebuildCleanupRows();
        RaiseName();
        OnPropertyChanged(nameof(ProfileTab));
        OnPropertyChanged(nameof(PanelsTabName));
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
    /// header's unsaved-changes dot and text show, and Save is enabled. A saved profile read without
    /// an id (a legacy file) counts as changed until Save gives it one, so "open it in the Builder and
    /// save it once" works with no edit (review N7); the id the save assigns clears it.
    /// </summary>
    public bool IsDirty => ProfileEditing.Snapshot(_profile) != _pristine || (_editMode && string.IsNullOrEmpty(_profile.Id));

    private void SnapshotPristine()
    {
        _pristine = ProfileEditing.Snapshot(_profile);
        ProfileEdited();
    }

    /// <summary>After any edit: the unsaved-changes indicator and Save's enabled state follow.</summary>
    internal void ProfileEdited()
    {
        OnPropertyChanged(nameof(IsDirty));
        RefreshCustomTools();
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

    /// <summary>The header button's green "Loaded" dot: the editor holds the profile the Loader has loaded.</summary>
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
        RefreshProfiles();
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
        RefreshProfiles();
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
            OnPropertyChanged(nameof(PanelsTabName));
            RebuildPresetTabs(); // the profile's own tab is never offered for hiding
            PreviewChanged();
        }
    }

    /// <summary>The Panels pane header's "on tab [name]" (a click focuses the tab field on General); "(no tab name)" while it is blank.</summary>
    public string PanelsTabName => string.IsNullOrWhiteSpace(_profile.Tab) ? UiStrings.Builder_NoTabName : _profile.Tab!;

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

    /// <summary>"No matches for "q".": a filter is active and matches nothing (no command and no custom tool).</summary>
    public bool HasNoMatches => !CatalogIsEmpty && _filter is { IsActive: true, AnyMatch: false } && !_customTools.HasMatches;

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
        _customTools.ApplyFilter(_filterText);
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

    /// <summary>
    /// A click on a card selects it (the ring). Only the flags change: the cards are not rebuilt, so
    /// a click that lands in a tile's name box keeps its focus.
    /// </summary>
    internal void SelectPanel(int index)
    {
        if (index == _selectedPanelIndex || index < 0 || index >= Panels.Count) return;
        _selectedPanelIndex = index;
        foreach (var card in Panels) card.IsSelected = card.Index == index;
        OnPropertyChanged(nameof(SelectedPanel));
        OnPropertyChanged(nameof(SelectedPanelIndex));
        RelayCommand.Refresh();
    }

    internal void MovePanel(int index, int delta)
    {
        var to = index + delta;
        // Both ends checked (SC-046, R4): a panel drag's source index can be stale by the time it lands.
        if (index < 0 || index >= _profile.Panels.Count || to < 0 || to >= _profile.Panels.Count) return;
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
        // Its stacks go with it, so Profile.Stacks and the stack slots stay in step (WU3 review N2).
        var orphans = ProfileEditing.RemoveOrphanStacks(_profile);
        if (orphans.Count > 0) Log.Information("Builder: deleted panel {Panel} and its stacks {Stacks}", name, string.Join(", ", orphans));
        if (_selectedPanelIndex >= _profile.Panels.Count) _selectedPanelIndex = _profile.Panels.Count - 1;
        RenderCanvas();
    }

    /// <summary>
    /// × on a tile or a stack row: the tool leaves this panel only, without a confirm (spec #19,
    /// "Remove from one panel"). A stack left with one tool becomes that tool in its place.
    /// </summary>
    internal void RemoveTile(ToolTileViewModel tile)
    {
        if (!IsCurrent(tile, "remove a tool")) return;
        var panel = tile.Panel.Index;
        if (tile.InStack)
        {
            if (PanelStacks.RemoveRow(_profile, panel, tile.Index, tile.Row) != StackResult.Done) return;
        }
        else
        {
            _profile.Panels[panel].Slots.RemoveAt(tile.Index);
        }
        Log.Information("Builder: removed {Tool} from panel {Panel}", tile.Name, _profile.Panels[panel].Name);
        RenderCanvas();
    }

    /// <summary>
    /// The card a command came from still shows the working copy (WU3 review N1): the tile's or
    /// stack's slot is the same object at the same place, and a stack holds the same tools in the
    /// same order. The stack operations address slots by index, so an edit made underneath (WU4's
    /// edit or remove everywhere on the open profile) must not let them hit another slot. Otherwise
    /// the action is refused, logged, and the cards are redrawn.
    /// </summary>
    private bool IsCurrent(PanelItemViewModel item, string action)
    {
        var panel = item.Panel.Index;
        var ok = panel >= 0 && panel < _profile.Panels.Count && ReferenceEquals(_profile.Panels[panel], item.Panel.Model);
        if (ok)
        {
            var slots = _profile.Panels[panel].Slots;
            switch (item)
            {
                case ToolTileViewModel { Stack: { } stack } row:
                    ok = IsCurrentStack(slots, stack) && row.Row < stack.Rows.Count && ReferenceEquals(stack.Rows[row.Row].Model, row.Model);
                    break;
                case StackGroupViewModel stack:
                    ok = IsCurrentStack(slots, stack);
                    break;
                default:
                    ok = item.Index >= 0 && item.Index < slots.Count && ReferenceEquals(slots[item.Index], item.Model);
                    break;
            }
        }
        if (ok) return true;
        Log.Warning("Builder: {Action} refused: the card no longer matches the profile; redrawing", action);
        RenderCanvas();
        return false;
    }

    private bool IsCurrentStack(List<Slot> slots, StackGroupViewModel stack) =>
        stack.Index >= 0 && stack.Index < slots.Count && ReferenceEquals(slots[stack.Index], stack.Model)
        && PanelStacks.ToolsOf(_profile, stack.Model).SequenceEqual(stack.Rows.Select(r => r.Model), ReferenceEqualityComparer.Instance);

    /// <summary>× on a stack: un-stacks it; its tools go back on the panel in order, in its place.</summary>
    internal void Unstack(StackGroupViewModel stack)
    {
        if (!IsCurrent(stack, "un-stack")) return;
        if (PanelStacks.Unstack(_profile, stack.Panel.Index, stack.Index) != StackResult.Done) return;
        Log.Information("Builder: un-stacked {Count} tools on panel {Panel}", stack.Rows.Count, stack.Panel.Name);
        _selectedPanelIndex = stack.Panel.Index;
        RenderCanvas();
    }

    /// <summary>+ New Stack: the stack dialog over the panel's loose tools; 2-3 picks become a stack in the first pick's place.</summary>
    internal void NewStack(int panelIndex)
    {
        if (panelIndex < 0 || panelIndex >= _profile.Panels.Count) return;
        var panel = _profile.Panels[panelIndex];
        var loose = PanelStacks.LooseTools(_profile, panelIndex).Select(i => (i, panel.Slots[i])).ToList();
        if (loose.Count < PanelStacks.MinTools) return;
        var dialog = StackDialogModel.ForNew(panel.Name ?? "", loose, PackDirectory);
        if (!_builderDialogs.EditStack(dialog)) return;
        if (!ReferenceEquals(_profile.Panels.ElementAtOrDefault(panelIndex), panel) || !LooseStillAt(panel, loose)) return;
        var picks = dialog.Picks.Where(p => !p.Member).Select(p => p.Index).ToList();
        var result = PanelStacks.NewStack(_profile, panelIndex, picks);
        Log.Information("Builder: new stack on {Panel} from {Count} tools → {Result}", panel.Name, picks.Count, result);
        AfterStackEdit(panelIndex, result);
    }

    /// <summary>
    /// ✎ on a stack: the same dialog with its rows ticked; reorder, untick (back on the panel), add
    /// the panel's loose tools. Saving with fewer than 2 removes the stack.
    /// </summary>
    internal void EditStack(StackGroupViewModel stack)
    {
        if (!IsCurrent(stack, "edit a stack")) return;
        var panelIndex = stack.Panel.Index;
        var panel = _profile.Panels[panelIndex];
        var loose = PanelStacks.LooseTools(_profile, panelIndex).Select(i => (i, panel.Slots[i])).ToList();
        var dialog = StackDialogModel.ForEdit(panel.Name ?? "", stack.Rows.Select(r => r.Model).ToList(), loose, PackDirectory);
        if (!_builderDialogs.EditStack(dialog)) return;
        // The dialog is modal, but the working copy may still have changed while it was open.
        if (!IsCurrent(stack, "edit a stack") || !LooseStillAt(panel, loose)) return;
        var result = PanelStacks.EditStack(_profile, panelIndex, stack.Index, dialog.Picks);
        Log.Information("Builder: edited a stack on {Panel} to {Count} tools → {Result}", panel.Name, dialog.Count, result);
        AfterStackEdit(panelIndex, result);
    }

    /// <summary>The loose tools a stack dialog listed are still at those places (else refused, logged, redrawn).</summary>
    private bool LooseStillAt(ProfilePanel panel, IReadOnlyList<(int Slot, Slot Tool)> loose)
    {
        if (loose.All(l => l.Slot < panel.Slots.Count && ReferenceEquals(panel.Slots[l.Slot], l.Tool))) return true;
        Log.Warning("Builder: stack dialog refused: the panel {Panel} changed while it was open; redrawing", panel.Name);
        RenderCanvas();
        return false;
    }

    private void AfterStackEdit(int panelIndex, StackResult result)
    {
        if (result == StackResult.Full) ShowToast(UiStrings.Builder_StackFull, BannerKind.Warning);
        if (result != StackResult.Done) return;
        _selectedPanelIndex = panelIndex;
        RenderCanvas();
    }

    /// <summary>
    /// ✎ on a placed custom tool (its parameter is the <see cref="ToolTileViewModel"/>): the Custom
    /// Tool dialog, edit everywhere (feature #18, WU4).
    /// </summary>
    public ICommand? EditCustomToolCommand { get; }

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
    /// A click on a tile's or a stack row's icon: opens the picker on the tool's current value (the
    /// pack list is read once). Only a variant or None changes the tool; Cancel, Escape and an
    /// outside click do not.
    /// </summary>
    internal void PickIcon(ToolTileViewModel tile)
    {
        IconPicker = new IconPickerViewModel(IconPackEntries, tile.Model.IconFile, PackDirectory, outcome =>
        {
            IconPicker = null;
            if (!outcome.Committed) return;
            tile.Model.IconFile = outcome.Value;
            Log.Information("Builder: icon for {Slot} → {Icon}", tile.Name, outcome.Value ?? "(none)");
            tile.IconChanged();
            PreviewChanged();
        });
    }

    /// <summary>renderCanvas: the cards, tiles and stacks again from the profile; the preview redraws.</summary>
    private void RenderCanvas()
    {
        Panels.Clear();
        for (var i = 0; i < _profile.Panels.Count; i++)
        {
            Panels.Add(new PanelCardViewModel(_profile.Panels[i], i, _profile, this) { IsSelected = i == _selectedPanelIndex });
        }
        OnPropertyChanged(nameof(HasPanels));
        OnPropertyChanged(nameof(SelectedPanel));
        OnPropertyChanged(nameof(SelectedPanelIndex));
        PreviewChanged();
    }

    /// <summary>
    /// The panel colour picker, starting from the selected panel's colour. Its Properties pane swatch
    /// is retired (spec #19); each card's swatch (<see cref="PanelCardViewModel.PickColorCommand"/>)
    /// selects its card and opens it.
    /// </summary>
    public ICommand PickColorCommand { get; }

    /// <summary>A card's colour swatch: select that card, then the colour picker.</summary>
    internal void PickPanelColor(int index)
    {
        SelectPanel(index);
        if (_selectedPanelIndex == index) PickColor();
    }

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

    /// <summary>A drag starts: a catalogue command, a tile or a stack row (drawn faded), a stack by its header (faded), or a panel header.</summary>
    public void BeginDrag(BuilderDrag drag)
    {
        _drag = drag ?? throw new ArgumentNullException(nameof(drag));
        if (drag.Kind != DragKind.Slot) return;
        if (Item(drag.PanelIndex, drag.SlotIndex) is not { } item) return;
        if (drag.Row < 0) item.IsDragSource = true;
        else if (item is StackGroupViewModel stack && drag.Row < stack.Rows.Count) stack.Rows[drag.Row].IsDragSource = true;
    }

    /// <summary>The drag ended (dropped or cancelled): every marker clears.</summary>
    public void EndDrag()
    {
        _drag = null;
        ClearDropMarkers();
        foreach (var item in AllItems()) item.IsDragSource = false;
    }

    /// <summary>
    /// The pointer is over a panel card outside its tiles: a panel marks before or after the card
    /// (the cursor's half); a tool or a stack highlights it (appended to the panel's end). True when
    /// the card accepts the drag.
    /// </summary>
    public bool DragOverPanel(int panelIndex, bool upperHalf)
    {
        var marker = _drag?.Kind == DragKind.Panel
            ? PanelDrops.CardMarker(_drag, panelIndex, upperHalf)
            : TileDrops.PlanCard(_profile, _drag, panelIndex).Marker;
        SetMarker(panelIndex, null, -1, marker);
        return marker != DropMarker.None;
    }

    /// <summary>
    /// The pointer is over a tile, a stack or a stack's row: the marker shows what a drop would do
    /// (TileDrops' rules). True when the target takes the drop: a change, or a full stack, which
    /// shows the refusal marker and refuses on drop with its message. False for nothing to do or a
    /// stale target (no marker).
    /// </summary>
    public bool DragOverTile(TileHit hit)
    {
        var plan = TileDrops.Plan(_profile, _drag, hit);
        var none = plan.Kind is TileDropKind.None or TileDropKind.Stale;
        SetMarker(hit.Panel, none ? null : plan.Slot, plan.MarkerRow, plan.Marker);
        // A full stack shows the refusal marker and still takes the drop, so the drop can refuse it
        // with its message: OLE delivers no Drop after a None effect (WU3 review N5).
        return !none;
    }

    /// <summary>The pointer left a target: its marker clears.</summary>
    public void ClearDropMarkers()
    {
        foreach (var card in Panels) card.DropMarker = DropMarker.None;
        foreach (var item in AllItems()) item.DropMarker = DropMarker.None;
    }

    /// <summary>A drop on a panel card outside its tiles: a panel reorder, or a tool or stack to the panel's end.</summary>
    public void DropOnPanel(int panelIndex, bool upperHalf)
    {
        if (_drag is not { } drag) return;
        if (drag.Kind == DragKind.Panel)
        {
            AfterDrop(PanelDrops.DropOnPanel(_profile, drag, panelIndex, upperHalf));
            return;
        }
        var plan = TileDrops.PlanCard(_profile, drag, panelIndex);
        AfterTileDrop(drag, plan, TileDrops.Apply(_profile, drag, plan));
    }

    /// <summary>A drop on a tile, a stack or a stack's row (TileDrops' rules); a full stack refuses with a toast.</summary>
    public void DropOnTile(TileHit hit)
    {
        if (_drag is not { } drag) return;
        var plan = TileDrops.Plan(_profile, drag, hit);
        AfterTileDrop(drag, plan, TileDrops.Apply(_profile, drag, plan));
    }

    private void AfterTileDrop(BuilderDrag drag, TileDrop plan, StackResult result)
    {
        Log.Information("Builder: drop {Kind} {Source} → panel {Panel} slot {Slot}: {Result}",
            plan.Kind, drag.Kind switch
            {
                DragKind.Catalog => drag.Command?.Id,
                DragKind.CustomTool => drag.Tool?.Link,
                _ => $"{drag.PanelIndex}/{drag.SlotIndex}/{drag.Row}",
            }, plan.Panel, plan.Slot, result);
        if (result == StackResult.Full) ShowToast(UiStrings.Builder_StackFull, BannerKind.Warning);
        if (plan.Kind == TileDropKind.Stale)
        {
            // The working copy changed under the cards: nothing moved; show what is there now.
            Log.Warning("Builder: drop refused: the dragged or target slot is no longer where the card showed it; redrawing");
            _drag = null;
            RenderCanvas();
            return;
        }
        AfterDrop(result == StackResult.Done ? plan.Panel : null);
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

    /// <summary>One target is marked at a time: a card (slot null), a tile or stack (row -1), or a stack's row.</summary>
    private void SetMarker(int panelIndex, int? slotIndex, int row, DropMarker marker)
    {
        foreach (var card in Panels)
        {
            card.DropMarker = slotIndex is null && card.Index == panelIndex ? marker : DropMarker.None;
            foreach (var item in card.Items)
            {
                var here = slotIndex == item.Index && card.Index == panelIndex;
                item.DropMarker = here && row < 0 ? marker : DropMarker.None;
                if (item is StackGroupViewModel stack)
                    foreach (var r in stack.Rows) r.DropMarker = here && r.Row == row ? marker : DropMarker.None;
            }
        }
    }

    private IEnumerable<PanelItemViewModel> AllItems() =>
        Panels.SelectMany(p => p.Items).SelectMany(i => i is StackGroupViewModel s ? s.Rows.Prepend<PanelItemViewModel>(s) : new[] { i });

    private PanelItemViewModel? Item(int panelIndex, int slotIndex) =>
        panelIndex >= 0 && panelIndex < Panels.Count && slotIndex >= 0 && slotIndex < Panels[panelIndex].Items.Count
            ? Panels[panelIndex].Items[slotIndex]
            : null;

    // ---- Custom Tools (feature #18, WU4) ---------------------------------------------------------

    /// <summary>The Custom Tools group at the top of the Commands pane (decision #20).</summary>
    public CustomToolsGroupViewModel CustomToolsGroup => _customTools;

    /// <summary>The Commands pane's count: the catalogue's commands plus the custom tools.</summary>
    public int CommandsCount => CatalogCount + _customTools.Count;

    /// <summary>+ Create new: the Custom Tool dialog; the tool goes on the selected panel (or the first).</summary>
    public ICommand CreateCustomToolCommand { get; }

    private IReadOnlyList<IconPackEntry> IconPackEntries => _iconPack ??= Safe(_backend.GetIconPack, [], "list_iconpack");

    /// <summary>
    /// The list again from the saved profiles (as last read) and the working copy: after every edit,
    /// and whenever the saved profiles are read. Rows are rebuilt only when what they show changed.
    /// </summary>
    private void RefreshCustomTools()
    {
        var tools = Safe(() => CustomTools.Derive(_library.Select(e => e.Profile), _profile), [], "derive_custom_tools");
        if (!_customTools.Update(tools)) return;
        _customTools.ApplyFilter(_filterText);
        OnPropertyChanged(nameof(CommandsCount));
        OnPropertyChanged(nameof(HasNoMatches));
    }

    /// <summary>
    /// + Create new: a profile with no panels is refused; otherwise the dialog, then a new slot at the
    /// end of the selected panel (the first when none is selected). A link the list already holds is
    /// added anyway, as another placement.
    /// </summary>
    private void CreateCustomTool()
    {
        if (IsWorking) return;
        if (_profile.Panels.Count == 0)
        {
            ShowToast(UiStrings.Builder_AddPanelFirst, BannerKind.Error);
            return;
        }
        var target = _selectedPanelIndex >= 0 && _selectedPanelIndex < _profile.Panels.Count ? _selectedPanelIndex : 0;
        var dialog = CustomToolDialogModel.ForCreate(_profile.Panels[target].Name ?? "", IconPackEntries, PackDirectory);
        if (!_builderDialogs.EditCustomTool(dialog)) return;
        var edit = dialog.Result;
        if (CustomTools.Check(edit) is not null) return;
        var existed = _customTools.Find(CustomTools.Normalize(edit.Address)) is not null;
        if (CustomTools.AddToPanel(_profile, target, CustomTools.NewSlot(edit)) is not { } panel)
        {
            ShowToast(UiStrings.Builder_AddPanelFirst, BannerKind.Error);
            return;
        }
        Log.Information("Builder: custom tool created {Label} → {Link} on {Panel} (existing link: {Existing})",
            edit.Label, CustomTools.Normalize(edit.Address), _profile.Panels[panel].Name, existed);
        _selectedPanelIndex = panel;
        RenderCanvas();
        ShowToast(existed ? UiStrings.Builder_ToolAddedExisting : UiStrings.Builder_ToolAdded(_profile.Panels[panel].Name ?? ""), BannerKind.Success);
    }

    /// <summary>A double-click on a Custom Tools row: a new placement on the selected panel (as a catalogue command).</summary>
    internal void AddCustomToolToSelectedPanel(CustomTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (_selectedPanelIndex < 0 || _selectedPanelIndex >= _profile.Panels.Count) AddPanelCore();
        _profile.Panels[_selectedPanelIndex].Slots.Add(CustomTools.SlotFor(tool));
        Log.Information("Builder: custom tool {Link} added to {Panel}", tool.Link, _profile.Panels[_selectedPanelIndex].Name);
        RenderCanvas();
    }

    /// <summary>✎ on a placed custom tool (a tile or a stack row): the same edit as ✎ on its Custom Tools row.</summary>
    private void EditPlacedCustomTool(ToolTileViewModel? tile)
    {
        if (tile is null || !tile.IsCustom) return;
        RefreshCustomTools();
        if (_customTools.Find(CustomTools.LinkOf(tile.Model)) is { } tool) EditCustomTool(tool);
    }

    /// <summary>✎ on a Custom Tools row or a placed custom tool: edit everywhere.</summary>
    internal void EditCustomTool(CustomTool tool) => UiGuard.Start("Builder: edit custom tool", () => EditCustomToolAsync(tool), this);

    /// <summary>× on a Custom Tools row: remove everywhere, after a confirmation that lists the profiles.</summary>
    internal void RemoveCustomTool(CustomTool tool) => UiGuard.Start("Builder: remove custom tool", () => RemoveCustomToolAsync(tool), this);

    /// <summary>
    /// Edit everywhere (decision #20): the dialog filled in, with a warning naming every profile that
    /// will change ("Update in N profiles", N being what will be written) and the ones that will
    /// not; then every saved profile holding the tool, and the working copy.
    /// </summary>
    internal async Task EditCustomToolAsync(CustomTool tool)
    {
        if (_abandoned || IsWorking) return;
        RefreshProfiles(); // another Revit session may have saved since
        tool = _customTools.Find(tool.Link) ?? tool;
        if (Safe<CustomToolPreview?>(() => _backend.PreviewCustomTool(tool.Link, _profile), null, "plan_custom_tool") is not { } preview)
        {
            ShowToast(UiStrings.Builder_ToolChangeFailed, BannerKind.Error);
            return;
        }
        // The confirmation's count follows the dialog: a dry run of the edit as it stands (delta review F8).
        var target = tool;
        var dialog = CustomToolDialogModel.ForEdit(tool, preview.Plan, IconPackEntries, PackDirectory, e => preview.PlanEdit(target, e));
        if (!_builderDialogs.EditCustomTool(dialog) || !dialog.CanConfirm) return;
        var edit = dialog.Result;
        if (CustomTools.Check(edit) is not null) return;
        Log.Information("Builder: edit custom tool {Link} everywhere ({Count} profiles)", tool.Link, dialog.Plan?.Count);
        var update = Safe<CustomToolUpdate?>(() => _backend.EditCustomTool(tool, edit, _profile, _activeId), null, "edit_custom_tool");
        await AfterCustomToolUpdateAsync(update, p => CustomTools.ApplyEdit(p, tool, edit), UiStrings.Builder_ToolUpdated);
    }

    /// <summary>
    /// Remove everywhere (decision #20): asks first, listing the profiles ("Remove from N profiles");
    /// then every slot with the link goes from every saved profile and the working copy.
    /// </summary>
    internal async Task RemoveCustomToolAsync(CustomTool tool)
    {
        if (_abandoned || IsWorking) return;
        RefreshProfiles();
        if (Safe(() => _backend.PreviewCustomTool(tool.Link, _profile).Plan, null, "plan_custom_tool") is not { } plan)
        {
            ShowToast(UiStrings.Builder_ToolChangeFailed, BannerKind.Error);
            return;
        }
        var text = UiStrings.Builder_RemoveToolText(tool.Label, CustomTools.ShortLink(tool.Link));
        if (plan.Refused.Count > 0)
            text += Environment.NewLine + Environment.NewLine + UiStrings.Builder_ToolNotChanged + Environment.NewLine
                    + string.Join(Environment.NewLine, plan.Refused.Select(CustomToolDialogModel.RefusalLine));
        if (plan.Count == 0)
        {
            _dialogs.ShowMessage(UiStrings.Builder_RemoveToolTitle, UiStrings.Builder_ToolNothingToChange + Environment.NewLine + Environment.NewLine + text, RemoveCustomToolDialogId);
            return;
        }
        var names = CustomToolDialogModel.TargetNames(plan.Changing);
        if (!_dialogs.Confirm(UiStrings.Builder_RemoveToolTitle, text, names,
                UiStrings.Builder_RemoveFrom(CustomToolDialogModel.ProfileCount(plan.Count)), danger: true, RemoveCustomToolDialogId))
            return;
        Log.Information("Builder: remove custom tool {Link} everywhere ({Count} profiles)", tool.Link, plan.Count);
        var update = Safe<CustomToolUpdate?>(() => _backend.RemoveCustomTool(tool.Link, _profile, _activeId), null, "remove_custom_tool");
        await AfterCustomToolUpdateAsync(update, p => CustomTools.ApplyRemove(p, tool.Link), UiStrings.Builder_ToolRemoved);
    }

    /// <summary>
    /// After an edit or remove everywhere: when the open profile's file was written, its clean
    /// baseline gets the same change (so a change already on disk does not read as unsaved, while
    /// other unsaved edits stay unsaved); the saved profiles are read again, the cards rebuilt from
    /// the working copy, failures listed (and logged), and the ribbon reloaded when the loaded
    /// profile's file changed.
    /// </summary>
    private async Task AfterCustomToolUpdateAsync(CustomToolUpdate? update, Action<Profile> change, Func<object?, string> done)
    {
        if (_abandoned) return;
        if (update is null)
        {
            // The backend threw (logged): the files may be partly written; show what is there now.
            RefreshProfiles();
            RenderCanvas();
            ShowToast(UiStrings.Builder_ToolChangeFailed, BannerKind.Error);
            return;
        }
        var openId = string.IsNullOrEmpty(_profile.Id) ? null : _profile.Id;
        var openWritten = _editMode && openId is not null && update.Written.Any(w => w.ProfileId == openId);
        if (openWritten)
        {
            try
            {
                var baseline = ProfileEditing.FromSnapshot(_pristine);
                change(baseline);
                _pristine = ProfileEditing.Snapshot(baseline);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Builder: the clean baseline could not take the custom tool change; the editor reads as unsaved");
            }
        }

        RefreshProfiles();
        RenderCanvas(); // the working copy changed underneath the cards: rebuild them, never reuse their indexes
        var changed = update.Written.Count + (update.WorkingCopyChanged && !openWritten ? 1 : 0);
        Log.Information("Builder: custom tool change: written={Written} failed={Failed} working={Working} reload={Reload}",
            update.Written.Count, update.Failed.Count, update.WorkingCopyChanged, update.ActiveProfileChanged);
        if (update.Failed.Count > 0)
        {
            foreach (var f in update.Failed) Log.Warning("Builder: custom tool change not written for {Name} ({File}): {Error}", f.ProfileName, f.FileName, f.Error);
            var lines = update.Failed.Select(f => UiStrings.Builder_ToolFailedLine(
                string.IsNullOrWhiteSpace(f.ProfileName) ? UiStrings.Builder_UnnamedProfile : f.ProfileName, f.FileName, f.Error));
            _dialogs.ShowMessage(UiStrings.Builder_ToolsPartialTitle, UiStrings.Builder_ToolsPartialText(string.Join(Environment.NewLine, lines)), CustomToolsPartialDialogId);
        }

        var message = done(CustomToolDialogModel.ProfileCount(changed));
        if (!update.ActiveProfileChanged)
        {
            if (update.Failed.Count == 0) ShowToast(message, BannerKind.Success);
            return;
        }
        var active = _library.FirstOrDefault(e => e.Profile.Id == _activeId)?.Profile;
        Log.Information("Builder: the loaded profile changed — auto-reloading");
        IsWorking = true;
        try
        {
            var load = await ReloadActiveAsync(active?.ProfileName ?? "", _activeId);
            if (_abandoned) return;
            if (load.Ok) ShowToast(message + " " + UiStrings.Builder_ToolRibbonReloaded, BannerKind.Success);
            else ShowToast(UiStrings.Builder_ToolReloadFailed, BannerKind.Error);
        }
        finally
        {
            IsWorking = false;
        }
    }

    /// <summary>
    /// The ribbon-only reload of the loaded profile (the existing reload-after-save path: Save and
    /// the Custom Tools' edit and remove everywhere), with the loaded profile's hidden tabs and
    /// disable flag. A throw is logged and returned as a failure.
    /// </summary>
    private async Task<LoadResult> ReloadActiveAsync(string name, string? id)
    {
        if (_abandoned) return LoadResult.Fail("abandoned after an error");
        try
        {
            return await _backend.LoadAsync(new LoadRequest(name, id, _activeDisable, _activeHiddenTabs));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Builder: auto-reload threw");
            return LoadResult.Fail(ex.Message);
        }
    }

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
    /// Rename… already refuses a taken name. Save re-reads the saved profiles and checks again, as a
    /// safety net for a name another profile took since (a second Revit session saving): a rename in
    /// edit mode (<see cref="ProfileEditing.PlanSave"/>), and a new profile's first save (WU1 review F5).
    /// </remarks>
    public async Task<bool> SaveAsync()
    {
        if (_abandoned || IsWorking) return false;
        RefreshProfiles();
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
                ShowToast(UiStrings.Builder_NameCollision((_profile.ProfileName ?? "").Trim()), BannerKind.Error);
                return false;
        }
        // A stack of 4+ tools cannot be written (read validation stays strict, decision #28).
        if (PanelStacks.SaveBlocker(_profile) is { } blocker)
        {
            Log.Warning("Builder: save refused: {Reason}", blocker);
            ShowToast(blocker, BannerKind.Error);
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
        // The loaded legacy profile's old file goes only once the reload has re-pointed
        // active_profile.json at the new one (review N8a); any other goes now.
        var oldFileKept = wasActiveIdless ? null : RemoveSupersededFile(result.FileName);

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
            var load = await ReloadActiveAsync(_profile.ProfileName ?? "", _profile.Id);
            if (_abandoned)
            {
                Log.Information("Builder: abandoned during the reload after a save; nothing more is done");
                return false;
            }
            if (!load.Ok)
            {
                if (wasActiveIdless && _supersededFile is { } old)
                {
                    // The pointer may still name the old file: keep it, so the loaded profile still resolves.
                    _supersededFile = null;
                    Log.Warning("Builder: auto-reload failed; kept {Old}, which active_profile.json may still name", old);
                    ShowToast(UiStrings.Builder_SavedReloadFailedOldKept(result.FileName, old), BannerKind.Warning);
                }
                else
                {
                    ShowToast(UiStrings.Builder_SavedReloadFailed, BannerKind.Error);
                }
                return true;
            }
            if (wasActiveIdless)
            {
                oldFileKept = RemoveSupersededFile(result.FileName);
                _library = Safe(_backend.GetProfiles, _library, "get_profiles");
                PopulateOptions(_profile.Id);
                if (oldFileKept is { } late)
                {
                    ShowToast(UiStrings.Builder_SavedOldFileKept(result.FileName, late.File, late.Error), BannerKind.Warning);
                    return true;
                }
            }
            if (oldFileKept is null) ShowToast(UiStrings.Builder_SavedReloaded, BannerKind.Success); // else the warning stays
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
        if (_abandoned || IsWorking) return;
        if (!IsDirty)
        {
            Close();
            return;
        }
        switch (AskUnsaved())
        {
            case DialogChoice.Primary:
                if (await SaveAsync() && !_abandoned) Close();
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
                _post(() => UiGuard.Start("Builder: save and exit", SaveThenCloseAsync, this));
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
        if (_abandoned) return;
        if (await SaveAsync() && !_abandoned) Close();
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

        public bool EditStack(StackDialogModel model) => false;

        public bool EditCustomTool(CustomToolDialogModel model) => false;

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
