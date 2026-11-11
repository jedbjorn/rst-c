// rst-c's own (not vendored): the Search-It window's code-behind (v3.4). The window is modeless and
// owned by Revit (SearchItLauncher), so the user can navigate the view behind it. It closes itself:
//   - Escape, while it has the keyboard;
//   - a left or right press anywhere outside it;
//   - another application coming to the front (Alt+Tab).
// A middle press and the wheel outside it never close it: they pan, orbit and zoom the view.
//
// How it sees the mouse outside itself: a DispatcherTimer reads the two buttons, the pointer and the
// foreground window every 30 ms (SampleNative), and RST.Core.Search.SearchItCloseRules decides. A
// WH_MOUSE_LL hook was the other option and was not taken: a low-level hook routes every mouse event
// of the whole desktop through this thread, which is Revit's UI thread. Whenever Revit is busy (a
// regeneration, a sync, a print) the pointer would stutter system-wide for as long as the window is
// open, and Windows silently removes a hook whose callback is late (LowLevelHooksTimeout), after
// which the window would never close on a click again, with nothing to tell us. The timer has no
// effect outside this process, cannot be removed behind our back, and stops with the window; while
// Revit is busy it is merely late. Deactivated alone is not enough: a middle-button pan activates
// Revit, and a later left click in Revit then raises no second Deactivated. A click that falls
// between two readings is still seen (GetAsyncKeyState's "pressed since the last call" bit), and the
// timer only ever reads the left and right buttons, so the middle button and the wheel cannot close
// the window by construction.

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using RST.Core.Logging;
using RST.Core.Search;
using RST.UI.Dialogs;
using RST.UI.Mvvm;
using RST.UI.Theming;

namespace RST.UI.SearchIt;

public partial class SearchItWindow : Window
{
    /// <summary>How often the mouse is read while the window is open.</summary>
    internal static readonly TimeSpan WatchInterval = TimeSpan.FromMilliseconds(30);

    private readonly DispatcherTimer _watch;
    private PointerWatchState _watchState;
    private bool _watching;
    private bool _closing;
    private readonly bool _injectedSampler;

