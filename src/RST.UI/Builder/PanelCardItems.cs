// rst-c's own (not vendored): the Profile Builder's panel cards (feature #18, spec #19, WU3; the
// approved layout of decision #25). A card shows its panel's settings inline (the colour swatch,
// the name edited in place, the tool count, + New Stack, ▲ ▼ ×) and its slots as tiles in ribbon
// order: a loose tool is a tile (32 px icon, the name box, a muted source line, × and, for a
// custom tool, WU4's ✎), a stack is a group ("STACK · n/3" with ✎ and ×, then a row per tool with
// a 16 px icon, the name box and ×). Name and icon edits write straight into the profile, as the
// page's inputs did, and tell the window's view-model so the live preview redraws; every
// structural change goes through RST.Core's PanelStacks and the view-model rebuilds the cards.
// Tiles, stacks and cards carry their drag-and-drop markers. The Builder never shows a stack's
// name (decision #19): the "Stack N" name is only the link between the slot and profile.stacks.

using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RST.Core.Builder;
using RST.Core.Profiles;
using RST.Core.Ribbon;
using RST.UI.Controls;
using RST.UI.Mvvm;
using RST.UI.Resources;
using RST.UI.Theming;
using ProfilePanel = RST.Core.Profiles.Panel;

namespace RST.UI.Builder;

/// <summary>
/// One card in the Panels pane: the colour stripe and swatch, the inline name, the tool count,
/// + New Stack, ▲ ▼ × and its tiles and stacks.
/// </summary>
public sealed class PanelCardViewModel : ObservableObject
{
    private readonly ProfileBuilderViewModel _owner;
    private bool _isSelected;
    private DropMarker _dropMarker;

    internal PanelCardViewModel(ProfilePanel model, int index, Profile profile, ProfileBuilderViewModel owner)
    {
        Model = model;
        Index = index;
        _owner = owner;
        var items = new List<PanelItemViewModel>();
        for (var i = 0; i < model.Slots.Count; i++)
        {
            var slot = model.Slots[i];
            items.Add(PanelStacks.IsStack(slot)
                ? new StackGroupViewModel(slot, i, PanelStacks.ToolsOf(profile, slot), this, owner)
                : new ToolTileViewModel(slot, i, -1, null, this, owner));
        }
        Items = items;
        ToolCount = BuilderRules.ToolCountText(BuilderRules.ToolCount(profile, model));
        LooseToolCount = PanelStacks.LooseTools(profile, index).Count;
        SelectCommand = new RelayCommand(() => _owner.SelectPanel(Index));
        MoveUpCommand = new RelayCommand(() => _owner.MovePanel(Index, -1), () => _owner.IsIdle && Index > 0);
        MoveDownCommand = new RelayCommand(() => _owner.MovePanel(Index, +1), () => _owner.IsIdle && Index < _owner.Panels.Count - 1);
        DeleteCommand = new RelayCommand(() => _owner.DeletePanel(Index), () => _owner.IsIdle);
        PickColorCommand = new RelayCommand(() => _owner.PickPanelColor(Index), () => _owner.IsIdle);
        NewStackCommand = new RelayCommand(() => _owner.NewStack(Index), () => _owner.IsIdle && LooseToolCount >= PanelStacks.MinTools);
    }

    public ProfilePanel Model { get; }

    /// <summary>The panel's position in the profile.</summary>
    public int Index { get; }

    /// <summary>The panel name, edited in place on the card (its only editor).</summary>
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

    /// <summary>The panel colour, normalized, or the default blue (the colour picker starts from it).</summary>
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

    /// <summary>The panel's slots in ribbon order: <see cref="ToolTileViewModel"/> and <see cref="StackGroupViewModel"/>.</summary>
    public IReadOnlyList<PanelItemViewModel> Items { get; }

    public bool HasItems => Items.Count > 0;

    /// <summary>Every tool on the card in ribbon order, loose or in a stack.</summary>
    public IEnumerable<ToolTileViewModel> Tiles => Items.SelectMany(i => i is StackGroupViewModel s ? s.Rows : new[] { (ToolTileViewModel)i });

    /// <summary>The stacks on the card.</summary>
    public IEnumerable<StackGroupViewModel> Stacks => Items.OfType<StackGroupViewModel>();

    /// <summary>"3 tools": every tool on the panel, loose or in a stack.</summary>
    public string ToolCount { get; }

    /// <summary>How many loose tools the panel has (+ New Stack needs 2).</summary>
    public int LooseToolCount { get; }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetField(ref _isSelected, value);
    }

    /// <summary>The drag feedback on the card: a line above or below it (panels), or the whole card (a tool or a stack to its end).</summary>
    public DropMarker DropMarker
    {
        get => _dropMarker;
        internal set => SetField(ref _dropMarker, value);
    }

    public ICommand SelectCommand { get; }

    public ICommand MoveUpCommand { get; }

    public ICommand MoveDownCommand { get; }

    public ICommand DeleteCommand { get; }

    /// <summary>The colour swatch: selects the card and opens the family colour picker on it.</summary>
    public ICommand PickColorCommand { get; }

    /// <summary>+ New Stack: the stack dialog over the panel's loose tools.</summary>
    public ICommand NewStackCommand { get; }

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

/// <summary>A slot on a card: a loose tool's tile or a stack group. Index is the slot's position on its panel.</summary>
public abstract class PanelItemViewModel : ObservableObject
{
    private DropMarker _dropMarker;
    private bool _isDragSource;

