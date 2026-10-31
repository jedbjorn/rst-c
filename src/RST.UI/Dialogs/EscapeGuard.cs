// rst-c's own (not vendored). SC-010 (feature #18, WU6): Escape closes the Loader and Health as their
// Close button does, but never past a dialog, menu or popup the window has open.

using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace RST.UI.Dialogs;

/// <summary>What keeps a window's Escape-to-close from acting: something in front of it takes Escape.</summary>
internal static class EscapeGuard
{
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
            if (node is not Visual and not System.Windows.Media.Media3D.Visual3D) continue;
            for (var i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--)
                pending.Push(VisualTreeHelper.GetChild(node, i));
        }
        return false;
    }
}
