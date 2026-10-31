// rst-c's own (not vendored): the Health → Models sub-tab's code-behind (spec #25 Models; F19 #25 WU3). The view-model
// is ModelsViewModel. Here: the cards per row from the list's width (the reflow), the scroll offset kept and restored
// (a reload, a group toggle, Back), the cards asking for their thumbnails as the list realizes them (and cancelling as
// they scroll away), focusing a card on Back, and the Revit version menu (a ContextMenu, which is not in the tree).

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using RST.UI.Mvvm;

namespace RST.Tray.Windows;

internal partial class ModelsView : UserControl
{
    /// <summary>A card's outer margin on each side (the ModelCard style's Margin).</summary>
    private const double CardMargin = 4;

    /// <summary>A group's card row: 1 px borders and 10 px padding each side (the card row template).</summary>
    private const double GroupInset = 2 * (1 + 10);

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
        List.SizeChanged += (_, _) => UiGuard.Run("Models: reflow", UpdateColumns);
        SizeChanged += (_, _) => UiGuard.Run("Models: reflow", UpdateColumns);
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
            _model.PropertyChanged -= OnModelChanged;
        }
        _model = model;
        if (_model is null) return;
        _model.ScrollRequested += OnScrollRequested;
        _model.FocusCardRequested += OnFocusCardRequested;
        _model.PropertyChanged += OnModelChanged;
        VersionMenu.DataContext = _model;
        VersionMenu.PlacementTarget = VersionButton;
        VersionMenu.Placement = PlacementMode.Bottom;
        VersionMenu.VerticalOffset = 4;
        UpdateColumns();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) => UiGuard.Run("Models: reflow", () =>
    {
        if (e.PropertyName is nameof(ModelsViewModel.GroupBy) or nameof(ModelsViewModel.HasCards)) UpdateColumns();
    });

    private void AttachScroller()
    {
        if (_scroller is not null) return;
        List.ApplyTemplate();
        _scroller = List.Template?.FindName("Scroller", List) as ScrollViewer;
        if (_scroller is null) return;
        _scroller.ScrollChanged += (_, e) => UiGuard.Run("Models: scrolled", () =>
        {
            if (e.ViewportWidthChange != 0) UpdateColumns();
            // Keep where the user is (not while a restore is settling, and not while the list is hidden: a hidden
            // list's offset reads 0, and Back must find the old one).
            if (!_restoring && _model is not null && List.IsVisible) _model.ScrollOffset = _scroller.VerticalOffset;
        });
        UpdateColumns();
    }

    /// <summary>As many cards per row as fit at <see cref="ModelsViewModel.CardWidth"/>; the cards share the rest.</summary>
    private void UpdateColumns()
    {
        if (_model is null) return;
        // The scroller's viewport once the list shows; before that (the first read, an empty state) the page's width
        // less the list's padding and a scroll bar, so the first rows are built at the right width.
        var width = _scroller is { ViewportWidth: > 0 } s && List.IsVisible
            ? s.ViewportWidth
            : ActualWidth - 44 - SystemParameters.VerticalScrollBarWidth;
        if (width <= 0) return;
        var content = _model.GroupBy == RST.Core.Tray.ModelGroupBy.None ? width + 2 * CardMargin : width - GroupInset;
        _model.Columns = Math.Max(1, (int)Math.Floor(content / (ModelsViewModel.CardWidth + 2 * CardMargin)));
    }

    private void OnScrollRequested(object? sender, double offset)
    {
        if (_scroller is null) return;
        _restoring = true;
        // After the new rows are measured, or the offset is clamped to the old extent.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => UiGuard.Run("Models: restore the scroll position", () =>
        {
            try
            {
                // The panel estimates the height of rows it has not realized yet, so the first jump can land a few
                // pixels off; once the rows there are measured, a second (rarely a third) jump lands exactly.
                for (var i = 0; i < 3; i++)
                {
                    _scroller.ScrollToVerticalOffset(offset);
                    _scroller.UpdateLayout();
                    if (Math.Abs(_scroller.VerticalOffset - Math.Min(offset, _scroller.ScrollableHeight)) < 0.5) break;
                }
            }
            finally
            {
                _restoring = false;
            }
            if (_model is not null && List.IsVisible) _model.ScrollOffset = _scroller.VerticalOffset;
        }));
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
