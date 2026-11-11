// rst-c's own (not vendored): the Search-It window's code-behind (v3.4). The window is modeless and
// owned by Revit (SearchItLauncher), so the user can navigate the view behind it. It closes itself:
//   - Escape, while it has the keyboard;
//   - a left or right press anywhere outside it;
//   - another application coming to the front (Alt+Tab).
// A middle press and the wheel outside it never close it: they pan, orbit and zoom the view. When a
// middle-button pan or orbit ends with Revit's main window active, the window takes the keyboard back,
// so typing goes on into the search box and not to Revit as shortcuts.
//
// How it sees the mouse outside itself: a DispatcherTimer reads the left, right and middle buttons,
// the pointer, the mouse capture and the foreground window every 30 ms (SampleNative), and
// RST.Core.Search.SearchItCloseRules decides. A WH_MOUSE_LL hook was the other option and was not
// taken: a low-level hook routes every mouse event of the whole desktop through this thread, which is
// Revit's UI thread. Whenever Revit is busy (a regeneration, a sync, a print) the pointer would
// stutter system-wide for as long as the window is open, and Windows silently removes a hook whose
// callback is late (LowLevelHooksTimeout), after which the window would never close on a click
// again, with nothing to tell us. The timer has no effect outside this process, cannot be removed
// behind our back, and stops with the window; while Revit is busy it is merely late. Deactivated
// alone is not enough: a middle-button pan activates Revit, and a later left click in Revit then
// raises no second Deactivated. A click that falls between two readings is still seen
// (GetAsyncKeyState's "pressed since the last call" bit). The wheel is never read, so it cannot do
// anything by construction; the middle button is read only for its release.
//
// A reading tells where the pointer is when it is taken, not where the button went down. So a press
// is the window's own, wherever the pointer is by then, when the window's own mouse-down saw it
// (client area: PreviewMouseDown; title bar and resize border: WM_NC*BUTTONDOWN) or the window holds
// the mouse capture (a drag of its edge).
//
// Keys (decision #83, RST.Core.Search.SearchItKeys): in the box Enter makes a chip and Down goes to
// the first card; on a card Up and Down move the highlight (Up on the first card goes back to the
// box), Enter runs the highlighted tool as a click does, and a printable character goes back to the
// box and is typed there. Escape closes from anywhere.

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
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

    /// <summary>The longest <see cref="DrawFirstFrame"/> waits for the first frame before the tools are read anyway.</summary>
    internal static readonly TimeSpan FirstFrameWait = TimeSpan.FromMilliseconds(500);

    private readonly DispatcherTimer _watch;
    private readonly bool _injectedSampler;
    private PointerWatchState _watchState;
    private bool _watching;
    private bool _closing;
    private bool _ownPress;
    private bool _userMoved;
    private int? _readyTick;

    /// <summary>Build after <c>RstTheme.Apply</c>: Theme.xaml reads the theme when the window loads it.</summary>
    /// <param name="sampler">Tests only: the mouse reading to use instead of Windows' (see <see cref="Sampler"/>).</param>
    public SearchItWindow(SearchItViewModel viewModel, Func<PointerSample?>? sampler = null)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _injectedSampler = sampler is not null;
        if (sampler is not null) Sampler = sampler;
        InitializeComponent();
        DarkTitleBar.Attach(this);
        FirstFrame.Attach(this);
        DataContext = viewModel;
        _watch = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = WatchInterval };
        _watch.Tick += (_, _) => UiGuard.Run("Search-It window: watch the mouse", WatchTick);
        viewModel.RequestClose += OnRequestClose;
        viewModel.Chips.CollectionChanged += OnChipsChanged;
        // Hidden without closing (the safety net's fallback when a close fails): no watch, nothing to refocus.
        IsVisibleChanged += (_, _) => UiGuard.Run("Search-It window: visibility", () =>
        {
            if (!IsVisible && IsLoaded) StopWatch();
        });
        ContentRendered += (_, _) => UiGuard.Run("Search-It window: first frame", () => FirstFrameDrawn = true);
        // Each on its own, the watch first: closing on a click outside must work even when sizing or focusing failed.
        Loaded += (_, _) => UiGuard.Run("Search-It window: start the watch", StartWatch);
        Loaded += (_, _) => UiGuard.Run("Search-It window: default size", FreeTheHeight);
        Loaded += (_, _) => UiGuard.Run("Search-It window: focus the search box", () => FocusSearch(activate: ShowActivated));
        Closed += (_, _) => UiGuard.Run("Search-It window: closed", () =>
        {
            IsOpen = false;
            StopWatch();
            viewModel.RequestClose -= OnRequestClose;
            viewModel.Chips.CollectionChanged -= OnChipsChanged;
        });
    }

    public SearchItViewModel ViewModel { get; }

    /// <summary>True from construction until the window has closed.</summary>
    internal bool IsOpen { get; private set; } = true;

    /// <summary>WPF has drawn the window's first frame (ContentRendered).</summary>
    internal bool FirstFrameDrawn { get; private set; }

    /// <summary>True while the timer that reads the mouse runs: from Loaded until the window closes (or the reading fails).</summary>
    internal bool IsWatching => _watching && _watch.IsEnabled;

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
            var helper = new WindowInteropHelper(this);
            var hwnd = helper.Handle;
            var owner = helper.Owner;
            // A press on the title bar or the resize border is the window's own too; WPF raises no mouse event for those.
            HwndSource.FromHwnd(hwnd)?.AddHook(NonClientPress);
            if (!_injectedSampler) Sampler = () => SampleNative(hwnd, owner);
        });
    }

    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int WM_NCRBUTTONDOWN = 0x00A4;
    internal const int WM_ENTERSIZEMOVE = 0x0231;

    private IntPtr NonClientPress(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is WM_NCLBUTTONDOWN or WM_NCRBUTTONDOWN) _ownPress = true;
        // The user has started to drag or resize the window: its size and place are theirs from here on.
        else if (msg == WM_ENTERSIZEMOVE) _userMoved = true;
        return IntPtr.Zero;
    }

    // ── size ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The window opened as tall as its content, with the list at eight cards
    /// (<see cref="SearchItLayout.ListHeight"/>). From here the user's resize decides: the list fills
    /// what the window leaves it. Nothing is saved; the next open starts over.
    /// </summary>
    private void FreeTheHeight()
    {
        SizeToContent = SizeToContent.Manual;
        ResultsList.ClearValue(HeightProperty);
        KeepEightCards();
        // Once more when the window has settled: its frame can change after Loaded (the list came out 8 px
        // taller on Windows Server once the window was up).
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => UiGuard.Run("Search-It window: default size", LateSizeCorrection));
    }

    /// <summary>
    /// The idle pass of the default size and centring. Skipped once the user has dragged or resized the
    /// window (WM_ENTERSIZEMOVE): a correction then would pull it back from under their pointer.
    /// </summary>
    internal void LateSizeCorrection()
    {
        if (_userMoved) return;
        KeepEightCards();
    }

    /// <summary>
    /// The list takes what the window leaves it; the window's height is corrected until that is eight cards.
    /// Only while the window opens: afterwards the user's resize decides.
    /// </summary>
    private void KeepEightCards()
    {
        if (!IsOpen) return;
        var before = ActualHeight;
        for (var pass = 0; pass < 3; pass++)
        {
            UpdateLayout();
            var extra = ResultsList.ActualHeight - SearchItLayout.ListHeight;
            if (Math.Abs(extra) < 0.5) break;
            var current = double.IsNaN(Height) ? ActualHeight : Height;
            if (current - extra <= MinHeight) break;
            Height = current - extra;
        }
        UpdateLayout();
        // WPF centred the window on Revit at the size it had before it settled: centre it again. (Unowned,
        // in tests or an odd host: keep its middle where it was.)
        if (WindowStartupLocation == WindowStartupLocation.Manual) return; // placed by hand (the off-screen tests)
        if (WindowPlacement.CentreOnOwner(this)) return;
        var change = ActualHeight - before;
        if (Math.Abs(change) >= 0.5 && !double.IsNaN(Top)) Top -= change / 2;
    }

    // ── opening: first frame, ready, focus ───────────────────────────────

    /// <summary>
    /// Runs the dispatcher until the window's first frame is drawn (ContentRendered), the window closes, or
    /// <see cref="FirstFrameWait"/> has passed. The launcher calls it before a cold catalogue read, so the
    /// busy overlay is on screen while the UI thread is busy.
    /// </summary>
    internal void DrawFirstFrame()
    {
        if (FirstFrameDrawn || !IsOpen) return;
        var frame = new DispatcherFrame();
        EventHandler done = (_, _) => UiGuard.Run("Search-It window: first frame drawn", () => frame.Continue = false);
        var timeout = new DispatcherTimer(FirstFrameWait, DispatcherPriority.Normal, done, Dispatcher);
        ContentRendered += done;
        Closed += done;
        try
        {
            timeout.Start();
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            timeout.Stop();
            ContentRendered -= done;
            Closed -= done;
        }
    }

    /// <summary>
    /// The tools are in (a cold open's read has finished). Mouse presses and Enter from before this moment
    /// were queued while the UI thread was busy and were aimed at the overlay; they are dropped, so a click
    /// made during the wait cannot land on a result card that appeared under it and run a tool the user
    /// never chose. Text typed meanwhile is kept: it was aimed at the search box, which is where it goes.
    /// </summary>
    internal void MarkReady() => _readyTick = Environment.TickCount;

    /// <summary>Input stamped before <see cref="MarkReady"/> (or arriving while the tools still load) is stale.</summary>
    private bool IsStale(int timestamp) =>
        ViewModel.IsLoading || (_readyTick is { } ready && unchecked(timestamp - ready) < 0);

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        UiGuard.Run("Search-It window: mouse down", () =>
        {
            _ownPress = true; // the window's own press, wherever the pointer is when the watch reads it
            if (IsStale(e.Timestamp)) e.Handled = true;
        });
    }

    protected override void OnPreviewMouseUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseUp(e);
        UiGuard.Run("Search-It window: mouse up", () =>
        {
            if (IsStale(e.Timestamp)) e.Handled = true;
        });
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        UiGuard.Run("Search-It window: key", () =>
        {
            // Only Enter can run a tool (on a card) or change the search (a chip); typed text is kept.
            if (e.Key is Key.Enter or Key.Return && IsStale(e.Timestamp)) e.Handled = true;
        });
    }

    /// <summary>How many times the window has asked Windows to make it the active one (tests).</summary>
    internal int Activations { get; private set; }

    /// <summary>
    /// False when another application is in front (the user Alt+Tabbed away): the window must not pull
    /// itself, and Revit, forward then. True when Revit or the window is in front, or it cannot be told.
    /// </summary>
    internal bool IsHostInFront() => ReadSample() is not { HostInFront: false };

    /// <summary>
    /// Makes the window the active one with the keyboard in the search box; the text and the caret stay
    /// as they were. <paramref name="activate"/> false only sets the focus inside the window. Nothing
    /// happens for a window that is closed or hidden.
    /// </summary>
    internal void FocusSearch(bool activate = true)
    {
        if (!IsOpen || !IsVisible) return;
        if (activate && !IsActive)
        {
            Activations++;
            Activate();
            // Activate can be refused when the click that opened the window is still being handled.
            if (!IsActive)
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero) SetForegroundWindow(hwnd);
            }
        }
        ResultsList.SelectedIndex = -1;
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
    }

    /// <summary>
    /// Once more when the dispatcher is idle: Revit can take the activation back as the ribbon click that
    /// opened the window finishes. Only while Revit or the window itself is in front, never over another
    /// application.
    /// </summary>
    internal void FocusWhenSettled() =>
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => UiGuard.Run("Search-It window: focus when settled", () =>
        {
            if (!IsOpen || !IsVisible || SearchBox.IsKeyboardFocused) return;
            if (!IsHostInFront()) return;
            FocusSearch();
        }));

    /// <summary>Closes the window unless it is closing or closed already.</summary>
    internal void CloseOnce(string? reason = null)
    {
        if (_closing || !IsOpen) return;
        _closing = true;
        CloseReason ??= reason;
        StopWatch();
        Close();
    }

    private void OnRequestClose(object? sender, EventArgs e) => UiGuard.Run("Search-It window: close", () => CloseOnce("chosen"));

    // ── keys ─────────────────────────────────────────────────────────────

    private static SearchKey KeyOf(Key key) => key switch
    {
        Key.Enter => SearchKey.Enter, // Key.Return is the same value
        Key.Up => SearchKey.Up,
        Key.Down => SearchKey.Down,
        Key.Back => SearchKey.Backspace,
        Key.Escape => SearchKey.Escape,
        _ => SearchKey.Other,
    };

    /// <summary>The search box's keys: Enter makes a chip (it never runs a tool), Backspace in the empty box removes the last chip, Down goes to the first card.</summary>
    private void OnSearchKeyDown(object sender, KeyEventArgs e) => UiGuard.Run("Search-It window: search box key", () =>
    {
        var key = KeyOf(e.Key);
        if (key is SearchKey.Escape or SearchKey.Other) return; // Escape is the window's
        switch (SearchItKeys.Decide(SearchFocus.Box, key, -1, ResultsList.Items.Count, SearchBox.Text.Length == 0, e.IsRepeat))
        {
            case SearchKeyAction.MakeChip:
                ViewModel.CommitChip();
                e.Handled = true;
                break;
            case SearchKeyAction.RemoveLastChip:
                if (ViewModel.RemoveLastChip()) e.Handled = true;
                break;
            case SearchKeyAction.FocusFirstCard:
                FocusCard(0);
                e.Handled = true;
                break;
            default:
                // A held Enter is swallowed all the same: it must not reach anything else as a new press.
                if (key == SearchKey.Enter) e.Handled = true;
                break;
        }
    });

    /// <summary>A result card's keys: Up and Down move the highlight, Up on the first card returns to the box, Enter runs the highlighted tool.</summary>
    private void OnResultsKeyDown(object sender, KeyEventArgs e) => UiGuard.Run("Search-It window: result card key", () =>
    {
        var key = KeyOf(e.Key);
        if (key is SearchKey.Escape or SearchKey.Other) return;
        var index = ResultsList.SelectedIndex;
        switch (SearchItKeys.Decide(SearchFocus.Card, key, index, ResultsList.Items.Count, SearchBox.Text.Length == 0, e.IsRepeat))
        {
            case SearchKeyAction.MoveUp:
                FocusCard(index - 1);
                break;
            case SearchKeyAction.MoveDown:
                FocusCard(index + 1);
                break;
            case SearchKeyAction.FocusBox:
                FocusSearch(activate: false);
                break;
            case SearchKeyAction.RunHighlighted:
                if (ResultsList.SelectedItem is SearchResultItem item) item.RunCommand.Execute(item);
                break;
        }
        // Every one of these keys is ours on a card: the list's own handling (selection, paging) never runs.
        e.Handled = true;
    });

    /// <summary>A printable character typed on a card goes back to the box and is typed there.</summary>
    private void OnResultsTextInput(object sender, TextCompositionEventArgs e) => UiGuard.Run("Search-It window: type on a result card", () =>
    {
        if (!SearchItKeys.IsPrintable(e.Text)) return;
        if (SearchItKeys.Decide(SearchFocus.Card, SearchKey.Character, ResultsList.SelectedIndex, ResultsList.Items.Count, SearchBox.Text.Length == 0, false)
            != SearchKeyAction.TypeInBox) return;
        // As typing in the box does: the character replaces the selection, or goes in at the caret.
        var text = SearchBox.Text;
        var start = Math.Clamp(SearchBox.SelectionStart, 0, text.Length);
        var length = Math.Clamp(SearchBox.SelectionLength, 0, text.Length - start);
        FocusSearch(activate: false);
        SearchBox.Text = text.Remove(start, length).Insert(start, e.Text);
        SearchBox.CaretIndex = start + e.Text.Length;
        e.Handled = true;
    });

    /// <summary>
    /// A right-click on a card does nothing: the list must not select and focus it (Enter would then run a
    /// tool the user only right-clicked).
    /// </summary>
    private void OnResultsRightDown(object sender, MouseButtonEventArgs e) => UiGuard.Run("Search-It window: right-click on a result card", () =>
    {
        e.Handled = true;
    });

    /// <summary>
    /// The chips changed (a chip's × while a card had the keyboard): the list is rebuilt and the card is
    /// gone, so the keyboard goes back to the search box instead of nowhere.
    /// </summary>
    private void OnChipsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        UiGuard.Run("Search-It window: chips changed", () =>
        {
            if (ResultsList.SelectedIndex < 0 && !ResultsList.IsKeyboardFocusWithin) return;
            FocusSearch(activate: false);
        });

    /// <summary>Highlights the card at <paramref name="index"/>, scrolls it into view and gives it the keyboard.</summary>
    internal void FocusCard(int index)
    {
        if (index < 0 || index >= ResultsList.Items.Count) return;
        ResultsList.SelectedIndex = index;
        ResultsList.ScrollIntoView(ResultsList.Items[index]);
        ResultsList.UpdateLayout();
        if (ResultsList.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem card)
        {
            card.Focus();
            Keyboard.Focus(card);
        }
    }

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

    // ── the watch: closing on a click outside, the keyboard back after a pan ─────

    private void StartWatch()
    {
        if (_watching || !IsOpen) return;
        _watching = true;
        RebaseWatch();
        _watch.Start();
    }

    private void StopWatch()
    {
        _watching = false;
        _watch.Stop();
    }

    /// <summary>
    /// Takes the buttons as they are now as the starting point, so nothing from before counts as a click
    /// outside: the click that opened the window, or one queued while the tools were read. Reading them
    /// also clears "pressed since the last call".
    /// </summary>
    internal void RebaseWatch()
    {
        if (!_watching) return;
        _ownPress = false;
        _watchState = PointerWatchState.StartingFrom(ReadSample() ?? default);
    }

    /// <summary>The reading, or null. A reading that throws stops the watch (logged once): Escape and the keyboard still close the window.</summary>
    private PointerSample? ReadSample()
    {
        try
        {
            return Sampler();
        }
        catch (Exception ex)
        {
            if (_watching)
            {
                StopWatch();
                Log.Warning(ex, "Search-It window: reading the mouse failed; the window no longer closes on a click outside it (Esc still closes it)");
            }
            return null;
        }
    }

    /// <summary>One reading (the timer's tick): what <see cref="SearchItCloseRules.Step"/> says is done.</summary>
    internal void WatchTick()
    {
        if (!_watching || !IsOpen) return;
        if (ReadSample() is not { } read) return;
        var sample = read with { PressOnWindow = read.PressOnWindow || _ownPress };
        if (!sample.LeftDown && !sample.RightDown) _ownPress = false;
        switch (SearchItCloseRules.Step(ref _watchState, sample))
        {
            case PointerWatchAction.Close:
                Log.Debug("Search-It window: closing ({Why})", sample.HostInFront ? "a click outside it" : "another application is in front");
                CloseOnce("outside");
                break;
            case PointerWatchAction.Refocus:
                Log.Debug("Search-It window: the middle button came up with Revit in front; taking the keyboard back");
                FocusSearch();
                break;
        }
    }

    internal const int VK_LBUTTON = 0x01;
    internal const int VK_RBUTTON = 0x02;
    internal const int VK_MBUTTON = 0x04;

    /// <summary>
    /// The three buttons, the pointer against the window's rectangle (GetWindowRect: the frame and the
    /// resize border are inside), the mouse capture, and whose window is in front. Null when Windows would
    /// not say. Nothing else is read: no wheel, no other key.
    /// </summary>
    /// <param name="keyState">Tests only: stands in for GetAsyncKeyState, to see which keys are read.</param>
    internal static PointerSample? SampleNative(IntPtr hwnd, IntPtr owner, Func<int, short>? keyState = null)
    {
        if (hwnd == IntPtr.Zero) return null;
        keyState ??= GetAsyncKeyState;
        var left = keyState(VK_LBUTTON);
        var right = keyState(VK_RBUTTON);
        var middle = keyState(VK_MBUTTON);
        if (!GetCursorPos(out var point) || !GetWindowRect(hwnd, out var rect)) return null;
        var foreground = GetForegroundWindow();
        var hostInFront = true; // no foreground window (mid-switch) is not "another application"
        if (foreground != IntPtr.Zero && GetWindowThreadProcessId(foreground, out var pid) != 0)
            hostInFront = pid == (uint)Environment.ProcessId;
        return new PointerSample(
            LeftDown: (left & 0x8000) != 0, LeftPressedSince: (left & 1) != 0,
            RightDown: (right & 0x8000) != 0, RightPressedSince: (right & 1) != 0,
            MiddleDown: (middle & 0x8000) != 0,
            PointerInside: SearchItCloseRules.Contains(rect.Left, rect.Top, rect.Right, rect.Bottom, point.X, point.Y),
            PressOnWindow: GetCapture() == hwnd,
            HostInFront: hostInFront,
            OwnerInFront: owner != IntPtr.Zero && foreground == owner);
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

    [DllImport("user32.dll")]
    private static extern IntPtr GetCapture();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
