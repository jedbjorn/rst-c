// rst-c's own (not vendored): drag and drop on a real ProfileBuilderWindow (spec #16, WU5 review
// B1; feature #18, spec #19, WU3, AC5: each row of the stack table). DragDrop.DoDragDrop runs the
// OS drag loop and cannot run headless, so these tests raise the routed events WPF's DragDrop
// raises for each step (PreviewDragOver then DragOver, PreviewDrop then Drop, each bubbling phase
// skipped once the preview is handled), with a DragEventArgs built through its internal
// constructor, on the real visual tree: the tiles, the stack groups, their rows and the name boxes
// inside them (a TextBox takes every drag that passes over it; the card's preview handlers must see
// the drag first). The pointer positions are real points on those elements, so the edge and middle
// zones are worked out by the window as they would be under a mouse.

using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RST.Core.Builder;
using RST.Core.Theming;
using RST.RuntimeTests.Support;
using RST.UI.Builder;
using RST.UI.Controls;
using RST.UI.Dialogs;
using RST.UI.Theming;
using Xunit;

namespace RST.RuntimeTests.Builder;

[Collection(WpfThemeCollection.Name)]
public class ProfileBuilderDragEventTests
{
    private static void Sta(Action action) => Screenshots.RunSta(() =>
    {
        try
        {
            RstTheme.Apply(UiTheme.Light);
            action();
        }
        finally
        {
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    });

    /// <summary>Structure: Frame [Beam, Column, Brace]; Review [Kinship, [Warnings Audit Purge], Notes]; Links [Wiki, Help].</summary>
    private static (ProfileBuilderWindow Window, BuilderHarness Harness) Open(Action<BuilderHarness>? arrange = null)
    {
        var h = BuilderHarness.Create();
        h.Model.SelectedProfileOption = h.Option("Structure");
        arrange?.Invoke(h);
        var window = new ProfileBuilderWindow(h.Model);
        WindowCapture.ShowOffScreen(window);
        return (window, h);
    }

    private static void Close(ProfileBuilderWindow window, BuilderHarness h)
    {
        h.Model.EndDrag();
        h.Dialogs.ChooseAnswers.Enqueue(DialogChoice.Secondary);
        window.Close();
    }

    /// <summary>A DragEventArgs as WPF's DragDrop builds it (the constructor is internal).</summary>
    private static DragEventArgs Args(IDataObject data, DependencyObject target, Point at)
    {
        var ctor = typeof(DragEventArgs).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Single(c => c.GetParameters().Length == 5);
        var values = ctor.GetParameters().Select(p => p.ParameterType switch
        {
            var t when t == typeof(IDataObject) => (object)data,
            var t when t == typeof(DragDropKeyStates) => DragDropKeyStates.LeftMouseButton,
            var t when t == typeof(DragDropEffects) => DragDropEffects.Copy | DragDropEffects.Move,
            var t when t == typeof(DependencyObject) => target,
            var t when t == typeof(Point) => at,
            var t => throw new InvalidOperationException("unexpected DragEventArgs parameter " + t),
        }).ToArray();
        return (DragEventArgs)ctor.Invoke(values);
    }

    /// <summary>One dragover step on <paramref name="over"/>: the preview, then (when not handled) the bubbling event.</summary>
    private static DragEventArgs DragOver(UIElement over, IDataObject data, Point at)
    {
        var args = Args(data, over, at);
        args.RoutedEvent = DragDrop.PreviewDragOverEvent;
        over.RaiseEvent(args);
        if (!args.Handled)
        {
            args.RoutedEvent = DragDrop.DragOverEvent;
            over.RaiseEvent(args);
        }
        return args;
    }

    private static DragEventArgs Drop(UIElement over, IDataObject data, Point at)
    {
        var args = Args(data, over, at);
        args.RoutedEvent = DragDrop.PreviewDropEvent;
        over.RaiseEvent(args);
        if (!args.Handled)
        {
            args.RoutedEvent = DragDrop.DropEvent;
            over.RaiseEvent(args);
        }
        return args;
    }

    private static IDataObject Payload(BuilderDrag drag) => new DataObject(typeof(BuilderDrag), drag);

    /// <summary>Drags <paramref name="drag"/> over <paramref name="over"/> at <paramref name="at"/> and drops it there.</summary>
    private static (DragEventArgs Over, DragEventArgs Drop) DragAndDrop(BuilderHarness h, BuilderDrag drag, UIElement over, Point at)
    {
        h.Model.BeginDrag(drag);
        var o = DragOver(over, Payload(drag), at);
        var d = Drop(over, Payload(drag), at);
        h.Model.EndDrag();
        return (o, d);
    }

    // ── the real elements ──

    private static string Id(DependencyObject d) => AutomationProperties.GetAutomationId(d);

    private static Grid Tile(ProfileBuilderWindow w, int panel, int slot) =>
        WindowCapture.Descendants<Grid>(w.PanelCards).Single(g => g.Name == "Tile" && g.DataContext is ToolTileViewModel { InStack: false } t && t.Panel.Index == panel && t.Index == slot);

    private static Grid StackGroup(ProfileBuilderWindow w, int panel, int slot) =>
        WindowCapture.Descendants<Grid>(w.PanelCards).Single(g => g.Name == "StackGroup" && g.DataContext is StackGroupViewModel s && s.Panel.Index == panel && s.Index == slot);

    private static DockPanel StackHeader(ProfileBuilderWindow w, int panel, int slot) =>
        WindowCapture.Descendants<DockPanel>(StackGroup(w, panel, slot)).Single(d => d.Name == "StackHeader");

    private static Grid StackRow(ProfileBuilderWindow w, int panel, int slot, int row) =>
        WindowCapture.Descendants<Grid>(w.PanelCards).Single(g => g.Name == "StackRow" && g.DataContext is ToolTileViewModel { InStack: true } t && t.Panel.Index == panel && t.Index == slot && t.Row == row);

    /// <summary>The name box inside a tile or a stack row.</summary>
    private static TextBox NameBox(FrameworkElement tileOrRow) =>
        WindowCapture.Descendants<TextBox>(tileOrRow).Single(t => Id(t) == "Builder.ToolName");

    private static TextBox PanelNameBox(ProfileBuilderWindow w, int panel) =>
        WindowCapture.Descendants<TextBox>(w.PanelCards).Single(t => Id(t) == "Builder.PanelName" && t.DataContext is PanelCardViewModel c && c.Index == panel);

    private static Grid Card(ProfileBuilderWindow w, int panel) =>
        WindowCapture.Descendants<Grid>(w.PanelCards).Single(g => g.Name == "CardRoot" && g.DataContext is PanelCardViewModel c && c.Index == panel);

    private static Point Middle(FrameworkElement e) => new(e.ActualWidth / 2, e.ActualHeight / 2);

    private static Point LeftEdge(FrameworkElement e) => new(3, e.ActualHeight / 2);

    private static Point RightEdge(FrameworkElement e) => new(e.ActualWidth - 3, e.ActualHeight / 2);

    private static Point Upper(FrameworkElement e) => new(e.ActualWidth / 2, 2);

    private static Point Lower(FrameworkElement e) => new(e.ActualWidth / 2, e.ActualHeight - 2);

    /// <summary>A point given on <paramref name="from"/> as the same screen point on <paramref name="to"/>.</summary>
    private static Point On(FrameworkElement from, Point p, FrameworkElement to) => from.TranslatePoint(p, to);

    private static string Shape(BuilderHarness h, int panel) => PanelCardTestExtensions.Shape(h.Model.Profile, panel);

    private static BuilderDrag Wall(BuilderHarness h) => BuilderDrag.FromCatalog(h.Command("ID_OBJECTS_WALL").Command!);

    /// <summary>Review's stack with Purge removed (× on its row): a 2-tool stack with room.</summary>
    private static void TwoToolStack(BuilderHarness h) => h.Model.Panels[1].Tile(1, row: 2).RemoveCommand.Execute(null);

    // ── the stack table, one row each ──

    [Fact]
    public void A_tool_dropped_on_the_middle_of_a_loose_tool_makes_a_stack_the_dropped_tool_second() => Sta(() =>
    {
        var (window, h) = Open();
        try
        {
            // Over Column's name box (the TextBox lesson): the card's preview handler sees it first.
            h.Model.BeginDrag(Wall(h));
            var box = NameBox(Tile(window, 0, 1));
            var at = On(Tile(window, 0, 1), Middle(Tile(window, 0, 1)), box);
            var over = DragOver(box, Payload(Wall(h)), at);
            Assert.True(over.Handled);
            Assert.Equal(DragDropEffects.Copy, over.Effects);
            Assert.Equal(DropMarker.Over, h.Model.Panels[0].Tile(1).DropMarker);
            Drop(box, Payload(Wall(h)), at);
            Assert.Equal("Beam,[Column Wall],Brace", Shape(h, 0));

            // A tile dropped on a tile's middle: Brace onto Beam.
            WindowCapture.DoEvents();
            var beam = Tile(window, 0, 0);
            DragAndDrop(h, BuilderDrag.FromSlot(0, 2), beam, Middle(beam));
            Assert.Equal("[Beam Brace],[Column Wall]", Shape(h, 0));
            Assert.True(h.Model.IsDirty);
        }
        finally
        {
            Close(window, h);
        }
    });

    [Fact]
    public void A_tool_dropped_on_the_middle_of_a_stack_or_a_row_s_edge_joins_it() => Sta(() =>
    {
        var (window, h) = Open(TwoToolStack);
        try
        {
            // Kinship onto Warnings' lower half (its name box): joins after Warnings.
            var warnings = StackRow(window, 1, 1, 0);
            var box = NameBox(warnings);
            h.Model.BeginDrag(BuilderDrag.FromSlot(1, 0));
            var at = On(warnings, Lower(warnings), box);
            Assert.Equal(DragDropEffects.Move, DragOver(box, Payload(BuilderDrag.FromSlot(1, 0)), at).Effects);
            Assert.Equal(DropMarker.After, h.Model.Panels[1].Tile(1, row: 0).DropMarker);
            Assert.Equal(DropMarker.None, h.Model.Panels[1].StackAt(1).DropMarker);
            Drop(box, Payload(BuilderDrag.FromSlot(1, 0)), at);
            h.Model.EndDrag();
            Assert.Equal("[Warnings Kinship Audit],Notes", Shape(h, 1));

            // Remove Kinship again, then Beam (another panel) onto the stack's middle (its header): joins at the end.
            h.Model.Panels[1].Tile(0, row: 1).RemoveCommand.Execute(null);
            WindowCapture.DoEvents();
            var header = StackHeader(window, 1, 0);
            h.Model.BeginDrag(BuilderDrag.FromSlot(0, 0));
            DragOver(header, Payload(BuilderDrag.FromSlot(0, 0)), On(StackGroup(window, 1, 0), Middle(StackGroup(window, 1, 0)), header));
            Assert.Equal(DropMarker.Over, h.Model.Panels[1].StackAt(0).DropMarker);
            Drop(header, Payload(BuilderDrag.FromSlot(0, 0)), On(StackGroup(window, 1, 0), Middle(StackGroup(window, 1, 0)), header));
            h.Model.EndDrag();
            Assert.Equal("[Warnings Audit Beam],Notes", Shape(h, 1));
            Assert.Equal("Column,Brace", Shape(h, 0));
        }
        finally
        {
            Close(window, h);
        }
    });

    [Fact]
    public void A_fourth_tool_is_refused_with_the_marker_and_the_message() => Sta(() =>
    {
        var (window, h) = Open();
        try
        {
            var group = StackGroup(window, 1, 1);
            var row = StackRow(window, 1, 1, 1);
            h.Model.BeginDrag(BuilderDrag.FromSlot(1, 0));
            var over = DragOver(NameBox(row), Payload(BuilderDrag.FromSlot(1, 0)), On(row, Upper(row), NameBox(row)));
            // The full stack still takes the drop (a None effect would make OLE skip Drop), so the drop
            // itself refuses it with the message (review N5).
            Assert.Equal(DragDropEffects.Move, over.Effects);
            Assert.Equal(DropMarker.Refused, h.Model.Panels[1].StackAt(1).DropMarker);
            WindowCapture.DoEvents();
            var note = WindowCapture.Descendants<Border>(group).Single(b => Id(b) == "Builder.StackFull");
            Assert.Equal(Visibility.Visible, note.Visibility);
            Assert.Equal("A stack holds 2–3 tools. This one is full.", ((TextBlock)note.Child).Text);

            Drop(NameBox(row), Payload(BuilderDrag.FromSlot(1, 0)), On(row, Upper(row), NameBox(row)));
            h.Model.EndDrag();
            Assert.Equal("Kinship,[Warnings Audit Purge],Notes", Shape(h, 1));
            Assert.True(h.Model.Toast.IsOpen);
            Assert.Equal(PanelStacks.FullMessage, h.Model.Toast.Text);
            Assert.Equal(BannerKind.Warning, h.Model.Toast.Kind);
            Assert.False(h.Model.IsDirty);

            // A catalogue command is refused the same way.
            DragAndDrop(h, Wall(h), StackHeader(window, 1, 1), On(group, Middle(group), StackHeader(window, 1, 1)));
            Assert.Equal("Kinship,[Warnings Audit Purge],Notes", Shape(h, 1));
        }
        finally
        {
            Close(window, h);
        }
    });

    [Fact]
    public void A_tool_dropped_on_the_edge_of_a_loose_tool_or_a_stack_is_inserted_beside_it() => Sta(() =>
    {
        var (window, h) = Open();
        try
        {
            var column = Tile(window, 0, 1);
            h.Model.BeginDrag(Wall(h));
            DragOver(column, Payload(Wall(h)), LeftEdge(column));
            Assert.Equal(DropMarker.Before, h.Model.Panels[0].Tile(1).DropMarker);
            Drop(column, Payload(Wall(h)), LeftEdge(column));
            h.Model.EndDrag();
            Assert.Equal("Beam,Wall,Column,Brace", Shape(h, 0));

            // Notes onto the left edge of the stack, over a row: beside the stack, not in it.
            WindowCapture.DoEvents();
            var group = StackGroup(window, 1, 1);
            var row = StackRow(window, 1, 1, 0);
            var at = On(group, LeftEdge(group), row);
            h.Model.BeginDrag(BuilderDrag.FromSlot(1, 2));
            DragOver(row, Payload(BuilderDrag.FromSlot(1, 2)), at);
            Assert.Equal(DropMarker.Before, h.Model.Panels[1].StackAt(1).DropMarker);
            Drop(row, Payload(BuilderDrag.FromSlot(1, 2)), at);
            h.Model.EndDrag();
            Assert.Equal("Kinship,Notes,[Warnings Audit Purge]", Shape(h, 1));

            // The trailing edge of a loose tool.
            WindowCapture.DoEvents();
            var help = Tile(window, 2, 1);
            DragAndDrop(h, BuilderDrag.FromSlot(2, 0), help, RightEdge(help));
            Assert.Equal("Help,Wiki", Shape(h, 2));
        }
        finally
        {
            Close(window, h);
        }
    });

    [Fact]
    public void A_tool_dragged_out_of_a_3_tool_stack_leaves_2() => Sta(() =>
    {
        var (window, h) = Open();
        try
        {
            // Audit onto the Links card (outside its tiles): appended there.
            var links = Card(window, 2);
            var drag = BuilderDrag.FromSlot(1, 1, row: 1);
            h.Model.BeginDrag(drag);
            Assert.True(h.Model.Panels[1].Tile(1, row: 1).IsDragSource);
            DragOver(links, Payload(drag), new Point(links.ActualWidth - 20, links.ActualHeight - 12));
            Assert.Equal(DropMarker.Over, h.Model.Panels[2].DropMarker);
            Drop(links, Payload(drag), new Point(links.ActualWidth - 20, links.ActualHeight - 12));
            h.Model.EndDrag();
            Assert.Equal("Kinship,[Warnings Purge],Notes", Shape(h, 1));
            Assert.Equal("Wiki,Help,Audit", Shape(h, 2));
        }
        finally
        {
            Close(window, h);
        }
    });

    [Fact]
    public void A_tool_dragged_out_of_a_2_tool_stack_dissolves_it_in_place() => Sta(() =>
    {
        var (window, h) = Open(TwoToolStack);
        try
        {
            var brace = Tile(window, 0, 2);
            DragAndDrop(h, BuilderDrag.FromSlot(1, 1, row: 0), brace, RightEdge(brace));
            Assert.Equal("Kinship,Audit,Notes", Shape(h, 1));
            Assert.Equal("Beam,Column,Brace,Warnings", Shape(h, 0));
            Assert.Empty(h.Model.Profile.Stacks);
            Assert.All(h.Model.Panels[1].Items, i => Assert.IsType<ToolTileViewModel>(i));
        }
        finally
        {
            Close(window, h);
        }
    });

    [Fact]
    public void A_stack_dragged_by_its_header_moves_whole_and_never_goes_inside_another() => Sta(() =>
    {
        var (window, h) = Open();
        try
        {
            // Onto Beam's middle, left half: before Beam, not a stack of a stack.
            var beam = Tile(window, 0, 0);
            var drag = BuilderDrag.FromSlot(1, 1);
            h.Model.BeginDrag(drag);
            Assert.True(h.Model.Panels[1].StackAt(1).IsDragSource);
            var at = new Point(beam.ActualWidth / 2 - 8, beam.ActualHeight / 2);
            DragOver(beam, Payload(drag), at);
            Assert.Equal(DropMarker.Before, h.Model.Panels[0].Tile(0).DropMarker);
            Drop(beam, Payload(drag), at);
            h.Model.EndDrag();
            Assert.Equal("[Warnings Audit Purge],Beam,Column,Brace", Shape(h, 0));
            Assert.Equal("Kinship,Notes", Shape(h, 1));
            Assert.Single(h.Model.Profile.Stacks);
        }
        finally
        {
            Close(window, h);
        }
    });

    [Fact]
    public void A_catalogue_command_dropped_onto_a_stack_joins_it_if_it_has_room() => Sta(() =>
    {
        var (window, h) = Open(TwoToolStack);
        try
        {
            var audit = StackRow(window, 1, 1, 1);
            DragAndDrop(h, Wall(h), NameBox(audit), On(audit, Upper(audit), NameBox(audit)));
            Assert.Equal("Kinship,[Warnings Wall Audit],Notes", Shape(h, 1));
            var added = h.Model.Profile.Stacks.Values.Single().Tools[1];
            Assert.Equal("ID_OBJECTS_WALL", added.CommandId);
        }
        finally
        {
            Close(window, h);
        }
    });

    [Fact]
    public void A_panel_over_a_tile_s_name_box_falls_through_to_its_card() => Sta(() =>
    {
        var (window, h) = Open();
        try
        {
            var frame = BuilderDrag.FromPanel(0);
            h.Model.BeginDrag(frame);
            var help = Tile(window, 2, 1);
            var box = NameBox(help);
            var card = Card(window, 2);
            var at = On(card, new Point(40, card.ActualHeight - 30), box);
            Assert.Equal(DragDropEffects.Move, DragOver(box, Payload(frame), at).Effects);
            Assert.Equal(DropMarker.After, h.Model.Panels[2].DropMarker);
            Assert.All(h.Model.Panels.SelectMany(p => p.Tiles), s => Assert.Equal(DropMarker.None, s.DropMarker));
            Drop(box, Payload(frame), at);
            Assert.Equal(new[] { "Review", "Links", "Frame" }, h.Model.Panels.Select(p => p.Name));

            // And over a panel's name box: before or after that card.
            WindowCapture.DoEvents();
            var links = BuilderDrag.FromPanel(1);
            h.Model.BeginDrag(links);
            var name = PanelNameBox(window, 0);
            DragOver(name, Payload(links), new Point(4, 2));
            Assert.Equal(DropMarker.Before, h.Model.Panels[0].DropMarker);
        }
        finally
        {
            Close(window, h);
        }
    });

    [Fact]
    public void Text_dragged_over_a_name_box_is_still_the_text_box_s() => Sta(() =>
    {
        var (window, h) = Open();
        try
        {
            var box = NameBox(Tile(window, 0, 0));
            var text = new DataObject(DataFormats.UnicodeText, "pasted");
            var over = DragOver(box, text, new Point(4, 4));
            Assert.True(over.Handled, "the TextBox handles a text drag");
            Assert.All(h.Model.Panels.SelectMany(p => p.Tiles), s => Assert.Equal(DropMarker.None, s.DropMarker));
            Assert.All(h.Model.Panels, p => Assert.Equal(DropMarker.None, p.DropMarker));
        }
        finally
        {
            Close(window, h);
        }
    });

    [Fact]
    public void Leaving_the_window_straight_from_a_name_box_clears_the_marker() => Sta(() =>
    {
        var (window, h) = Open();
        try
        {
            h.Model.BeginDrag(Wall(h));
            var tile = Tile(window, 0, 1);
            var box = NameBox(tile);
            DragOver(box, Payload(Wall(h)), On(tile, RightEdge(tile), box));
            Assert.Equal(DropMarker.After, h.Model.Panels[0].Tile(1).DropMarker);

            // The pointer leaves the window from the name box: WPF raises DragLeave there (the TextBox handles it).
            var leave = Args(Payload(Wall(h)), box, new Point(1, 1));
            leave.RoutedEvent = DragDrop.PreviewDragLeaveEvent;
            box.RaiseEvent(leave);
            if (!leave.Handled)
            {
                leave.RoutedEvent = DragDrop.DragLeaveEvent;
                box.RaiseEvent(leave);
            }
            Assert.All(h.Model.Panels.SelectMany(p => p.Tiles), s => Assert.Equal(DropMarker.None, s.DropMarker));
            Assert.All(h.Model.Panels, p => Assert.Equal(DropMarker.None, p.DropMarker));
        }
        finally
        {
            Close(window, h);
        }
    });

    [Fact]
    public void The_window_finds_the_tile_stack_or_row_under_the_pointer() => Sta(() =>
    {
        var (window, h) = Open();
        try
        {
            var tile = Tile(window, 0, 1);
            var hit = ProfileBuilderWindow.HitAt(NameBox(tile), Card(window, 0), e => tile.TranslatePoint(Middle(tile), (UIElement)e));
            Assert.Equal(0, hit!.Value.Panel);
            Assert.Equal(1, hit.Value.Slot);
            Assert.Equal(-1, hit.Value.Row);
            Assert.Equal(tile.ActualWidth, hit.Value.Width);

            var row = StackRow(window, 1, 1, 2);
            var rowHit = ProfileBuilderWindow.HitAt(NameBox(row), Card(window, 1), e => row.TranslatePoint(Lower(row), (UIElement)e));
            Assert.Equal((1, 1, 2), (rowHit!.Value.Panel, rowHit.Value.Slot, rowHit.Value.Row));
            Assert.Equal(StackGroup(window, 1, 1).ActualWidth, rowHit.Value.Width);
            Assert.True(rowHit.Value.Y > rowHit.Value.Height / 2);

            Assert.Null(ProfileBuilderWindow.HitAt(Card(window, 2), Card(window, 2), _ => new Point()));
        }
        finally
        {
            Close(window, h);
        }
    });

    // ── review fixes: the gutter (N4), back in place (N3), focus selects the card (F1) ──

    /// <summary>The tile's or stack's cell: its 10 px gutter on the right and below.</summary>
    private static Border Cell(FrameworkElement tile) => (Border)VisualTreeHelper.GetParent(tile);

    [Fact]
    public void A_command_dropped_in_the_gutter_between_two_tiles_goes_between_them() => Sta(() =>
    {
        var (window, h) = Open();
        try
        {
            var beam = Tile(window, 0, 0);
            var cell = Cell(beam);
            Assert.Equal("TileCell", cell.Name);
            var gutter = new Point(beam.ActualWidth + 5, beam.ActualHeight / 2); // between Beam and Column
            h.Model.BeginDrag(Wall(h));
            var over = DragOver(cell, Payload(Wall(h)), On(beam, gutter, cell));
            Assert.Equal(DragDropEffects.Copy, over.Effects);
            Assert.Equal(DropMarker.After, h.Model.Panels[0].Tile(0).DropMarker);
            Assert.Equal(DropMarker.None, h.Model.Panels[0].DropMarker);
            Drop(cell, Payload(Wall(h)), On(beam, gutter, cell));
            Assert.Equal("Beam,Wall,Column,Brace", Shape(h, 0));
        }
        finally
        {
            Close(window, h);
        }
    });

    [Fact]
    public void A_tile_dropped_on_its_neighbour_s_adjacent_edge_shows_no_marker_and_changes_nothing() => Sta(() =>
    {
        var (window, h) = Open();
        try
        {
            var column = Tile(window, 0, 1);
            var drag = ProfileBuilderWindow.DragFor(h.Model.Panels[0].Tile(0))!; // Beam onto Column's leading edge
            h.Model.BeginDrag(drag);
            var over = DragOver(column, Payload(drag), LeftEdge(column));
            Assert.Equal(DragDropEffects.None, over.Effects);
            Assert.All(h.Model.Panels[0].Tiles, t => Assert.Equal(DropMarker.None, t.DropMarker));
            Assert.Equal(DropMarker.None, h.Model.Panels[0].DropMarker);
            Drop(column, Payload(drag), LeftEdge(column));
            h.Model.EndDrag();
            Assert.Equal("Beam,Column,Brace", Shape(h, 0));
            Assert.False(h.Model.IsDirty);
        }
        finally
        {
            Close(window, h);
        }
    });

    [Fact]
    public void Focusing_a_tile_s_name_box_selects_its_card() => Sta(() =>
    {
        var (window, h) = Open();
        try
        {
            Assert.Equal(0, h.Model.SelectedPanelIndex);
            var box = NameBox(Tile(window, 2, 1));
            box.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, null, box)
            {
                RoutedEvent = Keyboard.PreviewGotKeyboardFocusEvent,
            });
            Assert.Equal(2, h.Model.SelectedPanelIndex);
            Assert.True(h.Model.Panels[2].IsSelected);
            Assert.False(h.Model.Panels[0].IsSelected);

            // A double-click in Commands now adds to Links, the panel being edited.
            h.Model.AddCatalogCommand.Execute(h.Command("ID_OBJECTS_WALL"));
            Assert.Equal("Wiki,Help,Wall", Shape(h, 2));
        }
        finally
        {
            Close(window, h);
        }
    });
}

