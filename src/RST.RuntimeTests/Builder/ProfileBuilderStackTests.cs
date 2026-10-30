// rst-c's own (not vendored): spec #17 (Ribbon Stacks), AC3 / T-A4. Stack authoring in the WPF
// Builder, through the view-model with the fake backend and dialogs: add ("Stack N"), drop and
// double-click commands in (a fourth refused with a toast), a per-tool icon, reorder and remove,
// rename (references rewritten, a duplicate refused), delete (the destructive confirm; references
// removed), place ("+ Panel", and a drag onto a panel card or slot), the read-only stack slot
// name, and Save (blocked by a stack with fewer than 2 tools; an unplaced stack removed; the saved
// file validates). Then the same on a real window: the stack cards, the read-only stack slot box,
// the stack name box's drag routing (the TextBox preview-routing lesson), a rename through the
// box's binding, and the delete confirm staying out of the taskbar.

using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FluentAssertions;
using RST.Core.Builder;
using RST.Core.Loader;
using RST.Core.Profiles;
using RST.Core.Theming;
using RST.RuntimeTests.Support;
using RST.UI.Builder;
using RST.UI.Controls;
using RST.UI.Dialogs;
using RST.UI.Theming;
using Xunit;
using Panel = RST.Core.Profiles.Panel;

namespace RST.RuntimeTests.Builder;

/// <summary>A profile with stacks of 2 and 3 tools, each with a pack icon, placed in two panels.</summary>
public static class StackFixtures
{
    public const string Id = "0d6b1c9e-5f7a-4e3b-9c21-7a8b9c0d1e2f";

    private static Slot Tool(string name, string commandId, string icon, string tab = "Architecture", string panel = "Build") =>
        new() { SlotType = "tool", Name = name, CommandId = commandId, IconFile = icon, SourceTab = tab, SourcePanel = panel };

    public static Profile Profile() => new()
    {
        Id = Id,
        ProfileName = "Stack Studio",
        Tab = "Studio Stacks",
        MinVersion = "3.1.0",
        ExportDate = "2026-10-30",
        PanelOpacity = 90,
        Stacks = new()
        {
            ["Openings"] = new Stack
            {
                Tools =
                [
                    Tool("Door", "ID_OBJECTS_DOOR", "pack:house_green"),
                    Tool("Window", "ID_OBJECTS_WINDOW", "pack:box_open_purple"),
                    Tool("Opening", "ID_OBJECTS_OPENING", "pack:box_slot_orange"),
                ],
            },
            ["Datum"] = new Stack
            {
                Tools = [Tool("Level", "ID_OBJECTS_LEVEL", "pack:level_green", panel: "Datum"), Tool("Grid", "ID_OBJECTS_GRID", "pack:parallel_dark_grey", panel: "Datum")],
            },
            ["Help"] = new Stack
            {
                Tools =
                [
                    Tool("Revit Help", "URL:https://help.autodesk.com", "pack:question_light_grey", "Custom", "Custom"),
                    Tool("Studio Wiki", "URL:https://wiki.example.com", "pack:link_external_blue", "Custom", "Custom"),
                ],
            },
        },
        Panels =
        [
            new Panel
            {
                Name = "Model", Color = "#4f8ef7",
                Slots = [Tool("Wall", "ID_OBJECTS_WALL", "pack:building_office_blue"), new Slot { SlotType = "stack", Name = "Openings" }, new Slot { SlotType = "stack", Name = "Datum" }],
            },
            new Panel
            {
                Name = "Links", Color = "#10b981",
                Slots = [new Slot { SlotType = "stack", Name = "Help" }, Tool("Enscape", "Enscape.Start", "pack:camera_orange", "Enscape", "Enscape")],
            },
        ],
    };

    /// <summary>The Builder fixture backend with the stack profile in its library.</summary>
    public static FakeBuilderBackend Backend() => BuilderFixtures.Backend().With(Profile());
}

public class ProfileBuilderStackTests
{
    private static BuilderHarness Fresh() => BuilderHarness.Create();

    private static BuilderHarness WithStacks()
    {
        var h = BuilderHarness.Create(StackFixtures.Backend());
        h.Model.SelectedProfileOption = h.Option("Stack Studio");
        return h;
    }

