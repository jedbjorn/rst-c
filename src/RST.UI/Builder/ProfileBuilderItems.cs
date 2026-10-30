// rst-c's own (not vendored): the Profile Builder's row view-models (spec #16, WU4): the profile
// dropdown's options and the catalogue tree's nodes (built once; a filter only updates counts and
// which children show). The panel cards, their tiles and stacks are in PanelCardItems.cs (feature
// #18, WU3).

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

/// <summary>
/// One profile in the header's profile menu (feature #18): its name, ticked when it is the one in
/// the editor, and a note with its tab ("loaded · tab "X"" for the one the Loader has loaded).
/// <see cref="New"/> stands for "+ New profile", which the menu offers as its own item.
/// </summary>
public sealed class ProfileOptionViewModel : ObservableObject
{
    private bool _isCurrent;

    private ProfileOptionViewModel(string key, string display, bool isLoaded, string note, ProfileEntry? entry)
    {
        Key = key;
        Display = display;
        IsLoaded = isLoaded;
        Note = note;
        Entry = entry;
    }

    /// <summary>The option's value: <c>__new__</c>, or the profile's id (its name when it has none).</summary>
    public string Key { get; }

    /// <summary>The profile's name ("+ New profile" for <see cref="New"/>).</summary>
    public string Display { get; }

    /// <summary>The Loader has this profile loaded (its id is the active one).</summary>
    public bool IsLoaded { get; }

    /// <summary>The menu row's right-hand note: "loaded · tab "X"" or "tab "X"".</summary>
    public string Note { get; }

    /// <summary>The profile (null for "+ New profile").</summary>
    public ProfileEntry? Entry { get; }

    public bool IsNew => Entry is null;

    /// <summary>The profile in the editor: ticked in the menu.</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        internal set => SetField(ref _isCurrent, value);
    }

    public const string NewKey = "__new__";

    public static ProfileOptionViewModel New() => new(NewKey, UiStrings.Builder_NewProfile, false, "", null);

    /// <summary>populateProfileSelector's option: the name, marked loaded when its id is the active one.</summary>
    public static ProfileOptionViewModel For(ProfileEntry entry, string? activeId)
    {
        var p = entry.Profile;
        var key = string.IsNullOrEmpty(p.Id) ? p.ProfileName : p.Id!;
        var loaded = !string.IsNullOrEmpty(p.Id) && p.Id == activeId;
        var note = loaded ? UiStrings.Builder_ProfileRowLoaded(p.Tab ?? "") : UiStrings.Builder_ProfileRowTab(p.Tab ?? "");
        return new ProfileOptionViewModel(key, p.ProfileName ?? "", loaded, note, entry);
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
