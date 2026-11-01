// rst-c's own (not vendored): the Health → Models sub-tab's code-behind (spec #25 Models; F19 #25 WU3). The view-model
// is ModelsViewModel. Here: the scroll offset kept and restored
// (a reload, a group toggle, Back), the cards asking for their thumbnails as the list realizes them (and cancelling as
// they scroll away), focusing a card on Back, and the Revit version menu (a ContextMenu, which is not in the tree).

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using RST.UI.Mvvm;

namespace RST.Tray.Windows;

internal partial class ModelsView : UserControl
{
    private ModelsViewModel? _model;
    private ScrollViewer? _scroller;
    private bool _restoring;

    public ModelsView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => UiGuard.Run("Models: bind", () => Bind(e.NewValue as ModelsViewModel));
        Loaded += (_, _) => UiGuard.Run("Models: bind", () => Bind(DataContext as ModelsViewModel));
        Unloaded += (_, _) => UiGuard.Run("Models: unbind", () => Bind(null));
        List.Loaded += (_, _) => UiGuard.Run("Models: find the scroller", AttachScroller);
    }

    /// <summary>The list's scroller (tests read its offset and its realized rows).</summary>
    internal ScrollViewer? Scroller => _scroller;

    private void Bind(ModelsViewModel? model)
    {
        if (ReferenceEquals(model, _model)) return;
        if (_model is not null)
        {
            _model.ScrollRequested -= OnScrollRequested;
            _model.FocusCardRequested -= OnFocusCardRequested;
        }
        _model = model;
        if (_model is null) return;
        _model.ScrollRequested += OnScrollRequested;
        _model.FocusCardRequested += OnFocusCardRequested;
        VersionMenu.DataContext = _model;
        VersionMenu.PlacementTarget = VersionButton;
        VersionMenu.Placement = PlacementMode.Bottom;
        VersionMenu.VerticalOffset = 4;
    }

    private void AttachScroller()
    {
        if (_scroller is not null) return;
        List.ApplyTemplate();
        _scroller = List.Template?.FindName("Scroller", List) as ScrollViewer;
        if (_scroller is null) return;
        _scroller.ScrollChanged += (_, e) => UiGuard.Run("Models: scrolled", () =>
        {
            // Keep where the user is (not while a restore is settling, and not while the list is hidden: a hidden
            // list's offset reads 0, and Back must find the old one).
            if (!_restoring && _model is not null && List.IsVisible) KeepPosition();
        });
    }

    /// <summary>Remembers the offset and the anchor: the row at the top of the viewport and how far it is scrolled past.</summary>
    private void KeepPosition()
    {
        if (_model is null || _scroller is null) return;
        _model.ScrollOffset = _scroller.VerticalOffset;
        if (TopRow() is not { } top) return;
        _model.ScrollAnchorKey = top.Key;
        _model.ScrollAnchorDelta = top.Delta;
    }

    /// <summary>The row at the top of the viewport (the first realized row not wholly above it) and its top relative to
    /// the viewport's top; null when no row is realized.</summary>
    internal (string Key, double Delta)? TopRow()
    {
        if (_scroller is null || ItemsHost() is not { } panel) return null;
        (string Key, double Delta)? best = null;
        foreach (UIElement child in panel.Children)
        {
            if (child is not FrameworkElement { IsVisible: true } row || ModelsViewModel.RowKey(row.DataContext) is not { } key) continue;
            var top = row.TransformToAncestor(_scroller).Transform(new Point()).Y;
            if (top + row.ActualHeight <= 0.5) continue; // wholly above the viewport (the panel's cache)
            if (best is null || top < best.Value.Delta) best = (key, top);
        }
        return best;
    }

    private VirtualizingStackPanel? ItemsHost() =>
        Descendants<VirtualizingStackPanel>(List).FirstOrDefault(p => p.IsItemsHost);

    // A rebuild (a reload, a group toggle) or Back: the same row back at the top, as far scrolled past as it
    // was. The panel estimates the height of rows it has not realized, so an offset alone can land a few pixels apart
    // on identical data (VM review R5); the anchor is measured after layout and corrected until it sits where it was.
    // A filter change has no anchor: the top.
    private void OnScrollRequested(object? sender, double offset)
    {
        if (_scroller is null) return;
        _restoring = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => UiGuard.Run("Models: restore the scroll position", () =>
        {
            try
            {
                if (_model?.ScrollAnchorKey is { } key)
                    RestoreAnchor(key, _model.ScrollAnchorDelta, offset);
                else
                {
                    _scroller.ScrollToVerticalOffset(offset);
                    _scroller.UpdateLayout();
                }
            }
            finally
            {
                _restoring = false;
            }
            if (_model is not null && List.IsVisible) KeepPosition();
        }));
    }

    private void RestoreAnchor(string key, double delta, double fallbackOffset)
    {
        var index = _model!.RowIndexOf(key);
        if (index < 0 || ItemsHost() is not { } panel)
        {
            _scroller!.ScrollToVerticalOffset(fallbackOffset); // the row is gone (a reload dropped it): about where it was
            _scroller.UpdateLayout();
            return;
        }
        for (var i = 0; i < 4; i++)
        {
            panel.BringIndexIntoViewPublic(index);
            _scroller!.UpdateLayout();
            if (List.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement row) return;
            var diff = row.TransformToAncestor(_scroller).Transform(new Point()).Y - delta;
            if (Math.Abs(diff) < 0.5) return;
            _scroller.ScrollToVerticalOffset(_scroller.VerticalOffset + diff);
            _scroller.UpdateLayout();
        }
    }

    private void OnFocusCardRequested(object? sender, string modelId) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => UiGuard.Run("Models: focus the opened card", () =>
        {
            foreach (var card in Descendants<Button>(List))
            {
                if (card.DataContext is not ModelCardItem item || item.ModelId != modelId) continue;
                card.Focus();
                return;
            }
        }));

    // A card asks for its thumbnail when the list realizes it, and cancels when it is recycled for another or unloaded.
    private void OnCardLoaded(object sender, RoutedEventArgs e) => UiGuard.Run("Models: a card shows", () =>
    {
        if (sender is FrameworkElement { DataContext: ModelCardItem card }) _model?.ThumbnailWanted(card);
    });

    private void OnCardUnloaded(object sender, RoutedEventArgs e) => UiGuard.Run("Models: a card hides", () =>
    {
        if (sender is FrameworkElement { DataContext: ModelCardItem card }) _model?.ThumbnailNotWanted(card);
    });

    private void OnCardDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => UiGuard.Run("Models: a card changes", () =>
    {
        if (e.OldValue is ModelCardItem old) _model?.ThumbnailNotWanted(old);
        if (e.NewValue is ModelCardItem card && sender is FrameworkElement { IsLoaded: true }) _model?.ThumbnailWanted(card);
    });

    // The version filter: checking the toggle (a click or Space) opens its menu; the menu closing unchecks it.
    private void OnVersionButtonChecked(object sender, RoutedEventArgs e) => UiGuard.Run("Models: open the version filter", () =>
    {
        VersionMenu.DataContext = _model;
        VersionMenu.IsOpen = true;
    });

    private void OnVersionMenuClosed(object sender, RoutedEventArgs e) => UiGuard.Run("Models: the version filter closed", () =>
    {
        VersionButton.IsChecked = false;
    });

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is T match) yield return match;
            for (var i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--) stack.Push(VisualTreeHelper.GetChild(node, i));
        }
    }
}
