// rst-c's own (not vendored): the New / Edit Stack dialog's model (feature #18, spec #19, WU3;
// decision #19). One dialog, two modes:
//   - + New Stack lists the panel's loose tools; the user ticks 2-3 in order (a pill shows each
//     one's place), and Create stack is enabled at 2; the stack takes the first pick's place;
//   - ✎ on a stack lists its rows (ticked, in stack order), then the panel's loose tools; ticking
//     adds a tool at the end, unticking puts it back on the panel, ▲ ▼ reorder the picks, and saving
//     with fewer than 2 removes the stack (PanelStacks.EditStack un-stacks).
// A fourth tick is refused: with 3 ticked, the other rows cannot be ticked. The picks come back as
// StackPick values for RST.Core's PanelStacks.NewStack and EditStack, which own the model.

using System.Windows.Input;
using System.Windows.Media.Imaging;
using RST.Core.Builder;
using RST.Core.Profiles;
using RST.Core.Ribbon;
using RST.UI.Controls;
using RST.UI.Mvvm;
using RST.UI.Resources;

namespace RST.UI.Builder;

/// <summary>The New / Edit Stack dialog: the tools to pick from, the picks in order, and the confirm.</summary>
public sealed class StackDialogModel : ObservableObject
{
    private readonly List<StackPickItemViewModel> _order = [];

    private StackDialogModel(string title, string help, string confirmText, bool isEdit, IEnumerable<(StackPick Pick, Slot Tool)> tools, string? packDirectory)
    {
        Title = title;
        Help = help;
        ConfirmText = confirmText;
        IsEdit = isEdit;
        Items = tools.Select(t => new StackPickItemViewModel(this, t.Pick, t.Tool, packDirectory)).ToList();
        // Edit: the stack's rows start ticked, in stack order.
        foreach (var item in Items.Where(i => i.Pick.Member)) _order.Add(item);
        Renumber();
    }

    /// <summary>+ New Stack on <paramref name="panelName"/>: its loose tools (slot index, tool), none ticked.</summary>
    public static StackDialogModel ForNew(string panelName, IEnumerable<(int Slot, Slot Tool)> loose, string? packDirectory = null) =>
        new(UiStrings.Builder_NewStackTitle(panelName), UiStrings.Builder_NewStackHelp, UiStrings.Builder_CreateStack, false,
            loose.Select(l => (StackPick.Loose(l.Slot), l.Tool)), packDirectory);

    /// <summary>✎ on a stack of <paramref name="panelName"/>: its rows (ticked, in order), then the panel's loose tools.</summary>
    public static StackDialogModel ForEdit(string panelName, IReadOnlyList<Slot> rows, IEnumerable<(int Slot, Slot Tool)> loose, string? packDirectory = null) =>
        new(UiStrings.Builder_EditStackTitle(panelName), UiStrings.Builder_EditStackHelp, UiStrings.Builder_SaveStack, true,
            rows.Select((t, r) => (StackPick.Row(r), t)).Concat(loose.Select(l => (StackPick.Loose(l.Slot), l.Tool))), packDirectory);

    public string Title { get; }

    public string Help { get; }

    public string ConfirmText { get; }

    /// <summary>✎ on a stack (else + New Stack).</summary>
    public bool IsEdit { get; }

    /// <summary>The tools listed, in the dialog's order.</summary>
    public IReadOnlyList<StackPickItemViewModel> Items { get; }

    /// <summary>The ticked tools, in pick order.</summary>
    public IReadOnlyList<StackPick> Picks => _order.Select(i => i.Pick).ToList();

    /// <summary>How many are ticked.</summary>
    public int Count => _order.Count;

    /// <summary>New: 2 or 3 ticked. Edit: always (fewer than 2 removes the stack).</summary>
    public bool CanConfirm => IsEdit || Count >= PanelStacks.MinTools;

