// rst-c's own (not vendored): the Profile Builder's view-model (spec #16, WU4), a port of
// profile_builder.html's script (1361-3453): boot, the profile dropdown (startFresh /
// loadIntoProfile), the machine-wide branding, the catalogue tree and filter, double-click to add,
// the panels list (add, select, rename, ▲ ▼ ×), the slot rows (rename, ▲ ▼ ×), drag and drop (the
// four kinds, WU5), the icon picker (WU5), the Properties pane (Min Revit, opacity, the selected
// panel's name and color, the URL button editor, RST Presets and Junk Cleanup with their Configure
// modals, WU5), the live preview, Save (rename-as-copy, required add-ins, the auto-reload of the
// active profile), Export ZIP and Exit. Every toast keeps its text.
// Changes are the spec's: the unsaved-changes prompt also guards the title-bar ✕ and the dropdown
// (Planner default 3), the panel delete confirm is the family's destructive MessageDialog, and the
// Cleanup modal's Cancel reverts (Planner default 4).
// Stacks (feature #15, spec #17, decision #16 item 5) port the page's hidden stack authoring
// (c5b0004a, 2017-2066 and 2553-2616) into the Properties pane's Stacks section: add ("Stack N"),
// drop or double-click commands in, a per-tool icon, reorder and remove tools, rename (references
// rewritten, a duplicate refused), delete (the destructive confirm; references removed), place in
// the selected panel ("+ Panel" or a drag onto a panel). A stack holds 2 or 3 tools while it is
// authored: a fourth is refused with a toast, and one with fewer than 2 is flagged and blocks Save.
// A stack no panel places is flagged and removed on save. The rules are RST.Core StackEditing.

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
using Serilog;
using ProfilePanel = RST.Core.Profiles.Panel;

namespace RST.UI.Builder;

public sealed class ProfileBuilderViewModel : ObservableObject
{
    public const string UnsavedChangesDialogId = MessageDialog.DiscardChangesId;
    public const string DeletePanelDialogId = "DeletePanelDialog";
    public const string DeleteStackDialogId = "DeleteStackDialog";

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
    private IReadOnlyList<string> _activeHiddenTabs = [];
    private bool _activeDisable;

    private Profile _profile = ProfileEditing.NewProfile();
    private string _pristine = "";
    private bool _editMode;
    private string _originalName = "";
    private int _selectedPanelIndex = -1;
    private string? _selectedStack;
    private ProfileOptionViewModel? _selectedOption;

    private BrandingState _branding = BrandingState.None;
    private bool _isBrandingOpen;

    private bool _isUrlEditorOpen;
    private string _urlLabel = "";
    private string _urlAddress = "";

    private int _previewRevision;
    private bool _isWorking;
    private bool _closeApproved;

    /// <param name="backend">The Builder's data and writes (a fake in tests).</param>
    /// <param name="dialogs">The family dialogs: confirm, the three-way prompt, the color picker, the file dialogs.</param>
    /// <param name="schedule">Runs an action once after a delay (the toasts); null uses the dispatcher. Tests pass a fake clock.</param>
    /// <param name="post">
    /// Runs an action after the current input event: the dropdown's snap-back, the switch after the
    /// unsaved prompt, and Save &amp; Exit from the title-bar ✕ (after the Closing event). Null uses the dispatcher.
    /// </param>
    /// <param name="builderDialogs">The RST Presets and Junk Cleanup modals; null cancels both (tests that do not open them).</param>
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