    private static StackCardViewModel Card(BuilderHarness h, string name) => h.Model.Stacks.Single(c => c.Name == name);

    private static void AssertToast(BuilderHarness h, string text) =>
        (h.Model.Toast.IsOpen, h.Model.Toast.Text, h.Model.Toast.Kind).Should().Be((true, text, BannerKind.Error));

    // ── add, fill, the 2-3 rule ──────────────────────────────────────────────

    [Fact]
    public void Add_Stack_makes_an_empty_selected_Stack_N_that_is_flagged()
    {
        var h = Fresh();
        h.Model.HasStacks.Should().BeFalse();
        h.Model.AddStackCommand.Execute(null);
        h.Model.AddStackCommand.Execute(null);

        h.Model.Stacks.Select(c => c.Name).Should().Equal("Stack 1", "Stack 2");
        h.Model.Profile.Stacks.Keys.Should().Equal("Stack 1", "Stack 2");
        h.Model.SelectedStack!.Name.Should().Be("Stack 2", "a new stack is selected, so double-clicks fill it");
        h.Model.Stacks.Select(c => c.IsSelected).Should().Equal(false, true);
        var card = Card(h, "Stack 1");
        card.HasTools.Should().BeFalse();
        card.ToolCount.Should().Be("0 of 3");
        card.IsUnderfilled.Should().BeTrue();
        card.IsUnplaced.Should().BeTrue();
        h.Model.IsDirty.Should().BeTrue();
    }

    [Fact]
    public void Commands_dropped_or_double_clicked_join_the_stack_and_a_fourth_is_refused_with_a_toast()
    {
        var h = Fresh();
        h.Model.AddStackCommand.Execute(null);
        var card = Card(h, "Stack 1");

        // Dropped on the card.
        h.Model.BeginDrag(BuilderDrag.FromCatalog(h.Command("ID_OBJECTS_WALL").Command!));
        h.Model.DragOverStack(card).Should().BeTrue();
        card.DropMarker.Should().Be(DropMarker.Over);
        h.Model.DropOnStack(card);
        card.DropMarker.Should().Be(DropMarker.None);

        // Double-clicked while the stack is selected.
        h.Model.AddCatalogCommand.Execute(h.Command("ID_OBJECTS_DOOR"));
        h.Model.AddCatalogCommand.Execute(h.Command("ID_OBJECTS_WINDOW"));

        card.Tools.Select(t => t.Name).Should().Equal("Wall", "Door", "Window");
        card.ToolCount.Should().Be("3 of 3");
        card.IsFull.Should().BeTrue();
        card.IsUnderfilled.Should().BeFalse();
        var wall = h.Model.Profile.Stacks["Stack 1"].Tools[0];
        (wall.SlotType, wall.CommandId, wall.SourceTab, wall.SourcePanel).Should().Be(("tool", "ID_OBJECTS_WALL", "Architecture", "Build"),
            "stack tools keep sourceTab and sourcePanel like panel tools");
        h.Model.Profile.Panels[0].Slots.Should().BeEmpty("the commands went into the stack, not the panel");

        // A fourth: by drop, then by double-click.
        h.Model.BeginDrag(BuilderDrag.FromCatalog(h.Command("ID_OBJECTS_LEVEL").Command!));
        h.Model.DropOnStack(card);
        AssertToast(h, "A stack holds 2 or 3 tools; \"Stack 1\" already has 3.");
        h.Model.AddCatalogCommand.Execute(h.Command("ID_OBJECTS_GRID"));
        h.Model.Profile.Stacks["Stack 1"].Tools.Should().HaveCount(3);

        // Clicking a panel makes it the double-click target again.
        h.Model.Panels[0].SelectCommand.Execute(null);
        h.Model.HasSelectedStack.Should().BeFalse();
        card.IsSelected.Should().BeFalse();
        h.Model.AddCatalogCommand.Execute(h.Command("ID_OBJECTS_GRID"));
        h.Model.Profile.Panels[0].Slots.Select(s => s.Name).Should().Equal("Grid");
    }

