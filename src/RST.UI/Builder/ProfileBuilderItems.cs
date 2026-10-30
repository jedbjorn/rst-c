// rst-c's own (not vendored): the Profile Builder's row view-models (spec #16, WU4): the profile
// dropdown's options, the catalogue tree's nodes (built once; a filter only updates counts and
// which children show), the panel cards and their slot rows. Panel and slot edits write straight
// into the profile, as the page's inputs did, and tell the window's view-model so the live
// preview redraws.

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

    /// <summary>The type tag (<c>slot.type || 'tool'</c>).</summary>
    public string TypeTag => string.IsNullOrEmpty(Model.SlotType) ? "tool" : Model.SlotType;

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

    /// <summary>After the slot's icon changed (the icon picker, WU5).</summary>
    internal void IconChanged()
    {
        OnPropertyChanged(nameof(IconLabel));
        OnPropertyChanged(nameof(IconThumbnail));
        OnPropertyChanged(nameof(HasIconThumbnail));
    }

    public override string ToString() => Name;
}
