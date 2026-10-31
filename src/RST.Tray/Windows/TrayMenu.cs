// rst-c's own (not vendored): the tray icon's menu (spec #20 §1; F17 WU1): Open RST, Check for updates (checks
// now and opens the Updates page, F17 WU3), Settings, Quit.
//
// SC-043: a native Win32 popup menu (CreatePopupMenu + TrackPopupMenuEx), the documented notification-icon pattern.
// It replaced a WPF ContextMenu in the family look, which opened and closed again within tens of milliseconds on
// real desktops: a WPF popup is a no-activate window with no owner here, so it never got the foreground (the field
// log said "menu False"), and a StaysOpen=false popup closes as soon as the app loses activation or mouse capture.
// The native menu runs its own modal loop and stays open until the user picks an item or dismisses it (a click
// elsewhere or Escape), whatever the activation. The rules it follows (Shell_NotifyIcon's documentation, KB135788):
// the owner window takes the foreground first, so a click elsewhere dismisses the menu, and WM_NULL is posted to
// it afterwards. The look is Windows' own menu, not the family palette (the trade-off SC-043 accepted).

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using RST.Core.Logging;
using RST.Core.Tray;
using RST.Tray.Resources;

namespace RST.Tray.Windows;

/// <summary>What the menu's items do.</summary>
internal sealed record TrayMenuActions(Action Open, Action CheckForUpdates, Action Settings, Action Quit)
{
    /// <summary>Check for updates is enabled (F17 WU3); false shows it disabled.</summary>
    public bool CanCheckForUpdates { get; init; } = true;
}

internal static class TrayMenu
{
    private const uint MF_STRING = 0x0000;
    private const uint MF_GRAYED = 0x0001;
    private const uint MF_SEPARATOR = 0x0800;
    private const uint TPM_LEFTALIGN = 0x0000;
    private const uint TPM_RIGHTALIGN = 0x0008;
    private const uint TPM_TOPALIGN = 0x0000;
    private const uint TPM_BOTTOMALIGN = 0x0020;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;
    private const int WM_NULL = 0x0000;
    private const int WM_INITMENUPOPUP = 0x0117;
    private const int WM_ENTERMENULOOP = 0x0211;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    private static bool _open;
    private static bool _closedByTray;
    private static readonly List<Action> AfterClose = new();

    /// <summary>The menu's entries, in the tray's strings.</summary>
    public static IReadOnlyList<TrayMenuEntry> Entries(TrayMenuActions actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        return TrayMenuModel.Entries(
            new TrayMenuTexts(TrayStrings.Menu_Open, TrayStrings.Menu_CheckForUpdates, TrayStrings.Menu_Settings, TrayStrings.Menu_Quit),
            actions.CanCheckForUpdates);
    }

    /// <summary>Whether the menu's loop is running now (UI thread).</summary>
    public static bool IsOpen => _open;

    /// <summary>
    /// SC-043 review N1: ends an open menu (EndMenu) so a quit that arrives while it is open (WM_CLOSE from Windows
    /// Installer, a self-update, sign-out) does not run inside the menu's loop, which runs inside the icon window's
    /// WM_CONTEXTMENU. False when no menu is open: the caller goes ahead now. True when one was: the menu closes as
    /// soon as the current message returns, and <paramref name="then"/>, if given, is queued on the dispatcher after
    /// TrackPopupMenuEx has returned. UI thread only.
    /// </summary>
    public static bool Close(Action? then = null)
    {
        if (!_open) return false;
        if (!_closedByTray) Log.Information("tray menu: closed by the tray");
        _closedByTray = true;
        if (then is not null) AfterClose.Add(then);
        EndMenu();
        return true;
    }

    /// <summary>
    /// Shows the menu at <paramref name="screenPixels"/> (the icon's anchor), owned by <paramref name="owner"/> (the
    /// icon's hidden window), and returns once the user picked an item or dismissed it, after running the item's
    /// action. Call it while handling the icon's WM_CONTEXTMENU, when Explorer lets this process take the foreground.
    /// UI thread only; a request while the menu is already open is ignored.
    /// </summary>
    public static TrayMenuCommand Open(TrayMenuActions actions, Point screenPixels, HwndSource owner)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(owner);
        if (_open)
        {
            Log.Information("tray menu: already open; request ignored");
            return TrayMenuCommand.None;
        }

        var entries = Entries(actions);
        var menu = Create(entries);
        if (menu == IntPtr.Zero) return TrayMenuCommand.None;

        var x = (int)Math.Round(screenPixels.X);
        var y = (int)Math.Round(screenPixels.Y);
        var ownerForeground = false;
        var opened = false;
        var shown = new Stopwatch();
        HwndSourceHook hook = (IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            // The menu's own loop is running: it is on screen. SC-020's line, so a field log tells a menu that opened
            // from one that never did.
            if ((msg == WM_ENTERMENULOOP || msg == WM_INITMENUPOPUP) && !opened)
            {
                opened = true;
                shown.Restart();
                Log.Information("tray menu opened at {X},{Y} (foreground: owner {Owner})", x, y, ownerForeground);
            }
            return IntPtr.Zero;
        };

