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
// Feature #18, WU4: the Commands tree's items are the Custom Tools group then the catalogue (one
// CompositeCollection, so both scroll together); a Custom Tools row is a drag source like a command.

using System.ComponentModel;
using System.Windows.Data;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using RST.Core.Builder;
using RST.Core.Logging;
using RST.UI.Dialogs;
using RST.UI.Mvvm;
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
        CatalogTreeView.ItemsSource = new CompositeCollection
        {
            model.CustomToolsGroup,
            new CollectionContainer { Collection = model.CatalogRoots },
        };
        model.CloseRequested += () => UiGuard.Run("Builder window: close", () =>
        {
            if (IsVisible) Close();
        });
        // The loaded profile's stack-repair toast waits for the first render, so its time runs while it shows.
        ContentRendered += (_, _) => UiGuard.Run("Builder window: boot toast", model.ShowBootToast);
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
        AddHandler(DragDrop.DragLeaveEvent, new DragEventHandler((_, _) => UiGuard.Run("Builder window: drag leaves", Model.ClearDropMarkers)), handledEventsToo: true);
        // A press arms a drag that only a move with the button still down starts. The release disarms
        // it wherever it happens (a still click on a tile, then a press-and-move elsewhere, must not
        // drag that tile: WU3 review leftover), so the window watches every left-button release.
        AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((_, _) => UiGuard.Run("Builder window: release", () => _pendingDrag = null)), handledEventsToo: true);
        // Every press disarms too (review N1): a release outside the window is never seen, and a later
        // press on a non-source would leave the old drag armed. The window's tunnelling handler runs
        // before a source's own OnDragSourceDown, so a press on a source re-arms as before.
        AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((_, _) => UiGuard.Run("Builder window: press", () => _pendingDrag = null)), handledEventsToo: true);
    }

    public ProfileBuilderViewModel Model { get; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        UiGuard.Run("Builder window: place", () => WindowPlacement.CapToWorkArea(this));
    }

    /// <summary>
    /// The title-bar ✕ goes through the unsaved-changes prompt, as Exit does. Not when the window safety net
    /// closes the Builder after an error (SC-045): no prompt and no save from a faulted state; unsaved edits
    /// are discarded, and the log says so. The same when the prompt itself fails (SC-046 review N2): the error
    /// is logged, the window closes, and the log says the edits were discarded.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (WindowGuard.IsClosingAfterFault(this))
        {
            AbandonAfterFault();
            base.OnClosing(e);
            return;
        }
        var asked = false;
        UiGuard.Run("Builder window: closing", () =>
        {
            if (!Model.CanClose()) e.Cancel = true;
            asked = true;
        });
        if (!asked && !e.Cancel) AbandonAfterFault();
        base.OnClosing(e);
    }

    /// <summary>The Builder closes after an error: unsaved edits are discarded (said in the log), and nothing more is saved.</summary>
    private void AbandonAfterFault()
    {
        if (HasUnsavedEdits()) Log.Warning("Builder: closed after an unexpected error; its unsaved changes were discarded, not saved");
        Model.Abandon(); // a queued or in-flight save, switch or reload does nothing more
    }

    private bool HasUnsavedEdits()
    {
        try
        {
            return Model.IsDirty;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Builder: could not tell whether there were unsaved changes after an error");
            return true;
        }
    }

    /// <summary>
    /// Escape closes the Builder the way Exit does, through the unsaved-changes prompt (SC-010). Only
    /// a fresh press (not a key-repeat), and only when nothing else takes it: an open menu (⚙, the
    /// profile menu) or popup, a dialog, and the icon picker handle their own Escape (EscapeGuard,
    /// shared with the Loader and Health), and a drag in progress ends on it.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        UiGuard.Run("Builder window: Escape", () =>
        {
            if (!EscapeGuard.IsFreshEscape(e.Key, e.IsRepeat, e.Handled) || !EscapeCloses) return;
            e.Handled = true;
            // Exit's own command, so a failed exit is logged and toasted like a click on Exit (SC-046, R5).
            Model.ExitCommand.Execute(null);
        });
    }

    private bool EscapeCloses =>
        Model.IsIdle && !Model.IsIconPickerOpen && Model.CurrentDrag is null && !EscapeGuard.HasSomethingOpen(this);

    // ---- the header's menus -------------------------------------------------------------------

    // ⚙ and the profile button are toggles, checked while their menu is open and not hit-testable
    // then, so the click that dismisses the menu cannot reopen it (VM finding D2). Checking one, by
    // a click or Space, opens its menu; the menu closing unchecks it.
    private void OnMenuToggleChecked(object sender, RoutedEventArgs e) => UiGuard.Run("Builder window: open a header menu", () =>
    {
        if (sender is not ToggleButton { ContextMenu: { } menu }) return;
        if (ReferenceEquals(menu, ProfileMenu)) FillProfileMenu();
        menu.IsOpen = true;
    });

    private void OnMenuClosed(object sender, RoutedEventArgs e) => UiGuard.Run("Builder window: a header menu closed", () =>
    {
        if (ReferenceEquals(sender, SettingsMenu)) SettingsButton.IsChecked = false;
        else if (ReferenceEquals(sender, ProfileMenu)) ProfileButton.IsChecked = false;
    });

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
        if (at < 0) at = ProfileMenu.Items.Count;
        // TryFindResource, not a FindResource cast (SC-046, R4): a missing template shows the plain header.
        var header = TryFindResource("ProfileMenuHeader") as DataTemplate;
        foreach (var option in Model.ProfileOptions)
        {
            var item = new MenuItem
            {
                Header = option,
                HeaderTemplate = header,
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

    /// <summary>The drag a press has armed, waiting for the pointer to move past the drag distance (tests).</summary>
    internal BuilderDrag? PendingDrag => _pendingDrag;

    /// <summary>The pointer's position for a mouse event (a seam: the CI desktop has no mouse, so tests script it).</summary>
    internal Func<MouseEventArgs, IInputElement, Point> PointerAt { get; set; } = (e, relativeTo) => e.GetPosition(relativeTo);

    /// <summary>Whether the left button is down during a mouse move (a seam, as <see cref="PointerAt"/>).</summary>
    internal Func<MouseEventArgs, bool> LeftPressed { get; set; } = e => e.LeftButton == MouseButtonState.Pressed;

    /// <summary>Runs the OS drag loop (a seam: the drag-start tests record the call instead of running it).</summary>
    internal Func<DependencyObject, IDataObject, DragDropEffects, DragDropEffects> RunDragDrop { get; set; } =
        (source, data, effects) => DragDrop.DoDragDrop(source, data, effects);

    /// <summary>
    /// The drag a source element would start, from its view-model: a catalogue command, a Custom Tools
    /// row, a panel (its header), a tile or a stack's row, or a whole stack (its header). A slot drag
    /// carries the slot it was started from, so the drop can check it is still there (WU3 review N1).
    /// </summary>
    internal static BuilderDrag? DragFor(object? dataContext) => dataContext switch
    {
        CatalogNodeViewModel { Command: { } command } => BuilderDrag.FromCatalog(command),
        CustomToolItemViewModel row => BuilderDrag.FromCustomTool(row.Tool),
        PanelCardViewModel card => BuilderDrag.FromPanel(card.Index),
        ToolTileViewModel tile => BuilderDrag.FromSlot(tile.Panel.Index, tile.Index, tile.Row, tile.Model),
        StackGroupViewModel stack => BuilderDrag.FromSlot(stack.Panel.Index, stack.Index, -1, stack.Model),
        _ => null,
    };

    private void OnDragSourceDown(object sender, MouseButtonEventArgs e) => UiGuard.Run("Builder window: press on a drag source", () => DragSourceDown(sender, e));

    private void DragSourceDown(object sender, MouseButtonEventArgs e)
    {
        _pendingDrag = null;
        // Typing in a name box or clicking ✎ × ▲ ▼ never starts a drag. A tile's or a row's icon does,
        // once the pointer moves past the drag distance; a still click opens the icon picker.
        if (IsInteractive(e.OriginalSource as DependencyObject, sender as DependencyObject)) return;
        _pendingDrag = DragFor((sender as FrameworkElement)?.DataContext);
        _dragStart = PointerAt(e, this);
    }

    // Wired to PreviewMouseMove (WU3 review N6): a ButtonBase that holds the mouse (the icon after a
    // press) marks the bubbling MouseMove handled, so a bubbling handler on the tile or row would never
    // see the move that should start the drag. The tunnelling move reaches the tile first.
    private void OnDragSourceMove(object sender, MouseEventArgs e) => UiGuard.Run("Builder window: drag", () => DragSourceMove(sender, e));

    private void DragSourceMove(object sender, MouseEventArgs e)
    {
        if (_pendingDrag is null || !Model.IsIdle) return;
        if (!LeftPressed(e))
        {
            // The button came up where the window did not see it (outside it): the armed drag is stale.
            _pendingDrag = null;
            return;
        }
        var p = PointerAt(e, this);
        if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var drag = _pendingDrag;
        _pendingDrag = null;
        // A press on the icon button captured the mouse: release it, so the button is no longer
        // pressed and no Click (the icon picker) follows the drag (WU3 review B1).
        if (Mouse.Captured is not null) Mouse.Capture(null);
        Model.BeginDrag(drag);
        try
        {
            var effects = drag.IsNew ? DragDropEffects.Copy : DragDropEffects.Move;
            RunDragDrop((DependencyObject)sender, new DataObject(typeof(BuilderDrag), drag), effects);
        }
        finally
        {
            Model.EndDrag();
        }
    }

    /// <summary>The icon buttons of tiles and stack rows are drag handles as well as buttons.</summary>
    internal const string IconHandleName = "IconHandle";

    private static bool IsInteractive(DependencyObject? source, DependencyObject? stop)
    {
        for (var d = source; d is not null && !ReferenceEquals(d, stop); d = ParentOf(d))
            if (d is TextBoxBase || d is ButtonBase { Name: not IconHandleName }) return true;
        return false;
    }

    private DragDropEffects DropEffect(bool accepted) =>
        !accepted ? DragDropEffects.None : Model.CurrentDrag?.IsNew == true ? DragDropEffects.Copy : DragDropEffects.Move;

    // The card's handlers run in the Preview phase, before anything inside the card sees the drag.
    // A TextBox takes every drag that passes over it (TextEditor marks DragOver handled, with no
    // effect, for a non-text payload), and the tiles' name boxes cover much of each tile (the
    // TextBox lesson, WU5 review B1). So the card handles any BuilderDrag itself: it finds the tile,
    // the stack or the stack row under the pointer and reports where (TileHit) to the view-model;
    // anything else over the card is the card (a panel reorder, or a tool to the panel's end). A text
    // drag is left alone, so it still reaches a name box.
    // On a failure the pointer must not promise a drop that will not land (SC-046 review N6).
    private void OnCardDragOver(object sender, DragEventArgs e) => UiGuard.Run("Builder window: drag over a card", () => CardDragOver(sender, e),
        onFailure: () =>
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
        });

    private void CardDragOver(object sender, DragEventArgs e)
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

    private void OnCardDrop(object sender, DragEventArgs e) => UiGuard.Run("Builder window: drop on a card", () => CardDrop(sender, e));

    private void CardDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(BuilderDrag)) || sender is not FrameworkElement { DataContext: PanelCardViewModel card } cardRoot) return;
        if (Model.CurrentDrag?.Kind != DragKind.Panel && HitAt(e.OriginalSource as DependencyObject, cardRoot, e) is { } hit)
            Model.DropOnTile(hit);
        else
            Model.DropOnPanel(card.Index, UpperHalf(e, cardRoot));
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => UiGuard.Run("Builder window: drag leaves a card", Model.ClearDropMarkers);

    /// <summary>
    /// Focus entering a card (its name, a tile's or a row's name box, a button) selects that card, so
    /// a double-click in Commands adds to the panel being edited (WU3 review F1). A TextBox handles
    /// the mouse press, so the card's click binding never sees a click into a name box.
    /// </summary>
    private void OnCardFocus(object sender, KeyboardFocusChangedEventArgs e) => UiGuard.Run("Builder window: focus a card", () =>
    {
        if (sender is FrameworkElement { DataContext: PanelCardViewModel card }) Model.SelectPanel(card.Index);
    });

    private static bool UpperHalf(DragEventArgs e, FrameworkElement target) => e.GetPosition(target).Y < target.ActualHeight / 2;

    /// <summary>
    /// The tile, stack or stack row under the pointer (from the drag's original source up to the
    /// card): a row's position is across its whole stack (its edges insert beside the stack) and down
    /// the row (before or after it); a tile's or a stack's is across it. The 10 px gutter after each
    /// tile or stack (its cell's padding) counts as that tile's nearer edge, so a drop between two
    /// tiles inserts between them (WU3 review N4). Null outside every tile and gutter.
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
                    return TileHit.On(tile.Panel.Index, tile.Index, p.X, element.ActualWidth, tile.Model);
                }
                case "StackGroup" when element.DataContext is StackGroupViewModel stack:
                {
                    var p = positionIn(element);
                    if (row is { DataContext: ToolTileViewModel r })
                    {
                        var y = positionIn(row).Y;
                        return TileHit.OnRow(stack.Panel.Index, stack.Index, r.Row, p.X, element.ActualWidth, y, row.ActualHeight, stack.Model);
                    }
                    return TileHit.On(stack.Panel.Index, stack.Index, p.X, element.ActualWidth, stack.Model);
                }
                case "TileCell" when element is Border { Child: FrameworkElement inner } && element.DataContext is PanelItemViewModel item:
                {
                    // In the gutter: the nearer edge of the tile or stack the cell holds.
                    var p = positionIn(inner);
                    var x = p.X < inner.ActualWidth / 2 ? 0 : inner.ActualWidth;
                    return TileHit.On(item.Panel.Index, item.Index, x, inner.ActualWidth, item.Model);
                }
            }
        }
        return null;
    }

    private static DependencyObject? ParentOf(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    // ---- the Custom Tools rows by keyboard (feature #18, WU4 review F7) ------------------------

    /// <summary>The Custom Tools rows the tree shows now, in order.</summary>
    internal IReadOnlyList<Border> CustomToolRows() =>
        BordersIn(CatalogTreeView).Where(b => b.Name == "Row" && b.DataContext is CustomToolItemViewModel && b.IsVisible).ToList();

    private static IEnumerable<Border> BordersIn(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Border b) yield return b;
            foreach (var deeper in BordersIn(child)) yield return deeper;
        }
    }

    /// <summary>↓ in Filter… moves to the first Custom Tools row (when the list shows one).</summary>
    private void OnFilterKeyDown(object sender, KeyEventArgs e) => UiGuard.Run("Builder window: key in Filter", () =>
    {
        if (e.Key != Key.Down || CustomToolRows().FirstOrDefault() is not { } first) return;
        e.Handled = first.Focus();
    });

    /// <summary>
    /// On a focused Custom Tools row: ↑ ↓ move between rows (↑ on the first goes to Filter…, ↓ on the
    /// last to the first catalogue node, not the row's own ✎: delta review N1); Delete removes it
    /// everywhere (asking first), then focus goes to the next row, the previous one when it was the
    /// last, or Filter… when the list is empty (delta review F10); a cancelled remove keeps it.
    /// </summary>
    private void OnCustomToolKeyDown(object sender, KeyEventArgs e) => UiGuard.Run("Builder window: key on a Custom Tools row", () => CustomToolKeyDown(sender, e));

    private void CustomToolKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not Border { DataContext: CustomToolItemViewModel item } row) return;
        var rows = CustomToolRows();
        var at = rows.ToList().IndexOf(row);
        switch (e.Key)
        {
            case Key.Delete:
                e.Handled = true;
                if (!item.RemoveCommand.CanExecute(null)) return;
                var links = rows.Select(r => ((CustomToolItemViewModel)r.DataContext).Tool.Link).ToList();
                item.RemoveCommand.Execute(null);
                // The rows are rebuilt by then: focus once the new ones are laid out.
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                    UiGuard.Run("Builder window: focus after a remove", () => FocusAfterRemove(item.Tool.Link, links, at))));
                break;
            case Key.Up or Key.Down:
                var next = at + (e.Key == Key.Down ? 1 : -1);
                if (next >= 0 && next < rows.Count) e.Handled = rows[next].Focus();
                else if (next >= rows.Count) e.Handled = FirstCatalogItem()?.Focus() ?? false;
                else e.Handled = FilterBox.Focus();
                break;
        }
    }

    /// <summary>The first catalogue node the tree shows (the Custom Tools group is not one).</summary>
    internal TreeViewItem? FirstCatalogItem() =>
        TreeItemsIn(CatalogTreeView).FirstOrDefault(t => t.DataContext is CatalogNodeViewModel && t.IsVisible);

    private static IEnumerable<TreeViewItem> TreeItemsIn(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TreeViewItem t) yield return t;
            foreach (var deeper in TreeItemsIn(child)) yield return deeper;
        }
    }

    /// <summary>
    /// After Delete on a row: the row again when the remove was cancelled; else the row that followed
    /// it, or the one before when it was the last; else Filter….
    /// </summary>
    private void FocusAfterRemove(string removed, IReadOnlyList<string> before, int at)
    {
        var rows = CustomToolRows();
        Border? Row(string link) => rows.FirstOrDefault(r => ((CustomToolItemViewModel)r.DataContext).Tool.Link == link);
        var target = Row(removed)
                     ?? before.Skip(at + 1).Select(Row).FirstOrDefault(r => r is not null)
                     ?? before.Take(at).Reverse().Select(Row).FirstOrDefault(r => r is not null);
        if (target is not null) target.Focus();
        else FilterBox.Focus();
    }

    // ---- the Panels pane header ---------------------------------------------------------------

    /// <summary>"on tab [name]": the General tab opens with its Ribbon tab name field focused.</summary>
    private void OnTabNameClick(object sender, RoutedEventArgs e) => UiGuard.Run("Builder window: on tab", FocusTabName);

    /// <summary>Opens the Profile pane's General tab and focuses the Ribbon tab name field.</summary>
    internal void FocusTabName()
    {
        ProfileTabs.SelectedItem = GeneralTab;
        // The tab's content is realized on selection: focus it once layout has run.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() => UiGuard.Run("Builder window: focus the tab name", () =>
        {
            FocusManager.SetFocusedElement(this, TabBox);
            TabBox.Focus();
            TabBox.SelectAll();
        })));
    }
}