    [Fact]
    public void A_stack_card_takes_catalogue_commands_only()
    {
        var h = WithStacks();
        var card = Card(h, "Datum");
        h.Model.BeginDrag(BuilderDrag.FromSlot(0, 0));
        h.Model.DragOverStack(card).Should().BeFalse();
        card.DropMarker.Should().Be(DropMarker.None);
        h.Model.DropOnStack(card);
        h.Model.Profile.Stacks["Datum"].Tools.Should().HaveCount(2);
        h.Model.Profile.Panels[0].Slots.Should().HaveCount(3);
    }

    [Fact]
    public void Each_stack_tool_picks_its_own_icon()
    {
        var h = WithStacks();
        var grid = Card(h, "Datum").Tools[1];
        grid.IconLabel.Should().Be("parallel");
        var before = h.Model.PreviewRevision;

        grid.PickIconCommand.Execute(null);
        var picker = h.Model.IconPicker!;
        picker.Model.SelectedDesign.Should().Be("parallel");
        picker.Model.SelectedColor.Should().Be("dark_grey");
        picker.TileCommand.Execute("move");
        h.Model.IconPicker!.VariantCommand.Execute("red");

        h.Model.IsIconPickerOpen.Should().BeFalse();
        h.Model.Profile.Stacks["Datum"].Tools[1].IconFile.Should().Be("pack:move_red");
        h.Model.Profile.Stacks["Datum"].Tools[0].IconFile.Should().Be("pack:level_green", "only that tool changed");
        grid.IconLabel.Should().Be("move");
        grid.HasIconThumbnail.Should().BeTrue();
        h.Model.PreviewRevision.Should().BeGreaterThan(before);
    }

    [Fact]
    public void Stack_tools_reorder_and_remove()
    {
        var h = WithStacks();
        var card = Card(h, "Openings");
        card.Tools[2].MoveUpCommand.Execute(null);
        card.Tools.Select(t => t.Name).Should().Equal("Door", "Opening", "Window");
        card.Tools[0].MoveUpCommand.Execute(null);
        card.Tools[2].MoveDownCommand.Execute(null);
        card.Tools.Select(t => t.Name).Should().Equal("Door", "Opening", "Window");

        card.Tools[1].RemoveCommand.Execute(null);
        card.Tools[1].RemoveCommand.Execute(null);
        h.Model.Profile.Stacks["Openings"].Tools.Select(t => t.Name).Should().Equal("Door");
        card.IsUnderfilled.Should().BeTrue("one tool left: flagged");
    }

    // ── rename, delete, place ────────────────────────────────────────────────

    [Fact]
    public void Rename_rewrites_the_slots_that_place_the_stack_and_refuses_a_duplicate()
    {
        var h = WithStacks();
        var card = Card(h, "Datum");
        var row = h.Model.Panels[0].Slots[2];
        row.Name.Should().Be("Datum");
        var before = h.Model.PreviewRevision;

        card.Name = "  Levels & Grids ";

        card.Name.Should().Be("Levels & Grids");
        h.Model.Profile.Stacks.Keys.Should().Equal("Openings", "Levels & Grids", "Help");
        h.Model.Profile.Panels[0].Slots[2].Name.Should().Be("Levels & Grids");
        row.Name.Should().Be("Levels & Grids");
        h.Model.PreviewRevision.Should().BeGreaterThan(before);
        ProfileSerializer.Validate(h.Model.Profile).Should().BeEmpty("the link survives the rename");

        card.Name = "Openings";
        AssertToast(h, "Stack \"Openings\" already exists.");
        card.Name.Should().Be("Levels & Grids");
        h.Model.Profile.Stacks.Keys.Should().Equal("Openings", "Levels & Grids", "Help");

        card.Name = "   ";
        card.Name.Should().Be("Levels & Grids", "a blank name snaps back");
    }

