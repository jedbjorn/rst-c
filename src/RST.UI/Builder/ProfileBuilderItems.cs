// rst-c's own (not vendored): the Profile Builder's row view-models (spec #16, WU4): the profile
// dropdown's options, the catalogue tree's nodes (built once; a filter only updates counts and
// which children show), the panel cards and their slot rows. Panel and slot edits write straight
// into the profile, as the page's inputs did, and tell the window's view-model so the live
// preview redraws. Cards and rows carry their drag-and-drop markers (WU5).
// Stacks (feature #15, spec #17): the Stacks section's cards and their tool rows. A stack slot's
// name in a panel card is read-only: it is the link to the stack, renamed only on the stack card.

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Input;
using System.Windows.Media;
using RST.Core.Builder;
using RST.Core.Loader;
using RST.Core.Profiles;
using RST.UI.Controls;
using RST.UI.Mvvm;
using RST.UI.Resources;
using ProfilePanel = RST.Core.Profiles.Panel;

namespace RST.UI.Builder;

/// <summary>One entry of the header's profile dropdown: "+ New Profile", or a profile ("(loaded)" when active).</summary>
public sealed class ProfileOptionViewModel
{
    private ProfileOptionViewModel(string key, string display, ProfileEntry? entry)
    {
        Key = key;
        Display = display;
        Entry = entry;
    }

    /// <summary>The option's value: <c>__new__</c>, or the profile's id (its name when it has none).</summary>
    public string Key { get; }

    public string Display { get; }

    /// <summary>The profile (null for "+ New Profile").</summary>
    public ProfileEntry? Entry { get; }

    public bool IsNew => Entry is null;

    public const string NewKey = "__new__";

    public static ProfileOptionViewModel New() => new(NewKey, UiStrings.Builder_NewProfile, null);

    /// <summary>populateProfileSelector's option: the name, suffixed "(loaded)" when its id is the active one.</summary>
    public static ProfileOptionViewModel For(ProfileEntry entry, string? activeId)
    {
        var p = entry.Profile;
        var key = string.IsNullOrEmpty(p.Id) ? p.ProfileName : p.Id!;
        var loaded = !string.IsNullOrEmpty(p.Id) && p.Id == activeId;
        return new ProfileOptionViewModel(key, loaded ? UiStrings.Builder_LoadedOption(p.ProfileName) : p.ProfileName, entry);
    }

    public override string ToString() => Display;
}

/// <summary>
/// An ObservableCollection that can swap its whole content with one Reset notification (a filter
/// that changes many rows), or follow small changes item by item (so realized rows stay).
/// </summary>
public sealed class FilteredCollection<T> : ObservableCollection<T>
{
    /// <summary>Makes the collection the given ordered subsequence of <paramref name="all"/>.</summary>
    public void Sync(IReadOnlyList<T> all, Func<T, bool> show)
    {
        var target = all.Where(show).ToList();
        if (target.Count == Count && target.SequenceEqual(this)) return;

        // Many changes: one Reset is cheaper than hundreds of single notifications.
        if (Math.Abs(target.Count - Count) > 16)
        {
            Items.Clear();
            foreach (var item in target) Items.Add(item);
            OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            return;
        }

        // Few changes: the visible list is an ordered subsequence of `all`, so one pass inserts and removes.
        var vi = 0;
        foreach (var item in all)
        {
            var visible = show(item);
            var present = vi < Count && EqualityComparer<T>.Default.Equals(this[vi], item);
            if (visible && !present) Insert(vi++, item);
            else if (visible) vi++;
            else if (present) RemoveAt(vi);
        }
    }
}

/// <summary>
/// One catalogue tree node: the "Autodesk Native" group, a tab, a panel or a command. Built once
/// for the whole catalogue; <see cref="Apply"/> updates its match count and its visible children.
/// </summary>
public sealed class CatalogNodeViewModel : ObservableObject
{
    private readonly CatalogExpansion _expansion;
    private readonly IReadOnlyList<CatalogNodeViewModel> _all;
    private int _count;

