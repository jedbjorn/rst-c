// rst-c's own (not vendored). v3.1.1, rc.3 VM finding F2: after Cancel in Edit custom tool, Escape did
// nothing in the Builder until a click into a control. WPF puts keyboard focus back on the element that
// had it when the owner lost focus (HwndKeyboardInputProvider's restore on WM_SETFOCUS). The Custom Tools
// row's ✎ shows only while the row is hovered or holds focus, so by the time the dialog closed it was
// collapsed; the restore failed, and because the focused element was still the closing dialog's, WPF
// cleared keyboard focus. With no focused element no key event is raised in the window, so its own
// KeyDown (Escape) never ran, and a click on a blank area does not focus anything. The same happens for
// any dialog opened from an element that is hidden or disabled while the dialog shows. In-window modals
// (the Builder's icon picker, Health's Junk modal) left focus on the window itself, not where it was.
// Decision #62 covers the add-in's windows only: the tray app's shared dialogs keep WPF's own behaviour.

using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using RST.UI.Mvvm;

namespace RST.UI.Dialogs;

/// <summary>
/// Puts keyboard focus back inside a window after a dialog it owns, or a modal overlay in it, closes:
/// on the element that had it before, if that element is still in the window and can take focus;
/// otherwise on its nearest ancestor that can, or on the window itself. The window's own Escape then
/// works on the next press, with no click first.
/// </summary>
internal static class FocusReturn
{
    /// <summary>Shows <paramref name="dialog"/> modally; once it closes, focus goes back inside its owner.</summary>
    public static bool? ShowDialog(Window dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        return Around(dialog.Owner, dialog.ShowDialog);
    }

    /// <summary>
    /// Runs <paramref name="show"/>, which shows a modal owned by <paramref name="owner"/> (a family
    /// dialog, or a native file dialog) and returns when it closes; then focus goes back inside the owner.
    /// </summary>
    public static T Around<T>(Window? owner, Func<T> show)
    {
        ArgumentNullException.ThrowIfNull(show);
        var previous = owner is null ? null : FocusedIn(owner);
        try
        {
            return show();
        }
        finally
        {
            if (owner is not null) Restore(owner, previous);
        }
    }

    /// <summary>The element of <paramref name="window"/> that has keyboard focus now, or null when focus is not in it.</summary>
    public static IInputElement? FocusedIn(Window window) =>
        Keyboard.FocusedElement is DependencyObject d && IsIn(window, d) ? (IInputElement)d : null;

    /// <summary>
    /// Puts focus back inside <paramref name="window"/> (see the class). Nothing happens while the window
    /// is not showing or a dialog it owns is showing (that one has the keyboard). Never throws.
    /// </summary>
    public static void Restore(Window window, IInputElement? previous)
    {
        ArgumentNullException.ThrowIfNull(window);
        UiGuard.Run(window.GetType().Name + ": focus after a modal", () =>
        {
            if (!window.IsVisible || EscapeGuard.HasOpenDialog(window)) return;
            if (previous is DependencyObject p && IsIn(window, p) && TryFocusAt(window, p)) return;
            if (Keyboard.FocusedElement is DependencyObject current && !ReferenceEquals(current, window)
                && IsIn(window, current) && CanFocus(current)) return; // WPF already put it somewhere that takes keys
            // The window itself: its Escape works there, and Tab starts from it.
            if (CanFocus(window)) Keyboard.Focus(window);
            if (!window.IsKeyboardFocusWithin) window.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        });
    }

    /// <summary>
    /// Focus on <paramref name="element"/>; when it cannot take focus now (collapsed with its hover row,
    /// disabled), on its nearest ancestor inside the window that can, and then on the element again if
    /// focusing the ancestor showed it (a Custom Tools row shows ✎ while it holds focus). False when
    /// neither can, so the caller falls back.
    /// </summary>
    private static bool TryFocusAt(Window window, DependencyObject element)
    {
        if (CanFocus(element)) return Focus(window, element);
        for (var a = ParentOf(element); a is not null && !ReferenceEquals(a, window); a = ParentOf(a))
        {
            if (!CanFocus(a)) continue;
            if (!Focus(window, a)) return false;
            if (CanFocus(element)) Focus(window, element);
            return true;
        }
        return false;
    }

    private static bool Focus(Window window, DependencyObject element)
    {
        if (!ReferenceEquals(Keyboard.FocusedElement, element)) Keyboard.Focus((IInputElement)element);
        return ReferenceEquals(Keyboard.FocusedElement, element) && window.IsKeyboardFocusWithin;
    }

    /// <summary>The element is in <paramref name="window"/>'s tree, shown in it (not a closed dialog's, not removed).</summary>
    private static bool IsIn(Window window, DependencyObject d) =>
        ReferenceEquals(Window.GetWindow(d), window) && PresentationSource.FromDependencyObject(d) is not null;

    /// <summary>What WPF's Keyboard.IsFocusable checks: focusable, enabled and, for an element, visible.</summary>
    private static bool CanFocus(DependencyObject d) => d switch
    {
        UIElement e => e.Focusable && e.IsEnabled && e.IsVisible,
        ContentElement c => c.Focusable && c.IsEnabled,
        UIElement3D e3 => e3.Focusable && e3.IsEnabled && e3.IsVisible,
        _ => false,
    };

    private static DependencyObject? ParentOf(DependencyObject d) =>
        (d is Visual or Visual3D ? VisualTreeHelper.GetParent(d) : null) ?? LogicalTreeHelper.GetParent(d);
}

/// <summary>
/// An in-window modal overlay's focus (the Builder's icon picker, Health's Junk modal): where focus was
/// when it opened, and focus back there once it closes. The window calls <see cref="Opened"/> and
/// <see cref="Closed"/> as the overlay's open flag changes.
/// </summary>
internal sealed class OverlayFocus(Window window)
{
    private IInputElement? _previous;

    /// <summary>The overlay is opening: remember the element that has focus (before the overlay takes it).</summary>
    public void Opened() => _previous = FocusReturn.FocusedIn(window);

    /// <summary>
    /// The overlay closed: focus goes back once the bindings have hidden it and enabled what was behind
    /// it (the dispatcher's Input priority, before the next key press is processed). When the overlay's
    /// own action opened a dialog as it closed (Health's Clean Selected asks "Delete selected junk?"),
    /// focus goes back once that dialog has closed too.
    /// </summary>
    public void Closed()
    {
        var previous = _previous;
        _previous = null;
        Later(previous);
    }

    private void Later(IInputElement? previous) =>
        window.Dispatcher.BeginInvoke(DispatcherPriority.Input, () => UiGuard.Run(window.GetType().Name + ": focus after a modal", () =>
        {
            var dialog = window.OwnedWindows.OfType<Window>().FirstOrDefault(w => w.IsVisible);
            if (dialog is null)
            {
                FocusReturn.Restore(window, previous);
                return;
            }
            void OnDialogClosed(object? sender, EventArgs e) => UiGuard.Run(window.GetType().Name + ": focus after a modal", () =>
            {
                dialog.Closed -= OnDialogClosed;
                Later(previous);
            });
            dialog.Closed += OnDialogClosed;
        }));
}