    [Fact]
    public void Delete_asks_with_the_destructive_confirm_and_removes_the_slots_that_place_it()
    {
        var h = WithStacks();
        var card = Card(h, "Openings");

        h.Dialogs.ConfirmResult = false;
        card.DeleteCommand.Execute(null);
        var ask = h.Dialogs.ConfirmCalls.Single();
        (ask.Title, ask.Message, ask.ConfirmText, ask.Danger, ask.AutomationId).Should().Be(
            ("Delete stack \"Openings\"?", "Slots referencing it will also be removed.", "Delete", true, ProfileBuilderViewModel.DeleteStackDialogId));
        h.Model.Profile.Stacks.Should().ContainKey("Openings", "Cancel keeps the stack");

        h.Dialogs.ConfirmResult = true;
        card.DeleteCommand.Execute(null);
        h.Model.Profile.Stacks.Keys.Should().Equal("Datum", "Help");
        h.Model.Stacks.Select(c => c.Name).Should().Equal("Datum", "Help");
        h.Model.Profile.Panels[0].Slots.Select(s => s.Name).Should().Equal("Wall", "Datum");
        h.Model.Panels[0].Slots.Should().HaveCount(2);
    }

    [Fact]
    public void Plus_Panel_and_a_drag_place_the_stack_in_a_panel()
    {
        var h = Fresh();
        h.Model.AddStackCommand.Execute(null);
        var card = Card(h, "Stack 1");
        card.IsUnplaced.Should().BeTrue();

        card.PlaceCommand.Execute(null);
        h.Model.Profile.Panels[0].Slots.Should().ContainSingle().Which.Should().BeEquivalentTo(new Slot { SlotType = "stack", Name = "Stack 1" });
        card.IsUnplaced.Should().BeFalse();
        h.Model.Panels[0].Slots[0].IsStack.Should().BeTrue();
        h.Model.Panels[0].Slots[0].TypeTag.Should().Be("stack");

        // The card's header dragged onto a panel card, then onto a slot row (before it).
        h.Model.AddPanelCommand.Execute(null);
        h.Model.BeginDrag(BuilderDrag.FromStack("Stack 1"));
        h.Model.DragOverPanel(1, upperHalf: true).Should().BeTrue();
        h.Model.Panels[1].DropMarker.Should().Be(DropMarker.Over);
        h.Model.DropOnPanel(1, upperHalf: true);
        h.Model.BeginDrag(BuilderDrag.FromStack("Stack 1"));
        h.Model.DragOverSlot(0, 0, upperHalf: true).Should().BeTrue();
        h.Model.DropOnSlot(0, 0, upperHalf: true);

        h.Model.Profile.Panels.Select(p => p.Slots.Count).Should().Equal(2, 1);
        h.Model.SelectedPanelIndex.Should().Be(0);
    }

    [Fact]
    public void A_stack_slot_s_name_cannot_be_edited_in_the_panel_card()
    {
        var h = WithStacks();
        var row = h.Model.Panels[0].Slots[1];
        row.IsStack.Should().BeTrue();
        row.NameToolTip.Should().Be("A stack is renamed on its card under Stacks.");
        row.Name = "Broken link";
        row.Name.Should().Be("Openings");
        h.Model.Profile.Panels[0].Slots[1].Name.Should().Be("Openings");
        h.Model.IsDirty.Should().BeFalse();

        var tool = h.Model.Panels[0].Slots[0];
        tool.IsStack.Should().BeFalse();
        tool.NameToolTip.Should().BeNull();
        tool.Name = "Big Wall";
        h.Model.Profile.Panels[0].Slots[0].Name.Should().Be("Big Wall", "tool names stay editable");
    }

    // ── save ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Save_is_blocked_while_a_stack_has_fewer_than_2_tools_then_writes_a_valid_profile()
    {
        var h = Fresh();
        h.Model.ProfileName = "Stacked";
        h.Model.ProfileTab = "Stacked";
        h.Model.AddStackCommand.Execute(null);
        h.Model.AddCatalogCommand.Execute(h.Command("Enscape.Start"));
        Card(h, "Stack 1").PlaceCommand.Execute(null);

        (await h.Model.SaveAsync()).Should().BeFalse();
        AssertToast(h, "Stack \"Stack 1\" needs 2 or 3 tools before you can save.");
        h.Backend.Saved.Should().BeEmpty();
        h.Model.IsEditMode.Should().BeFalse();

        Card(h, "Stack 1").SelectCommand.Execute(null);
        h.Model.AddCatalogCommand.Execute(h.Command("Enscape.Sync"));
        (await h.Model.SaveAsync()).Should().BeTrue();

        var saved = h.Backend.Saved.Single();
        ProfileSerializer.Validate(saved).Should().BeEmpty();
        saved.Stacks["Stack 1"].Tools.Select(t => (t.CommandId, t.SourceTab)).Should().Equal(("Enscape.Start", "Enscape"), ("Enscape.Sync", "Enscape"));
        saved.RequiredAddins.Select(r => r.TabName).Should().Contain("Enscape", "stack tools count towards the required add-ins");
        RstifyRules.SourceTabs(saved).Should().Contain("Enscape", "RSTify never offers to hide a stack tool's source tab");
        h.Model.IsDirty.Should().BeFalse();
    }

