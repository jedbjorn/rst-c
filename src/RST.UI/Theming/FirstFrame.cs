// rst-c's own (not vendored). SC-011: in the dark theme, a window must not show a white client area before WPF
// draws it. Until WPF's first frame reaches the screen, Windows shows a new window's client area white, whatever
// the window's Background. A dark window is therefore cloaked (DWM's DWMWA_CLOAK: laid out and drawn as usual,
// but not shown) from when its handle exists, before it is first shown, until its first frame is drawn
// (ContentRendered), so the first thing on screen is the themed window. A fallback timer uncloaks it anyway, so
// a window can never stay invisible. The light theme needs none of this (the white matches) and is left alone.

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using RST.Core.Logging;

namespace RST.UI.Theming;

/// <summary>Keeps a dark window off the screen until its first frame is drawn (SC-011). Best effort.</summary>
public static class FirstFrame
{
    /// <summary>DWMWA_CLOAK: the window is cloaked (TRUE) or not.</summary>
    private const int DwmwaCloak = 13;

    /// <summary>DWMWA_CLOAKED: why the window is cloaked (0: it is not).</summary>
    private const int DwmwaCloaked = 14;

    /// <summary>A window not drawn by then is shown anyway.</summary>
    internal static readonly TimeSpan Fallback = TimeSpan.FromSeconds(2);

    /// <summary>Call from the window's constructor, before it is shown. Does nothing in the light theme.</summary>
    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!RstTheme.IsDark) return;

        DispatcherTimer? fallback = null;
        var hwnd = IntPtr.Zero;

        void Uncloak()
        {
            fallback?.Stop();
            fallback = null;
            if (hwnd == IntPtr.Zero) return;
            Set(hwnd, false);
            hwnd = IntPtr.Zero;
        }

        window.SourceInitialized += (_, _) =>
        {
            try
            {
                hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero || !Set(hwnd, true))
                {
                    hwnd = IntPtr.Zero;
                    return;
                }
                fallback = new DispatcherTimer(Fallback, DispatcherPriority.Normal, (_, _) =>
                {
                    Log.Warning("{Window}: not drawn {Seconds} s after it was created; showing it anyway",
                        window.GetType().Name, Fallback.TotalSeconds);
                    Uncloak();
                }, window.Dispatcher);
            }
            catch (Exception ex)
            {
                // Never leave a window cloaked because something here failed.
                Log.Warning(ex, "{Window}: cloaking until the first frame failed", window.GetType().Name);
                Uncloak();
            }
        };
        window.ContentRendered += (_, _) => Uncloak();
        window.Closed += (_, _) => Uncloak();
    }

    /// <summary>The window is cloaked now (for any reason). False when DWM cannot say.</summary>
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