    internal CatalogNodeViewModel(CatalogNode node, CatalogExpansion expansion)
    {
        Node = node;
        _expansion = expansion;
        _all = node.Children.Select(c => new CatalogNodeViewModel(c, expansion)).ToList();
        _count = node.Kind == CatalogNodeKind.Command ? 1 : 0;
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    public CatalogNode Node { get; }

    public CatalogNodeKind Kind => Node.Kind;

    public bool IsCommand => Node.Kind == CatalogNodeKind.Command;

    public bool IsGroup => !IsCommand;

    /// <summary>The group, tab or panel name, or the command's name.</summary>
    public string Label => Node.Label;

    /// <summary>The command's shortened id (the row's right-hand text).</summary>
    public string ShortId => Node.Command is { } c ? BuilderRules.ShortId(c.Id) : "";

    /// <summary>The command row's tooltip: the full id.</summary>
    public string? ToolTip => Node.Command?.Id;

    public CatalogCommand? Command => Node.Command;

    /// <summary>How many matching commands the node holds (the count pill).</summary>
    public int Count
    {
        get => _count;
        private set => SetField(ref _count, value);
    }

    /// <summary>The children that hold a match, in tree order.</summary>
    public FilteredCollection<CatalogNodeViewModel> Children { get; } = [];

    /// <summary>Every child, matched or not.</summary>
    public IReadOnlyList<CatalogNodeViewModel> AllChildren => _all;

    /// <summary>
    /// Expanded unless the user collapsed it (kept per tab/panel key). The "Autodesk Native" group is
    /// collapsed by default and always open while a filter is active.
    /// </summary>
    public bool IsExpanded
    {
        get => _expansion.IsExpanded(Node);
        set
        {
            _expansion.Toggle(Node, value);
            OnPropertyChanged();
        }
    }

    /// <summary>A click on a group's header toggles it, as the page's header did.</summary>
    public ICommand ToggleCommand { get; }

    internal void Apply(CatalogFilter filter)
    {
        Count = filter.Count(Node);
        if (IsCommand) return;
        foreach (var child in _all) child.Apply(filter);
        Children.Sync(_all, c => filter.IsVisible(c.Node));
        if (Node.Kind == CatalogNodeKind.NativeGroup) OnPropertyChanged(nameof(IsExpanded));
    }

    /// <summary>This node and every node under it (tests).</summary>
    public IEnumerable<CatalogNodeViewModel> Flatten() => new[] { this }.Concat(_all.SelectMany(c => c.Flatten()));

    public override string ToString() => Kind + " " + Label + " (" + Count + ")";
}

/// <summary>The tree's expansion state: the collapsed tab/panel keys, and whether the native group is open.</summary>
internal sealed class CatalogExpansion
{
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private bool _nativeOpen;

    public bool FilterActive { get; set; }

    public bool IsExpanded(CatalogNode node) => node.Kind switch
    {
        CatalogNodeKind.NativeGroup => FilterActive || _nativeOpen,
        CatalogNodeKind.Command => false,
        _ => !_collapsed.Contains(node.Key),
    };

    public void Toggle(CatalogNode node, bool expanded)
    {
        switch (node.Kind)
        {
            case CatalogNodeKind.NativeGroup:
                // The header click flips the user's choice; while filtering the group stays open either way.
                _nativeOpen = FilterActive ? !_nativeOpen : expanded;
                break;
            case CatalogNodeKind.Command:
                break;
            default:
                if (expanded) _collapsed.Remove(node.Key);
                else _collapsed.Add(node.Key);
                break;
        }
    }
}

/// <summary>One card in the Panels list: the color stripe, the inline name, the slot count, ▲ ▼ × and its slot rows.</summary>
public sealed class PanelCardViewModel : ObservableObject
{
    private readonly ProfileBuilderViewModel _owner;
    private bool _isSelected;

    internal PanelCardViewModel(ProfilePanel model, int index, ProfileBuilderViewModel owner)
    {
        Model = model;
        Index = index;
        _owner = owner;
        Slots = new ObservableCollection<SlotRowViewModel>(model.Slots.Select((s, i) => new SlotRowViewModel(s, i, this, owner)));
        SelectCommand = new RelayCommand(() => _owner.SelectPanel(Index));
        MoveUpCommand = new RelayCommand(() => _owner.MovePanel(Index, -1), () => _owner.IsIdle);
        MoveDownCommand = new RelayCommand(() => _owner.MovePanel(Index, +1), () => _owner.IsIdle);
        DeleteCommand = new RelayCommand(() => _owner.DeletePanel(Index), () => _owner.IsIdle);
    }

