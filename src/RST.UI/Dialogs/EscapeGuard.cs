// rst-c's own (not vendored). SC-010 (feature #18, WU6): Escape closes the Builder, the Loader and
// Health as their Exit or Close button does, but never past a dialog, menu or popup the window has
// open, and never on a key-repeat (the press that closed a dialog in front must not, held, close the
// window behind it: WU6 review N1). Decision #62 ("escape should close all modals yes. not the tray
// app."): the same rules for every dialog the add-in opens (CancelOnEscape) and its in-window modals
// (Health's Junk modal, the Builder's icon picker); the tray app keeps WPF's own Escape.

using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using RST.UI.Mvvm;

namespace RST.UI.Dialogs;

/// <summary>What keeps a window's Escape-to-close from acting: something in front of it takes Escape.</summary>
internal static class EscapeGuard
{
    /// <summary>
    /// A fresh Escape press nothing has handled yet. A key-repeat is not one: holding Escape on a
    /// dialog closes the dialog with the first press, and the repeats then reach the window behind it.
    /// </summary>
    public static bool IsFreshEscape(Key key, bool isRepeat, bool handled) => key == Key.Escape && !isRepeat && !handled;

    /// <summary>A held Escape's repeat: a dialog swallows it, so it neither cancels the dialog nor reaches anything in it.</summary>
    public static bool IsHeldEscape(Key key, bool isRepeat) => key == Key.Escape && isRepeat;

    /// <summary>
    /// Escape cancels <paramref name="dialog"/> the way its Cancel (or Close, or the title-bar ✕)
    /// does: <paramref name="cancel"/>, by default closing it with no result, which every RST dialog
    /// reads as Cancel. Only a fresh press, and only one the dialog's own content has not taken (a
    /// text box or drop-down that uses Escape acts first); never past a dialog, menu or popup the
    /// dialog has open (that one takes the press), so a press closes the innermost modal only. (No
    /// RST dialog has a busy state of its own: what it waits on, a file or colour dialog or the icon
    /// picker, is a dialog it owns.)
    /// A held Escape's repeats do nothing in the dialog: WPF's IsCancel button acts on a repeat too,
    /// so without this the press that closed a dialog in front would, held, cancel this one as well.
    /// </summary>
    public static void CancelOnEscape(Window dialog, Action? cancel = null)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        var site = dialog.GetType().Name + ": Escape";
        dialog.PreviewKeyDown += (_, e) => UiGuard.Run(site, () =>
        {
            if (IsHeldEscape(e.Key, e.IsRepeat)) e.Handled = true;
        });
        dialog.KeyDown += (_, e) => UiGuard.Run(site, () =>
        {
            if (!IsFreshEscape(e.Key, e.IsRepeat, e.Handled)) return;
            // Handled either way, so the dialog's IsCancel button cannot act on it after us.
            e.Handled = true;
            if (HasSomethingOpen(dialog)) return;
            if (cancel is not null) cancel();
            else if (dialog.IsVisible) dialog.Close();
        });
    }

    /// <summary>A dialog the window opened is showing, or one of its menus or popups is open.</summary>
    public static bool HasSomethingOpen(Window window) => HasOpenDialog(window) || HasOpenPopup(window);

    /// <summary>
    /// True while a window <paramref name="window"/> owns is showing: a family dialog it opened
    /// (DialogService and the Loader's dialogs set the owner).
    /// </summary>
    public static bool HasOpenDialog(Window window) =>
        window.OwnedWindows.OfType<Window>().Any(w => w.IsVisible);

    /// <summary>
    /// True while an element of <paramref name="root"/>'s visual tree has its context menu open, or a
    /// popup in the tree (a drop-down's) is open. Tooltips are not in the tree and do not count.
    /// </summary>
    public static bool HasOpenPopup(DependencyObject root)
    {
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (node is Popup { IsOpen: true }) return true;
            if (node is FrameworkElement { ContextMenu.IsOpen: true }) return true;
            if (node is not Visual and not Visual3D) continue;
            for (var i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--)
                pending.Push(VisualTreeHelper.GetChild(node, i));
        }
        return false;
    }
}
