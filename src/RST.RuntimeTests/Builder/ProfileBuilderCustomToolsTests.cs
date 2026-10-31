// rst-c's own (not vendored): the Commands pane's Custom Tools on a real ProfileBuilderWindow
// (feature #18, spec #19, WU4, AC5; decision #20), over a temp profiles folder: + Create new adds
// the tool to the panel and the list; ✎ (a row or a placed tile) edits it everywhere after a
// confirmation whose count is what will be written, updating the files on disk and the working
// copy and reloading the ribbon when the loaded profile changed, while the unsaved-changes indicator
// stays as it was; × on a tile removes one placement; × on a row removes it everywhere; a partial
// failure (two files sharing an id) is reported; a row dragged onto a panel and onto a stack places
// fresh slots; and the Filter… box filters the list. The drags raise the routed events WPF's
// DragDrop raises (ProfileBuilderDragEventTests explains why) on the real tiles and cards.

using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using RST.Core.Builder;
using RST.Core.Profiles;
using RST.Core.Theming;
using RST.RuntimeTests.Loader;
using RST.RuntimeTests.Support;
using RST.UI.Builder;
using RST.UI.Controls;
using RST.UI.Dialogs;
using RST.UI.Theming;
using Xunit;

namespace RST.RuntimeTests.Builder;

/// <summary>
/// The Custom Tools fixture: Architecture and Structure (loaded) on disk, with URL and email tools.
/// Architecture's Links: BIM Standards, Support, Help; Structure's Links: Wiki, Help. So the list is
/// BIM Standards, Help (in both), Support, Wiki.
/// </summary>
public static class CustomToolFixtures
{
    public const string Wiki = "https://wiki.studio.example";
    public const string Help = "mailto:bim@studio.example";
    public const string Support = "mailto:support@studio.example";
    public const string Standards = "https://bim.studio.example/standards";

    private static Slot Url(string name, string link, string icon) =>
        new() { SlotType = "tool", Name = name, BaseName = name, CommandId = "URL:" + link, IconFile = icon };

    public static Profile Architecture()
    {
        var p = LoaderFixtures.Architecture();
        p.Panels[2] = new RST.Core.Profiles.Panel
        {
            Name = "Links", Color = "#8b5cf6",
            Slots = [Url("BIM Standards", Standards, "pack:sheets_purple"), Url("Support", Support, "pack:at_green"), Url("Help", Help, "pack:question_blue")],
        };
        return p;
    }

    public static Profile Structure()
    {
        var p = LoaderFixtures.Structure();
        p.Panels[2].Slots = [Url("Wiki", Wiki, "pack:link_external_blue"), Url("Help", Help, "pack:question_light_grey")];
        return p;
    }

    /// <summary>The realistic catalogue, Architecture and Structure on disk in <paramref name="dir"/>, Structure loaded.</summary>
    public static FakeBuilderBackend Backend(string dir)
    {
        var backend = BuilderFixtures.Backend();
        backend.Library.Clear();
        backend.With(Architecture(), Structure()).OnDisk(dir);
        backend.Active = ActiveProfile.FromProfile(Structure(), ProfileStore.CanonicalFileName(Structure()), ["Analyze"], true);
        return backend;
    }

    public static Profile OnDisk(string dir, string id) => ProfileStore.List(dir).Single(e => e.Profile.Id == id).Profile;

    public static IEnumerable<Slot> Tools(Profile p) => p.Panels.SelectMany(x => x.Slots).SelectMany(s =>
        PanelStacks.IsStack(s) ? PanelStacks.ToolsOf(p, s) : [s]);

    public static int Holding(Profile p, string link) => Tools(p).Count(s => CustomTools.LinkOf(s) == link);
}

[Collection(WpfThemeCollection.Name)]
public sealed class ProfileBuilderCustomToolsTests : IDisposable
{
    private readonly TempFolder _dir = new();

    public void Dispose() => _dir.Dispose();

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