        int command;
        long openMs;
        var previousForeground = IntPtr.Zero;
        try
        {
            _open = true;
            _closedByTray = false;
            owner.AddHook(hook);
            // The notification-menu rule: the owner takes the foreground first, so a click anywhere else dismisses
            // the menu. Explorer lets the icon's process do this while it handles the icon's message.
            previousForeground = GetForegroundWindow();
            ownerForeground = SetForegroundWindow(owner.Handle);
            shown.Start();
            command = TrackPopupMenuEx(menu, Alignment(x, y) | TPM_RIGHTBUTTON | TPM_RETURNCMD, x, y, owner.Handle, IntPtr.Zero);
            openMs = shown.ElapsedMilliseconds;
            var error = command == 0 ? Marshal.GetLastWin32Error() : 0;
            if (!opened)
                Log.Warning("tray menu: did not open at {X},{Y} (foreground: owner {Owner}; error {Error})", x, y, ownerForeground, error);
            // ...and WM_NULL afterwards, so the owner's next menu works (KB135788).
            PostMessageW(owner.Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            owner.RemoveHook(hook);
            DestroyMenu(menu);
            _open = false;
        }

        var chosen = TrayMenuModel.Chosen(entries, command);
        Log.Information("tray menu closed after {Ms} ms: {Choice}", openMs,
                        chosen != TrayMenuCommand.None ? chosen.ToString() : _closedByTray ? "closed by the tray" : "dismissed");
        // Review N4: dismissed with Escape, the foreground is still on the hidden window, where keys go nowhere; give
        // it back to the window that had it (the taskbar, for a keyboard user on the icon). A click elsewhere has
        // already moved it, and an item opens a window or quits.
        if (chosen == TrayMenuCommand.None && previousForeground != IntPtr.Zero && previousForeground != owner.Handle
            && GetForegroundWindow() == owner.Handle)
            SetForegroundWindow(previousForeground);
        if (AfterClose.Count > 0)
        {
            var queued = AfterClose.ToArray();
            AfterClose.Clear();
            foreach (var action in queued) Dispatcher.CurrentDispatcher.BeginInvoke(action);
        }
        Run(actions, chosen);
        return chosen;
    }

    /// <summary>
    /// The native menu for <paramref name="entries"/> (the strings' access keys as Win32's &amp;), Open shown bold as the
    /// default; zero (logged) on failure. The caller destroys it.
    /// </summary>
    internal static IntPtr Create(IReadOnlyList<TrayMenuEntry> entries)
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            Log.Warning("tray menu: CreatePopupMenu failed (error {Error})", Marshal.GetLastWin32Error());
            return IntPtr.Zero;
        }
        foreach (var entry in entries)
        {
            var ok = entry.IsSeparator
                ? AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null)
                : AppendMenuW(menu, MF_STRING | (entry.Enabled ? 0u : MF_GRAYED), (UIntPtr)(uint)entry.Command, TrayMenuModel.Win32Text(entry.Text));
            if (!ok) Log.Warning("tray menu: AppendMenu {Item} failed (error {Error})", entry.Command, Marshal.GetLastWin32Error());
            if (entry.IsDefault) SetMenuDefaultItem(menu, (uint)entry.Command, 0);
        }
        return menu;
    }

    /// <summary>Runs the action of <paramref name="command"/>; nothing for None.</summary>
    internal static void Run(TrayMenuActions actions, TrayMenuCommand command)
    {
        switch (command)
        {
            case TrayMenuCommand.Open: actions.Open(); break;
            case TrayMenuCommand.CheckForUpdates: actions.CheckForUpdates(); break;
            case TrayMenuCommand.Settings: actions.Settings(); break;
            case TrayMenuCommand.Quit: actions.Quit(); break;
        }
    }

    /// <summary>
    /// Opens the menu away from the nearest screen edge: above the anchor in the lower half of its monitor (a bottom
    /// taskbar), left of it in the right half, so it stays beside the notification area wherever the taskbar is.
    /// </summary>
    private static uint Alignment(int x, int y)
    {
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        var monitor = MonitorFromPoint(new Point32 { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero || !GetMonitorInfoW(monitor, ref info)) return TPM_RIGHTALIGN | TPM_BOTTOMALIGN;
        var horizontal = x > (info.rcMonitor.Left + info.rcMonitor.Right) / 2 ? TPM_RIGHTALIGN : TPM_LEFTALIGN;
        var vertical = y > (info.rcMonitor.Top + info.rcMonitor.Bottom) / 2 ? TPM_BOTTOMALIGN : TPM_TOPALIGN;
        return horizontal | vertical;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Rect32 rcMonitor;
        public Rect32 rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? text);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPosition);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpmParams);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndMenu();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point32 point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);
}