    /// <summary>"2 selected · a stack holds 2–3 tools", or, editing below 2, that saving removes the stack.</summary>
    public string Status => IsEdit && Count < PanelStacks.MinTools
        ? UiStrings.Builder_StackWillDissolve(Count)
        : UiStrings.Builder_StackPicked(Count);

    /// <summary>A fourth tick is refused.</summary>
    public bool IsFull => Count >= PanelStacks.MaxTools;

    /// <summary>Ticks or unticks <paramref name="item"/> (a tick beyond 3 is refused). True when it changed.</summary>
    internal bool SetChecked(StackPickItemViewModel item, bool check)
    {
        var at = _order.IndexOf(item);
        if (check == at >= 0) return false;
        if (check)
        {
            if (IsFull) return false;
            _order.Add(item);
        }
        else
        {
            _order.RemoveAt(at);
        }
        Renumber();
        return true;
    }

    /// <summary>Moves a ticked tool one place earlier (-1) or later (+1) in the stack.</summary>
    internal void Move(StackPickItemViewModel item, int delta)
    {
        var at = _order.IndexOf(item);
        var to = at + delta;
        if (at < 0 || to < 0 || to >= _order.Count) return;
        (_order[at], _order[to]) = (_order[to], _order[at]);
        Renumber();
    }

    private void Renumber()
    {
        foreach (var item in Items) item.SetOrder(_order.IndexOf(item) + 1);
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(Picks));
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(IsFull));
        RelayCommand.Refresh();
    }
}

/// <summary>One tool in the stack dialog: the tick, its 16 px icon and name, its place in the stack ("#1"), ▲ ▼.</summary>
public sealed class StackPickItemViewModel : ObservableObject
{
    private readonly StackDialogModel _owner;
    private int _order;

    internal StackPickItemViewModel(StackDialogModel owner, StackPick pick, Slot tool, string? packDirectory)
    {
        _owner = owner;
        Pick = pick;
        Name = tool.Name ?? "";
        Icon = IconPack.TryParseValue(tool.IconFile, out var value) ? PackIcons.Load(value, packDirectory, 16) ?? PackIcons.Default16 : PackIcons.Default16;
        EarlierCommand = new RelayCommand(() => _owner.Move(this, -1), () => _order > 1);
        LaterCommand = new RelayCommand(() => _owner.Move(this, +1), () => _order > 0 && _order < _owner.Count);
    }

    public StackPick Pick { get; }

    public string Name { get; }

    public BitmapSource? Icon { get; }

    /// <summary>A row of the stack being edited (else a loose tool of the panel).</summary>
    public bool IsMember => Pick.Member;

    /// <summary>A row of the stack: "in this stack" while ticked, "goes back on the panel" once unticked; "" for a loose tool.</summary>
    public string MemberNote => !IsMember ? "" : IsChecked ? UiStrings.Builder_StackPickInStack : UiStrings.Builder_StackPickReturns;

    public bool IsChecked
    {
        get => _order > 0;
        set
        {
            if (!_owner.SetChecked(this, value)) OnPropertyChanged(); // a refused tick snaps back
        }
    }

    /// <summary>Unticked rows cannot be ticked while 3 are.</summary>
    public bool CanCheck => IsChecked || !_owner.IsFull;

    /// <summary>The place in the stack, from 1; 0 when not ticked.</summary>
    public int Order => _order;

    /// <summary>"#1".</summary>
    public string OrderText => _order > 0 ? "#" + _order : "";

    public ICommand EarlierCommand { get; }

    public ICommand LaterCommand { get; }

    internal void SetOrder(int order)
    {
        var was = _order;
        _order = order;
        if (was != order)
        {
            OnPropertyChanged(nameof(Order));
            OnPropertyChanged(nameof(OrderText));
            OnPropertyChanged(nameof(IsChecked));
            OnPropertyChanged(nameof(MemberNote));
        }
        OnPropertyChanged(nameof(CanCheck));
    }

    public override string ToString() => Name;
}
