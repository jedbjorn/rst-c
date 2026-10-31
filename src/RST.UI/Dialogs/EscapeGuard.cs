// rst-c's own (not vendored). SC-010 (feature #18, WU6): Escape closes the Builder, the Loader and
// Health as their Exit or Close button does, but never past a dialog, menu or popup the window has
// open, and never on a key-repeat (the press that closed a dialog in front must not, held, close the
// window behind it: WU6 review N1).

using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace RST.UI.Dialogs;

/// <summary>What keeps a window's Escape-to-close from acting: something in front of it takes Escape.</summary>
internal static class EscapeGuard
{
    /// <summary>
    /// A fresh Escape press nothing has handled yet. A key-repeat is not one: holding Escape on a
    /// dialog closes the dialog with the first press, and the repeats then reach the window behind it.
    /// </summary>
    public static bool IsFreshEscape(Key key, bool isRepeat, bool handled) => key == Key.Escape && !isRepeat && !handled;

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
