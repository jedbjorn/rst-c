// Copied from splash-it src/SplashIt.UI/Swatches/ScreenColorPicker.cs (splash-it commit ba86a71; originally gantt-it src/GanttIt.Revit/UI/Swatches/ScreenColorPicker.cs, gantt-it commit a96d0a1), MIT, same owner.
// Changes: namespaces (SplashIt.* to RST.*); comments say RST; HideSplashItWindows is named HideRstWindows; the two Trace.TraceWarning calls are Log.Warning (rst-c's logger, RST.Core.Logging).

using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RST.Core.Colors;
using RST.Core.Picking;
using RST.UI.Resources;
using RST.Core.Logging;

namespace RST.UI.Swatches;

/// <summary>
/// "Pick from screen" (the eyedropper): picks the color of any pixel on any monitor.
/// <list type="number">
/// <item>One overlay window per monitor is created off-screen; the first is activated so
/// RST keeps the foreground while its own windows are out of the way.</item>
/// <item>The color picker and every RST window that owns it are hidden at the Win32
/// level (see <see cref="NativeScreen.Hide"/>) with the DWM animation off.</item>
/// <item>The whole virtual screen is captured once, in physical pixels, and each overlay
/// shows its monitor's part full screen: snapshot-then-pick, so the magnifier and the
/// result both come from the same frozen bitmap.</item>
/// <item>Left click picks the pixel under the pointer's hotspot; Esc, right click, Alt+F4
/// or switching to another app cancels, also while the screen is still being captured.
/// Arrow keys move the pointer one pixel (Shift: ten), Enter or Space picks.</item>
/// </list>
/// Every path, exceptions included, closes the overlays and shows the hidden windows
/// again where they were, with the color picker active. Clicks and keys that trail the
/// pick (a double click's second click, a held Enter or Esc) are swallowed so they don't
/// land on the restored windows.
/// </summary>
internal sealed class ScreenColorPicker
{
    private static readonly TimeSpan HideSettle = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan TrailingInput = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan HeldKeyLimit = TimeSpan.FromSeconds(5);

    private readonly Window _popup;
    private readonly List<Window> _hidden = [];
    private readonly List<ScreenPickOverlay> _overlays = [];
    private ScreenSnapshot? _snapshot;
    private DispatcherFrame? _frame;
    private bool _finished;
    private (int X, int Y)? _pressedAt;
    private Key? _finishKey;
    private Rgb? _result;
    private Exception? _error;

    private ScreenColorPicker(Window popup)
    {
        _popup = popup;
    }

    /// <summary>
    /// Runs a pick over everything on screen. <paramref name="popup"/> is the color picker;
    /// it and its owner windows are hidden meanwhile. Null when cancelled; throws when the
    /// screen couldn't be read (the windows are back by then).
    /// </summary>
    public static Rgb? Pick(Window popup) => new ScreenColorPicker(popup).Run();