    private protected PanelItemViewModel(Slot model, int index, PanelCardViewModel panel)
    {
        Model = model;
        Index = index;
        Panel = panel;
    }

    /// <summary>The slot (a tool slot for a tile or a stack row; the stack slot for a group).</summary>
    public Slot Model { get; }

    /// <summary>The slot's position on its panel (a stack row: its stack's position).</summary>
    public int Index { get; }

    public PanelCardViewModel Panel { get; }

    /// <summary>
    /// The drag feedback: before or after (a line left or right of a tile or a stack, above or below
    /// a stack row), over (a stack forms, or the tool joins), or refused (a full stack).
    /// </summary>
    public DropMarker DropMarker
    {
        get => _dropMarker;
        internal set => SetField(ref _dropMarker, value);
    }

    /// <summary>The tile or stack being dragged (drawn faded, <c>.drag-source</c>).</summary>
    public bool IsDragSource
    {
        get => _isDragSource;
        internal set => SetField(ref _isDragSource, value);
    }
}

/// <summary>
/// A tool: a loose tile on its card, or a row of a stack. The 32 px (tile) or 16 px (row) icon opens
/// the icon picker; the name box is this panel's label for the tool; × removes it from this panel.
/// </summary>
public sealed class ToolTileViewModel : PanelItemViewModel
{
    private readonly ProfileBuilderViewModel _owner;

    internal ToolTileViewModel(Slot model, int index, int row, StackGroupViewModel? stack, PanelCardViewModel panel, ProfileBuilderViewModel owner)
        : base(model, index, panel)
    {
        Row = row;
        Stack = stack;
        _owner = owner;
        PickIconCommand = new RelayCommand(() => _owner.PickIcon(this), () => _owner.IsIdle);
        RemoveCommand = new RelayCommand(() => _owner.RemoveTile(this), () => _owner.IsIdle);
    }

    /// <summary>The tool's row in its stack, or -1 for a loose tile.</summary>
    public int Row { get; }

    /// <summary>The stack this tool is a row of, or null.</summary>
    public StackGroupViewModel? Stack { get; }

    public bool InStack => Row >= 0;

    /// <summary>The tool's label on this panel (the ribbon label).</summary>
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

    /// <summary>A URL or email tool (a Custom Tool).</summary>
    public bool IsCustom => CustomTools.IsCustom(Model);

    /// <summary>The muted line under the name: "tab · panel" for a command, the link for a custom tool.</summary>
    public string SourceLine => BuilderRules.SourceLine(Model);

    /// <summary>The full command id or link.</summary>
    public string ToolTip => BuilderRules.ToolTipOf(Model);

    /// <summary>The tool's pack icon: 32 px on a tile, 16 px on a stack row (the ribbon's default_16.png when it has none).</summary>
    public BitmapSource? Icon => PackIcons.ForSlot(Model, _owner.PackDirectory, InStack ? 16 : 32, RstTheme.IsDark)
        ?? (InStack ? PackIcons.Default16 : null);

    /// <summary>False: the tile shows the empty icon frame.</summary>
    public bool HasIcon => Icon is not null;

    public ICommand PickIconCommand { get; }

    /// <summary>× : removes the tool from this panel (no confirm). A stack left with one tool becomes that tool.</summary>
    public ICommand RemoveCommand { get; }

    /// <summary>
    /// ✎ on a placed custom tool: the Custom Tool dialog, edit everywhere (WU4,
    /// <see cref="ProfileBuilderViewModel.EditCustomToolCommand"/>). The button shows only on a custom
    /// tool. Its parameter is this tile.
    /// </summary>
    public ICommand? EditCustomCommand => _owner.EditCustomToolCommand;

    public bool ShowsEditCustom => IsCustom && EditCustomCommand is not null;

    /// <summary>After the tool's icon changed (the icon picker).</summary>
    internal void IconChanged()
    {
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(HasIcon));
    }

    public override string ToString() => Name;
}

/// <summary>A stack on a card: "STACK · n/3" with ✎ (the stack dialog) and × (un-stack), then its rows.</summary>
public sealed class StackGroupViewModel : PanelItemViewModel
{
    internal StackGroupViewModel(Slot model, int index, IReadOnlyList<Slot> tools, PanelCardViewModel panel, ProfileBuilderViewModel owner)
        : base(model, index, panel)
    {
        Rows = tools.Select((t, r) => new ToolTileViewModel(t, index, r, this, panel, owner)).ToList();
        EditCommand = new RelayCommand(() => owner.EditStack(this), () => owner.IsIdle);
        UnstackCommand = new RelayCommand(() => owner.Unstack(this), () => owner.IsIdle);
    }

    public IReadOnlyList<ToolTileViewModel> Rows { get; }

    /// <summary>"STACK · 3/3".</summary>
    public string Header => UiStrings.Builder_StackHeader(Rows.Count);

    /// <summary>The stack holds 3 tools: a fourth is refused.</summary>
    public bool IsFull => Rows.Count >= PanelStacks.MaxTools;

    /// <summary>✎: the stack dialog in edit mode.</summary>
    public ICommand EditCommand { get; }

    /// <summary>×: un-stacks; its tools go back on the panel in order.</summary>
    public ICommand UnstackCommand { get; }

    public override string ToString() => Header;
}