    [Fact]
    public async Task Save_removes_a_stack_that_no_panel_places()
    {
        var h = WithStacks();
        h.Model.AddStackCommand.Execute(null);
        h.Model.AddCatalogCommand.Execute(h.Command("ID_OBJECTS_WALL"));
        h.Model.AddCatalogCommand.Execute(h.Command("ID_OBJECTS_DOOR"));
        Card(h, "Stack 1").IsUnplaced.Should().BeTrue();

        (await h.Model.SaveAsync()).Should().BeTrue();

        h.Backend.Saved.Single().Stacks.Keys.Should().Equal("Openings", "Datum", "Help");
        h.Model.Stacks.Select(c => c.Name).Should().Equal("Openings", "Datum", "Help");
        h.Model.IsDirty.Should().BeFalse("the editor matches what was written");
    }

    [Fact]
    public void Loading_a_profile_shows_its_stacks_and_a_fresh_profile_has_none()
    {
        var h = WithStacks();
        h.Model.Stacks.Select(c => (c.Name, c.Tools.Count, c.IsUnplaced)).Should().Equal(("Openings", 3, false), ("Datum", 2, false), ("Help", 2, false));
        h.Model.Stacks[2].Tools.Select(t => t.ShortCommandId).Should().Equal("URL:http…desk.com", "URL:http…mple.com");

        h.Model.SelectedProfileOption = h.Model.ProfileOptions[0];
        h.Model.Stacks.Should().BeEmpty();
        h.Model.HasStacks.Should().BeFalse();
    }
}

