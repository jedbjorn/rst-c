// rst-c's own (not vendored): what the Search-It window needs from its host (v3.4). RST.UI stays
// Revit-free: the Engine's SearchItCommand supplies the tools of the open file and the way to run one.

using RST.Core.Search;

namespace RST.UI.SearchIt;

/// <summary>The Search-It window's backend; tests drive the view-model with a fake.</summary>
public interface ISearchItBackend
{
    /// <summary>
    /// The tools to search, and whether they are a family's (spec #33). In Revit this may build the
    /// command catalogue cold (a few seconds, once a session) and must run on Revit's UI thread inside
    /// the command's Execute.
    /// </summary>
    SearchList LoadTools();

    /// <summary>
    /// True when <see cref="LoadTools"/> will return at once (the catalogue is already built this session):
    /// the window then opens with its results, with no busy overlay.
    /// </summary>
    bool IsReady { get; }

    /// <summary>Runs <paramref name="tool"/>. Called after the window has closed.</summary>
    void Run(SearchTool tool);

    /// <summary>
    /// Asked on a click, before the window closes: false when the tool can't run now, and the window then stays open
    /// with the amber message (spec #35, "Running a Tool", 4). Revit UI thread, outside an API context.
    /// </summary>
    bool CanRunNow(SearchTool tool) => true;
}

/// <summary>The backend in Revit: the tools the ribbon is showing for the open file (<see cref="SearchShowing"/>).</summary>
public sealed class SearchItBackend(Func<SearchList> tools, Action<SearchTool> run, Func<bool>? isReady = null,
    Func<SearchTool, bool>? canRunNow = null) : ISearchItBackend
{
    public bool CanRunNow(SearchTool tool) => canRunNow?.Invoke(tool) ?? true;

    public bool IsReady => isReady?.Invoke() ?? false;

    private readonly Func<SearchList> _tools = tools ?? throw new ArgumentNullException(nameof(tools));
    private readonly Action<SearchTool> _run = run ?? throw new ArgumentNullException(nameof(run));

    public SearchList LoadTools() => _tools();

    public void Run(SearchTool tool) => _run(tool);
}
