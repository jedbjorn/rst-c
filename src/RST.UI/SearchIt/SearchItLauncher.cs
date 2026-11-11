// rst-c's own (not vendored): opens the Search-It window for RST.Engine's SearchItCommand (v3.4).
//
// Unlike the Loader, the Builder and Health, this window is modeless (Show, not ShowDialog): the user
// can pan, zoom and orbit the Revit view behind it. It is owned by Revit's main window, so it stays
// above Revit and has no taskbar entry, and it closes itself (Escape, a left or right click outside,
// another application in front: SearchItWindow). The tools are read here, synchronously, before Show
// returns to the command: the catalogue build needs Revit's UI thread inside the command's Execute,
// and a modeless window has neither once Execute has returned. So the window is shown first with its
// busy overlay, drawn, and only then is the catalogue read; a cold build (a few seconds, once a
// session) happens under a window that says what it is doing. The overlay's spinner does not turn
// meanwhile: the UI thread is the one doing the work.

using System.Windows.Threading;
using RST.Core.Logging;
using RST.Core.Scanning;
using RST.Core.Search;
using RST.UI.Dialogs;
using RST.UI.Mvvm;

namespace RST.UI.SearchIt;

public static class SearchItLauncher
{
    private static SearchItWindow? _open;

    /// <summary>The open Search-It window, if there is one.</summary>
    internal static SearchItWindow? Current => _open;

    /// <summary>
    /// Shows the Search-It window, owned by <paramref name="owner"/> (Revit's main window; zero opens it
    /// unowned, centred on the screen), reads the tools, and returns with the window still open. When
    /// one is already open it is brought to the front instead and nothing else happens (returns false).
    /// Revit UI thread only, inside the command's Execute; call <c>RstTheme.Apply</c> (the Engine's
    /// <c>RevitThemeDetector.ApplyToWindows</c>) first.
    /// </summary>
    /// <param name="catalog">The command catalogue (RstApplication.GetOrBuildCatalog); called once, after the window has drawn.</param>
    /// <param name="run">Runs the tool a click chose; called after the window has closed.</param>
    public static bool Show(IntPtr owner, Func<IReadOnlyList<ScannedCommand>?> catalog, Action<SearchTool> run) =>
        Show(owner, new SearchItBackend(catalog, run));

    /// <inheritdoc cref="Show(IntPtr, Func{IReadOnlyList{ScannedCommand}?}, Action{SearchTool})"/>
    public static bool Show(IntPtr owner, ISearchItBackend backend) => Show(owner, backend, sampler: null);

    /// <param name="sampler">Tests only: the mouse reading the window's close watch uses instead of Windows'.</param>
    internal static bool Show(IntPtr owner, ISearchItBackend backend, Func<PointerSample?>? sampler)
    {
        if (_open is { IsOpen: true } existing)
        {
            Log.Information("Search-It window is already open; bringing it to the front");
            existing.FocusSearch();
            return false;
        }

        var viewModel = new SearchItViewModel(backend);
        viewModel.BeginLoading();
        var window = new SearchItWindow(viewModel, sampler);
        WindowPlacement.Own(window, owner);
        window.Closed += (_, _) => UiGuard.Run("Search-It launcher: closed", () =>
        {
            if (ReferenceEquals(_open, window)) _open = null;
            Log.Information("Search-It window closed ({Outcome})", viewModel.Chosen is null ? "nothing run" : "tool chosen");
        });
        _open = window;
        Log.Information("Search-It window opening (owner {Owner})", owner == IntPtr.Zero ? "none" : "Revit");
        WindowGuard.Show(window, "Search-It");
        try
        {
            // Draw the first frame (the chrome and the busy overlay) before the catalogue is read.
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            viewModel.Load();
        }
        catch
        {
            window.CloseOnce();
            throw;
        }
        Log.Information("Search-It ready: {Count} tools", viewModel.ToolCount);
        if (window.IsOpen) window.FocusSearch();
        return true;
    }
}
