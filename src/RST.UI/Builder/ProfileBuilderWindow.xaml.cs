// rst-c's own (not vendored); the window wiring follows splash-it
// src/SplashIt.UI/Settings/SettingsWindow.xaml.cs (splash-it commit ba86a71, MIT, same owner).
// The Profile Builder window (spec #16, WU4). All behavior is in ProfileBuilderViewModel; the
// code here is view plumbing: the title-bar ✕ asks the view-model first (Planner default 3), the
// Branding popover commits its URL box when it closes and closes on Escape (Branding ▾ is a
// ToggleButton bound to the popup, so the dismissing click cannot reopen it), the URL editor's
// label box takes focus when the editor opens, and drag and drop
// (WU5): DragDrop.DoDragDrop with a typed BuilderDrag payload from the catalogue rows, the panel
// headers and the slot rows; the cards and rows report the cursor's half to the view-model, which
// owns the markers and the index arithmetic (RST.Core PanelDrops).

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RST.Core.Builder;
using RST.UI.Dialogs;
using RST.UI.Theming;

namespace RST.UI.Builder;

public partial class ProfileBuilderWindow : Window
{
    public ProfileBuilderWindow(ProfileBuilderViewModel model)
    {
        InitializeComponent();
        DarkTitleBar.Attach(this);
        Model = model ?? throw new ArgumentNullException(nameof(model));
        DataContext = model;
        model.CloseRequested += () =>
        {
            if (IsVisible) Close();
        };
        model.PropertyChanged += (_, e) =>
        {
            // buildUrlEditor: the label box takes focus when the editor opens.
            if (e.PropertyName == nameof(ProfileBuilderViewModel.IsUrlEditorOpen) && model.IsUrlEditorOpen)
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () => UrlLabelBox.Focus());
        };
        // A drag that leaves the window straight from a card, a row or a name box (whose own DragLeave
        // the TextBox handles) still clears its marker.
        AddHandler(DragDrop.DragLeaveEvent, new DragEventHandler((_, _) => Model.ClearDropMarkers()), handledEventsToo: true);
    }

    public ProfileBuilderViewModel Model { get; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowPlacement.CapToWorkArea(this);
    }

    /// <summary>The title-bar ✕ goes through the unsaved-changes prompt, as Exit does.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!Model.CanClose()) e.Cancel = true;
        base.OnClosing(e);
    }

    // Escape inside the popover closes it and puts the focus back on Branding ▾.
    private void OnBrandingPopoverKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        Model.IsBrandingOpen = false;
        BrandingButton.Focus();
        e.Handled = true;
    }

    // The URL is saved on change (blur or Enter); closing the popover is a blur.
    private void OnBrandingClosed(object? sender, EventArgs e)
    {
        BrandingUrlBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    // ---- drag and drop ------------------------------------------------------------------------

    private Point _dragStart;
    private BuilderDrag? _pendingDrag;

    /// <summary>The drag a source element would start, from its row's view-model.</summary>
    internal static BuilderDrag? DragFor(object? dataContext) => dataContext switch
    {
        CatalogNodeViewModel { Command: { } command } => BuilderDrag.FromCatalog(command),
        PanelCardViewModel card => BuilderDrag.FromPanel(card.Index),
        SlotRowViewModel slot => BuilderDrag.FromSlot(slot.Panel.Index, slot.Index),
        _ => null,
    };

    private void OnDragSourceDown(object sender, MouseButtonEventArgs e)
    {
        _pendingDrag = null;
        // Typing in a name box or clicking a button never starts a drag.
        if (IsInteractive(e.OriginalSource as DependencyObject, sender as DependencyObject)) return;
        _pendingDrag = DragFor((sender as FrameworkElement)?.DataContext);
        _dragStart = e.GetPosition(this);
    }

    private void OnDragSourceMove(object sender, MouseEventArgs e)
    {
        if (_pendingDrag is null || e.LeftButton != MouseButtonState.Pressed || !Model.IsIdle) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var drag = _pendingDrag;
        _pendingDrag = null;
        Model.BeginDrag(drag);
        try
        {
            var effects = drag.Kind == DragKind.Catalog ? DragDropEffects.Copy : DragDropEffects.Move;
            DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(BuilderDrag), drag), effects);
        }
        finally
        {
            Model.EndDrag();
        }
    }

    private static bool IsInteractive(DependencyObject? source, DependencyObject? stop)
    {
        for (var d = source; d is not null && !ReferenceEquals(d, stop); d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is TextBoxBase or ButtonBase) return true;
        return false;
    }

    private static bool UpperHalf(DragEventArgs e, FrameworkElement target) => e.GetPosition(target).Y < target.ActualHeight / 2;

    private DragDropEffects DropEffect(bool accepted) =>
        !accepted ? DragDropEffects.None : Model.CurrentDrag?.Kind == DragKind.Catalog ? DragDropEffects.Copy : DragDropEffects.Move;

    private void OnPanelDragOver(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PanelCardViewModel card } target)
            e.Effects = DropEffect(Model.DragOverPanel(card.Index, UpperHalf(e, target)));
        e.Handled = true;
    }

    private void OnPanelDrop(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PanelCardViewModel card } target)
            Model.DropOnPanel(card.Index, UpperHalf(e, target));
        e.Handled = true;
    }

    // The slot handlers mark their events handled, so the card under them does not act too
    // (the page's stopPropagation).
    private void OnSlotDragOver(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SlotRowViewModel slot } target)
        {
            var accepted = Model.DragOverSlot(slot.Panel.Index, slot.Index, UpperHalf(e, target));
            if (!accepted) return; // a panel drag falls through to the card
            e.Effects = DropEffect(true);
        }
        e.Handled = true;
    }

    private void OnSlotDrop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SlotRowViewModel slot } target) return;
        if (Model.CurrentDrag?.Kind == DragKind.Panel) return; // falls through to the card
        Model.DropOnSlot(slot.Panel.Index, slot.Index, UpperHalf(e, target));
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => Model.ClearDropMarkers();

    // The slot and panel name boxes fill most of each row and header. A TextBox takes every drag
    // that passes over it (TextEditor marks DragOver handled, with no effect, for a non-text
    // payload), so the row and card handlers above would never run there. The boxes hand a
    // BuilderDrag to their row or card in the Preview phase instead, before the TextBox sees it;
    // a text drag still reaches the TextBox. (WU5 review B1.)
    private void OnNameBoxDragOver(object sender, DragEventArgs e)
    {
        if (sender is not DependencyObject box || !e.Data.GetDataPresent(typeof(BuilderDrag))) return;
        if (Ancestor(box, "SlotRow") is { DataContext: SlotRowViewModel slot } row
            && Model.DragOverSlot(slot.Panel.Index, slot.Index, UpperHalf(e, row)))
        {
            e.Effects = DropEffect(true);
        }
        else if (Ancestor(box, "CardRoot") is { DataContext: PanelCardViewModel card } cardRoot)
        {
            // A panel drag over a slot's name falls through to the card, as over the rest of the row.
            e.Effects = DropEffect(Model.DragOverPanel(card.Index, UpperHalf(e, cardRoot)));
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void OnNameBoxDrop(object sender, DragEventArgs e)
    {
        if (sender is not DependencyObject box || !e.Data.GetDataPresent(typeof(BuilderDrag))) return;
        if (Model.CurrentDrag?.Kind != DragKind.Panel && Ancestor(box, "SlotRow") is { DataContext: SlotRowViewModel slot } row)
            Model.DropOnSlot(slot.Panel.Index, slot.Index, UpperHalf(e, row));
        else if (Ancestor(box, "CardRoot") is { DataContext: PanelCardViewModel card } cardRoot)
            Model.DropOnPanel(card.Index, UpperHalf(e, cardRoot));
        e.Handled = true;
    }

    /// <summary>The nearest visual ancestor named <paramref name="name"/> (a slot row or a card).</summary>
    private static FrameworkElement? Ancestor(DependencyObject from, string name)
    {
        for (var d = VisualTreeHelper.GetParent(from); d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is FrameworkElement { Name: var n } element && n == name) return element;
        return null;
    }
}
