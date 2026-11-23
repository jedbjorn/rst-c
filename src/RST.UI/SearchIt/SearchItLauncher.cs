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
//
// Deferred (spec #35; ISearchItBackend.DefersLoad): Revit greys more of its own tools while an external command
// is executing than it draws greyed, so the live CanExecute gate must not be asked inside the command's Execute
// (VM, F21-2: about twice as many tools answered "cannot run" there as the census and the ribbon show). The host
// then does what needs the API in Prepare (the catalogue, cold or warm, and the open file's facts), inside
// Execute, and the tools are read on the dispatcher once Execute has returned. The window shows meanwhile with
// its list empty (and the busy overlay on a cold open); input from before the read is handled as on a cold open.

using RST.Core.Logging;
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
    /// <param name="tools">The tools to list for the open file (may build the catalogue); called once.</param>
    /// <param name="run">Runs the tool the user chose; called after the window has closed.</param>
    /// <param name="isReady">True when <paramref name="tools"/> will return at once (catalogue already built): no busy overlay.</param>
    /// <param name="canRunNow">Asked on a click before the window closes; false keeps it open with the amber message (spec #35).</param>
    /// <param name="prepare">
    /// When given, called inside the command (it may build the catalogue), and <paramref name="tools"/> is called later,
    /// on the dispatcher, once the command has returned (the file header's "Deferred").
    /// </param>
    /// <param name="recents">The tools Search-It ran most recently, newest first: read with the tools, shown as chips.</param>
    public static bool Show(IntPtr owner, Func<SearchList> tools, Action<SearchTool> run, Func<bool>? isReady = null,
        Func<SearchTool, bool>? canRunNow = null, Action? prepare = null, Func<IReadOnlyList<SearchRecent>>? recents = null) =>
        Show(owner, new SearchItBackend(tools, run, isReady, canRunNow, prepare, recents));

    /// <inheritdoc cref="Show(IntPtr, Func{SearchList}, Action{SearchTool}, Func{bool}?, Func{SearchTool, bool}?, Action?, Func{IReadOnlyList{SearchRecent}}?)"/>
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
                // The press that ran the command again (the second click of a double-click on the ribbon button)
                // may not have been read yet. It is not a click outside: it asked for this window. A fresh
                // baseline consumes it, so the watch does not close the window a moment later (VM smoke test, D1).
                existing.RebaseWatch();
                existing.FocusSearch();
                return false;
            }
            Log.Warning("Search-It window was left hidden or half-closed; opening a new one");
            _open = null;
            UiGuard.Run("Search-It launcher: close the stale window", () => existing.CloseOnce("stale"));
        }

        var viewModel = new SearchItViewModel(backend);
        var cold = !backend.IsReady;
        var deferred = backend.DefersLoad;
        if (cold) viewModel.BeginLoading();
        else if (deferred) viewModel.BeginLoading(showBusy: false);
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
        Log.Information("Search-It window opening ({Mode}{Deferred}, owner {Owner})", cold ? "cold: reading the tools" : "warm",
            deferred ? ", tools read after the command" : "", owner == IntPtr.Zero ? "none" : "Revit");
        WindowGuard.Show(window, "Search-It");
        try
        {
            if (cold)
            {
                // The first frame (the chrome and the busy overlay) is on screen before the catalogue is read.
                window.DrawFirstFrame();
                if (!deferred)
                {
                    viewModel.Load();
                    // Input from before this moment was aimed at the overlay, not at the results now under it.
                    window.MarkReady();
                }
            }
            if (deferred)
            {
                backend.Prepare();
                // Queued only now: DrawFirstFrame pumps the dispatcher, and nothing may read the tools before the
                // command has returned.
                window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    () => UiGuard.Run("Search-It launcher: read the tools", () => LoadDeferred(window, viewModel)));
            }
        }
        catch
        {
            window.CloseOnce("failed");
            throw;
        }
        if (!deferred) Log.Information("Search-It ready: {Count} tools", viewModel.ToolCount);
        if (window.IsOpen)
        {
            window.RebaseWatch();
            // Not when the user has switched to another application meanwhile (a cold read takes seconds):
            // pulling Revit and this window forward then would take their typing. The watch closes the window.
            if (window.IsHostInFront()) window.FocusSearch();
            else Log.Information("Search-It: another application is in front; not taking the keyboard");
            window.FocusWhenSettled();
        }
        return true;
    }

    /// <summary>The deferred read (file header): the tools, then the same "ready" steps a cold open takes.</summary>
    private static void LoadDeferred(SearchItWindow window, SearchItViewModel viewModel)
    {
        if (!window.IsOpen) return;
        try
        {
            viewModel.Load();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Search-It: reading the tools failed; closing the window");
            window.CloseOnce("failed");
            return;
        }
        window.MarkReady();
        Log.Information("Search-It ready: {Count} tools", viewModel.ToolCount);
        if (!window.IsOpen) return;
        window.RebaseWatch();
        if (window.IsHostInFront()) window.FocusSearch();
    }
}