    /// <summary>The Builder on Structure (the loaded profile), shown off-screen.</summary>
    private (ProfileBuilderWindow Window, BuilderHarness Harness) Open(Action<FakeBuilderBackend>? arrange = null)
    {
        var backend = CustomToolFixtures.Backend(_dir.Path);
        arrange?.Invoke(backend);
        var h = BuilderHarness.Create(backend);
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

    private Profile Disk(string id) => CustomToolFixtures.OnDisk(_dir.Path, id);

    private static string Id(DependencyObject d) => AutomationProperties.GetAutomationId(d);

    private static List<Border> Rows(ProfileBuilderWindow w) =>
        WindowCapture.Descendants<Border>(w.CatalogTreeView).Where(b => Id(b) == "Builder.CustomTool" && b.IsVisible).ToList();

    private static Border Row(ProfileBuilderWindow w, string label) =>
        Rows(w).Single(b => ((CustomToolItemViewModel)b.DataContext).Label == label);

    private static List<string> Labels(ProfileBuilderWindow w) => Rows(w).Select(b => ((CustomToolItemViewModel)b.DataContext).Label).ToList();

    private static Button RowButton(Border row, string id) => WindowCapture.Descendants<Button>(row).Single(b => Id(b) == id);

    private static Grid Tile(ProfileBuilderWindow w, int panel, int slot) =>
        WindowCapture.Descendants<Grid>(w.PanelCards).Single(g => g.Name == "Tile" && g.DataContext is ToolTileViewModel { InStack: false } t && t.Panel.Index == panel && t.Index == slot);

    private static Grid StackGroup(ProfileBuilderWindow w, int panel, int slot) =>
        WindowCapture.Descendants<Grid>(w.PanelCards).Single(g => g.Name == "StackGroup" && g.DataContext is StackGroupViewModel s && s.Panel.Index == panel && s.Index == slot);

    private static Grid Card(ProfileBuilderWindow w, int panel) =>
        WindowCapture.Descendants<Grid>(w.PanelCards).Single(g => g.Name == "CardRoot" && g.DataContext is PanelCardViewModel c && c.Index == panel);

    private static string Shape(BuilderHarness h, int panel) => PanelCardTestExtensions.Shape(h.Model.Profile, panel);

    private static void Click(Button button)
    {
        Assert.True(button.Command.CanExecute(button.CommandParameter), $"{Id(button)} is enabled");
        button.Command.Execute(button.CommandParameter);
        WindowCapture.DoEvents();
    }

    // ── drag events, as WPF's DragDrop raises them ──

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

    private static DragEventArgs Raise(UIElement over, IDataObject data, Point at, RoutedEvent preview, RoutedEvent bubble)
    {
        var args = Args(data, over, at);
        args.RoutedEvent = preview;
        over.RaiseEvent(args);
        if (!args.Handled)
        {
            args.RoutedEvent = bubble;
            over.RaiseEvent(args);
        }
        return args;
    }

    /// <summary>Drags the Custom Tools row <paramref name="row"/> (its own drag, as the window starts it) over <paramref name="over"/> and drops it.</summary>
    private static DragEventArgs DragRow(ProfileBuilderWindow w, BuilderHarness h, Border row, UIElement over, Point at)
    {
        var drag = ProfileBuilderWindow.DragFor(row.DataContext);
        Assert.NotNull(drag);
        Assert.Equal(DragKind.CustomTool, drag!.Kind);
        var data = new DataObject(typeof(BuilderDrag), drag);
        h.Model.BeginDrag(drag);
        var o = Raise(over, data, at, DragDrop.PreviewDragOverEvent, DragDrop.DragOverEvent);
        Raise(over, data, at, DragDrop.PreviewDropEvent, DragDrop.DropEvent);
        h.Model.EndDrag();
        WindowCapture.DoEvents();
        return o;
    }

    // ── the list ──

    [Fact]
    public void The_list_comes_first_in_the_tree_and_counts_with_the_commands() => Sta(() =>
    {
        var (window, h) = Open();
        try
        {
            Assert.Equal(["BIM Standards", "Help", "Support", "Wiki"], Labels(window));
            var group = h.Model.CustomToolsGroup;
            Assert.Equal(4, group.Count);
            Assert.Equal(15 + 4, h.Model.CommandsCount);
            Assert.Equal("19", WindowCapture.Descendants<ContentControl>(window.Root).Single(c => Id(c) == "Builder.CatalogCount").Content?.ToString());
            Assert.Same(group, window.CatalogTreeView.Items[0]);
            Assert.Equal(CustomTools.ListNote, WindowCapture.Descendants<TextBlock>(window.CatalogTreeView).Single(t => Id(t) == "Builder.CustomToolsNote").Text);

            var help = (CustomToolItemViewModel)Row(window, "Help").DataContext;
            Assert.Equal("EMAIL", help.KindText);
            Assert.Equal("mailto:bim@studio.example\nUsed in: Architecture, Structure", help.ToolTip);
            Assert.Equal("URL", ((CustomToolItemViewModel)Row(window, "Wiki").DataContext).KindText);

            // One scroll region: the group is the tree's first item, above the catalogue's roots.
            var groupTop = Row(window, "BIM Standards").TranslatePoint(new Point(), window.CatalogTreeView).Y;
            var native = WindowCapture.Descendants<TreeViewItem>(window.CatalogTreeView).First(t => t.DataContext is CatalogNodeViewModel);
            Assert.True(native.TranslatePoint(new Point(), window.CatalogTreeView).Y > groupTop);

            // The group collapses and opens from its header.
            group.ToggleCommand.Execute(null);
            WindowCapture.DoEvents();
            Assert.Empty(Rows(window));
            group.ToggleCommand.Execute(null);
            WindowCapture.DoEvents();
            Assert.Equal(4, Rows(window).Count);
        }
        finally
        {
            Close(window, h);
        }
    });

    [Fact]
    public void The_filter_covers_the_Custom_Tools() => Sta(() =>
    {
        var (window, h) = Open();
        try
        {
            h.Model.FilterText = "wiki";
            WindowCapture.DoEvents();
            Assert.Equal(["Wiki"], Labels(window));
            Assert.False(h.Model.HasNoMatches);

            h.Model.FilterText = "studio.example/standards"; // the link matches too
            WindowCapture.DoEvents();
            Assert.Equal(["BIM Standards"], Labels(window));

            h.Model.FilterText = "Wall"; // a command only: the group hides
            WindowCapture.DoEvents();
            Assert.Empty(Rows(window));
            Assert.False(h.Model.CustomToolsGroup.IsVisible);
            Assert.False(h.Model.HasNoMatches);

            h.Model.FilterText = "zzz";
            WindowCapture.DoEvents();
            Assert.True(h.Model.HasNoMatches);

            // Collapsed, a filter still shows the matches; cleared, the group is as it was.
            h.Model.CustomToolsGroup.IsExpanded = false;
            h.Model.FilterText = "help";
            WindowCapture.DoEvents();
            Assert.Equal(["Help"], Labels(window));
            h.Model.FilterText = "";
            WindowCapture.DoEvents();
            Assert.Empty(Rows(window));
        }
        finally
        {
            Close(window, h);
        }
    });

    // ── + Create new ──

    [Fact]
    public void Create_new_adds_the_tool_to_the_selected_panel_and_to_the_list() => Sta(() =>
    {
        var (window, h) = Open();
        try
        {
            h.Model.Panels[1].SelectCommand.Execute(null); // Review
            h.Modals.CustomTools.Enqueue(d =>
            {
                Assert.False(d.IsEdit);
                Assert.Equal("Create new tool", d.Title);
                Assert.Equal("Create", d.ConfirmText);
                Assert.Equal("Added to the panel “Review”. Tools live in profiles: a tool on no panel of any profile leaves Custom Tools.", d.Note);
                d.IsEmail = true;
                d.Label = "Studio desk";
                d.Address = "desk@studio.example";
                Assert.Equal("Opens: mailto:desk@studio.example", d.Opens);
                Assert.Equal("Email address", d.AddressLabel);
                d.IconFile = "pack:at_orange";
                return true;
            });
            var create = WindowCapture.Descendants<Button>(window.Root).Single(b => Id(b) == "Builder.CreateNew");
            Click(create);

            Assert.Equal("Kinship,[Warnings Audit Purge],Notes,Studio desk", Shape(h, 1));
            var slot = h.Model.Profile.Panels[1].Slots[^1];
            Assert.Equal(("URL:mailto:desk@studio.example", "Studio desk", "pack:at_orange"), (slot.CommandId, slot.BaseName, slot.IconFile));
            Assert.Contains("Studio desk", Labels(window));
            Assert.Equal(5, h.Model.CustomToolsGroup.Count);
            Assert.Equal("Added to “Review”.", h.Model.Toast.Text);
            Assert.True(h.Model.IsDirty, "a placement is an unsaved change");
            Assert.Equal(0, CustomToolFixtures.Holding(Disk(LoaderFixtures.StructId), "mailto:desk@studio.example"));

            // A link the list holds already: added anyway, as another placement.
            h.Modals.CustomTools.Enqueue(d =>
            {
                d.Label = "Wiki again";
                d.Address = "wiki.studio.example";
                return true;
            });
            Click(create);
            Assert.Equal(2, CustomToolFixtures.Holding(h.Model.Profile, CustomToolFixtures.Wiki));
            Assert.Equal("That link is already in Custom Tools — another copy was added to the panel.", h.Model.Toast.Text);

            // A label and an address are required: Confirm is refused and the dialog stays open.
            h.Modals.CustomTools.Enqueue(d =>
            {
                d.Address = "x.example";
                return true;
            });
            Click(create);
            Assert.Equal("A label is required.", h.Modals.CustomToolDialogs[^1].Error);
            Assert.Equal(5, h.Model.CustomToolsGroup.Count); // nothing was added
        }
        finally
        {
            Close(window, h);
        }
    });

    [Fact]
    public void Create_new_on_a_profile_with_no_panels_is_refused() => Sta(() =>
    {
        var (window, h) = Open();
        try
        {
            while (h.Model.Panels.Count > 0)
            {
                h.Dialogs.ConfirmResult = true;
                h.Model.Panels[0].DeleteCommand.Execute(null);
            }
            h.Model.CreateCustomToolCommand.Execute(null);
            Assert.Empty(h.Modals.CustomToolDialogs);
            Assert.Equal("Add a panel first.", h.Model.Toast.Text);
            Assert.Equal(BannerKind.Error, h.Model.Toast.Kind);
        }
        finally
        {
            Close(window, h);
        }
    });

    // ── ✎ edit everywhere ──

    [Fact]
    public void Edit_everywhere_updates_both_files_and_the_working_copy_and_reloads_the_loaded_profile() => Sta(() =>
    {
        var (window, h) = Open();
        try
        {
            Assert.False(h.Model.IsDirty);
            h.Modals.CustomTools.Enqueue(d =>
            {
                Assert.True(d.IsEdit);
                Assert.Equal("Edit custom tool", d.Title);
                Assert.True(d.IsEmail);
                Assert.Equal(("Help", "bim@studio.example"), (d.Label, d.Address));
                Assert.Equal("Changes apply everywhere this tool is used: Architecture, Structure. Names you gave it on a panel are kept.", d.Warning);
                Assert.Equal("Update in 2 profiles", d.ConfirmText);
                Assert.False(d.HasRefusals);
                d.Label = "BIM help";
                d.Address = "help@studio.example";
                d.IconFile = "pack:question_green";
                return true;
            });
            Click(RowButton(Row(window, "Help"), "Builder.EditCustomToolEverywhere"));

            const string newLink = "mailto:help@studio.example";
            foreach (var id in new[] { LoaderFixtures.ArchId, LoaderFixtures.StructId })
            {
                var disk = Disk(id);
                Assert.Equal(0, CustomToolFixtures.Holding(disk, CustomToolFixtures.Help));
                var tool = Assert.Single(CustomToolFixtures.Tools(disk), s => CustomTools.LinkOf(s) == newLink);
                Assert.Equal(("BIM help", "BIM help", "pack:question_green"), (tool.Name, tool.BaseName, tool.IconFile));
            }
            Assert.Equal(1, CustomToolFixtures.Holding(h.Model.Profile, newLink));
            Assert.Equal("BIM help", h.Model.Panels[2].Tile(1).Name);

            // The loaded profile (Structure) changed: the ribbon reloaded through the save path's reload.
            var load = Assert.Single(h.Backend.Loads);
            Assert.Equal((LoaderFixtures.StructId, "Structure"), (load.Id, load.Name));
            Assert.Equal("Updated in 2 profiles. Ribbon reloaded.", h.Model.Toast.Text);
            Assert.False(h.Model.IsDirty, "the change is on disk: the clean baseline took it too");
            Assert.Contains("BIM help", Labels(window));
            Assert.DoesNotContain("Help", Labels(window));
        }
        finally
        {
            Close(window, h);
        }
    });

    [Fact]
    public void Edit_from_a_placed_tile_keeps_other_unsaved_edits_unsaved_and_skips_the_reload_when_the_loaded_file_is_not_touched() => Sta(() =>
    {
        // Architecture is open; Structure is loaded. The Standards tool is only in Architecture.
        var (window, h) = Open();
        try
        {
            h.Model.SelectedProfileOption = h.Option("Architecture");
            WindowCapture.DoEvents();
            h.Model.ProfileTab = "Studio Arch (renamed)"; // another unsaved edit
            Assert.True(h.Model.IsDirty);

            var tile = h.Model.Panels[2].Tile(0);
            Assert.True(tile.ShowsEditCustom);
            Assert.Same(h.Model.EditCustomToolCommand, tile.EditCustomCommand);
            h.Modals.CustomTools.Enqueue(d =>
            {
                Assert.Equal(("BIM Standards", CustomToolFixtures.Standards), (d.Label, d.Address));
                Assert.Equal("Update in 1 profile", d.ConfirmText);
                d.Address = "bim.studio.example/standards-2027";
                return true;
            });
            var edit = WindowCapture.Descendants<Button>(Tile(window, 2, 0)).Single(b => Id(b) == "Builder.EditCustomTool");
            Assert.Equal(Visibility.Visible, edit.Visibility);
            Click(edit);

            Assert.Equal(1, CustomToolFixtures.Holding(Disk(LoaderFixtures.ArchId), "https://bim.studio.example/standards-2027"));
            Assert.Equal("URL:https://bim.studio.example/standards-2027", h.Model.Profile.Panels[2].Slots[0].CommandId);
            Assert.Empty(h.Backend.Loads);
            Assert.Equal("Updated in 1 profile.", h.Model.Toast.Text);
            Assert.True(h.Model.IsDirty, "the tab edit is still unsaved");
            h.Model.ProfileTab = LoaderFixtures.Architecture().Tab!;
            Assert.False(h.Model.IsDirty, "and the custom tool change alone is not");
        }
        finally
        {
            Close(window, h);
        }
    });

    [Fact]
    public void The_confirmation_count_is_what_will_be_written_and_a_partial_failure_is_reported() => Sta(() =>
    {
        // A stale copy of Architecture under another file name: two files share its id, so neither is written.
        var (window, h) = Open(b =>
        {
            var stale = CustomToolFixtures.Architecture();
            stale.Panels[0].Name = "Stale";
            using var fs = File.Create(Path.Combine(b.ProfilesDir!, "Architecture_2024-01-01.json"));
            ProfileSerializer.Write(stale, fs);
        });
        try
        {
            var archFiles = Directory.GetFiles(_dir.Path, "Architecture*.json").ToDictionary(f => f, File.ReadAllText);
            h.Modals.CustomTools.Enqueue(d =>
            {
                Assert.Equal("Update in 1 profile", d.ConfirmText);
                Assert.Equal("Changes apply everywhere this tool is used: Structure. Names you gave it on a panel are kept.", d.Warning);
                Assert.Equal(2, d.Refusals.Count);
                Assert.All(d.Refusals, r => Assert.StartsWith("Architecture (Architecture_", r));
                d.Label = "Desk";
                return true;
            });
            Click(RowButton(Row(window, "Help"), "Builder.EditCustomToolEverywhere"));

            Assert.Equal("Desk", CustomToolFixtures.Tools(Disk(LoaderFixtures.StructId)).Single(s => CustomTools.LinkOf(s) == CustomToolFixtures.Help).BaseName);
            foreach (var (file, bytes) in archFiles) Assert.Equal(bytes, File.ReadAllText(file));
            var message = Assert.Single(h.Dialogs.Messages);
            Assert.Equal("Some profiles were not changed", message.Title);
            Assert.Contains("Architecture (Architecture_2024-01-01.json): 2 files share this profile's id", message.Message);
            Assert.Contains("The other profiles were changed and stay changed.", message.Message);

            // Remove everywhere: the confirmation lists Structure only, and its count says so.
            h.Dialogs.ConfirmResult = false;
            // (The list's label is the first occurrence by profile name: Architecture's "Help".)
            Click(RowButton(Row(window, "Help"), "Builder.RemoveCustomToolEverywhere"));
            var confirm = h.Dialogs.ConfirmCalls[^1];
            Assert.Equal(("Remove custom tool", "Remove from 1 profile", true), (confirm.Title, confirm.ConfirmText, confirm.Danger));
            Assert.Equal(["Structure"], h.Dialogs.ConfirmItems[^1]);
            Assert.Contains("Not changed:", confirm.Message);
        }
        finally
        {
            Close(window, h);
        }
    });

    [Fact]
    public void A_backend_that_throws_is_reported_not_crashed() => Sta(() =>
    {
        var (window, h) = Open(b => b.OnCustomToolChange = _ => throw new IOException("disk gone"));
        try
        {
            h.Modals.CustomTools.Enqueue(_ => true);
            Click(RowButton(Row(window, "Wiki"), "Builder.EditCustomToolEverywhere"));
            Assert.Equal("The tool could not be changed. See the RST log.", h.Model.Toast.Text);
            Assert.Equal(BannerKind.Error, h.Model.Toast.Kind);
            Assert.True(h.Model.IsIdle);
        }
        finally
        {
            Close(window, h);
        }
    });

    // ── × ──

    [Fact]
    public void Remove_on_a_tile_takes_it_off_that_panel_only() => Sta(() =>
    {
        var (window, h) = Open();
        try
        {
            var remove = WindowCapture.Descendants<Button>(Tile(window, 2, 0)).Single(b => Id(b) == "Builder.RemoveTool");
            Click(remove);
            Assert.Empty(h.Dialogs.ConfirmCalls);
            Assert.Equal("Help", Shape(h, 2));
            Assert.True(h.Model.IsDirty);
            Assert.Equal(1, CustomToolFixtures.Holding(Disk(LoaderFixtures.StructId), CustomToolFixtures.Wiki)); // the file is not changed until Save
            Assert.Empty(h.Backend.CustomToolChanges);
            // The list follows the working copy: no other profile holds the wiki, so it leaves.
            Assert.DoesNotContain("Wiki", Labels(window));
        }
        finally
        {
            Close(window, h);
        }
    });

    [Fact]
    public void Remove_on_a_row_asks_then_removes_it_from_every_profile() => Sta(() =>
    {
        var (window, h) = Open();
        try
        {
            h.Dialogs.ConfirmResult = false;
            Click(RowButton(Row(window, "Help"), "Builder.RemoveCustomToolEverywhere"));
            var confirm = Assert.Single(h.Dialogs.ConfirmCalls);
            Assert.Equal("Remove from 2 profiles", confirm.ConfirmText);
            Assert.Equal("Remove “Help” (bim@studio.example) from every profile that uses it? A stack left with one tool becomes a plain button.", confirm.Message);
            Assert.Equal(["Architecture", "Structure"], h.Dialogs.ConfirmItems[0]);
            Assert.Equal(ProfileBuilderViewModel.RemoveCustomToolDialogId, confirm.AutomationId);
            Assert.Empty(h.Backend.CustomToolChanges); // cancelled

            h.Dialogs.ConfirmResult = true;
            Click(RowButton(Row(window, "Help"), "Builder.RemoveCustomToolEverywhere"));
            Assert.Equal(0, CustomToolFixtures.Holding(Disk(LoaderFixtures.ArchId), CustomToolFixtures.Help));
            Assert.Equal(0, CustomToolFixtures.Holding(Disk(LoaderFixtures.StructId), CustomToolFixtures.Help));
            Assert.Equal("Wiki", Shape(h, 2));
            Assert.DoesNotContain("Help", Labels(window));
            Assert.Single(h.Backend.Loads);
            Assert.Equal("Removed from 2 profiles. Ribbon reloaded.", h.Model.Toast.Text);
            Assert.False(h.Model.IsDirty);
        }
        finally
        {
            Close(window, h);
        }
    });

    // ── drag and double-click ──

    [Fact]
    public void A_row_dragged_onto_a_panel_a_tile_and_a_stack_places_fresh_slots() => Sta(() =>
    {
        var (window, h) = Open();
        try
        {
            // Onto Frame's card outside its tiles: appended.
            var frame = Card(window, 0);
            var over = DragRow(window, h, Row(window, "Support"), frame, new Point(frame.ActualWidth - 8, frame.ActualHeight - 6));
            Assert.Equal(DragDropEffects.Copy, over.Effects);
            Assert.Equal("Beam,Column,Brace,Support", Shape(h, 0));

            // Onto Beam's middle: a stack of Beam and the tool.
            var beam = Tile(window, 0, 0);
            DragRow(window, h, Row(window, "Wiki"), beam, new Point(beam.ActualWidth / 2, beam.ActualHeight / 2));
            Assert.Equal("[Beam Wiki],Column,Brace,Support", Shape(h, 0));

            // Onto that stack: the tool joins it.
            var stack = StackGroup(window, 0, 0);
            DragRow(window, h, Row(window, "BIM Standards"), stack, new Point(stack.ActualWidth / 2, 6));
            Assert.Equal("[Beam Wiki BIM Standards],Column,Brace,Support", Shape(h, 0));

            var placed = CustomToolFixtures.Tools(h.Model.Profile).Where(s => CustomTools.LinkOf(s) == CustomToolFixtures.Wiki).ToList();
            Assert.Equal(2, placed.Count);
            Assert.NotSame(placed[0], placed[1]);
            Assert.True(h.Model.IsDirty);

            // A double-click adds to the selected panel.
            h.Model.Panels[1].SelectCommand.Execute(null);
            ((CustomToolItemViewModel)Row(window, "Help").DataContext).AddCommand.Execute(null);
            Assert.EndsWith(",Help", Shape(h, 1));
        }
        finally
        {
            Close(window, h);
        }
    });

    // ── the cards after a change underneath them (WU3 delta review) ──

    [Fact]
    public void After_an_edit_everywhere_the_cards_are_rebuilt_and_panel_up_moves_the_panel_you_pressed() => Sta(() =>
    {
        var (window, h) = Open();
        try
        {
            var before = h.Model.Panels.ToList();
            h.Modals.CustomTools.Enqueue(d =>
            {
                d.Label = "Studio wiki";
                return true;
            });
            Click(RowButton(Row(window, "Wiki"), "Builder.EditCustomToolEverywhere"));
            // Rebuilt from the changed working copy before anything else can run: no old card survives.
            Assert.All(h.Model.Panels, card => Assert.DoesNotContain(card, before));
            Assert.True(h.Model.IsIdle);

            // ▲ on Links, the card on screen now: Links moves, nothing else does.
            var up = WindowCapture.Descendants<Button>(Card(window, 2)).Single(b => Id(b) == "Builder.MovePanelUp");
            Assert.Equal("Links", ((PanelCardViewModel)up.DataContext).Name);
            Click(up);
            Assert.Equal(["Frame", "Links", "Review"], h.Model.Profile.Panels.Select(p => p.Name));
            Assert.Equal("Studio wiki", h.Model.Panels[1].Tile(0).Name);

            // And after a remove everywhere: ▼ on Frame.
            h.Dialogs.ConfirmResult = true;
            Click(RowButton(Row(window, "Help"), "Builder.RemoveCustomToolEverywhere"));
            var down = WindowCapture.Descendants<Button>(Card(window, 0)).Single(b => Id(b) == "Builder.MovePanelDown");
            Click(down);
            Assert.Equal(["Links", "Frame", "Review"], h.Model.Profile.Panels.Select(p => p.Name));
            Assert.Equal("Studio wiki", PanelCardTestExtensions.Shape(h.Model.Profile, 0));
        }
        finally
        {
            Close(window, h);
        }
    });

    // ── review F4, F5: the dialog's refusals and the confirmation's names ──

    [Fact]
    public void Email_mode_refuses_a_URL_before_anything_is_written() => Sta(() =>
    {
        var (window, h) = Open();
        try
        {
            var before = Directory.GetFiles(_dir.Path).ToDictionary(f => f, File.ReadAllText);
            h.Modals.CustomTools.Enqueue(d =>
            {
                d.IsEmail = true; // the URL tool's address stays "https://wiki.studio.example"
                Assert.Equal("Opens: mailto:https://wiki.studio.example", d.Opens);
                return true;
            });
            Click(RowButton(Row(window, "Wiki"), "Builder.EditCustomToolEverywhere"));

            var dialog = h.Modals.CustomToolDialogs.Single();
            Assert.Equal("That is not an email address. In Email mode, type an address such as bim@studio.example.", dialog.Error);
            Assert.Empty(h.Backend.CustomToolChanges);
            foreach (var (file, bytes) in before) Assert.Equal(bytes, File.ReadAllText(file));
        }
        finally
        {
            Close(window, h);
        }
    });

    [Fact]
    public void The_confirmation_tells_profiles_that_share_a_name_apart_and_says_when_nothing_can_change() => Sta(() =>
    {
        var tool = new CustomTool(CustomToolFixtures.Wiki, "Wiki", null, CustomToolKind.Url, ["Arch", "Struct"]);
        var plan = new CustomToolPlan(
        [
            new CustomToolTarget("Arch", "a1", "Arch_2026-01-01.json", CustomToolTargetState.Write, null, false),
            new CustomToolTarget("arch", "a2", "arch (2).json", CustomToolTargetState.Write, null, false),
            new CustomToolTarget("Struct", "s1", "Struct_2026-01-01.json", CustomToolTargetState.SharedId, "2 files share this profile's id", true),
            new CustomToolTarget("Struct", "s1", "", CustomToolTargetState.WorkingCopy, null, true),
            new CustomToolTarget("", null, "", CustomToolTargetState.WorkingCopy, null, true),
        ]);
        Assert.Equal(
            ["Arch (Arch_2026-01-01.json)", "arch (arch (2).json)", "Struct (open here; only its unsaved copy changes)", "New profile (open, not saved yet)"],
            CustomToolDialogModel.TargetNames(plan.Changing));
        var edit = CustomToolDialogModel.ForEdit(tool, plan, [], null);
        Assert.Equal("Update in 4 profiles", edit.ConfirmText);
        Assert.StartsWith("Changes apply everywhere this tool is used: Arch (Arch_2026-01-01.json), arch (arch (2).json), ", edit.Warning);

        var none = CustomToolDialogModel.ForEdit(tool, new CustomToolPlan([plan.Targets[2]]), [], null);
        Assert.Equal("No profile can be changed.", none.Warning);
        Assert.False(none.CanConfirm);
        Assert.Equal(["Struct (Struct_2026-01-01.json): files share this profile's id. Remove the stale copy."], none.Refusals);
    });
}
