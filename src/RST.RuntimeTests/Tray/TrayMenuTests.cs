// rst-c's own (not vendored): the RST tray icon's menu on a real Windows desktop (spec #20 §1; SC-020, SC-043).
// The icon's callback message is sent to the icon's hidden window as Explorer sends it under NOTIFYICON_VERSION_4,
// so the menu opens through the tray's own code path (TrayIcon's WndProc, MenuRequested, TrayMenu.Open) and the
// native menu's modal loop runs on the test's UI thread. A second thread watches that thread's menu window: it must
// still be open a second later (SC-043: the WPF menu closed within ~60 ms), then it presses keys in it, Escape to
// dismiss or Up and Enter to pick Quit, as a user would. A screenshot of the open menu lands in
// TestResults/screenshots (tray-menu-native.png) for review.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RST.Core.Logging;
using RST.Core.Tray;
using RST.RuntimeTests.Support;
using RST.Tray.Resources;
using RST.Tray.Shell;
using RST.Tray.Windows;
using Xunit;

namespace RST.RuntimeTests.Tray;

[Collection(WpfThemeCollection.Name)]
public class TrayMenuTests
{
    private const int WmContextMenu = 0x007B;
    private const int WmRButtonUp = 0x0205;
    private const int NinSelect = 0x0400;
    private const int VkReturn = 0x0D;
    private const int VkEscape = 0x1B;
    private const int VkUp = 0x26;

    private static readonly TimeSpan StaysOpenFor = TimeSpan.FromSeconds(1);

    [Fact]
    public void The_menu_holds_Open_Check_Settings_Quit_with_Open_the_default()
    {
        Sta(() =>
        {
            AssertMenu(new TrayMenuActions(() => { }, () => { }, () => { }, () => { }), checkEnabled: true);
            AssertMenu(new TrayMenuActions(() => { }, () => { }, () => { }, () => { }) { CanCheckForUpdates = false }, checkEnabled: false);
        });

        static void AssertMenu(TrayMenuActions actions, bool checkEnabled)
        {
            var menu = TrayMenu.Create(TrayMenu.Entries(actions));
            Assert.NotEqual(IntPtr.Zero, menu);
            try
            {
                Assert.Equal(6, GetMenuItemCount(menu));
                var texts = Enumerable.Range(0, 6).Select(i => MenuText(menu, i)).ToList();
                // The strings' WPF access keys ("_Quit") become Win32 mnemonics ("&Quit").
                Assert.Equal(new[] { "&Open RST", "Check for &updates", "", "&Settings", "", "&Quit" }, texts);
                Assert.Equal(TrayMenuModel.Win32Text(TrayStrings.Menu_Quit), texts[5]);
                Assert.Equal(new uint[] { 1, 2, 0, 3, 0, 4 }, Enumerable.Range(0, 6).Select(i => GetMenuItemID(menu, i)));
                Assert.True((GetMenuState(menu, 2, MfByPosition) & MfSeparator) != 0);
                Assert.True((GetMenuState(menu, 4, MfByPosition) & MfSeparator) != 0);
                Assert.Equal(!checkEnabled, (GetMenuState(menu, 1, MfByPosition) & MfGrayed) != 0);
                foreach (var enabled in new[] { 0, 3, 5 }) Assert.Equal(0u, GetMenuState(menu, (uint)enabled, MfByPosition) & MfGrayed);
                Assert.Equal((uint)TrayMenuCommand.Open, GetMenuDefaultItem(menu, 0, 0)); // shown bold
            }
            finally
            {
                DestroyMenu(menu);
            }
        }
    }

    [Fact]
    public void Each_item_runs_its_action_and_Check_for_updates_is_wired()
    {
        var calls = new List<string>();
        var actions = new TrayMenuActions(() => calls.Add("open"), () => calls.Add("check"), () => calls.Add("settings"), () => calls.Add("quit"));
        foreach (var command in new[] { TrayMenuCommand.Open, TrayMenuCommand.CheckForUpdates, TrayMenuCommand.Settings, TrayMenuCommand.Quit, TrayMenuCommand.None })
            TrayMenu.Run(actions, command);
        Assert.Equal(new[] { "open", "check", "settings", "quit" }, calls);
    }

