// rst-c's own (not vendored, no third-party code): the RST tray's notification-area icon (spec #20 §1; F17 WU1),
// a small wrapper over shell32's Shell_NotifyIconW written for rst-c, so the tray takes no new UI dependency
// (research Q7: the own wrapper over WinForms' NotifyIcon or H.NotifyIcon). Owned and maintained in this repo.
//
// It owns one hidden, ordinary top-level window (an HwndSource that is never shown). That window:
//   - receives the icon's callback messages (NOTIFYICON_VERSION_4: left click, keyboard select, context menu,
//     notification click);
//   - re-adds the icon when Explorer restarts (the registered "TaskbarCreated" message);
//   - is the window Windows Installer's util:CloseApplication sends WM_CLOSE to on uninstall and upgrade. It is
//     deliberately NOT a message-only (HWND_MESSAGE) window: EnumWindows, which CloseApplication walks, does not
//     list message-only windows;
//   - reports sign-out (WM_ENDSESSION), Windows theme or high-contrast changes (WM_SETTINGCHANGE), and waking
//     from sleep (WM_POWERBROADCAST), which the update checks use to catch up a slot missed while asleep.
// UI thread only.

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RST.Tray.Shell;

internal sealed class TrayIcon : IDisposable
{
    /// <summary>The hidden window's title, so a smoke test or a support person can find it.</summary>
    public const string WindowName = "RST.Tray";

    private const int IconId = 1;
    private const int CallbackMessage = 0x8000 + 1; // WM_APP + 1

    private const int WM_CLOSE = 0x0010;
    private const int WM_QUERYENDSESSION = 0x0011;
    private const int WM_ENDSESSION = 0x0016;
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int WM_POWERBROADCAST = 0x0218;
    private const int PBT_APMRESUMEAUTOMATIC = 0x0012;
    private const int NIN_SELECT = 0x0400;
    private const int NIN_KEYSELECT = 0x0401;
    private const int NIN_BALLOONUSERCLICK = 0x0405;
    private const int SPI_SETHIGHCONTRAST = 0x0043;

    private const int NIM_ADD = 0x0;
    private const int NIM_MODIFY = 0x1;
    private const int NIM_DELETE = 0x2;
    private const int NIM_SETVERSION = 0x4;
    private const int NIF_MESSAGE = 0x01;
    private const int NIF_ICON = 0x02;
    private const int NIF_TIP = 0x04;
    private const int NIF_INFO = 0x10;
    private const int NIF_SHOWTIP = 0x80;
    private const int NOTIFYICON_VERSION_4 = 4;
    private const int NIIF_INFO = 0x01;
    private const int NIIF_RESPECT_QUIET_TIME = 0x80;
    private const int WS_EX_TOOLWINDOW = 0x80;

    private readonly HwndSource _window;
    private readonly int _taskbarCreated;
    private readonly Action<string> _log;
    private IntPtr _icon;
    private string _tooltip;
    private bool _added;
    private bool _addFailureLogged;
    private bool _disposed;

