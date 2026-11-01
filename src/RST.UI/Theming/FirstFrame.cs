// rst-c's own (not vendored). SC-011: in the dark theme, a window must not show a white client area before WPF
// draws it. Until WPF's first frame reaches the screen, Windows shows a new window's client area white, whatever
// the window's Background. A dark window is therefore cloaked (DWM's DWMWA_CLOAK: laid out and drawn as usual,
// but not shown) from when its handle exists, before it is first shown, until its first frame is drawn
// (ContentRendered), so the first thing on screen is the themed window. The light theme needs none of this (the
// white matches) and is left alone.
//
// The fallback must not need the UI thread: when the cold first layout is what keeps the frame from coming (the
// SC-011 case, 6-11 s), the dispatcher is busy for exactly that long, so a DispatcherTimer would not fire either
// and the modal window would stay invisible (PR #187 review F1). The watchdog is a thread-pool timer that uncloaks
// the native handle from its own thread (DwmSetWindowAttribute works from any thread of the process). The first
// of the frame, the window closing and the watchdog wins, under a lock; the others then do nothing, so the
// watchdog never touches a handle after the window has closed.

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using RST.Core.Logging;

namespace RST.UI.Theming;

/// <summary>Keeps a dark window off the screen until its first frame is drawn (SC-011). Best effort.</summary>
public static class FirstFrame
{
    /// <summary>DWMWA_CLOAK: the window is cloaked (TRUE) or not.</summary>
    private const int DwmwaCloak = 13;

    /// <summary>DWMWA_CLOAKED: why the window is cloaked (0: it is not).</summary>
    private const int DwmwaCloaked = 14;

    /// <summary>A window not drawn by then is shown anyway, even while its UI thread is still busy.</summary>
    internal static readonly TimeSpan Fallback = TimeSpan.FromSeconds(2);

    /// <summary>Call from the window's constructor, before it is shown. Does nothing in the light theme.</summary>
    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!RstTheme.IsDark) return;

        var cloak = new Cloak(window.GetType().Name);
        window.SourceInitialized += (_, _) => cloak.Start(new WindowInteropHelper(window).Handle);
        window.ContentRendered += (_, _) => cloak.End(fromWatchdog: false);
        // Closed is raised while the handle is being destroyed: ending here keeps the watchdog off a dead handle.
        window.Closed += (_, _) => cloak.End(fromWatchdog: false);
    }

    /// <summary>One window's cloak: on from its handle's creation until the first frame, its close or the watchdog.</summary>
    private sealed class Cloak(string name)
    {
        private readonly object _gate = new();
        private IntPtr _hwnd;
        private Timer? _watchdog;
        private bool _ended;

        public void Start(IntPtr hwnd)
        {
            lock (_gate)
            {
                if (_ended || hwnd == IntPtr.Zero) return;
                try
                {
                    if (!Set(hwnd, true)) return;
                    _hwnd = hwnd;
                    _watchdog = new Timer(_ => End(fromWatchdog: true), null, Fallback, Timeout.InfiniteTimeSpan);
                }
                catch (Exception ex)
                {
                    // Never leave a window cloaked because something here failed.
                    Log.Warning(ex, "{Window}: cloaking until the first frame failed", name);
                    Set(hwnd, false);
                    _hwnd = IntPtr.Zero;
                }
            }
        }

        public void End(bool fromWatchdog)
        {
            IntPtr hwnd;
            lock (_gate)
            {
                if (_ended) return;
                _ended = true;
                _watchdog?.Dispose();
                _watchdog = null;
                hwnd = _hwnd;
                _hwnd = IntPtr.Zero;
                // Uncloaked inside the lock, so a close that wins the race has already stopped any later use.
                if (hwnd != IntPtr.Zero) Set(hwnd, false);
            }
            if (fromWatchdog && hwnd != IntPtr.Zero)
                Log.Warning("{Window}: not drawn {Seconds} s after it was created; shown anyway", name, Fallback.TotalSeconds);
        }
    }

    /// <summary>The window is cloaked now (for any reason). False when DWM cannot say. Any thread.</summary>
    internal static bool IsCloaked(IntPtr hwnd)
    {
        try
        {
            return hwnd != IntPtr.Zero && DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var reason, sizeof(int)) == 0 && reason != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool Set(IntPtr hwnd, bool cloaked)
    {
        try
        {
            var value = cloaked ? 1 : 0;
            return DwmSetWindowAttribute(hwnd, DwmwaCloak, ref value, sizeof(int)) == 0;
        }
        catch (Exception)
        {
            // dwmapi missing or the call unsupported: the window shows as it always has.
            return false;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