    /// <summary>
    /// SC-020: the icon's callback reaches the menu request, and only WM_CONTEXTMENU does (v4 also sends the mouse
    /// messages); the request is logged with its signed anchor.
    /// </summary>
    [Fact]
    public void A_context_menu_request_is_logged_with_its_anchor() => WithLog(events => Sta(() =>
    {
        var failures = new List<string>();
        using var icon = new TrayIcon(TrayAssets.IconFile(), "RST", failures.Add);
        var requested = new List<Point>();
        var activated = 0;
        icon.Activated += (_, _) => activated++;
        icon.MenuRequested += (_, p) => requested.Add(p);

        // The event is LOWORD(lParam) and the icon id HIWORD(lParam); the anchor is wParam, signed x and y.
        Send(icon, WmRButtonUp, -1200, 850); // not a menu request
        Send(icon, WmContextMenu, -1200, 850); // a monitor left of the primary
        Send(icon, NinSelect, 10, 20);
        Assert.Equal(new[] { new Point(-1200, 850) }, requested);
        Assert.Equal(1, activated);
        Assert.Equal(new Point(-1200, 850), TrayIcon.AnchorOf(MakeLong(-1200, 850)));
        var request = Assert.Single(Lines(events, "tray icon: menu requested"));
        Assert.Contains("-1200", request);
        Assert.Empty(failures);
    }));

    /// <summary>
    /// SC-043: a right click (WM_CONTEXTMENU) opens the menu through the tray's code path, it is still open a second
    /// later, and Escape dismisses it without running anything. The log says requested, opened, closed and dismissed.
    /// </summary>
    [Fact]
    public void The_menu_stays_open_until_Escape_dismisses_it() => WithLog(events => Sta(() =>
    {
        var calls = new List<string>();
        var result = OpenThroughTheIcon(Recording(calls), VkEscape, screenshot: "tray-menu-native", out var watch);

        Assert.True(watch.Appeared, "the menu never appeared on screen");
        Assert.True(watch.OpenAfterWait, $"the menu closed before {StaysOpenFor.TotalSeconds:0} s (SC-043)");
        Assert.True(watch.ClosedByKeys, "Escape did not close the menu");
        Assert.Equal(TrayMenuCommand.None, result);
        Assert.Empty(calls);
        Assert.Single(Lines(events, "tray icon: menu requested"));
        Assert.Single(Lines(events, "tray menu opened at 200,200"));
        var closed = Assert.Single(Lines(events, "tray menu closed after"));
        Assert.EndsWith(": dismissed", closed);
        Assert.True(ClosedAfterMs(closed) >= StaysOpenFor.TotalMilliseconds, closed);
        Assert.Empty(Lines(events, "tray menu: did not open"));
    }));

    /// <summary>SC-043: Up (the last item) and Enter pick Quit in the open menu: Quit's action runs once, nothing else.</summary>
    [Fact]
    public void Quit_in_the_menu_runs_the_Quit_action() => WithLog(events => Sta(() =>
    {
        var calls = new List<string>();
        var result = OpenThroughTheIcon(Recording(calls), new[] { VkUp, VkReturn }, screenshot: null, out var watch);

        Assert.True(watch.Appeared, "the menu never appeared on screen");
        Assert.True(watch.OpenAfterWait, $"the menu closed before {StaysOpenFor.TotalSeconds:0} s (SC-043)");
        Assert.True(watch.ClosedByKeys, "Up and Enter did not close the menu");
        Assert.Equal(TrayMenuCommand.Quit, result);
        Assert.Equal(new[] { "quit" }, calls);
        Assert.EndsWith(": Quit", Assert.Single(Lines(events, "tray menu closed after")));
    }));

