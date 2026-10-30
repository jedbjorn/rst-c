// rst-c's own (not vendored): drag and drop on a real ProfileBuilderWindow (spec #16, WU5 review
// B1). DragDrop.DoDragDrop runs the OS drag loop and cannot run headless, so these tests raise the
// routed events WPF's DragDrop raises for each step (PreviewDragOver then DragOver, PreviewDrop
// then Drop, each bubbling phase skipped once the preview is handled), with a DragEventArgs built
// through its internal constructor, on the real visual tree. The cases aim at the slot and panel
// name TextBoxes, which take every drag passing over them: on the WU5 code before the fix, no
// marker was set and nothing dropped there.

using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RST.Core.Builder;
using RST.Core.Theming;
using RST.RuntimeTests.Support;
using RST.UI.Builder;
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

    private static (ProfileBuilderWindow Window, BuilderHarness Harness) Open()
    {
        var h = BuilderHarness.Create();
        h.Model.SelectedProfileOption = h.Option("Structure");
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

    private static TextBox NameBox(ProfileBuilderWindow window, string automationId, Func<object, bool> owner) =>
        WindowCapture.Descendants<TextBox>(window.PanelCards)
            .Single(t => System.Windows.Automation.AutomationProperties.GetAutomationId(t) == automationId && owner(t.DataContext));

    private static TextBox SlotNameBox(ProfileBuilderWindow w, int panel, int slot) =>
        NameBox(w, "Builder.SlotName", dc => dc is SlotRowViewModel s && s.Panel.Index == panel && s.Index == slot);

    private static TextBox PanelNameBox(ProfileBuilderWindow w, int panel) =>
        NameBox(w, "Builder.PanelName", dc => dc is PanelCardViewModel c && c.Index == panel);

    private static IDataObject Payload(BuilderDrag drag) => new DataObject(typeof(BuilderDrag), drag);

    private static Point Lower(FrameworkElement e) => new(e.ActualWidth / 2, e.ActualHeight - 2);

    private static Point Upper(FrameworkElement e) => new(e.ActualWidth / 2, 2);

    [Fact]
    public void A_command_dropped_on_a_slot_s_name_box_is_inserted_after_that_slot() => Sta(() =>
    {
        var (window, h) = Open();
        try
        {
            var wall = BuilderDrag.FromCatalog(h.Command("ID_OBJECTS_WALL").Command!);
            h.Model.BeginDrag(wall);
            var box = SlotNameBox(window, 0, 1); // Column
            var over = DragOver(box, Payload(wall), Lower(box));
            Assert.Equal(DragDropEffects.Copy, over.Effects);
            Assert.Equal(DropMarker.After, h.Model.Panels[0].Slots[1].DropMarker);

            var drop = Drop(box, Payload(wall), Lower(box));
            Assert.True(drop.Handled);
            Assert.Equal(new[] { "Beam", "Column", "Wall", "Brace" }, h.Model.Profile.Panels[0].Slots.Select(s => s.Name));
        }
        finally
        {
            Close(window, h);
        }
    });

    [Fact]
    public void A_slot_dropped_on_another_slot_s_name_box_moves_before_it() => Sta(() =>
    {
        var (window, h) = Open();
        try
        {
            var beam = BuilderDrag.FromSlot(0, 0);
            h.Model.BeginDrag(beam);
            var box = SlotNameBox(window, 1, 1); // Review › Checks
            var over = DragOver(box, Payload(beam), Upper(box));
            Assert.Equal(DragDropEffects.Move, over.Effects);
            Assert.Equal(DropMarker.Before, h.Model.Panels[1].Slots[1].DropMarker);

            Drop(box, Payload(beam), Upper(box));
            Assert.Equal(new[] { "Column", "Brace" }, h.Model.Profile.Panels[0].Slots.Select(s => s.Name));
            Assert.Equal(new[] { "Kinship", "Beam", "Checks", "Notes" }, h.Model.Profile.Panels[1].Slots.Select(s => s.Name));
            Assert.Equal(1, h.Model.SelectedPanelIndex);
        }
        finally
        {
            Close(window, h);
        }
    });

    [Fact]
    public void A_panel_dropped_on_another_panel_s_name_box_lands_before_it() => Sta(() =>
    {
        var (window, h) = Open();
        try
        {
            var links = BuilderDrag.FromPanel(2);
            h.Model.BeginDrag(links);
            var box = PanelNameBox(window, 0); // Frame's header name
            var over = DragOver(box, Payload(links), Lower(box));
            Assert.Equal(DragDropEffects.Move, over.Effects);
            Assert.Equal(DropMarker.Before, h.Model.Panels[0].DropMarker);

            Drop(box, Payload(links), Lower(box));
            Assert.Equal(new[] { "Links", "Frame", "Review" }, h.Model.Panels.Select(p => p.Name));
            Assert.Equal("Links", h.Model.SelectedPanel!.Name);
        }
        finally
        {
            Close(window, h);
        }
    });

    [Fact]
    public void A_panel_over_a_slot_s_name_box_falls_through_to_its_card() => Sta(() =>
    {
        var (window, h) = Open();
        try
        {
            var frame = BuilderDrag.FromPanel(0);
            h.Model.BeginDrag(frame);
            var box = SlotNameBox(window, 2, 1); // Links › Help, in the card's lower half
            var over = DragOver(box, Payload(frame), Lower(box));
            Assert.Equal(DragDropEffects.Move, over.Effects);
            Assert.Equal(DropMarker.After, h.Model.Panels[2].DropMarker);
            Assert.All(h.Model.Panels.SelectMany(p => p.Slots), s => Assert.Equal(DropMarker.None, s.DropMarker));

            Drop(box, Payload(frame), Lower(box));
            Assert.Equal(new[] { "Review", "Links", "Frame" }, h.Model.Panels.Select(p => p.Name));
        }
        finally
        {
            Close(window, h);
        }
    });

    [Fact]
    public void A_command_over_a_panel_s_name_box_highlights_the_card_and_appends() => Sta(() =>
    {
        var (window, h) = Open();
        try
        {
            var wall = BuilderDrag.FromCatalog(h.Command("ID_OBJECTS_WALL").Command!);
            h.Model.BeginDrag(wall);
            var box = PanelNameBox(window, 2);
            Assert.Equal(DragDropEffects.Copy, DragOver(box, Payload(wall), Upper(box)).Effects);
            Assert.Equal(DropMarker.Over, h.Model.Panels[2].DropMarker);
            Drop(box, Payload(wall), Upper(box));
            Assert.Equal(new[] { "Wiki", "Help", "Wall" }, h.Model.Profile.Panels[2].Slots.Select(s => s.Name));
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
            var box = SlotNameBox(window, 0, 0);
            var text = new DataObject(DataFormats.UnicodeText, "pasted");
            var over = DragOver(box, text, Upper(box));
            Assert.True(over.Handled, "the TextBox handles a text drag");
            Assert.All(h.Model.Panels.SelectMany(p => p.Slots), s => Assert.Equal(DropMarker.None, s.DropMarker));
            Assert.All(h.Model.Panels, p => Assert.Equal(DropMarker.None, p.DropMarker));
        }
        finally
        {
            Close(window, h);
        }
    });

    [Fact]
    public void The_slot_row_and_card_outside_the_name_boxes_still_take_drops() => Sta(() =>
    {
        var (window, h) = Open();
        try
        {
            var wall = BuilderDrag.FromCatalog(h.Command("ID_OBJECTS_WALL").Command!);
            h.Model.BeginDrag(wall);
            var row = WindowCapture.Descendants<Grid>(window.PanelCards).Single(g => g.Name == "SlotRow" && g.DataContext is SlotRowViewModel { Panel.Index: 0, Index: 2 });
            Assert.Equal(DragDropEffects.Copy, DragOver(row, Payload(wall), Upper(row)).Effects);
            Assert.Equal(DropMarker.Before, h.Model.Panels[0].Slots[2].DropMarker);
            Drop(row, Payload(wall), Upper(row));
            Assert.Equal(new[] { "Beam", "Column", "Wall", "Brace" }, h.Model.Profile.Panels[0].Slots.Select(s => s.Name));
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
            var wall = BuilderDrag.FromCatalog(h.Command("ID_OBJECTS_WALL").Command!);
            h.Model.BeginDrag(wall);
            var box = SlotNameBox(window, 0, 1);
            DragOver(box, Payload(wall), Lower(box));
            Assert.Equal(DropMarker.After, h.Model.Panels[0].Slots[1].DropMarker);

            // The pointer leaves the window from the name box: WPF raises DragLeave there (the TextBox handles it).
            var leave = Args(Payload(wall), box, Lower(box));
            leave.RoutedEvent = DragDrop.PreviewDragLeaveEvent;
            box.RaiseEvent(leave);
            if (!leave.Handled)
            {
                leave.RoutedEvent = DragDrop.DragLeaveEvent;
                box.RaiseEvent(leave);
            }
            Assert.All(h.Model.Panels.SelectMany(p => p.Slots), s => Assert.Equal(DropMarker.None, s.DropMarker));
            Assert.All(h.Model.Panels, p => Assert.Equal(DropMarker.None, p.DropMarker));
        }
        finally
        {
            Close(window, h);
        }
    });
}
