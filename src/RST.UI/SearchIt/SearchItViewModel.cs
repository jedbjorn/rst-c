// rst-c's own (not vendored): the Search-It window's view-model (v3.4). The rules are RST.Core.Search's:
// ToolSearch (chips filter exactly and are ANDed; the text ranks fuzzily inside them) and SearchChips.
// Enter makes a chip and never runs a tool; a click on a result closes the window, then runs the tool.
//
// Spec #35: a card carries the census's label, right-aligned: NOT IN GUI (amber; the tool runs) or Must Run In
// <views> (the theme's neutral chip; the card is dimmed and never runs: a click, a double-click or Enter on it leaves
// the window open and runs nothing). A click on a runnable card that the host says can't run now leaves the window
// open with the amber message naming the tool's ribbon place: no click is silent.

using System.Collections.ObjectModel;
using System.Windows.Input;
using RST.Core.Logging;
using RST.Core.Search;
using RST.UI.Controls;
using RST.UI.Mvvm;
using RST.UI.Resources;

namespace RST.UI.SearchIt;

/// <summary>One result card: the name that matched and, under it, where that is on the ribbon.</summary>
public sealed class SearchResultItem(SearchHit hit, ICommand runCommand)
{
    public SearchTool Tool { get; } = hit.Tool;
    public string Name { get; } = hit.Place.Label;
    public string Location { get; } = hit.Place.Location;
    public bool HasMore => Tool.OtherPlaces > 0;
    public string MoreText => Tool.OtherPlaces switch
    {
        <= 0 => "",
        1 => UiStrings.SearchIt_OneMorePlace,
        var n => UiStrings.SearchIt_MorePlaces(n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)),
    };
    public string ToolTip => IsMustRunIn ? BadgeToolTip
        : HasMore ? UiStrings.SearchIt_ToolTipMore(Location, MoreText) : Location;
    public ICommand RunCommand { get; } = runCommand;

    /// <summary>The tool runs only in other views of this file: dimmed, badged, never run from here (spec #35).</summary>
    public bool IsMustRunIn => Tool.Mark == SearchToolMark.MustRunIn;

    /// <summary>The tool runs here, but only from places Revit does not draw: the amber NOT IN GUI badge.</summary>
    public bool IsNotInGui => Tool.Mark == SearchToolMark.NotInGui;

    /// <summary>The card carries a badge.</summary>
    public bool HasBadge => IsMustRunIn || IsNotInGui;

    /// <summary>"NOT IN GUI", or "Must Run In Elevation, Section"; empty for no badge.</summary>
    public string BadgeText => IsMustRunIn ? SearchViews.MustRunInBadge(Tool.MustRunIn) : IsNotInGui ? SearchViews.NotInGui : "";

    /// <summary>A Must Run In badge's tooltip: every view it runs in; empty otherwise.</summary>
    public string BadgeToolTip => IsMustRunIn ? SearchViews.MustRunInToolTip(Tool.MustRunIn) : "";
}

/// <summary>One chip: an exact filter on the tool name, with its × button.</summary>
public sealed class SearchChipItem(string text, ICommand removeCommand)
{
    public string Text { get; } = text;
    public ICommand RemoveCommand { get; } = removeCommand;
}

public sealed class SearchItViewModel : ObservableObject
{
    private readonly ISearchItBackend _backend;
    private readonly Action<Action> _post;
    private readonly RelayCommand<SearchResultItem> _run;
    private readonly RelayCommand<SearchChipItem> _removeChip;
    private ToolSearch _search = new(null);
    private IReadOnlyList<SearchResultItem> _results = [];
    private string _text = "";
    private string _status = "";
    private string _emptyText = "";
    private bool _isLoading;
    private bool _isFamily;
    private string _message = "";

    /// <param name="post">Tests only: how the chosen tool's run is queued (default: the dispatcher, at Background priority).</param>
    public SearchItViewModel(ISearchItBackend backend, Action<Action>? post = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _post = post ?? PostToDispatcher;
        _run = new RelayCommand<SearchResultItem>(Run);
        _removeChip = new RelayCommand<SearchChipItem>(RemoveChip);
    }

    /// <summary>Raised when the window should close (a result was clicked).</summary>
    public event EventHandler? RequestClose;

    public BusyState Busy { get; } = new();

    public ObservableCollection<SearchChipItem> Chips { get; } = [];

    /// <summary>The result cards, best first. Replaced whole on every change (the list virtualizes).</summary>
    public IReadOnlyList<SearchResultItem> Results
    {
        get => _results;
        private set => SetField(ref _results, value);
    }

