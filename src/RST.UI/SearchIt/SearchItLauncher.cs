// rst-c's own (not vendored): opens the Search-It window for RST.Engine's SearchItCommand (v3.4).
//
// Unlike the Loader, the Builder and Health, this window is modeless (Show, not ShowDialog): the user
// can pan, zoom and orbit the Revit view behind it. It is owned by Revit's main window, so it stays
// above Revit and has no taskbar entry, opens centred on it, and closes itself (Escape, a left or
// right click outside, another application in front: SearchItWindow).
//
// The tools are read here, synchronously, before Show returns to the command: the catalogue build
// needs Revit's UI thread inside the command's Execute, and a modeless window has neither once
// Execute has returned.
//   - Warm (the catalogue is already built this session, ISearchItBackend.IsReady): the tools are read
//     first and the window opens with its results. No overlay, no extra pump.
//   - Cold (first use in a session, a few seconds): the window is shown with its busy overlay, its
//     first frame is drawn, and only then is the catalogue read, so the build happens under a window
//     that says what it is doing. The overlay's spinner does not turn meanwhile: the UI thread is the
//     one doing the work. Clicks and Enter queued during the read are dropped (SearchItWindow.MarkReady).
// Either way the window ends up active with the keyboard in the search box: the first keystroke after
// the ribbon click lands there.

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
    /// Shows the Search-It window, owned by <paramref name="owner"/> and centred on it (Revit's main
    /// window; zero opens it unowned, centred on the screen), reads the tools, and returns with the
    /// window still open, active, and the keyboard in its search box. When one is already open it is
    /// brought to the front instead and nothing else happens (returns false). Revit UI thread only,
    /// inside the command's Execute; call <c>RstTheme.Apply</c> (the Engine's
    /// <c>RevitThemeDetector.ApplyToWindows</c>) first.
    /// </summary>
    /// <param name="catalog">The command catalogue (RstApplication.GetOrBuildCatalog); called once.</param>
    /// <param name="run">Runs the tool the user chose; called after the window has closed.</param>
    /// <param name="isReady">True when <paramref name="catalog"/> will return at once (already built): no busy overlay.</param>
    public static bool Show(IntPtr owner, Func<IReadOnlyList<ScannedCommand>?> catalog, Action<SearchTool> run, Func<bool>? isReady = null) =>
        Show(owner, new SearchItBackend(catalog, run, isReady));

    /// <inheritdoc cref="Show(IntPtr, Func{IReadOnlyList{ScannedCommand}?}, Action{SearchTool}, Func{bool}?)"/>
    public static bool Show(IntPtr owner, ISearchItBackend backend) => Show(owner, backend, sampler: null);

    /// <param name="sampler">Tests only: the mouse reading the window's watch uses instead of Windows'.</param>
    internal static bool Show(IntPtr owner, ISearchItBackend backend, Func<PointerSample?>? sampler)
    {
        if (_open is { } existing)
        {
            // A window the safety net could only hide (WindowGuard.Show) is not open: it is replaced.
            if (existing.IsOpen && existing.IsVisible)
            {
                Log.Information("Search-It window is already open; bringing it to the front");
                existing.FocusSearch();
                return false;
            }
            Log.Warning("Search-It window was left hidden or half-closed; opening a new one");
            _open = null;
            UiGuard.Run("Search-It launcher: close the stale window", () => existing.CloseOnce("stale"));
        }

        var viewModel = new SearchItViewModel(backend);
        var cold = !backend.IsReady;
        if (cold) viewModel.BeginLoading();
        else viewModel.Load();
        var window = new SearchItWindow(viewModel, sampler);
        WindowPlacement.Own(window, owner);
        window.Closed += (_, _) => UiGuard.Run("Search-It launcher: closed", () =>
        {
            if (ReferenceEquals(_open, window)) _open = null;
            Log.Information("Search-It window closed ({Reason}; {Outcome})",
                window.CloseReason ?? "closed", viewModel.Chosen is null ? "nothing run" : "tool chosen");
        });
        _open = window;
        Log.Information("Search-It window opening ({Mode}, owner {Owner})", cold ? "cold: reading the tools" : "warm", owner == IntPtr.Zero ? "none" : "Revit");
        WindowGuard.Show(window, "Search-It");
        try
        {
            if (cold)
            {
                // The first frame (the chrome and the busy overlay) is on screen before the catalogue is read.
                window.DrawFirstFrame();
                viewModel.Load();
                // Input from before this moment was aimed at the overlay, not at the results now under it.
                window.MarkReady();
            }
        }
        catch
        {
            window.CloseOnce("failed");
            throw;
        }
        Log.Information("Search-It ready: {Count} tools", viewModel.ToolCount);
        if (window.IsOpen)
        {
            window.RebaseWatch();
            window.FocusSearch();
            window.FocusWhenSettled();
        }
        return true;
    }
}