    private Rgb? Run()
    {
        var dispatcher = _popup.Dispatcher;
        dispatcher.UnhandledExceptionFilter += OnUnhandledException;
        try
        {
            List<PixelRect> monitors;
            using (NativeScreen.PerMonitorDpi())
            {
                monitors = NativeScreen.Monitors();
                if (monitors.Count == 0) throw new InvalidOperationException(UiStrings.Pick_NoMonitors);
                foreach (var monitor in monitors)
                {
                    var overlay = new ScreenPickOverlay(monitor);
                    overlay.KeyDown += OnKeyDown;
                    overlay.MouseMove += (_, _) => Guard(Track);
                    overlay.MouseLeftButtonDown += OnLeftDown;
                    overlay.MouseLeftButtonUp += OnLeftUp;
                    overlay.MouseRightButtonDown += (_, e) => e.Handled = true;
                    overlay.MouseRightButtonUp += (_, e) =>
                    {
                        e.Handled = true;
                        Finish(null);
                    };
                    overlay.Deactivated += (_, _) => dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(CancelIfInactive));
                    overlay.Closed += (_, _) => Finish(null); // closed from outside: cancel
                    _overlays.Add(overlay);
                    overlay.Show(); // off-screen and tiny until Cover()
                }
            }
            _overlays[0].Activate();

            HideRstWindows();
            Wait(HideSettle);
            // Esc or Alt+Tab while the windows were going away (the first overlay has the keyboard).
            if (_finished) return _result;
            NativeScreen.FlushCompositor();
            NativeScreen.FlushCompositor();

            using (NativeScreen.PerMonitorDpi())
            {
                _snapshot = NativeScreen.Capture(PixelRect.Union(monitors));
                var image = BitmapSource.Create(_snapshot.Bounds.Width, _snapshot.Bounds.Height, 96, 96,
                    PixelFormats.Bgr32, null, _snapshot.Pixels, _snapshot.Stride);
                image.Freeze();
                foreach (var overlay in _overlays)
                {
                    var m = overlay.Monitor;
                    var part = new CroppedBitmap(image,
                        new Int32Rect(m.Left - _snapshot.Bounds.Left, m.Top - _snapshot.Bounds.Top, m.Width, m.Height));
                    part.Freeze();
                    overlay.ShowScreenshot(part);
                    overlay.Cover();
                }
            }

            ActivateOverlayUnderPointer();
            Track();

            // Never leave topmost overlays up without the keyboard: if another app holds the
            // foreground now, Esc couldn't reach them and the next click would pick by accident.
            if (!_finished && !_overlays.Any(o => o.IsActive)) Finish(null);
            if (!_finished)
            {
                _frame = new DispatcherFrame();
                Dispatcher.PushFrame(_frame);
            }
            if (_error is not null) throw new InvalidOperationException(_error.Message, _error);
            return _result;
        }
        finally
        {
            dispatcher.UnhandledExceptionFilter -= OnUnhandledException;
            Restore();
            SwallowTrailingInput(_finishKey);
        }
    }

    // ---- input ---------------------------------------------------------------------------

    /// <summary>Moves the magnifier to the pointer, on whichever monitor it is.</summary>
    private void Track()
    {
        if (_snapshot is null || _finished) return;
        var (x, y) = Pointer();
        var index = PixelRect.IndexAt(_overlays.Select(o => o.Monitor).ToList(), x, y);
        for (var i = 0; i < _overlays.Count; i++)
        {
            if (i == index && _overlays[i].Monitor.Contains(x, y)) _overlays[i].ShowLoupe(_snapshot, x, y);
            else _overlays[i].HideLoupe();
        }
    }

    private void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Guard(() =>
        {
            if (_snapshot is null) return; // still capturing
            _pressedAt = Pointer();
            ((UIElement)sender).CaptureMouse();
        });
    }

    private void OnLeftUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Guard(() =>
        {
            ((UIElement)sender).ReleaseMouseCapture();
            if (_pressedAt is not { } at) return; // the press started before the overlay was up
            PickAt(at.X, at.Y);
        });
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.System)
        {
            // Alt+F4 cancels; other Alt combinations are left alone.
            if (e.SystemKey != Key.F4) return;
            e.Handled = true;
            _finishKey = Key.F4;
            Finish(null);
            return;
        }

        e.Handled = true;
        if (e.IsRepeat && e.Key is Key.Enter or Key.Space or Key.Escape) return;
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        Guard(() =>
        {
            switch (e.Key)
            {
                case Key.Escape:
                    _finishKey = e.Key;
                    Finish(null);
                    break;
                case Key.Enter:
                case Key.Space:
                    if (_snapshot is null) break; // still capturing
                    _finishKey = e.Key;
                    var (x, y) = Pointer();
                    PickAt(x, y);
                    break;
                case Key.Left: Nudge(-step, 0); break;
                case Key.Right: Nudge(step, 0); break;
                case Key.Up: Nudge(0, -step); break;
                case Key.Down: Nudge(0, step); break;
            }
        });
    }

    private void Nudge(int dx, int dy)
    {
        if (_snapshot is null) return;
        var (x, y) = Pointer();
        var (nx, ny) = _snapshot.Bounds.Clamp(x + dx, y + dy);
        using (NativeScreen.PerMonitorDpi()) NativeScreen.SetCursorPosition(nx, ny);
        Track();
    }

    private void PickAt(int x, int y)
    {
        // The pointer can sit in a gap between monitors of different sizes: nothing to pick there.
        if (_snapshot?.ColorAt(x, y) is { } color) Finish(color);
    }

    /// <summary>
    /// Another app took the foreground (Alt+Tab, Win key): cancel instead of leaving overlays
    /// up. Also during the capture, so the pick never starts in the background.
    /// </summary>
    private void CancelIfInactive()
    {
        if (_finished || _overlays.Any(o => o.IsActive)) return;
        Finish(null);
    }

    /// <summary>Ends the pick once; a cancel before the overlays are up is kept and honoured.</summary>
    private void Finish(Rgb? color)
    {
        if (_finished) return;
        _finished = true;
        _result = color;
        if (_frame is not null) _frame.Continue = false;
        _frame = null;
    }

    /// <summary>Input handlers run inside Revit: any failure ends the pick and is reported by the caller.</summary>
    private void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _error ??= ex;
            Finish(null);
        }
    }

    /// <summary>
    /// Any other unhandled exception on the UI thread while picking: end the pick and put
    /// the windows back first, so the RST window's error dialog doesn't open hidden
    /// under the topmost overlays. The dialog itself still reports the error.
    /// </summary>
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionFilterEventArgs e)
    {
        Finish(null);
        Restore();
    }

    private static (int X, int Y) Pointer()
    {
        using (NativeScreen.PerMonitorDpi()) return NativeScreen.CursorPosition();
    }

    private void ActivateOverlayUnderPointer()
    {
        var (x, y) = Pointer();
        var index = PixelRect.IndexAt(_overlays.Select(o => o.Monitor).ToList(), x, y);
        var overlay = _overlays[Math.Max(0, index)];
        overlay.Activate();
        overlay.Focus();
    }

    // ---- hiding and restoring the RST windows ---------------------------------------

    /// <summary>The color picker first, then each window that owns it (the RST window).</summary>
    private void HideRstWindows()
    {
        for (var w = _popup; w is not null; w = w.Owner)
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) continue;
            NativeScreen.SetTransitionsDisabled(hwnd, true);
            NativeScreen.Hide(hwnd);
            _hidden.Add(w);
        }
    }

    /// <summary>
    /// Never throws, safe to call twice: shows the hidden windows (owners first), closes
    /// the overlays, activates the picker.
    /// </summary>
    private void Restore()
    {
        if (_frame is not null) _frame.Continue = false;
        _frame = null;
        for (var i = _hidden.Count - 1; i >= 0; i--)
        {
            var window = _hidden[i];
            Try(() =>
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                NativeScreen.Show(hwnd, activate: window == _popup);
                NativeScreen.SetTransitionsDisabled(hwnd, false);
            });
        }
        _hidden.Clear();

        var overlays = _overlays.ToList();
        _overlays.Clear();
        foreach (var overlay in overlays) Try(overlay.Close);
        _snapshot = null;

        Try(() =>
        {
            _popup.Activate();
            _popup.Focus();
        });
    }

    /// <summary>
    /// For a moment after the pick, mouse buttons and key presses don't reach RST's
    /// windows (a double click's second click, a key that ended the pick), and a key that
    /// ended the pick is ignored until it is released (auto-repeat of a held Enter or Esc
    /// would otherwise press the restored window's buttons or close the picker).
    /// </summary>
    private static void SwallowTrailingInput(Key? heldKey)
    {
        var started = DateTime.UtcNow;
        bool Active() =>
            DateTime.UtcNow - started < TrailingInput
            || (heldKey is { } k && Keyboard.IsKeyDown(k) && DateTime.UtcNow - started < HeldKeyLimit);

        PreProcessInputEventHandler filter = (_, e) =>
        {
            var input = e.StagingItem.Input;
            if (DateTime.UtcNow - started < TrailingInput)
            {
                if (input is MouseButtonEventArgs || input is KeyEventArgs { IsDown: true }) e.Cancel();
            }
            else if (heldKey is { } k && input is KeyEventArgs { IsDown: true } key && (key.Key == Key.System ? key.SystemKey : key.Key) == k)
            {
                e.Cancel();
            }
        };

        try
        {
            InputManager.Current.PreProcessInput += filter;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "RST: Pick from screen input filter failed");
            return;
        }

        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            if (Active()) return;
            timer.Stop();
            InputManager.Current.PreProcessInput -= filter;
        };
        timer.Start();
    }

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "RST: Pick from screen cleanup failed");
        }
    }

    /// <summary>Keeps the message loop running for <paramref name="duration"/> (windows repaint, DWM composes).</summary>
    private static void Wait(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