/// <summary>
/// Starting a drag with the mouse (feature #18, WU3 review B1): real routed mouse events on the
/// real tile, stack row and name box, with no BeginDrag shortcut. The CI desktop has no mouse, so the
/// pointer position and the left button come from the window's seams, and the OS drag loop is
/// recorded instead of run (it cannot run headless). A press on a tile's or a row's icon arms a drag
/// that starts once the pointer moves past the drag distance; a still click opens the icon picker; a
/// press on a name box or on × arms nothing.
/// </summary>
[Collection(WpfThemeCollection.Name)]
public class ProfileBuilderDragStartTests
{
    private static void Sta(Action action) => Screenshots.RunSta(() =>
    {
        try
        {
            RstTheme.Apply(UiTheme.Light);
            action();
        }
        finally
        {
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    });

    private sealed class Rig
    {
        public required ProfileBuilderWindow Window { get; init; }

        public required BuilderHarness Harness { get; init; }

        public Point Pointer { get; set; } = new(200, 200);

        public List<(DependencyObject Source, BuilderDrag Drag, DragDropEffects Effects, bool SourceFaded)> Started { get; } = [];
    }

    private static Rig Open()
    {
        var h = BuilderHarness.Create();
        h.Model.SelectedProfileOption = h.Option("Structure");
        var window = new ProfileBuilderWindow(h.Model);
        WindowCapture.ShowOffScreen(window);
        var rig = new Rig { Window = window, Harness = h };
        window.PointerAt = (_, _) => rig.Pointer;
        window.LeftPressed = _ => true;
        window.RunDragDrop = (source, data, effects) =>
        {
            var drag = (BuilderDrag)data.GetData(typeof(BuilderDrag))!;
            var faded = h.Model.Panels.SelectMany(p => p.Items).Any(i => i.IsDragSource)
                        || h.Model.Panels.SelectMany(p => p.Tiles).Any(t => t.IsDragSource);
            rig.Started.Add((source, drag, effects, faded));
            return DragDropEffects.None;
        };
        return rig;
    }

    private static void Close(Rig rig)
    {
        rig.Harness.Model.EndDrag();
        rig.Harness.Dialogs.ChooseAnswers.Enqueue(DialogChoice.Secondary);
        rig.Window.Close();
    }

    private static string Id(DependencyObject d) => AutomationProperties.GetAutomationId(d);

    /// <summary>
    /// A left press as the input system raises it: the tunnelling PreviewMouseDown, then the bubbling
    /// MouseDown. Each element on the route re-raises them as its own (direct) Preview/MouseLeftButtonDown,
    /// which is what the tile's and row's handlers listen to.
    /// </summary>
    private static void Press(UIElement on)
    {
        on.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
        on.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent });
    }

    /// <summary>A move as the input system raises it: the tunnelling PreviewMouseMove, then (unless handled) the bubbling MouseMove.</summary>
    private static void Move(UIElement on)
    {
        var preview = new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.PreviewMouseMoveEvent };
        on.RaiseEvent(preview);
        if (preview.Handled) return;
        on.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseMoveEvent });
    }

    private static Button IconOf(FrameworkElement tileOrRow) =>
        WindowCapture.Descendants<Button>(tileOrRow).Single(b => b.Name == ProfileBuilderWindow.IconHandleName);

    private static Grid Tile(ProfileBuilderWindow w, int panel, int slot) =>
        WindowCapture.Descendants<Grid>(w.PanelCards).Single(g => g.Name == "Tile" && g.DataContext is ToolTileViewModel { InStack: false } t && t.Panel.Index == panel && t.Index == slot);

    private static Grid StackRow(ProfileBuilderWindow w, int panel, int slot, int row) =>
        WindowCapture.Descendants<Grid>(w.PanelCards).Single(g => g.Name == "StackRow" && g.DataContext is ToolTileViewModel { InStack: true } t && t.Panel.Index == panel && t.Index == slot && t.Row == row);

    [Fact]
    public void Pressing_a_tile_s_icon_and_moving_starts_a_drag_of_that_tile() => Sta(() =>
    {
        var rig = Open();
        try
        {
            var tile = Tile(rig.Window, 0, 1); // Column
            var icon = IconOf(tile);
            Press(icon);
            var expected = BuilderDrag.FromSlot(0, 1, -1, rig.Harness.Model.Profile.Panels[0].Slots[1]);
            Assert.Equal(expected, rig.Window.PendingDrag);

            // Within the drag distance: still a click, no drag.
            rig.Pointer = new Point(201, 200);
            Move(icon);
            Assert.Empty(rig.Started);

            rig.Pointer = new Point(200 + SystemParameters.MinimumHorizontalDragDistance + 4, 200);
            Move(icon);
            var started = Assert.Single(rig.Started);
            Assert.Same(tile, started.Source);
            Assert.Equal(expected, started.Drag);
            Assert.Equal(DragDropEffects.Move, started.Effects);
            Assert.True(started.SourceFaded, "the dragged tile is drawn faded during the drag");
            Assert.Null(rig.Window.PendingDrag);
            Assert.Null(rig.Harness.Model.CurrentDrag);
            Assert.False(rig.Harness.Model.IsIconPickerOpen, "a drag from the icon is not a click on it");
        }
        finally
        {
            Close(rig);
        }
    });

    [Fact]
    public void A_drag_starts_from_an_icon_that_holds_the_mouse_and_handles_the_bubbling_move() => Sta(() =>
    {
        // Review N6. With a real mouse the icon button captures the mouse on the press and marks every
        // bubbling MouseMove handled while it is held (ButtonBase.OnMouseMove). The CI desktop has no
        // pressed button, so ButtonBase never does that here: the icons get a handler that does the
        // same, and the button takes the capture for real where the session allows it.
        var rig = Open();
        try
        {
            foreach (var (element, expected) in new (FrameworkElement, BuilderDrag)[]
                     {
                         (Tile(rig.Window, 0, 1), ProfileBuilderWindow.DragFor(rig.Harness.Model.Panels[0].Tile(1))!),
                         (StackRow(rig.Window, 1, 1, 2), ProfileBuilderWindow.DragFor(rig.Harness.Model.Panels[1].Tile(1, row: 2))!),
                     })
            {
                rig.Started.Clear();
                var icon = IconOf(element);
                var bubblingMoves = 0;
                icon.AddHandler(UIElement.MouseMoveEvent, new MouseEventHandler((_, e) =>
                {
                    bubblingMoves++;
                    e.Handled = true; // as ButtonBase does while it holds the mouse
                }));
                rig.Pointer = new Point(200, 200);
                Press(icon);
                var captured = icon.CaptureMouse();
                Assert.True(captured, "the runner granted the capture, so the test is not vacuous");
                rig.Pointer = new Point(230, 200);
                Move(icon);

                var started = Assert.Single(rig.Started);
                Assert.Equal(expected, started.Drag);
                Assert.Same(element, started.Source);
                // The button saw the move too and handled it (a capture change can add a synthesized move).
                Assert.True(bubblingMoves >= 1, "the bubbling move reached the icon");
                Assert.False(icon.IsMouseCaptured, $"the icon released the mouse (captured before: {captured})");
                Assert.False(icon.IsPressed, "no Click (the icon picker) follows the drag");
                Assert.False(rig.Harness.Model.IsIconPickerOpen);
                WindowCapture.DoEvents();
            }
        }
        finally
        {
            Close(rig);
        }
    });

    [Fact]
    public void Pressing_a_stack_row_s_icon_and_moving_starts_a_drag_of_that_row() => Sta(() =>
    {
        var rig = Open();
        try
        {
            var row = StackRow(rig.Window, 1, 1, 1); // Review › Audit
            Press(IconOf(row));
            rig.Pointer = new Point(200, 200 + SystemParameters.MinimumVerticalDragDistance + 4);
            Move(IconOf(row));
            var started = Assert.Single(rig.Started);
            var audit = PanelStacks.ToolsOf(rig.Harness.Model.Profile, rig.Harness.Model.Profile.Panels[1].Slots[1])[1];
            Assert.Equal(BuilderDrag.FromSlot(1, 1, 1, audit), started.Drag);
            Assert.Same(row, started.Source);

            // The row's empty area right of its name box starts one too.
            rig.Started.Clear();
            var face = WindowCapture.Descendants<Border>(row).Single(b => b.Name == "RowFace");
            rig.Pointer = new Point(200, 200);
            Press(face);
            rig.Pointer = new Point(230, 200);
            Move(face);
            Assert.Single(rig.Started);
        }
        finally
        {
            Close(rig);
        }
    });

    [Fact]
    public void A_still_click_on_the_icon_opens_the_picker_and_starts_no_drag() => Sta(() =>
    {
        var rig = Open();
        try
        {
            var icon = IconOf(Tile(rig.Window, 0, 0));
            Press(icon);
            Assert.NotNull(rig.Window.PendingDrag);
            ((IInvokeProvider)new ButtonAutomationPeer(icon).GetPattern(PatternInterface.Invoke)).Invoke();
            WindowCapture.DoEvents();
            Assert.True(rig.Harness.Model.IsIconPickerOpen);
            Assert.Empty(rig.Started);

            var rowIcon = IconOf(StackRow(rig.Window, 1, 1, 0));
            rig.Harness.Model.IconPicker!.CancelCommand.Execute(null);
            ((IInvokeProvider)new ButtonAutomationPeer(rowIcon).GetPattern(PatternInterface.Invoke)).Invoke();
            WindowCapture.DoEvents();
            Assert.True(rig.Harness.Model.IsIconPickerOpen, "a stack row's icon opens the picker too");
        }
        finally
        {
            Close(rig);
        }
    });

    [Fact]
    public void A_press_on_a_name_box_or_on_x_arms_no_drag_and_a_hidden_x_takes_no_press() => Sta(() =>
    {
        var rig = Open();
        try
        {
            var tile = Tile(rig.Window, 0, 0);
            var box = WindowCapture.Descendants<TextBox>(tile).Single(t => Id(t) == "Builder.ToolName");
            Press(box);
            Assert.Null(rig.Window.PendingDrag);
            rig.Pointer = new Point(260, 260);
            Move(box);
            Assert.Empty(rig.Started);

            var remove = WindowCapture.Descendants<Button>(tile).Single(b => Id(b) == "Builder.RemoveTool");
            Press(remove);
            Assert.Null(rig.Window.PendingDrag);

            // Not hovered and not focused (Brace; the presses above focused Beam): the × (and WU4's ✎)
            // are invisible and take no press.
            var corner = WindowCapture.Descendants<StackPanel>(Tile(rig.Window, 0, 2)).Single(s => s.Name == "Corner");
            Assert.Equal(0, corner.Opacity);
            Assert.False(corner.IsHitTestVisible);
            var rowCorner = WindowCapture.Descendants<StackPanel>(StackRow(rig.Window, 1, 1, 0)).Single(s => s.Name == "Corner");
            Assert.False(rowCorner.IsHitTestVisible);

            // The tile's source line (no button, no box) starts a drag.
            var source = WindowCapture.Descendants<TextBlock>(tile).Single(t => Id(t) == "Builder.ToolSource");
            rig.Pointer = new Point(200, 200);
            Press(source);
            rig.Pointer = new Point(230, 200);
            Move(source);
            Assert.Single(rig.Started);
        }
        finally
        {
            Close(rig);
        }
    });

    /// <summary>
    /// The element a real press at <paramref name="at"/> (a point on <paramref name="on"/>) lands on: the
    /// window's input hit test, the one the mouse uses (it skips what is hidden, disabled or not hit-test
    /// visible, such as the hover-only ×; VisualTreeHelper.HitTest would not).
    /// </summary>
    private static UIElement HitAt(ProfileBuilderWindow w, FrameworkElement on, Point at) =>
        Assert.IsAssignableFrom<UIElement>(w.InputHitTest(on.TranslatePoint(at, w)));

    private static bool IsInside(DependencyObject? d, DependencyObject ancestor)
    {
        for (; d is not null; d = VisualTreeHelper.GetParent(d))
            if (ReferenceEquals(d, ancestor)) return true;
        return false;
    }

    [Fact]
    public void A_press_anywhere_on_a_tile_but_its_name_box_drags_it_even_when_the_move_leaves_the_tile() => Sta(() =>
    {
        // SC-032 (F18 WU5 VM finding F2): a press in the strip below a tile's source line, at the
        // tile's centre 3 px above its border, started no drag. The tile is hit-tested as a real
        // mouse would hit it, every 2 px across its face: each point belongs to the tile, and the
        // only one that does not drag is the name box (the hidden × takes no press).
        var rig = Open();
        try
        {
            var tile = Tile(rig.Window, 0, 1); // Column
            var face = WindowCapture.Descendants<Border>(tile).Single(b => b.Name == "TileFace");
            var box = WindowCapture.Descendants<TextBox>(tile).Single(t => Id(t) == "Builder.ToolName");
            var source = WindowCapture.Descendants<TextBlock>(tile).Single(t => Id(t) == "Builder.ToolSource");
            for (var y = 2.0; y < face.ActualHeight - 2; y += 2)
            for (var x = 6.0; x < face.ActualWidth - 6; x += 2)
            {
                var hit = HitAt(rig.Window, face, new Point(x, y));
                Assert.True(IsInside(hit, tile), $"({x}, {y}) on the tile hits {hit.GetType().Name} outside it");
                if (IsInside(hit, box)) continue;
                Assert.False(ProfileBuilderWindow.IsInteractive(hit, tile), $"a press at ({x}, {y}) on the tile lands on a button or box ({hit.GetType().Name})");
            }

            // VM finding F2's point: below the source line, 3 px above the border's inner edge.
            var at = new Point(face.ActualWidth / 2, face.ActualHeight - face.BorderThickness.Bottom - 3);
            var sourceBottom = source.TranslatePoint(new Point(0, source.ActualHeight), face).Y;
            Assert.True(at.Y > sourceBottom, $"the point (y {at.Y}) is below the source line (ends at y {sourceBottom})");
            var strip = HitAt(rig.Window, face, at);
            Assert.True(IsInside(strip, tile) && !ProfileBuilderWindow.IsInteractive(strip, tile), $"the strip below the source line hits {strip.GetType().Name}");

            // The press arms the tile's drag; the move that crosses the drag distance is already in the
            // tile's gutter (its cell, outside the tile and no drag source): the drag of the tile starts.
            var expected = ProfileBuilderWindow.DragFor(rig.Harness.Model.Panels[0].Tile(1))!;
            rig.Pointer = new Point(200, 200);
            Press(strip);
            Assert.Equal(expected, rig.Window.PendingDrag);
            var cell = Assert.IsType<Border>(VisualTreeHelper.GetParent(tile));
            Assert.Equal("TileCell", cell.Name);
            rig.Pointer = new Point(200, 200 + SystemParameters.MinimumVerticalDragDistance + 4);
            Move(cell);
            var started = Assert.Single(rig.Started);
            Assert.Same(tile, started.Source);
            Assert.Equal(expected, started.Drag);
            Assert.True(started.SourceFaded, "the dragged tile is drawn faded during the drag");
            Assert.Null(rig.Window.PendingDrag);

            // A move that lands on another tile first still drags the pressed one, from the pressed one.
            rig.Started.Clear();
            rig.Pointer = new Point(200, 200);
            Press(HitAt(rig.Window, face, at));
            rig.Pointer = new Point(240, 200);
            Move(Tile(rig.Window, 0, 2));
            started = Assert.Single(rig.Started);
            Assert.Same(tile, started.Source);
            Assert.Equal(expected, started.Drag);
        }
        finally
        {
            Close(rig);
        }
    });

    /// <summary>A left release as the input system raises it: PreviewMouseUp, then MouseUp (each element cracks its own left-button event).</summary>
    private static void Release(UIElement on)
    {
        on.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent });
        on.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent });
    }

    [Fact]
    public void A_still_click_disarms_the_drag_so_a_later_move_does_not_drag_that_tile() => Sta(() =>
    {
        // WU3 review leftover (folded into WU4): the armed drag is cleared on the button's release.
        var rig = Open();
        try
        {
            var beam = Tile(rig.Window, 0, 0);
            var face = WindowCapture.Descendants<Border>(beam).Single(b => b.Name == "TileFace");
            rig.Pointer = new Point(200, 200);
            Press(face);
            Assert.NotNull(rig.Window.PendingDrag);
            Release(face);
            Assert.Null(rig.Window.PendingDrag);

            // A press elsewhere that arms nothing (the card's empty area), then a move over Beam: no drag.
            var empty = WindowCapture.Descendants<FrameworkElement>(rig.Window.PanelCards).First(e => e.Name == "CardRoot");
            Press(empty);
            rig.Pointer = new Point(240, 200);
            Move(face);
            Assert.Empty(rig.Started);

            // A release the window never saw (outside it): the next move with the button up disarms it.
            Press(face);
            Assert.NotNull(rig.Window.PendingDrag);
            rig.Window.LeftPressed = _ => false;
            rig.Pointer = new Point(280, 200);
            Move(face);
            Assert.Null(rig.Window.PendingDrag);
            rig.Window.LeftPressed = _ => true;
            Move(face);
            Assert.Empty(rig.Started);

            // Review N1: a release outside the window (never seen), back in without a move over a source,
            // a press on a non-source with the button held, then a move over Beam: no drag of the old tile.
            rig.Pointer = new Point(200, 200);
            Press(face);
            Assert.NotNull(rig.Window.PendingDrag);
            Press(empty); // no Release(face) in between: the release happened outside the window
            Assert.Null(rig.Window.PendingDrag);
            rig.Pointer = new Point(260, 200);
            Move(face);
            Assert.Empty(rig.Started);

            // A real source still arms on its own press (the window's handler runs first).
            rig.Pointer = new Point(200, 200);
            Press(face);
            Assert.NotNull(rig.Window.PendingDrag);
            rig.Pointer = new Point(240, 200);
            Move(face);
            Assert.Single(rig.Started);
        }
        finally
        {
            Close(rig);
        }
    });
}
