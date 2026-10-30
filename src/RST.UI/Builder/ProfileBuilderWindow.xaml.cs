// rst-c's own (not vendored); the window wiring follows splash-it
// src/SplashIt.UI/Settings/SettingsWindow.xaml.cs (splash-it commit ba86a71, MIT, same owner).
// The Profile Builder window (spec #16, WU4). All behavior is in ProfileBuilderViewModel; the
// code here is view plumbing: the title-bar ✕ and Escape ask the view-model first, as Exit does
// (Planner default 3; SC-010), the header's ⚙ and profile menus open under their toggles (feature
// #18: the profile menu lists ProfileOptions each time it opens), and drag and drop (WU5):
// DragDrop.DoDragDrop with a typed BuilderDrag payload from the catalogue rows, the panel headers,
// the tiles, the stack rows and the stack headers (feature #18, WU3). Each card handles every drag
// over it in the Preview phase: it reports the tile, stack or stack row under the pointer and where
// on it (TileHit) to the view-model, which owns the markers and the rules (RST.Core TileDrops and
// PanelStacks; PanelDrops for the panel reorder). "on tab [name]" opens General on the tab field.

using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
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
        // A ContextMenu is not in the window's tree: it takes the view-model and its toggle directly.
        foreach (var (toggle, menu) in new[] { (SettingsButton, SettingsMenu), (ProfileButton, ProfileMenu) })
        {
            menu.DataContext = model;
            menu.PlacementTarget = toggle;
            menu.Placement = PlacementMode.Bottom;
            menu.VerticalOffset = 4;
        }
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

    /// <summary>
    /// Escape closes the Builder the way Exit does, through the unsaved-changes prompt (SC-010). Only
    /// when nothing else takes it: an open menu, the icon picker and the dialogs handle their own
    /// Escape, and a drag in progress ends on it.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Key != Key.Escape || !EscapeCloses) return;
        e.Handled = true;
        _ = Model.ExitAsync();
    }

    private bool EscapeCloses =>
        Model.IsIdle && !Model.IsIconPickerOpen && Model.CurrentDrag is null && !SettingsMenu.IsOpen && !ProfileMenu.IsOpen;

    // ---- the header's menus -------------------------------------------------------------------

    // ⚙ and the profile button are toggles, checked while their menu is open and not hit-testable
    // then, so the click that dismisses the menu cannot reopen it (VM finding D2). Checking one, by
    // a click or Space, opens its menu; the menu closing unchecks it.
    private void OnMenuToggleChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { ContextMenu: { } menu }) return;
        if (ReferenceEquals(menu, ProfileMenu)) FillProfileMenu();
        menu.IsOpen = true;
    }

    private void OnMenuClosed(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, SettingsMenu)) SettingsButton.IsChecked = false;
        else if (ReferenceEquals(sender, ProfileMenu)) ProfileButton.IsChecked = false;
    }

    private readonly List<MenuItem> _profileItems = [];

    /// <summary>The profile menu's rows for the saved profiles (tests).</summary>
    internal IReadOnlyList<MenuItem> ProfileMenuItems => _profileItems;

    /// <summary>The saved profiles go between the heading and the divider: the current one ticked, a note with the tab.</summary>
    private void FillProfileMenu()
    {
        Model.RefreshProfiles(); // another Revit session may have saved, renamed or removed one
        foreach (var item in _profileItems) ProfileMenu.Items.Remove(item);
        _profileItems.Clear();
        var at = ProfileMenu.Items.IndexOf(ProfileMenuDivider);
        foreach (var option in Model.ProfileOptions)
        {
            var item = new MenuItem
            {
                Header = option,
                HeaderTemplate = (DataTemplate)FindResource("ProfileMenuHeader"),
                InputGestureText = option.Note,
                Command = Model.SwitchProfileCommand,
                CommandParameter = option,
            };
            AutomationProperties.SetAutomationId(item, "Builder.ProfileMenuItem");
            AutomationProperties.SetName(item, option.Display);
            ProfileMenu.Items.Insert(at++, item);
            _profileItems.Add(item);
        }
    }

    // ---- drag and drop ------------------------------------------------------------------------

    private Point _dragStart;
    private BuilderDrag? _pendingDrag;

    /// <summary>
    /// The drag a source element would start, from its view-model: a catalogue command, a panel (its
    /// header), a tile or a stack's row, or a whole stack (its header).
    /// </summary>
    internal static BuilderDrag? DragFor(object? dataContext) => dataContext switch
    {
        CatalogNodeViewModel { Command: { } command } => BuilderDrag.FromCatalog(command),
        PanelCardViewModel card => BuilderDrag.FromPanel(card.Index),
        ToolTileViewModel tile => BuilderDrag.FromSlot(tile.Panel.Index, tile.Index, tile.Row),
        StackGroupViewModel stack => BuilderDrag.FromSlot(stack.Panel.Index, stack.Index),
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

    private DragDropEffects DropEffect(bool accepted) =>
        !accepted ? DragDropEffects.None : Model.CurrentDrag?.Kind == DragKind.Catalog ? DragDropEffects.Copy : DragDropEffects.Move;

    // The card's handlers run in the Preview phase, before anything inside the card sees the drag.
    // A TextBox takes every drag that passes over it (TextEditor marks DragOver handled, with no
    // effect, for a non-text payload), and the tiles' name boxes cover much of each tile (the
    // TextBox lesson, WU5 review B1). So the card handles any BuilderDrag itself: it finds the tile,
    // the stack or the stack row under the pointer and reports where (TileHit) to the view-model;
    // anything else over the card is the card (a panel reorder, or a tool to the panel's end). A text
    // drag is left alone, so it still reaches a name box.
    private void OnCardDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(BuilderDrag)) || sender is not FrameworkElement { DataContext: PanelCardViewModel card } cardRoot) return;
        bool accepted;
        if (Model.CurrentDrag?.Kind != DragKind.Panel && HitAt(e.OriginalSource as DependencyObject, cardRoot, e) is { } hit)
            accepted = Model.DragOverTile(hit);
        else
            accepted = Model.DragOverPanel(card.Index, UpperHalf(e, cardRoot));
        e.Effects = DropEffect(accepted);
        e.Handled = true;
    }

    private void OnCardDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(BuilderDrag)) || sender is not FrameworkElement { DataContext: PanelCardViewModel card } cardRoot) return;
        if (Model.CurrentDrag?.Kind != DragKind.Panel && HitAt(e.OriginalSource as DependencyObject, cardRoot, e) is { } hit)
            Model.DropOnTile(hit);
        else
            Model.DropOnPanel(card.Index, UpperHalf(e, cardRoot));
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => Model.ClearDropMarkers();

    private static bool UpperHalf(DragEventArgs e, FrameworkElement target) => e.GetPosition(target).Y < target.ActualHeight / 2;

    /// <summary>
    /// The tile, stack or stack row under the pointer (from the drag's original source up to the
    /// card): a row's position is across its whole stack (its edges insert beside the stack) and down
    /// the row (before or after it); a tile's or a stack's is across it. Null outside every tile.
    /// </summary>
    internal static TileHit? HitAt(DependencyObject? source, DependencyObject card, DragEventArgs e) =>
        HitAt(source, card, (element) => e.GetPosition(element));

    /// <summary><see cref="HitAt(DependencyObject?, DependencyObject, DragEventArgs)"/> with the pointer's position relative to an element.</summary>
    internal static TileHit? HitAt(DependencyObject? source, DependencyObject card, Func<IInputElement, Point> positionIn)
    {
        FrameworkElement? row = null;
        for (var d = source; d is not null && !ReferenceEquals(d, card); d = ParentOf(d))
        {
            if (d is not FrameworkElement element) continue;
            switch (element.Name)
            {
                case "StackRow" when element.DataContext is ToolTileViewModel:
                    row ??= element;
                    break;
                case "Tile" when element.DataContext is ToolTileViewModel tile:
                {
                    var p = positionIn(element);
                    return TileHit.On(tile.Panel.Index, tile.Index, p.X, element.ActualWidth);
                }
                case "StackGroup" when element.DataContext is StackGroupViewModel stack:
                {
                    var p = positionIn(element);
                    if (row is { DataContext: ToolTileViewModel r })
                    {
                        var y = positionIn(row).Y;
                        return TileHit.OnRow(stack.Panel.Index, stack.Index, r.Row, p.X, element.ActualWidth, y, row.ActualHeight);
                    }
                    return TileHit.On(stack.Panel.Index, stack.Index, p.X, element.ActualWidth);
                }
            }
        }
        return null;
    }

    private static DependencyObject? ParentOf(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    // ---- the Panels pane header ---------------------------------------------------------------

    /// <summary>"on tab [name]": the General tab opens with its Ribbon tab name field focused.</summary>
    private void OnTabNameClick(object sender, RoutedEventArgs e) => FocusTabName();

    /// <summary>Opens the Profile pane's General tab and focuses the Ribbon tab name field.</summary>
    internal void FocusTabName()
    {
        ProfileTabs.SelectedItem = GeneralTab;
        // The tab's content is realized on selection: focus it once layout has run.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            FocusManager.SetFocusedElement(this, TabBox);
            TabBox.Focus();
            TabBox.SelectAll();
        }));
    }
}