        CloseBrandingCommand = new RelayCommand(() => IsBrandingOpen = false);
        PickLogoCommand = new RelayCommand(PickLogo, () => IsIdle);
        ClearLogoCommand = new RelayCommand(ClearLogo, () => IsIdle);
        ExportCommand = new RelayCommand(Export, () => IsIdle);
        SaveCommand = new RelayCommand(() => _ = SaveAsync(), () => IsIdle);
        ExitCommand = new RelayCommand(() => _ = ExitAsync(), () => IsIdle);
        AddPanelCommand = new RelayCommand(AddPanel, () => IsIdle);
        AddCatalogCommand = new RelayCommand<CatalogNodeViewModel>(n => AddCatalog(n.Command!), n => IsIdle && n.IsCommand);
        AddStackCommand = new RelayCommand(AddStack, () => IsIdle);
        PickColorCommand = new RelayCommand(PickColor, () => IsIdle && SelectedPanel is not null);
        OpenUrlEditorCommand = new RelayCommand(OpenUrlEditor, () => IsIdle && SelectedPanel is not null);
        CancelUrlEditorCommand = new RelayCommand(() => IsUrlEditorOpen = false);
        AddUrlCommand = new RelayCommand(AddUrlButton, () => IsIdle && SelectedPanel is not null);
        ConfigurePresetsCommand = new RelayCommand(ConfigurePresets, () => IsIdle);
        ConfigureCleanupCommand = new RelayCommand(ConfigureCleanup, () => IsIdle);
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
        _activeHiddenTabs = active is { IsBlank: false } ? active.HiddenTabs ?? [] : [];
        _activeDisable = active is { IsBlank: false } && active.DisableNonRequired;
        _lookup = Safe(_backend.GetAddinLookup, new Dictionary<string, AddinLookupEntry>(), "get_addin_lookup");
        Log.Information("Builder boot: catalog={Catalog} profiles={Profiles} active={Active} lookup={Lookup} tabs={Tabs}",
            catalog.Count, _library.Count, _activeId, _lookup.Count, _backend.AllTabs.Count);