    public ProfilePanel Model { get; }

    /// <summary>The panel's position in the profile.</summary>
    public int Index { get; }

    /// <summary>The name, shared by the card's input and the Properties pane's (kept in sync).</summary>
    public string Name
    {
        get => Model.Name ?? "";
        set
        {
            if ((Model.Name ?? "") == (value ?? "")) return;
            Model.Name = value ?? "";
            OnPropertyChanged();
            _owner.PreviewChanged();
        }
    }

    /// <summary>The color the hex box shows: the panel's, normalized, or the default blue (as the page's picker).</summary>
    public string ColorHex
    {
        get => BuilderRules.NormalizeHex(Model.Color) ?? BuilderRules.DefaultPanelColor;
        set
        {
            // A typed hex applies when it is a valid color; anything else snaps back.
            if (BuilderRules.NormalizeHex(value) is { } hex && hex != Model.Color) SetColor(hex);
            else OnPropertyChanged();
        }
    }

    /// <summary>The stripe and swatch brush.</summary>
    public Brush ColorBrush => BrushFor(Model.Color);

    internal void SetColor(string hex)
    {
        Model.Color = hex;
        OnPropertyChanged(nameof(ColorHex));
        OnPropertyChanged(nameof(ColorBrush));
        _owner.PreviewChanged();
    }

    public string SlotCount => BuilderRules.SlotCount(Model.Slots.Count);

    public ObservableCollection<SlotRowViewModel> Slots { get; }

    public bool HasSlots => Slots.Count > 0;

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetField(ref _isSelected, value);
    }

    /// <summary>The drag feedback on the card: a line above or below it (panels), or the whole card (commands, slots).</summary>
    public DropMarker DropMarker
    {
        get => _dropMarker;
        internal set => SetField(ref _dropMarker, value);
    }

    private DropMarker _dropMarker;

    public ICommand SelectCommand { get; }

    public ICommand MoveUpCommand { get; }

    public ICommand MoveDownCommand { get; }

    public ICommand DeleteCommand { get; }

    /// <summary>The panel's CSS color as a brush; a value WPF cannot read shows the default blue.</summary>
    internal static Brush BrushFor(string? color)
    {
        var hex = BuilderRules.NormalizeHex(color) ?? BuilderRules.NormalizeHex(BuilderRules.DefaultPanelColor)!;
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public override string ToString() => Name;
}

/// <summary>One slot row: the Pick-icon button (tools) or the type tag, the inline name, the short command id, ▲ ▼ ×.</summary>
public sealed class SlotRowViewModel : ObservableObject
{
    private readonly ProfileBuilderViewModel _owner;

    internal SlotRowViewModel(Slot model, int index, PanelCardViewModel panel, ProfileBuilderViewModel owner)
    {
        Model = model;
        Index = index;
        Panel = panel;
        _owner = owner;
        MoveUpCommand = new RelayCommand(() => _owner.MoveSlot(Panel.Index, Index, -1), () => _owner.IsIdle);
        MoveDownCommand = new RelayCommand(() => _owner.MoveSlot(Panel.Index, Index, +1), () => _owner.IsIdle);
        RemoveCommand = new RelayCommand(() => _owner.DeleteSlot(Panel.Index, Index), () => _owner.IsIdle);
        PickIconCommand = new RelayCommand(() => _owner.PickIcon(this), () => _owner.IsIdle);
    }

    public Slot Model { get; }

    public int Index { get; }

    public PanelCardViewModel Panel { get; }

    /// <summary>Tool slots get the Pick-icon button; other types show their type tag.</summary>
    public bool IsTool => Model.SlotType == "tool";

    /// <summary>A stack slot: its name is the link to the stack, so the row shows it read-only (spec #17).</summary>
    public bool IsStack => Model.SlotType == "stack";

    /// <summary>The type tag (<c>slot.type || 'tool'</c>).</summary>
    public string TypeTag => string.IsNullOrEmpty(Model.SlotType) ? "tool" : Model.SlotType;