    /// <param name="iconFile">An .ico file's bytes (RST.msi's rst.ico, embedded in the tray).</param>
    /// <param name="tooltip">The tooltip (at most 127 characters are shown).</param>
    /// <param name="log">Where failures are reported; nothing here throws after construction.</param>
    public TrayIcon(byte[] iconFile, string tooltip, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(iconFile);
        _tooltip = tooltip ?? "";
        _log = log ?? (_ => { });
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        _window = new HwndSource(new HwndSourceParameters(WindowName)
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0, // WS_OVERLAPPED without WS_VISIBLE: a real top-level window that is never shown.
            ExtendedWindowStyle = WS_EX_TOOLWINDOW,
        });
        _window.AddHook(WndProc);
        AllowFromShell(_window.Handle, _taskbarCreated, log: _log);
        AllowFromShell(_window.Handle, CallbackMessage, log: _log);
        _icon = LoadIcon(iconFile, GetSystemMetrics(49 /* SM_CXSMICON */), GetSystemMetrics(50 /* SM_CYSMICON */));
    }

    /// <summary>Left click, or Enter/Space on the focused icon.</summary>
    public event EventHandler? Activated;

    /// <summary>Right click, Shift+F10 or the menu key: where to open the menu, in screen pixels.</summary>
    public event EventHandler<Point>? MenuRequested;

    /// <summary>The user clicked a notification shown by <see cref="ShowNotification"/>.</summary>
    public event EventHandler? NotificationClicked;

    /// <summary>WM_CLOSE reached the hidden window (Windows Installer's CloseApplication on uninstall or upgrade).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Windows is signing out or shutting down (WM_ENDSESSION).</summary>
    public event EventHandler? SessionEnding;

    /// <summary>The Windows app theme or high contrast changed.</summary>
    public event EventHandler? ThemeChanged;

    /// <summary>The PC woke from sleep or hibernation (WM_POWERBROADCAST, PBT_APMRESUMEAUTOMATIC; F17 WU3's check slots).</summary>
    public event EventHandler? Resumed;

    /// <summary>The hidden window's handle (the menu's foreground anchor).</summary>
    public IntPtr Handle => _window.Handle;

    /// <summary>The hidden window, for converting the menu point from pixels to WPF units.</summary>
    public HwndSource Source => _window;

    /// <summary>Whether the icon is in the notification area now.</summary>
    public bool IsShown => _added;

    /// <summary>Adds the icon. False (logged once) when the shell refuses, e.g. no Explorer; it is retried when Explorer starts.</summary>
    public bool Show()
    {
        if (_disposed) return false;
        var data = NewData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        if (!Shell_NotifyIconW(NIM_ADD, ref data) && !Shell_NotifyIconW(NIM_MODIFY, ref data))
        {
            _added = false;
            if (!_addFailureLogged)
            {
                _addFailureLogged = true;
                _log($"tray icon: Shell_NotifyIcon add failed (error {Marshal.GetLastWin32Error()}); retried when Explorer starts");
            }
            return false;
        }
        data.uVersion = NOTIFYICON_VERSION_4;
        if (!Shell_NotifyIconW(NIM_SETVERSION, ref data))
            _log($"tray icon: NOTIFYICON_VERSION_4 refused (error {Marshal.GetLastWin32Error()})");
        _added = true;
        _addFailureLogged = false;
        return true;
    }

    /// <summary>Changes the tooltip.</summary>
    public void SetTooltip(string tooltip)
    {
        _tooltip = tooltip ?? "";
        if (!_added) return;
        var data = NewData(NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    /// <summary>
    /// Shows a notification from the icon (a toast on Windows 10 and 11), respecting quiet hours and Focus assist.
    /// False when the icon is not shown or the shell refused. For WU3's "RST 3.1.1 is available".
    /// </summary>
    public bool ShowNotification(string title, string text)
    {
        if (!_added) return false;
        var data = NewData(NIF_INFO);
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(text, 255);
        data.dwInfoFlags = NIIF_INFO | NIIF_RESPECT_QUIET_TIME;
        if (Shell_NotifyIconW(NIM_MODIFY, ref data)) return true;
        _log($"tray icon: notification refused (error {Marshal.GetLastWin32Error()})");
        return false;
    }

    /// <summary>Removes the icon from the notification area (idempotent).</summary>
    public void Hide()
    {
        if (!_added) return;
        var data = NewData(0);
        Shell_NotifyIconW(NIM_DELETE, ref data);
        _added = false;
    }

    /// <summary>Removes the icon and releases the hidden window and the icon handle. Never throws (it runs at exit).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            Hide();
            if (!_window.IsDisposed)
            {
                _window.RemoveHook(WndProc);
                _window.Dispose();
            }
        }
        catch (Exception ex)
        {
            // The dispatcher may already have shut the window down at exit.
            _log("tray icon: dispose: " + ex.Message);
        }
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        _icon = IntPtr.Zero;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            // NOTIFYICON_VERSION_4: the event in LOWORD(lParam), the anchor point in wParam.
            switch ((int)(lParam.ToInt64() & 0xFFFF))
            {
                case NIN_SELECT:
                case NIN_KEYSELECT:
                    Activated?.Invoke(this, EventArgs.Empty);
                    break;
                case WM_CONTEXTMENU:
                    var w = wParam.ToInt64();
                    MenuRequested?.Invoke(this, new Point((short)(w & 0xFFFF), (short)((w >> 16) & 0xFFFF)));
                    break;
                case NIN_BALLOONUSERCLICK:
                    NotificationClicked?.Invoke(this, EventArgs.Empty);
                    break;
            }
            handled = true;
            return IntPtr.Zero;
        }

        if (_taskbarCreated != 0 && msg == _taskbarCreated)
        {
            // Explorer (re)started: every notification icon is gone; put ours back.
            _added = false;
            Show();
            return IntPtr.Zero;
        }

        switch (msg)
        {
            case WM_CLOSE:
                handled = true;
                CloseRequested?.Invoke(this, EventArgs.Empty);
                return IntPtr.Zero;
            case WM_QUERYENDSESSION:
                handled = true;
                return new IntPtr(1);
            case WM_ENDSESSION:
                handled = true;
                if (wParam != IntPtr.Zero) SessionEnding?.Invoke(this, EventArgs.Empty);
                return IntPtr.Zero;
            case WM_SETTINGCHANGE:
                if (wParam.ToInt64() == SPI_SETHIGHCONTRAST || IsImmersiveColorSet(lParam))
                    ThemeChanged?.Invoke(this, EventArgs.Empty);
                break;
            case WM_POWERBROADCAST:
                if (wParam.ToInt64() == PBT_APMRESUMEAUTOMATIC) Resumed?.Invoke(this, EventArgs.Empty);
                break;
        }
        return IntPtr.Zero;
    }

    private static bool IsImmersiveColorSet(IntPtr lParam)
    {
        if (lParam == IntPtr.Zero) return false;
        try
        {
            return string.Equals(Marshal.PtrToStringUni(lParam), "ImmersiveColorSet", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Lets Explorer's messages through User Interface Privilege Isolation when the tray runs elevated (e.g. started
    /// again by Windows Installer after an install run from an elevated prompt): "TaskbarCreated" after an Explorer
    /// restart, and the icon's own callback message. A no-op for a normal tray.
    /// </summary>
    private static void AllowFromShell(IntPtr hwnd, int message, Action<string> log)
    {
        if (message == 0) return;
        try
        {
            if (!ChangeWindowMessageFilterEx(hwnd, (uint)message, 1 /* MSGFLT_ALLOW */, IntPtr.Zero))
                log($"tray icon: message filter for 0x{message:X} not changed (error {Marshal.GetLastWin32Error()})");
        }
        catch (Exception ex)
        {
            log("tray icon: message filter: " + ex.Message);
        }
    }

    private NotifyIconData NewData(int flags) => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(),
        hWnd = _window.Handle,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = Truncate(_tooltip, 127),
        szInfo = "",
        szInfoTitle = "",
    };

    private static string Truncate(string? text, int max)
    {
        text ??= "";
        return text.Length <= max ? text : text[..max];
    }

    /// <summary>
    /// The .ico entry that best fits <paramref name="cx"/> x <paramref name="cy"/> (the smallest at least that
    /// size, else the largest), turned into an HICON scaled to that size. Zero when the file is unusable.
    /// </summary>
    internal static IntPtr LoadIcon(byte[] ico, int cx, int cy)
    {
        if (ico.Length < 6 || BitConverter.ToUInt16(ico, 2) != 1) return IntPtr.Zero;
        var count = BitConverter.ToUInt16(ico, 4);
        int bestOffset = -1, bestLength = 0, bestSize = 0;
        for (var i = 0; i < count; i++)
        {
            var entry = 6 + 16 * i;
            if (entry + 16 > ico.Length) break;
            var size = ico[entry] == 0 ? 256 : ico[entry];
            var length = BitConverter.ToInt32(ico, entry + 8);
            var offset = BitConverter.ToInt32(ico, entry + 12);
            if (offset < 0 || length <= 0 || offset + length > ico.Length) continue;
            var better = bestOffset < 0
                || (size >= cx && (bestSize < cx || size < bestSize))
                || (bestSize < cx && size > bestSize);
            if (!better) continue;
            bestOffset = offset;
            bestLength = length;
            bestSize = size;
        }
        if (bestOffset < 0) return IntPtr.Zero;
        var image = ico.AsSpan(bestOffset, bestLength).ToArray();
        return CreateIconFromResourceEx(image, (uint)image.Length, true, 0x00030000, cx, cy, 0);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(int message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegisterWindowMessageW(string name);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr changeFilterStruct);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconFromResourceEx(byte[] bits, uint size, [MarshalAs(UnmanagedType.Bool)] bool icon,
                                                          uint version, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