    /// <summary>Build after <c>RstTheme.Apply</c>: Theme.xaml reads the theme when the window loads it.</summary>
    /// <param name="sampler">Tests only: the mouse reading to use instead of Windows' (see <see cref="Sampler"/>).</param>
    public SearchItWindow(SearchItViewModel viewModel, Func<PointerSample?>? sampler = null)
    {
        _injectedSampler = sampler is not null;
        if (sampler is not null) Sampler = sampler;
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DarkTitleBar.Attach(this);
        FirstFrame.Attach(this);
        DataContext = viewModel;
        _watch = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = WatchInterval };
        _watch.Tick += (_, _) => UiGuard.Run("Search-It window: watch the mouse", WatchTick);
        viewModel.RequestClose += OnRequestClose;
        Loaded += (_, _) => UiGuard.Run("Search-It window: loaded", () =>
        {
            FreeTheHeight();
            FocusSearch(activate: ShowActivated);
            StartWatch();
        });
        Closed += (_, _) => UiGuard.Run("Search-It window: closed", () =>
        {
            IsOpen = false;
            _watching = false;
            _watch.Stop();
            viewModel.RequestClose -= OnRequestClose;
        });
    }

    public SearchItViewModel ViewModel { get; }

    /// <summary>True from construction until the window has closed.</summary>
    internal bool IsOpen { get; private set; } = true;

    /// <summary>The reading the watch takes; tests replace it. Null result: nothing could be read, nothing happens.</summary>
    internal Func<PointerSample?> Sampler { get; set; } = () => null;

    /// <summary>Why the window closed itself, for the log and the tests: "escape", "outside", "chosen".</summary>
    internal string? CloseReason { get; private set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        UiGuard.Run("Search-It window: place", () =>
        {
            WindowPlacement.CapToWorkArea(this);
            if (_injectedSampler) return;
            var hwnd = new WindowInteropHelper(this).Handle;
            Sampler = () => SampleNative(hwnd);
        });
    }

    /// <summary>
    /// The window opened as tall as its content, with the list at eight cards
    /// (<see cref="SearchItLayout.ListHeight"/>). From here the user's resize decides: the height is
    /// handed to the window and the list fills what is left. Nothing is saved; the next open starts over.
    /// </summary>
    private void FreeTheHeight()
    {
        SizeToContent = SizeToContent.Manual;
        ResultsList.ClearValue(HeightProperty);
        KeepEightCards();
        // Once more when the window has settled: its frame can change after Loaded (the list came out 8 px
        // taller on Windows Server once the window was up).
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => UiGuard.Run("Search-It window: default size", KeepEightCards));
    }

    /// <summary>
    /// The list takes what the window leaves it; the window's height is corrected until that is eight cards.
    /// Only while the window opens: afterwards the user's resize decides.
    /// </summary>
    private void KeepEightCards()
    {
        if (!IsOpen) return;
        for (var pass = 0; pass < 3; pass++)
        {
            UpdateLayout();
            var extra = ResultsList.ActualHeight - SearchItLayout.ListHeight;
            if (Math.Abs(extra) < 0.5) break;
            var current = double.IsNaN(Height) ? ActualHeight : Height;
            if (current - extra <= MinHeight) break;
            Height = current - extra;
        }
    }

    /// <summary>Brings the window to the front with the cursor in the search box.</summary>
    internal void FocusSearch(bool activate = true)
    {
        if (!IsOpen) return;
        if (activate && !IsActive) Activate();
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        SearchBox.CaretIndex = SearchBox.Text.Length;
    }

    /// <summary>Closes the window unless it is closing or closed already.</summary>
    internal void CloseOnce(string? reason = null)
    {
        if (_closing || !IsOpen) return;
        _closing = true;
        CloseReason ??= reason;
        Close();
    }

    private void OnRequestClose(object? sender, EventArgs e) => UiGuard.Run("Search-It window: close", () => CloseOnce("chosen"));

    /// <summary>Enter makes a chip (it never runs a tool); Backspace in the empty box removes the last chip.</summary>
    private void OnSearchKeyDown(object sender, KeyEventArgs e) => UiGuard.Run("Search-It window: search box key", () =>
    {
        if (e.Key is Key.Enter or Key.Return)
        {
            ViewModel.CommitChip();
            e.Handled = true;
        }
        else if (e.Key == Key.Back && SearchBox.Text.Length == 0)
        {
            if (ViewModel.RemoveLastChip()) e.Handled = true;
        }
    });

    /// <summary>Escape closes the window without running anything: a fresh press only, and not one a popup in front wants.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        UiGuard.Run("Search-It window: Escape", () =>
        {
            if (!EscapeGuard.IsFreshEscape(e.Key, e.IsRepeat, e.Handled)) return;
            if (EscapeGuard.HasSomethingOpen(this)) return;
            e.Handled = true;
            CloseOnce("escape");
        });
    }

    // ── closing on a click outside ───────────────────────────────────────

    private void StartWatch()
    {
        if (_watching || !IsOpen) return;
        // The buttons as they are now: the click that opened the window is not a click outside it.
        // Reading them also clears "pressed since the last call" from before the window was up.
        _watchState = PointerWatchState.StartingFrom(Sampler() ?? default);
        _watching = true;
        _watch.Start();
    }

    /// <summary>One reading (the timer's tick): closes the window when <see cref="SearchItCloseRules.Step"/> says so.</summary>
    internal void WatchTick()
    {
        if (!_watching || !IsOpen) return;
        if (Sampler() is not { } sample) return;
        if (!SearchItCloseRules.Step(ref _watchState, sample)) return;
        Log.Debug("Search-It window: closing ({Why})", sample.HostInFront ? "a click outside it" : "another application is in front");
        CloseOnce("outside");
    }

    private const int VK_LBUTTON = 0x01;
    private const int VK_RBUTTON = 0x02;

    /// <summary>The buttons, the pointer against the window's rectangle, and whose window is in front. Null when Windows would not say.</summary>
    private static PointerSample? SampleNative(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        var left = GetAsyncKeyState(VK_LBUTTON);
        var right = GetAsyncKeyState(VK_RBUTTON);
        if (!GetCursorPos(out var point) || !GetWindowRect(hwnd, out var rect)) return null;
        var foreground = GetForegroundWindow();
        var hostInFront = true; // no foreground window (mid-switch) is not "another application"
        if (foreground != IntPtr.Zero && GetWindowThreadProcessId(foreground, out var pid) != 0)
            hostInFront = pid == (uint)Environment.ProcessId;
        return new PointerSample(
            LeftDown: (left & 0x8000) != 0, LeftPressedSince: (left & 1) != 0,
            RightDown: (right & 0x8000) != 0, RightPressedSince: (right & 1) != 0,
            PointerInside: SearchItCloseRules.Contains(rect.Left, rect.Top, rect.Right, rect.Bottom, point.X, point.Y),
            HostInFront: hostInFront);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