    /// <summary>The name box's tooltip: a stack is renamed on its card in the Stacks section.</summary>
    public string? NameToolTip => IsStack ? UiStrings.Builder_StackSlotNameTip : null;

    /// <summary>The slot's name (the ribbon label). A stack slot's name cannot be edited here.</summary>
    public string Name
    {
        get => Model.Name ?? "";
        set
        {
            if (IsStack)
            {
                // Renaming here would break the link to profile.stacks (the v3.0.0 save failure).
                OnPropertyChanged();
                return;
            }
            if ((Model.Name ?? "") == (value ?? "")) return;
            Model.Name = value ?? "";
            OnPropertyChanged();
            _owner.PreviewChanged();
        }
    }

    /// <summary>After the stack it refers to was renamed.</summary>
    internal void NameChanged() => OnPropertyChanged(nameof(Name));

    /// <summary>A tool slot with a command id shows it shortened, with the full id as the tooltip.</summary>
    public bool ShowsCommandId => IsTool && !string.IsNullOrEmpty(Model.CommandId);

    public string ShortCommandId => BuilderRules.ShortId(Model.CommandId);

    public string? CommandId => Model.CommandId;

    /// <summary>The Pick-icon button's label: the pack design's name, or "Icon".</summary>
    public string IconLabel => BuilderRules.IconButtonLabel(Model);

    /// <summary>The 14 px thumbnail of the slot's pack icon (null: the button shows "+").</summary>
    public ImageSource? IconThumbnail => PackIcons.ForIconFile(Model.IconFile, _owner.PackDirectory);

    public bool HasIconThumbnail => IconThumbnail is not null;

    public ICommand MoveUpCommand { get; }

    public ICommand MoveDownCommand { get; }

    public ICommand RemoveCommand { get; }

    public ICommand PickIconCommand { get; }

    /// <summary>The drag feedback on the row: a line above or below it.</summary>
    public DropMarker DropMarker
    {
        get => _dropMarker;
        internal set => SetField(ref _dropMarker, value);
    }

    private DropMarker _dropMarker;

    /// <summary>The row being dragged (drawn faded, <c>.drag-source</c>).</summary>
    public bool IsDragSource
    {
        get => _isDragSource;
        internal set => SetField(ref _isDragSource, value);
    }

    private bool _isDragSource;

    /// <summary>After the slot's icon changed (the icon picker).</summary>
    internal void IconChanged()
    {
        OnPropertyChanged(nameof(IconLabel));
        OnPropertyChanged(nameof(IconThumbnail));
        OnPropertyChanged(nameof(HasIconThumbnail));
    }

    public override string ToString() => Name;
}

/// <summary>
/// One card in the Stacks section (spec #17): the name (rename on Enter or when the box loses focus),
/// "+ Panel", ×, the 2-3 tools, and its flags: fewer than 2 tools (blocks Save), not on any panel
/// (removed on save). A catalogue command dropped on the card, or double-clicked while it is
/// selected, joins the stack; the card's header drags onto a panel to place the stack.
/// </summary>
public sealed class StackCardViewModel : ObservableObject
{
    private readonly ProfileBuilderViewModel _owner;
    private string _name;
    private bool _isSelected;
    private DropMarker _dropMarker;

    internal StackCardViewModel(string name, Stack model, ProfileBuilderViewModel owner)
    {
        _name = name;
        Model = model;
        _owner = owner;
        Tools = new ObservableCollection<StackToolRowViewModel>(BuildTools());
        SelectCommand = new RelayCommand(() => _owner.SelectStack(this));
        PlaceCommand = new RelayCommand(() => _owner.PlaceStack(this), () => _owner.IsIdle);
        DeleteCommand = new RelayCommand(() => _owner.DeleteStack(this), () => _owner.IsIdle);
    }

    public Stack Model { get; }

    /// <summary>The stack's name. Setting it renames the stack (a duplicate is refused) and the slots that place it.</summary>
    public string Name
    {
        get => _name;
        set => _owner.RenameStack(this, value);
    }

    /// <summary>The name as committed (after a rename, or to snap the box back after a refused one).</summary>
    internal void SetName(string name)
    {
        _name = name;
        OnPropertyChanged(nameof(Name));
    }

    internal void RaiseName() => OnPropertyChanged(nameof(Name));