    private static TrayMenuActions Recording(List<string> calls) =>
        new(() => calls.Add("open"), () => calls.Add("check"), () => calls.Add("settings"), () => calls.Add("quit"));

    private static TrayMenuCommand OpenThroughTheIcon(TrayMenuActions actions, int key, string? screenshot, out MenuWatch watch) =>
        OpenThroughTheIcon(actions, new[] { key }, screenshot, out watch);

    /// <summary>
    /// Sends the icon's WM_CONTEXTMENU at 200,200 with the tray's own handler wired (TrayHost.OpenMenu's call), while
    /// a watcher thread checks the menu stays open, captures it and presses <paramref name="keys"/> in it.
    /// </summary>
    private static TrayMenuCommand OpenThroughTheIcon(TrayMenuActions actions, int[] keys, string? screenshot, out MenuWatch watch)
    {
        var failures = new List<string>();
        using var icon = new TrayIcon(TrayAssets.IconFile(), "RST", failures.Add);
        var result = (TrayMenuCommand?)null;
        icon.MenuRequested += (_, p) => result = TrayMenu.Open(actions, p, icon.Source);

        // A last resort, so a menu that ignores the watcher cannot hang the run: EndMenu from the menu's own thread.
        var rescue = new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Normal, (s, _) =>
        {
            ((DispatcherTimer)s!).Stop();
            EndMenu();
        }, Dispatcher.CurrentDispatcher);

        var (uiThread, owner) = (GetCurrentThreadId(), icon.Handle);
        var watcher = Task.Run(() => Watch(uiThread, owner, keys, screenshot is not null));
        Send(icon, WmContextMenu, 200, 200); // returns when the menu has closed
        rescue.Stop();
        watch = watcher.GetAwaiter().GetResult();
        if (screenshot is not null && watch.Capture != IntPtr.Zero)
        {
            try
            {
                var bitmap = Imaging.CreateBitmapSourceFromHBitmap(watch.Capture, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                Screenshots.Save(bitmap, screenshot);
            }
            finally
            {
                DeleteObject(watch.Capture);
            }
        }
        Assert.Empty(failures);
        Assert.NotNull(result);
        return result!.Value;
    }

    private sealed record MenuWatch(bool Appeared, bool OpenAfterWait, bool ClosedByKeys, IntPtr Capture);

    private static MenuWatch Watch(uint uiThread, IntPtr owner, int[] keys, bool capture)
    {
        var clock = Stopwatch.StartNew();
        var menu = IntPtr.Zero;
        while (clock.Elapsed < TimeSpan.FromSeconds(10) && (menu = MenuWindowOf(uiThread)) == IntPtr.Zero) Thread.Sleep(20);
        if (menu == IntPtr.Zero)
        {
            PostMessageW(owner, WmCancelMode, IntPtr.Zero, IntPtr.Zero);
            return new MenuWatch(false, false, false, IntPtr.Zero);
        }

        Thread.Sleep(StaysOpenFor + TimeSpan.FromMilliseconds(100));
        var openAfterWait = IsWindow(menu) && IsWindowVisible(menu);
        var bitmap = capture && openAfterWait ? CaptureScreen(menu) : IntPtr.Zero;

        foreach (var key in keys)
        {
            PostMessageW(menu, WmKeyDown, new IntPtr(key), new IntPtr(1));
            PostMessageW(menu, WmKeyUp, new IntPtr(key), new IntPtr(unchecked((int)0xC0000001)));
            Thread.Sleep(100);
        }
        clock.Restart();
        while (clock.Elapsed < TimeSpan.FromSeconds(5) && IsWindow(menu) && IsWindowVisible(menu)) Thread.Sleep(20);
        var closedByKeys = !(IsWindow(menu) && IsWindowVisible(menu));
        // DefWindowProc's WM_CANCELMODE ends the menu's loop: the test thread is not left waiting.
        if (!closedByKeys) PostMessageW(owner, WmCancelMode, IntPtr.Zero, IntPtr.Zero);
        return new MenuWatch(true, openAfterWait, closedByKeys, bitmap);
    }