    /// <summary>The text in the search box; every change re-ranks the results.</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (!SetField(ref _text, value ?? "")) return;
            OnPropertyChanged(nameof(IsTextEmpty));
            Refresh();
        }
    }

    public bool IsTextEmpty => _text.Length == 0;

    public bool HasChips => Chips.Count > 0;

    /// <summary>True until the tools have been read (<see cref="Load"/>); the window shows the busy overlay.</summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set => SetField(ref _isLoading, value);
    }

    /// <summary>"12 of 2,431 tools".</summary>
    public string StatusText
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    /// <summary>What the empty list says ("No tools match."); empty while there are results or the tools load.</summary>
    public string EmptyText
    {
        get => _emptyText;
        private set
        {
            if (SetField(ref _emptyText, value)) OnPropertyChanged(nameof(IsEmpty));
        }
    }

    public bool IsEmpty => _emptyText.Length > 0;

    /// <summary>
    /// The amber message of a click that could not run (spec #35, "Running a Tool", 4); empty when there is none.
    /// Cleared by the next search or click.
    /// </summary>
    public string Message
    {
        get => _message;
        private set
        {
            if (SetField(ref _message, value ?? "")) OnPropertyChanged(nameof(HasMessage));
        }
    }

    public bool HasMessage => _message.Length > 0;

    /// <summary>The tool a click chose; null when the window closed without running anything.</summary>
    public SearchTool? Chosen { get; private set; }

    /// <summary>How many tools can be searched.</summary>
    public int ToolCount => _search.Count;

    /// <summary>Before the window shows: the first frame is the chrome and the busy overlay.</summary>
    /// <param name="showBusy">False for a warm open whose read is deferred (spec #35): the list is empty for a moment, with no overlay.</param>
    public void BeginLoading(bool showBusy = true)
    {
        IsLoading = true;
        if (showBusy) Busy.Show(UiStrings.SearchIt_LoadingTitle, UiStrings.SearchIt_LoadingMessage);
        Refresh();
    }

    /// <summary>
    /// Reads the tools from the backend, on the calling thread (in Revit: its UI thread, inside the
    /// command's Execute, where the catalogue may be built). Text typed and chips made meanwhile are kept.
    /// </summary>
    public void Load()
    {
        try
        {
            var list = _backend.LoadTools();
            SetTools(list.Tools, list.IsFamily);
        }
        finally
        {
            IsLoading = false;
            Busy.Hide();
            Refresh();
        }
    }

    /// <summary>Replaces the searchable tools and re-runs the search.</summary>
    /// <param name="isFamily">The tools are a family's: the count line and the empty text say so (spec #33).</param>
    public void SetTools(IEnumerable<SearchTool>? tools, bool isFamily = false)
    {
        _search = new ToolSearch(tools);
        _isFamily = isFamily;
        OnPropertyChanged(nameof(ToolCount));
        Refresh();
    }

    /// <summary>Enter: the text becomes a chip and the box empties. Blank text, or a chip that already exists, adds none.</summary>
    public bool CommitChip()
    {
        var added = SearchChips.CanAdd(Chips.Select(c => c.Text), _text, out var label);
        if (added) Chips.Add(new SearchChipItem(label, _removeChip));
        // A repeat of an existing chip clears the box too: the filter the user asked for is in force.
        if (label.Length > 0 || _text.Length > 0) Text = "";
        if (added) ChipsChanged();
        return added;
    }

    /// <summary>Backspace in an empty box: the last chip goes.</summary>
    public bool RemoveLastChip()
    {
        if (!IsTextEmpty || Chips.Count == 0) return false;
        Chips.RemoveAt(Chips.Count - 1);
        ChipsChanged();
        return true;
    }

    private void RemoveChip(SearchChipItem chip)
    {
        if (Chips.Remove(chip)) ChipsChanged();
    }

    private void ChipsChanged()
    {
        OnPropertyChanged(nameof(HasChips));
        Refresh();
    }

    /// <summary>
    /// A click on a result card, or Enter on the highlighted one: the window closes first, and the tool's
    /// run is queued behind that, so whatever the run shows (Revit's "couldn't run" message) is not shown
    /// from inside the click handler of a window that has just been destroyed.
    /// </summary>
    private void Run(SearchResultItem item)
    {
        if (item is null || Chosen is not null || IsLoading) return;
        var tool = item.Tool;
        if (!tool.CanRun)
        {
            // A Must Run In card: nothing runs and the window stays open; the badge and its tooltip say where it runs.
            Log.Information("Search-It: {Name} ({CommandId}) must run in {Views}; not run", item.Name, tool.Id, string.Join(", ", tool.MustRunIn));
            return;
        }
        bool canRun;
        try
        {
            canRun = _backend.CanRunNow(tool);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Search-It: could not check whether {Name} ({CommandId}) can run", item.Name, tool.Id);
            canRun = false;
        }
        if (!canRun)
        {
            Message = SearchViews.CantRunHere(item.Name, item.Location);
            Log.Information("Search-It: {Name} ({CommandId}) at {Location} can't be run with RST now; the window stays open with the message",
                item.Name, tool.Id, item.Location);
            return;
        }
        Message = "";
        Chosen = tool;
        Log.Information("Search-It: chose {Name} ({CommandId}) at {Location}; text={Text}, chips={Chips}",
            item.Name, tool.Id, item.Location, _text, Chips.Count);
        RequestClose?.Invoke(this, EventArgs.Empty);
        _post(() => _backend.Run(tool));
    }

    private static void PostToDispatcher(Action action) =>
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            () => UiGuard.Run("Search-It: run the chosen tool", action));

    private static string Number(int value) => value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    private void Refresh()
    {
        Message = "";
        if (IsLoading)
        {
            Results = [];
            StatusText = "";
            EmptyText = "";
            return;
        }
        var hits = _search.Search(Chips.Select(c => c.Text), _text);
        var items = new List<SearchResultItem>(hits.Count);
        foreach (var hit in hits) items.Add(new SearchResultItem(hit, _run));
        Results = items;
        var (shown, all) = (Number(items.Count), Number(_search.Count));
        StatusText = items.Count == _search.Count
            ? (_isFamily ? UiStrings.SearchIt_FamilyCountAll(all) : UiStrings.SearchIt_CountAll(all))
            : (_isFamily ? UiStrings.SearchIt_FamilyCount(shown, all) : UiStrings.SearchIt_Count(shown, all));
        EmptyText = items.Count > 0 ? ""
            : _search.Count == 0 ? UiStrings.SearchIt_NoTools
            : _isFamily ? UiStrings.SearchIt_FamilyNoMatches
            : UiStrings.SearchIt_NoMatches;
    }
}
