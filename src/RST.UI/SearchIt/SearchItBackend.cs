// rst-c's own (not vendored): what the Search-It window needs from its host (v3.4). RST.UI stays
// Revit-free: the Engine's SearchItCommand supplies the catalogue and the way to run a tool.

using RST.Core.Scanning;
using RST.Core.Search;

namespace RST.UI.SearchIt;

/// <summary>The Search-It window's backend; tests drive the view-model with a fake.</summary>
public interface ISearchItBackend
{
    /// <summary>
    /// The tools to search. In Revit this may build the command catalogue cold (a few seconds, once a
    /// session) and must run on Revit's UI thread inside the command's Execute.
    /// </summary>
    IReadOnlyList<SearchTool> LoadTools();

    /// <summary>
    /// True when <see cref="LoadTools"/> will return at once (the catalogue is already built this session):
    /// the window then opens with its results, with no busy overlay.
    /// </summary>
    bool IsReady { get; }

    /// <summary>Runs <paramref name="tool"/>. Called after the window has closed.</summary>
    void Run(SearchTool tool);
}

/// <summary>The backend in Revit: the Builder's catalogue, folded to one tool per command (<see cref="SearchIndex"/>).</summary>
public sealed class SearchItBackend(Func<IReadOnlyList<ScannedCommand>?> catalog, Action<SearchTool> run, Func<bool>? isReady = null) : ISearchItBackend
{
    public bool IsReady => isReady?.Invoke() ?? false;

    private readonly Func<IReadOnlyList<ScannedCommand>?> _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly Action<SearchTool> _run = run ?? throw new ArgumentNullException(nameof(run));

    public IReadOnlyList<SearchTool> LoadTools() => SearchIndex.Build(_catalog());

    public void Run(SearchTool tool) => _run(tool);
}