    /// <summary>The visible popup menu window (class #32768) owned by <paramref name="thread"/>, or zero.</summary>
    private static IntPtr MenuWindowOf(uint thread)
    {
        var found = IntPtr.Zero;
        var name = new StringBuilder(64);
        EnumWindowsProc each = (hwnd, lParam) =>
        {
            if (GetWindowThreadProcessId(hwnd, out _) != thread || !IsWindowVisible(hwnd)) return true;
            name.Clear();
            GetClassNameW(hwnd, name, name.Capacity);
            if (name.ToString() != "#32768") return true;
            found = hwnd;
            return false;
        };
        EnumWindows(each, IntPtr.Zero);
        GC.KeepAlive(each);
        return found;
    }

    /// <summary>The window's pixels as on screen (an HBITMAP the caller deletes), or zero.</summary>
    private static IntPtr CaptureScreen(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var r) || r.Right <= r.Left || r.Bottom <= r.Top) return IntPtr.Zero;
        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return IntPtr.Zero;
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, r.Right - r.Left, r.Bottom - r.Top);
        var previous = SelectObject(memory, bitmap);
        BitBlt(memory, 0, 0, r.Right - r.Left, r.Bottom - r.Top, screen, r.Left, r.Top, 0x00CC0020 /* SRCCOPY */ | 0x40000000 /* CAPTUREBLT */);
        SelectObject(memory, previous);
        DeleteDC(memory);
        ReleaseDC(IntPtr.Zero, screen);
        return bitmap;
    }

    private static double ClosedAfterMs(string line)
    {
        var digits = new string(line["tray menu closed after ".Length..].TakeWhile(char.IsDigit).ToArray());
        return double.Parse(digits, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Sta(Action action) => Screenshots.RunSta(() =>
    {
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            action();
        }
        finally
        {
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    });

    private static void WithLog(Action<List<LogEvent>> test)
    {
        var events = new List<LogEvent>();
        var previous = Log.Logger;
        Log.Logger = new Logger(LogLevel.Information, new ListSink(events));
        try
        {
            test(events);
        }
        finally
        {
            Log.Logger = previous;
        }
    }

    private static IntPtr MakeLong(int low, int high) => new((long)(uint)(((high & 0xFFFF) << 16) | (low & 0xFFFF)));

    private static void Send(TrayIcon icon, int notifyEvent, int x, int y) =>
        SendMessageW(icon.Handle, TrayIcon.CallbackMessage, MakeLong(x, y), MakeLong(notifyEvent, 1));

    private static List<string> Lines(List<LogEvent> events, string start)
    {
        lock (events) return events.Select(e => e.RenderMessage()).Where(m => m.StartsWith(start, StringComparison.Ordinal)).ToList();
    }

    private static string MenuText(IntPtr menu, int position)
    {
        var text = new StringBuilder(128);
        GetMenuStringW(menu, (uint)position, text, text.Capacity, MfByPosition);
        return text.ToString();
    }

    private sealed class ListSink(List<LogEvent> events) : ILogSink
    {
        public void Emit(LogEvent logEvent, IReadOnlyDictionary<string, object?> properties)
        {
            lock (events) events.Add(logEvent);
        }

        public void Dispose()
        {
        }
    }

    private const int WmCancelMode = 0x001F;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const uint MfByPosition = 0x0400;
    private const uint MfGrayed = 0x0001;
    private const uint MfSeparator = 0x0800;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageW(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int capacity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect32 rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndMenu();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern int GetMenuItemCount(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern uint GetMenuItemID(IntPtr menu, int position);

    [DllImport("user32.dll")]
    private static extern uint GetMenuState(IntPtr menu, uint item, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetMenuDefaultItem(IntPtr menu, uint byPosition, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMenuStringW(IntPtr menu, uint item, StringBuilder text, int max, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sx, int sy, uint rop);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr gdiObject);
}