        PopulateOptions();
        StartFresh();
    }

    // ---- the profile dropdown -----------------------------------------------------------------

    public ObservableCollection<ProfileOptionViewModel> ProfileOptions { get; } = [];

    /// <summary>The dropdown's selection. Choosing another entry switches the editor (asking first when dirty).</summary>
    public ProfileOptionViewModel? SelectedProfileOption
    {
        get => _selectedOption;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedOption)) return;
            RequestSwitch(value);
        }
    }

    /// <summary>"edit" mode: the editor holds a profile from the dropdown, or one saved once.</summary>
    public bool IsEditMode => _editMode;

    private void PopulateOptions(string? selectKey = null)
    {
        var previous = selectKey ?? _selectedOption?.Key;
        ProfileOptions.Clear();
        ProfileOptions.Add(ProfileOptionViewModel.New());
        foreach (var entry in _library) ProfileOptions.Add(ProfileOptionViewModel.For(entry, _activeId));
        _selectedOption = ProfileOptions.FirstOrDefault(o => o.Key == previous) ?? ProfileOptions[0];
        OnPropertyChanged(nameof(SelectedProfileOption));
    }

    private void RequestSwitch(ProfileOptionViewModel target)
    {
        if (IsWorking)
        {
            SnapDropdownBack();
            return;
        }
        if (!IsDirty)
        {
            ApplySwitch(target);
            return;
        }
        switch (_dialogs.Choose(UiStrings.Builder_UnsavedTitle, UiStrings.Builder_UnsavedText,
                    UiStrings.Builder_SaveAndSwitch, UiStrings.Builder_DiscardAndSwitch, UnsavedChangesDialogId))
        {
            // Both run after the ComboBox's selection write is over: the save rebuilds the
            // dropdown's list, which must not happen inside its own selection change.
            case DialogChoice.Secondary:
                Log.Information("Builder: dropdown switch → discard unsaved changes");
                _post(() => ApplySwitch(target));
                break;
            case DialogChoice.Primary:
                Log.Information("Builder: dropdown switch → save first");
                SnapDropdownBack();
                _post(() => _ = SaveThenSwitchAsync(target.Key));
                break;
            default:
                SnapDropdownBack();
                break;
        }
    }

    private async Task SaveThenSwitchAsync(string targetKey)
    {
        if (!await SaveAsync()) return;
        var target = ProfileOptions.FirstOrDefault(o => o.Key == targetKey) ?? ProfileOptions[0];
        ApplySwitch(target);
    }

    // The ComboBox has already shown the new entry; put it back once the input event is over.
    private void SnapDropdownBack() => _post(() => OnPropertyChanged(nameof(SelectedProfileOption)));

    /// <summary>onProfileSelectChange: "+ New Profile" starts fresh; a profile loads into the editor.</summary>
    private void ApplySwitch(ProfileOptionViewModel target)
    {
        _selectedOption = target;
        OnPropertyChanged(nameof(SelectedProfileOption));
        if (target.Entry is null)
        {
            Log.Information("Builder: dropdown → new profile");
            StartFresh();
            return;
        }
        Log.Information("Builder: dropdown → load existing {Name} ({Id})", target.Entry.Profile.ProfileName, target.Entry.Profile.Id);
        LoadIntoEditor(target.Entry.Profile);
    }

    /// <summary>startFresh: a blank profile with one panel ("Panel 1"), new mode, a clean baseline.</summary>
    private void StartFresh()
    {
        _profile = ProfileEditing.NewProfile();
        _selectedPanelIndex = -1;
        _selectedStack = null;
        _editMode = false;
        _originalName = "";
        AddPanelCore();
        ProfileChanged();
        SnapshotPristine();
    }

    /// <summary>loadIntoProfile + the dropdown handler: edit mode, panel 0 selected, a clean baseline.</summary>
    private void LoadIntoEditor(Profile source)
    {
        _profile = ProfileEditing.ForEditing(source);
        _selectedPanelIndex = _profile.Panels.Count > 0 ? 0 : -1;
        _selectedStack = null;
        _editMode = true;
        _originalName = _profile.ProfileName ?? "";
        ProfileChanged();
        SnapshotPristine();
    }

    private void ProfileChanged()
    {
        IsUrlEditorOpen = false;
        IconPicker = null;
        RenderStacks();
        RenderCanvas();
        OnPropertyChanged(nameof(ProfileName));
        OnPropertyChanged(nameof(ProfileTab));
        OnPropertyChanged(nameof(MinVersion));
        OnPropertyChanged(nameof(PanelOpacity));
        OnPropertyChanged(nameof(PresetsSummary));
        OnPropertyChanged(nameof(CleanupSummary));
        OnPropertyChanged(nameof(PreviewProfile));
        OnPropertyChanged(nameof(IsEditMode));
    }

    // ---- dirty tracking -----------------------------------------------------------------------

    /// <summary>The profile differs from the clean baseline (boot, a dropdown switch, the last save).</summary>
    public bool IsDirty => ProfileEditing.Snapshot(_profile) != _pristine;

    private void SnapshotPristine() => _pristine = ProfileEditing.Snapshot(_profile);

    /// <summary>The profile being edited (the preview draws it; tests read it).</summary>
    public Profile Profile => _profile;

    // ---- header fields ------------------------------------------------------------------------

    public string ProfileName
    {
        get => _profile.ProfileName ?? "";
        set
        {
            if (_profile.ProfileName == (value ?? "")) return;
            _profile.ProfileName = value ?? "";
            OnPropertyChanged();
        }
    }

    /// <summary>The ribbon tab; the preview's tab pill follows it as you type.</summary>
    public string ProfileTab
    {
        get => _profile.Tab ?? "";
        set
        {
            if (_profile.Tab == (value ?? "")) return;
            _profile.Tab = value ?? "";
            OnPropertyChanged();
            PreviewChanged();
        }
    }

    // ---- branding (machine-wide, not part of the profile) ------------------------------------

    public bool IsBrandingOpen
    {
        get => _isBrandingOpen;
        set
        {
            if (!SetField(ref _isBrandingOpen, value)) return;
            if (value) RaiseBranding();
        }
    }

    /// <summary>The Branding button's dot: filled when a logo or a URL is set.</summary>
    public bool HasBranding => _branding.IsSet;

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

    public ICommand CloseBrandingCommand { get; }

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
        // The file dialog takes the focus and the popover closes behind it; the page's popover stayed
        // open, so it reopens once the dialog is done (VM finding, WU5).
        var wasOpen = IsBrandingOpen;
        try
        {
            PickLogoCore();
        }
        finally
        {
            if (wasOpen) _post(() => IsBrandingOpen = true);
        }
    }

    private void PickLogoCore()
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
        OnPropertyChanged(nameof(HasBranding));
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
    /// Double-click a command: into the selected stack when one is selected (spec #17), else
    /// addToolToSelectedPanel.
    /// </summary>
    public void AddCatalog(CatalogCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (SelectedStack is { } stack) AddToolToStack(stack, command);
        else AddToSelectedPanel(command);
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

    /// <summary>The selected panel (the Properties pane edits it), or null.</summary>
    public PanelCardViewModel? SelectedPanel =>
        _selectedPanelIndex >= 0 && _selectedPanelIndex < Panels.Count ? Panels[_selectedPanelIndex] : null;

    public bool HasSelectedPanel => SelectedPanel is not null;

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
        // Clicking a panel makes it the double-click target again (a selected stack lets go).
        ClearStackSelection();
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
    internal void PickIcon(SlotRowViewModel slot) => PickIcon(slot.Model, slot.Name, slot.IconChanged);

    /// <summary>A stack tool's Pick-icon button: the same picker, for the tool in the stack (spec #17).</summary>
    internal void PickIcon(StackToolRowViewModel tool) => PickIcon(tool.Model, tool.Stack.Name + " › " + tool.Name, tool.IconChanged);

    private void PickIcon(Slot model, string what, Action changed)
    {
        _iconPack ??= Safe(_backend.GetIconPack, [], "list_iconpack");
        IconPicker = new IconPickerViewModel(_iconPack, model.IconFile, PackDirectory, outcome =>
        {
            IconPicker = null;
            if (!outcome.Committed) return;
            model.IconFile = outcome.Value;
            Log.Information("Builder: icon for {Slot} → {Icon}", what, outcome.Value ?? "(none)");
            changed();
            PreviewChanged();
        });
    }

    /// <summary>renderCanvas: the cards and rows again from the profile; the URL editor closes, the preview redraws.</summary>
    private void RenderCanvas()
    {
        IsUrlEditorOpen = false;
        Panels.Clear();
        for (var i = 0; i < _profile.Panels.Count; i++)
        {
            Panels.Add(new PanelCardViewModel(_profile.Panels[i], i, this) { IsSelected = i == _selectedPanelIndex });
        }
        OnPropertyChanged(nameof(HasPanels));
        OnPropertyChanged(nameof(SelectedPanel));
        OnPropertyChanged(nameof(HasSelectedPanel));
        OnPropertyChanged(nameof(SelectedPanelIndex));
        // A stack slot may have been placed, moved or removed: the "not on a panel" flags follow.
        foreach (var card in Stacks) card.PlacementChanged();
        PreviewChanged();
    }

    // ---- stacks (spec #17) --------------------------------------------------------------------

    /// <summary>The Stacks section's cards, in the profile's stack order.</summary>
    public ObservableCollection<StackCardViewModel> Stacks { get; } = [];

    public bool HasStacks => Stacks.Count > 0;

    /// <summary>The selected stack (double-click adds commands to it), or null.</summary>
    public StackCardViewModel? SelectedStack => _selectedStack is null ? null : Stacks.FirstOrDefault(c => c.Name == _selectedStack);

    public bool HasSelectedStack => SelectedStack is not null;

    /// <summary>"+ Add Stack".</summary>
    public ICommand AddStackCommand { get; }

    internal bool IsStackPlaced(string name) => StackEditing.IsPlaced(_profile, name);

    /// <summary>addStack: a new empty "Stack N", selected so double-clicks fill it.</summary>
    private void AddStack()
    {
        var name = StackEditing.AddStack(_profile);
        Log.Information("Builder: stack added {Stack}", name);
        _selectedStack = name;
        RenderStacks();
        PreviewChanged();
    }

    internal void SelectStack(StackCardViewModel card)
    {
        if (_selectedStack == card.Name) return;
        _selectedStack = card.Name;
        RaiseStackSelection();
    }

    private void ClearStackSelection()
    {
        if (_selectedStack is null) return;
        _selectedStack = null;
        RaiseStackSelection();
    }

    private void RaiseStackSelection()
    {
        foreach (var card in Stacks) card.IsSelected = card.Name == _selectedStack;
        OnPropertyChanged(nameof(SelectedStack));
        OnPropertyChanged(nameof(HasSelectedStack));
    }

    /// <summary>
    /// renameStack, from the card's name box (Enter, or focus leaving it): a blank or unchanged name
    /// snaps back; a name another stack has is refused with a toast; otherwise the stack and every
    /// slot that places it take the new name. The box is re-read after the binding's write.
    /// </summary>
    internal void RenameStack(StackCardViewModel card, string? requested)
    {
        var old = card.Name;
        var result = StackEditing.Rename(_profile, old, requested, out var final);
        switch (result)
        {
            case StackRename.Renamed:
                Log.Information("Builder: stack renamed {Old} → {New}", old, final);
                if (_selectedStack == old) _selectedStack = final;
                card.SetName(final);
                foreach (var row in Panels.SelectMany(p => p.Slots).Where(r => r.IsStack && r.Model.Name == final)) row.NameChanged();
                PreviewChanged();
                break;
            case StackRename.Duplicate:
                ShowToast(UiStrings.Builder_StackExists((requested ?? "").Trim()), BannerKind.Error);
                break;
        }
        // The text box wrote the value itself; it reads the committed name once its write is over.
        _post(card.RaiseName);
    }

    /// <summary>deleteStack: the destructive confirm (Cancel is the default), then the stack and every slot that places it go.</summary>
    internal void DeleteStack(StackCardViewModel card)
    {
        var name = card.Name;
        if (!_dialogs.Confirm(UiStrings.Builder_DeleteStackTitle(name), UiStrings.Builder_DeleteStackText, [], UiStrings.Builder_Delete, danger: true, DeleteStackDialogId)) return;
        var removed = StackEditing.Delete(_profile, name);
        Log.Information("Builder: stack deleted {Stack} (slots removed={Removed})", name, removed);
        if (_selectedStack == name) _selectedStack = null;
        RenderStacks();
        RenderCanvas();
    }

    /// <summary>addToolToStack (a drop on the card, or a double-click while it is selected): a fourth tool is refused with a toast.</summary>
    internal void AddToolToStack(StackCardViewModel card, CatalogCommand command)
    {
        if (!StackEditing.AddTool(_profile, card.Name, command))
        {
            Log.Information("Builder: stack {Stack} is full; {Command} refused", card.Name, command.Id);
            ShowToast(UiStrings.Builder_StackFull(card.Name), BannerKind.Error);
            return;
        }
        Log.Information("Builder: {Command} added to stack {Stack}", command.Id, card.Name);
        card.ToolsChanged();
        PreviewChanged();
    }

    internal void RemoveStackTool(StackCardViewModel card, int index)
    {
        StackEditing.RemoveTool(_profile, card.Name, index);
        card.ToolsChanged();
        PreviewChanged();
    }

    internal void MoveStackTool(StackCardViewModel card, int index, int delta)
    {
        StackEditing.MoveTool(_profile, card.Name, index, delta);
        card.ToolsChanged();
        PreviewChanged();
    }

    /// <summary>"+ Panel" (addStackRefToSelectedPanel): the stack at the end of the selected panel, adding a panel first when none is selected.</summary>
    internal void PlaceStack(StackCardViewModel card)
    {
        if (_selectedPanelIndex < 0 || _selectedPanelIndex >= _profile.Panels.Count) AddPanelCore();
        _profile.Panels[_selectedPanelIndex].Slots.Add(StackEditing.StackSlot(card.Name));
        Log.Information("Builder: stack {Stack} placed in panel {Panel}", card.Name, _selectedPanelIndex);
        RenderCanvas();
    }

    /// <summary>The pointer is over a stack card. True when the card accepts the drag (a catalogue command).</summary>
    public bool DragOverStack(StackCardViewModel card)
    {
        var accepted = _drag?.Kind == DragKind.Catalog;
        ClearDropMarkers();
        if (accepted) card.DropMarker = DropMarker.Over;
        return accepted;
    }

    /// <summary>A drop on a stack card: a catalogue command joins the stack (a fourth is refused with a toast).</summary>
    public void DropOnStack(StackCardViewModel card)
    {
        var drag = _drag;
        EndDrag();
        if (drag is { Kind: DragKind.Catalog, Command: { } command }) AddToolToStack(card, command);
    }

    /// <summary>The stack cards again from the profile.</summary>
    private void RenderStacks()
    {
        Stacks.Clear();
        foreach (var (name, stack) in _profile.Stacks)
            Stacks.Add(new StackCardViewModel(name, stack ?? new Stack(), this) { IsSelected = name == _selectedStack });
        if (_selectedStack is not null && !_profile.Stacks.ContainsKey(_selectedStack)) _selectedStack = null;
        OnPropertyChanged(nameof(HasStacks));
        OnPropertyChanged(nameof(SelectedStack));
        OnPropertyChanged(nameof(HasSelectedStack));
    }

    // ---- the Properties pane ------------------------------------------------------------------

    public string MinVersion
    {
        get => _profile.MinVersion ?? "";
        set
        {
            if (_profile.MinVersion == (value ?? "")) return;
            _profile.MinVersion = value ?? "";
            OnPropertyChanged();
        }
    }

    /// <summary>Panel Opacity, 10-100 in steps of 5; the preview follows it.</summary>
    public int PanelOpacity
    {
        get => _profile.PanelOpacity;
        set
        {
            var v = Math.Clamp(value, 10, 100);
            if (_profile.PanelOpacity == v) return;
            _profile.PanelOpacity = v;
            OnPropertyChanged();
            PreviewChanged();
        }
    }

    public ICommand PickColorCommand { get; }

    /// <summary>The swatch: the family color picker, starting from the panel's color.</summary>
    private void PickColor()
    {
        if (SelectedPanel is not { } panel) return;
        var current = Rgb.TryParse(panel.ColorHex, out var rgb) ? rgb : Rgb.Parse(BuilderRules.DefaultPanelColor);
        if (_dialogs.PickColor(current, PanelPalette, UiStrings.Builder_ColorDialogTitle) is not { } picked) return;
        panel.SetColor(picked.ToHex().ToLowerInvariant());
    }

    public bool IsUrlEditorOpen
    {
        get => _isUrlEditorOpen;
        private set
        {
            if (!SetField(ref _isUrlEditorOpen, value)) return;
            if (!value)
            {
                UrlLabel = "";
                UrlAddress = "";
            }
        }
    }

    public string UrlLabel
    {
        get => _urlLabel;
        set => SetField(ref _urlLabel, value ?? "");
    }

    public string UrlAddress
    {
        get => _urlAddress;
        set => SetField(ref _urlAddress, value ?? "");
    }

    public ICommand OpenUrlEditorCommand { get; }

    public ICommand CancelUrlEditorCommand { get; }

    public ICommand AddUrlCommand { get; }

    private void OpenUrlEditor()
    {
        if (IsUrlEditorOpen) return;
        IsUrlEditorOpen = true;
    }

    /// <summary>The editor's Add: a label and a URL are required; the URL is normalized and the slot gets "URL:&lt;url&gt;".</summary>
    private void AddUrlButton()
    {
        if (SelectedPanel is not { } panel) return;
        var name = UrlLabel.Trim();
        var url = BuilderRules.NormalizeUrl(UrlAddress.Trim());
        if (name.Length == 0)
        {
            ShowToast(UiStrings.Builder_LabelRequired, BannerKind.Error);
            return;
        }
        if (url.Length == 0)
        {
            ShowToast(UiStrings.Builder_UrlRequired, BannerKind.Error);
            return;
        }
        panel.Model.Slots.Add(BuilderRules.UrlSlot(name, url));
        Log.Information("Builder: url button added to panel {Panel}: {Name} → {Url}", panel.Index, name, url);
        IsUrlEditorOpen = false;
        RenderCanvas();
    }

    public string PresetsSummary => BuilderRules.PresetsSummary(_profile.Presets);

    public string CleanupSummary => BuilderRules.CleanupSummary(_profile.CleanupTargets);

    /// <summary>RST Presets → Configure.</summary>
    public ICommand ConfigurePresetsCommand { get; }

    /// <summary>Junk Cleanup → Configure.</summary>
    public ICommand ConfigureCleanupCommand { get; }

    /// <summary>The RST Presets modal: Apply replaces the profile's presets; Cancel changes nothing.</summary>
    private void ConfigurePresets()
    {
        var model = new PresetsViewModel(_backend.AllTabs, _profile.Presets, _profile.Tab);
        if (!_builderDialogs.EditPresets(model)) return;
        _profile.Presets = model.Result();
        Log.Information("Builder: presets updated (hiddenTabs={Hidden}, disableUnused={Disable})",
            _profile.Presets.HiddenTabs.Count, _profile.Presets.DisableUnusedAddins);
        OnPropertyChanged(nameof(PresetsSummary));
    }

    /// <summary>The Junk Cleanup modal over a copy: Apply keeps the non-blank rows; Cancel reverts (Planner default 4).</summary>
    private void ConfigureCleanup()
    {
        var model = new CleanupViewModel(_profile.CleanupTargets);
        if (!_builderDialogs.EditCleanup(model)) return;
        _profile.CleanupTargets = model.Result();
        Log.Information("Builder: cleanup targets saved (count={Count}, enabled={Enabled})",
            _profile.CleanupTargets.Count, _profile.CleanupTargets.Count(t => t.Enabled));
        OnPropertyChanged(nameof(CleanupSummary));
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
        foreach (var stack in Stacks) stack.DropMarker = DropMarker.None;
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
        foreach (var stack in Stacks) stack.DropMarker = DropMarker.None;
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

    internal void PreviewChanged() => PreviewRevision++;

    // ---- save, export, exit -------------------------------------------------------------------

    public ICommand SaveCommand { get; }

    public ICommand ExportCommand { get; }

    public ICommand ExitCommand { get; }

    /// <summary>
    /// saveAndMaybeReload: name and tab required; rename-as-copy in edit mode (a name collision
    /// aborts); the required add-ins recomputed; the save; the dropdown refreshed; a clean baseline;
    /// then, when the saved profile is the active one, the ribbon reloaded with the active hidden
    /// tabs and disable flag. True when the profile was saved.
    /// </summary>
    public async Task<bool> SaveAsync()
    {
        if (IsWorking) return false;
        // Spec #17: a stack needs 2 or 3 tools before the profile can be saved (checked before the
        // rename-as-copy plan, which clears the id of a copy).
        if (StackEditing.FirstUnderfilled(_profile) is { } underfilled)
        {
            Log.Warning("Builder: save blocked, stack {Stack} has fewer than 2 tools", underfilled);
            ShowToast(UiStrings.Builder_StackNeedsTools(underfilled), BannerKind.Error);
            return false;
        }
        var plan = ProfileEditing.PlanSave(_profile, _editMode, _originalName, _library.Select(e => e.Profile));
        switch (plan)
        {
            case SavePlanKind.MissingName:
                ShowToast(UiStrings.Builder_NameRequired, BannerKind.Error);
                return false;
            case SavePlanKind.MissingTab:
                ShowToast(UiStrings.Builder_TabRequired, BannerKind.Error);
                return false;
            case SavePlanKind.NameCollision:
                Log.Warning("Builder: rename-as-copy aborted (name collision) {New} ← {Original}", _profile.ProfileName, _originalName);
                ShowToast(UiStrings.Builder_NameCollision(_profile.ProfileName), BannerKind.Error);
                return false;
        }
        var savedAsCopy = plan == SavePlanKind.AsCopy;

        // Spec #17: a stack no panel places is removed on save (its card said so); the editor drops
        // it too, so it matches what is written.
        var orphans = ProfileEditing.RemoveOrphanStacks(_profile);
        if (orphans.Count > 0)
        {
            Log.Information("Builder: save removes stacks not on any panel: {Stacks}", string.Join(", ", orphans));
            if (_selectedStack is not null && orphans.Contains(_selectedStack)) _selectedStack = null;
            RenderStacks();
        }

        _profile.RequiredAddins = RequiredAddinDetector.Compute(_profile, _catalog, _lookup);
        Log.Information("Builder: save {Name} (edit={Edit}, copy={Copy}, requiredAddins={Required})",
            _profile.ProfileName, _editMode, savedAsCopy, _profile.RequiredAddins.Count);
        var result = Safe(() => _backend.Save(_profile), SaveOutcome.Fail("unknown"), "save_profile");
        if (!result.Ok)
        {
            ShowToast(UiStrings.Builder_SaveFailed(result.Error ?? "unknown"), BannerKind.Error);
            return false;
        }
        if (result.Saved is { } saved)
        {
            _profile.Id = saved.Id;
            _profile.ExportDate = saved.ExportDate;
        }
        _editMode = true;
        _originalName = _profile.ProfileName ?? "";
        OnPropertyChanged(nameof(IsEditMode));
        var savedIsActive = !string.IsNullOrEmpty(_profile.Id) && _profile.Id == _activeId;

        _library = Safe(_backend.GetProfiles, _library, "get_profiles");
        PopulateOptions(_profile.Id);
        SnapshotPristine();

        if (!savedIsActive)
        {
            ShowToast(savedAsCopy ? UiStrings.Builder_SavedAsCopy(_profile.ProfileName) : UiStrings.Builder_SavedAs(result.FileName), BannerKind.Success);
            return true;
        }

        Log.Information("Builder: saved profile is active — auto-reloading");
        IsWorking = true;
        try
        {
            var load = await _backend.LoadAsync(new LoadRequest(_profile.ProfileName ?? "", _profile.Id, _activeDisable, _activeHiddenTabs));
            if (load.Ok) ShowToast(UiStrings.Builder_SavedReloaded, BannerKind.Success);
            else ShowToast(UiStrings.Builder_SavedReloadFailed, BannerKind.Error);
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

    /// <summary>No modal host: both modals answer Cancel.</summary>
    private sealed class NoBuilderDialogs : IBuilderDialogs
    {
        public static readonly NoBuilderDialogs Instance = new();

        public bool EditPresets(PresetsViewModel model) => false;

        public bool EditCleanup(CleanupViewModel model) => false;
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