/// <summary>Stack authoring on a real ProfileBuilderWindow.</summary>
[Collection(WpfThemeCollection.Name)]
public class ProfileBuilderStackWindowTests
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

    private static (ProfileBuilderWindow Window, BuilderHarness Harness) Open(bool deferPosts = false)
    {
        var h = BuilderHarness.Create(StackFixtures.Backend(), deferPosts);
        h.Model.SelectedProfileOption = h.Option("Stack Studio");
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

    private static string Id(DependencyObject d) => System.Windows.Automation.AutomationProperties.GetAutomationId(d);

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

    /// <summary>One step as WPF's DragDrop raises it: the preview, then (when not handled) the bubbling event.</summary>
    private static DragEventArgs Raise(UIElement over, IDataObject data, RoutedEvent preview, RoutedEvent bubble)
    {
        var args = Args(data, over, new Point(4, 4));
        args.RoutedEvent = preview;
        over.RaiseEvent(args);
        if (!args.Handled)
        {
            args.RoutedEvent = bubble;
            over.RaiseEvent(args);
        }
        return args;
    }

    [Fact]
    public void The_Stacks_section_shows_a_card_per_stack_and_a_stack_slot_s_box_is_read_only() => Sta(() =>
    {
        var (window, h) = Open();
        try
        {
            WindowCapture.Descendants<TextBox>(window.StackCards).Where(t => Id(t) == "Builder.StackName").Select(t => t.Text)
                .Should().Equal("Openings", "Datum", "Help");
            WindowCapture.Descendants<TextBlock>(window.StackCards).Count(t => Id(t) == "Builder.StackToolName").Should().Be(7);
            WindowCapture.Descendants<Button>(window.StackCards).Count(b => Id(b) == "Builder.StackToolPickIcon").Should().Be(7);

            var slotBoxes = WindowCapture.Descendants<TextBox>(window.PanelCards).Where(t => Id(t) == "Builder.SlotName").ToList();
            slotBoxes.Select(b => (b.Text, b.IsReadOnly)).Should().Equal(
                ("Wall", false), ("Openings", true), ("Datum", true), ("Help", true), ("Enscape", false));
        }
        finally
        {
            Close(window, h);
        }
    });

    [Fact]
    public void A_command_dragged_over_a_stack_s_name_box_reaches_the_card_and_drops_into_the_stack() => Sta(() =>
    {
        var (window, h) = Open();
        try
        {
            var drag = BuilderDrag.FromCatalog(h.Command("ID_OBJECTS_COLUMN").Command!);
            var data = new DataObject(typeof(BuilderDrag), drag);
            h.Model.BeginDrag(drag);
            var box = WindowCapture.Descendants<TextBox>(window.StackCards).Single(t => Id(t) == "Builder.StackName" && t.Text == "Datum");

            var over = Raise(box, data, DragDrop.PreviewDragOverEvent, DragDrop.DragOverEvent);
            over.Effects.Should().Be(DragDropEffects.Copy);
            h.Model.Stacks[1].DropMarker.Should().Be(DropMarker.Over);

            var drop = Raise(box, data, DragDrop.PreviewDropEvent, DragDrop.DropEvent);
            drop.Handled.Should().BeTrue();
            h.Model.Profile.Stacks["Datum"].Tools.Select(t => t.Name).Should().Equal("Level", "Grid", "Structural Column");

            // A panel drag over a stack card is refused there.
            var panel = BuilderDrag.FromPanel(0);
            h.Model.BeginDrag(panel);
            var root = WindowCapture.Descendants<Grid>(window.StackCards).First(g => g.Name == "StackRoot");
            Raise(root, new DataObject(typeof(BuilderDrag), panel), DragDrop.PreviewDragOverEvent, DragDrop.DragOverEvent)
                .Effects.Should().Be(DragDropEffects.None);
        }
        finally
        {
            Close(window, h);
        }
    });

    [Fact]
    public void Renaming_through_the_name_box_rewrites_the_slot_row_and_a_refused_name_snaps_the_box_back() => Sta(() =>
    {
        // The view-model posts the box's re-read to after the binding's write, as the dispatcher does in Revit.
        var (window, h) = Open(deferPosts: true);
        try
        {
            TextBox Box(string text) => WindowCapture.Descendants<TextBox>(window.StackCards).Single(t => Id(t) == "Builder.StackName" && t.Text == text);

            var help = Box("Help");
            help.Text = "Links & Help";
            help.GetBindingExpression(TextBox.TextProperty)!.UpdateSource(); // what Enter or leaving the box does
            h.RunPosted();
            WindowCapture.DoEvents();
            h.Model.Profile.Stacks.Keys.Should().Contain("Links & Help");
            WindowCapture.Descendants<TextBox>(window.PanelCards).Where(t => Id(t) == "Builder.SlotName").Select(t => t.Text)
                .Should().Contain("Links & Help");

            var datum = Box("Datum");
            datum.Text = "Openings";
            datum.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            h.RunPosted();
            WindowCapture.DoEvents();
            datum.Text.Should().Be("Datum", "a duplicate is refused and the box shows the name again");
            h.Model.Toast.Text.Should().Be("Stack \"Openings\" already exists.");
        }
        finally
        {
            Close(window, h);
        }
    });

    [Fact]
    public void The_delete_stack_confirm_stays_out_of_the_taskbar() => Sta(() =>
    {
        var title = RST.UI.Resources.UiStrings.Builder_DeleteStackTitle("Openings");
        var dialog = new MessageDialog(
            new MessageDialogModel(title, title, RST.UI.Resources.UiStrings.Builder_DeleteStackText, [], RST.UI.Resources.UiStrings.Builder_Delete, HasCancel: true, Danger: true),
            ProfileBuilderViewModel.DeleteStackDialogId);
        dialog.ShowInTaskbar.Should().BeFalse("the delete-stack confirm must not get its own taskbar entry");
        dialog.Close();
    });
}