    public ObservableCollection<StackToolRowViewModel> Tools { get; }

    public bool HasTools => Tools.Count > 0;

    /// <summary>"2 of 3".</summary>
    public string ToolCount => UiStrings.Builder_StackCount(Model.Tools.Count);

    /// <summary>Fewer than 2 tools: flagged, and Save is blocked until it has 2 or 3.</summary>
    public bool IsUnderfilled => StackEditing.IsUnderfilled(Model);

    /// <summary>3 tools: a fourth drop is refused.</summary>
    public bool IsFull => StackEditing.IsFull(Model);

    /// <summary>No panel slot places the stack: it is removed when the profile is saved.</summary>
    public bool IsUnplaced => !_owner.IsStackPlaced(_name);

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetField(ref _isSelected, value);
    }

    /// <summary>A catalogue command over the card (<see cref="Core.Builder.DropMarker.Over"/>).</summary>
    public DropMarker DropMarker
    {
        get => _dropMarker;
        internal set => SetField(ref _dropMarker, value);
    }

    public ICommand SelectCommand { get; }

    /// <summary>"+ Panel": places the stack at the end of the selected panel.</summary>
    public ICommand PlaceCommand { get; }

    public ICommand DeleteCommand { get; }

    /// <summary>After the tools changed: the rows again, the count and the flags.</summary>
    internal void ToolsChanged()
    {
        Tools.Clear();
        foreach (var row in BuildTools()) Tools.Add(row);
        OnPropertyChanged(nameof(HasTools));
        OnPropertyChanged(nameof(ToolCount));
        OnPropertyChanged(nameof(IsUnderfilled));
        OnPropertyChanged(nameof(IsFull));
    }

    /// <summary>After the panels changed (a stack slot placed, moved or removed).</summary>
    internal void PlacementChanged() => OnPropertyChanged(nameof(IsUnplaced));

    private IEnumerable<StackToolRowViewModel> BuildTools() =>
        Model.Tools.Select((t, i) => new StackToolRowViewModel(t, i, this, _owner)).ToList();

    public override string ToString() => Name;
}

/// <summary>One tool in a stack card: the Pick-icon button, the name, the short command id, ▲ ▼ ×.</summary>
public sealed class StackToolRowViewModel : ObservableObject
{
    private readonly ProfileBuilderViewModel _owner;

    internal StackToolRowViewModel(Slot model, int index, StackCardViewModel stack, ProfileBuilderViewModel owner)
    {
        Model = model;
        Index = index;
        Stack = stack;
        _owner = owner;
        MoveUpCommand = new RelayCommand(() => _owner.MoveStackTool(Stack, Index, -1), () => _owner.IsIdle);
        MoveDownCommand = new RelayCommand(() => _owner.MoveStackTool(Stack, Index, +1), () => _owner.IsIdle);
        RemoveCommand = new RelayCommand(() => _owner.RemoveStackTool(Stack, Index), () => _owner.IsIdle);
        PickIconCommand = new RelayCommand(() => _owner.PickIcon(this), () => _owner.IsIdle);
    }

    public Slot Model { get; }

    public int Index { get; }

    public StackCardViewModel Stack { get; }

    public string Name => Model.Name ?? "";

    public string ShortCommandId => BuilderRules.ShortId(Model.CommandId);

    public string? CommandId => Model.CommandId;

    /// <summary>The Pick-icon button's tooltip: the icon's design name, or "Pick icon".</summary>
    public string IconLabel => BuilderRules.IconButtonLabel(Model);

    /// <summary>The 14 px thumbnail of the tool's pack icon (null: the button shows "+").</summary>
    public ImageSource? IconThumbnail => PackIcons.ForIconFile(Model.IconFile, _owner.PackDirectory);

    public bool HasIconThumbnail => IconThumbnail is not null;

    public ICommand MoveUpCommand { get; }

    public ICommand MoveDownCommand { get; }

    public ICommand RemoveCommand { get; }

    public ICommand PickIconCommand { get; }

    internal void IconChanged()
    {
        OnPropertyChanged(nameof(IconLabel));
        OnPropertyChanged(nameof(IconThumbnail));
        OnPropertyChanged(nameof(HasIconThumbnail));
    }

    public override string ToString() => Name;
}
