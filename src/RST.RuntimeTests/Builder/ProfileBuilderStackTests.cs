// rst-c's own (not vendored): spec #17 (Ribbon Stacks), AC3, revised 2026-10-30, and feature #18
// (spec #19, WU3, AC5): stacks in the Builder. A profile with stacks opens and saves byte-stable over
// the real backend; the Live Preview draws the stacks with 16 px icons (the ribbon's default_16.png
// for a tool without a pack icon); and the stack authoring on the panel cards: + New Stack and ✎
// (the stack dialog, including saving below 2), × on a stack and on its rows, the 4+ Save refusal,
// and the load normaliser (a shared stack is split, the editor is dirty, Save writes two stacks).

using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FluentAssertions;
using RST.Core.Builder;
using RST.Core.Loader;
using RST.Core.Profiles;
using RST.Core.Theming;
using RST.RuntimeTests.Support;
using RST.Tests.Loader;
using RST.UI.Builder;
using RST.UI.Controls;
using RST.UI.Dialogs;
using RST.UI.Loader;
using RST.UI.Theming;
using Xunit;
using Panel = RST.Core.Profiles.Panel;

namespace RST.RuntimeTests.Builder;

/// <summary>
/// A hand-edited profile with stacks of 2 and 3 tools placed in two panels. Every stack tool has a
/// pack icon except Window, which shows the default 16 px icon (as on the VM's StackTest).
/// </summary>
public static class StackFixtures
{
    public const string Id = "0d6b1c9e-5f7a-4e3b-9c21-7a8b9c0d1e2f";

    private static Slot Tool(string name, string commandId, string? icon, string tab = "Architecture", string panel = "Build") =>
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
                    Tool("Window", "ID_OBJECTS_WINDOW", null),
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

    /// <summary>The preview's 16 px stack-row icons (the large tools' icons are 20 px).</summary>
    public static IReadOnlyList<Image> StackRowIcons(RibbonPreview preview) =>
        WindowCapture.Descendants<Image>(preview).Where(i => i.Source is BitmapSource { PixelWidth: 16 } && i.Width == 16).ToList();

    /// <summary>
    /// A hand-edited profile whose two panels place the same stack (the one shape read validation
    /// lets through): the normaliser gives the second slot its own copy.
    /// </summary>
    public static Profile Shared() => new()
    {
        Id = "5b1f0c2e-9d3a-4c7b-8e21-3f4a5b6c7d8e",
        ProfileName = "Shared Stack",
        Tab = "Studio Shared",
        MinVersion = "3.1.0",
        ExportDate = "2026-10-30",
        Stacks = new()
        {
            ["Datum"] = new Stack { Tools = [Tool("Level", "ID_OBJECTS_LEVEL", "pack:level_green"), Tool("Grid", "ID_OBJECTS_GRID", "pack:parallel_dark_grey")] },
        },
        Panels =
        [
            new Panel { Name = "Model", Color = "#4f8ef7", Slots = [Tool("Wall", "ID_OBJECTS_WALL", null), new Slot { SlotType = "stack", Name = "Datum" }] },
            new Panel { Name = "Annotate", Color = "#10b981", Slots = [new Slot { SlotType = "stack", Name = "Datum" }] },
        ],
    };
}

/// <summary>Stacks in the Builder through the view-model, over the real backend (the real save) where the file matters.</summary>
public sealed class ProfileBuilderStackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rst-stacks-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string ProfilesDir => Path.Combine(_root, "profiles");

    /// <summary>
    /// The real Builder backend on a temp profiles folder holding <paramref name="source"/>, written
    /// as the Builder writes a profile (its edit defaults and required add-ins). Returns the backend
    /// and the file's path.
    /// </summary>
    private (ProfileBuilderBackend Backend, string File) RealBackendWith(Profile source)
    {
        Directory.CreateDirectory(ProfilesDir);
        var backend = new ProfileBuilderBackend("2026", [], new FakeScheduler(), ["Architecture", "Enscape"],
            LoaderPaths.Under(_root), LoaderAssets.LookupPath);
        var profile = ProfileEditing.ForEditing(source);
        profile.RequiredAddins = RequiredAddinDetector.Compute(profile, backend.Catalog, backend.GetAddinLookup(), 2026);
        var fileName = ProfileStore.Save(profile, ProfilesDir);
        return (backend, Path.Combine(ProfilesDir, fileName));
    }

    private static BuilderHarness Open(IProfileBuilderBackend backend, string name = "Stack Studio")
    {
        var h = BuilderHarness.Create(backend);
        h.Model.SelectedProfileOption = h.Option(name);
        return h;
    }

    private static string Shape(BuilderHarness h, int panel) => PanelCardTestExtensions.Shape(h.Model.Profile, panel);

    /// <summary>Every stack slot names its own stack of 2-3 tools; no stack is shared or orphaned; Validate passes.</summary>
    private static void AssertConsistent(Profile p)
    {
        var refs = p.Panels.SelectMany(x => x.Slots).Where(PanelStacks.IsStack).Select(s => s.Name).ToList();
        refs.Should().OnlyHaveUniqueItems();
        refs.Should().BeEquivalentTo(p.Stacks.Keys);
        p.Stacks.Values.Should().OnlyContain(s => s.Tools.Count >= 2 && s.Tools.Count <= 3);
        ProfileSerializer.Validate(p).Should().BeEmpty();
    }

    [Fact]
    public async Task A_profile_with_stacks_opens_and_saves_byte_stable()
    {
        var (backend, file) = RealBackendWith(StackFixtures.Profile());
        var before = File.ReadAllBytes(file);
        var h = Open(backend);
        h.Model.IsDirty.Should().BeFalse("a consistent profile needs no repair");
        h.Model.Profile.Stacks.Keys.Should().Equal("Openings", "Datum", "Help");

        (await h.Model.SaveAsync()).Should().BeTrue();

        File.ReadAllBytes(file).Should().Equal(before, "opening and saving a profile with stacks changes nothing");
        Directory.GetFiles(ProfilesDir).Should().ContainSingle("saving in place adds no file");
        h.Model.IsDirty.Should().BeFalse();

        var saved = JsonNode.Parse(File.ReadAllText(file))!["stacks"];
        var source = JsonNode.Parse(ProfileSerializer.WriteString(StackFixtures.Profile()))!["stacks"];
        JsonNode.DeepEquals(saved, source).Should().BeTrue();
    }

    [Fact]
    public async Task A_shared_stack_is_split_on_open_the_editor_is_dirty_and_save_writes_two_stacks()
    {
        var (backend, file) = RealBackendWith(StackFixtures.Shared());
        var h = Open(backend, "Shared Stack");

        // WU1 review F2, WU2 review F1: the baseline is taken before the normaliser, so the repair is an unsaved change.
        h.Model.IsDirty.Should().BeTrue("a normalised profile counts as changed");
        h.Model.SaveCommand.CanExecute(null).Should().BeTrue("Save is enabled, so the repair can be saved");
        h.Model.Toast.Text.Should().Be("This profile's stacks were repaired when it opened (1). Save to keep the repair.");
        h.Model.Profile.Stacks.Should().HaveCount(2);
        AssertConsistent(h.Model.Profile);
        h.Model.Panels[1].StackAt(0).Rows.Select(r => r.Name).Should().Equal("Level", "Grid");

        (await h.Model.SaveAsync()).Should().BeTrue();
        var written = ProfileSerializer.ReadString(File.ReadAllText(file));
        written.Stacks.Should().HaveCount(2, "the file holds two separate stacks");
        var names = written.Panels.SelectMany(p => p.Slots).Where(PanelStacks.IsStack).Select(s => s.Name).ToList();
        names.Should().OnlyHaveUniqueItems().And.HaveCount(2);
        written.Stacks.Values.Should().OnlyContain(s => s.Tools.Select(t => t.Name).SequenceEqual(new[] { "Level", "Grid" }));
        ProfileSerializer.Validate(written).Should().BeEmpty();
        h.Model.IsDirty.Should().BeFalse();

        // Reopened: nothing left to repair.
        var again = Open(backend, "Shared Stack");
        again.Model.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void New_stack_groups_the_picked_loose_tools_in_pick_order_in_the_first_pick_s_place()
    {
        var h = Open(StackFixtures.Backend());
        var links = h.Model.Panels[1]; // [Revit Help, Studio Wiki], Enscape: one loose tool
        links.NewStackCommand.CanExecute(null).Should().BeFalse("+ New Stack needs 2 loose tools");

        h.Model.AddToSelectedPanel(h.Command("ID_OBJECTS_BEAM").Command!); // Model: Wall, [..], [..], Beam
        h.Model.Panels[0].NewStackCommand.CanExecute(null).Should().BeTrue();

        // Cancel changes nothing.
        h.Model.Panels[0].NewStackCommand.Execute(null);
        var cancelled = h.Modals.StackDialogs.Single().Model;
        cancelled.Title.Should().Be("New stack on “Model”");
        cancelled.Items.Select(i => i.Name).Should().Equal("Wall", "Beam");
        cancelled.ConfirmText.Should().Be("Create stack");
        Shape(h, 0).Should().Be("Wall,[Door Window Opening],[Level Grid],Beam");

        // Pick Beam then Wall: one pick is not enough, two are.
        h.Modals.Stacks.Enqueue(m =>
        {
            FakeBuilderDialogs.Tick(m, "Beam");
            m.CanConfirm.Should().BeFalse("Create stack is enabled at 2");
            m.Status.Should().Be("1 selected · a stack holds 2–3 tools");
            FakeBuilderDialogs.Tick(m, "Wall");
            m.CanConfirm.Should().BeTrue();
            m.Items.Select(i => i.OrderText).Should().Equal("#2", "#1");
            return true;
        });
        h.Model.Panels[0].NewStackCommand.Execute(null);
        Shape(h, 0).Should().Be("[Door Window Opening],[Level Grid],[Beam Wall]", "in the first pick's place (Beam's), in pick order");
        h.Model.Profile.Stacks.Keys.Should().Contain("Stack 1");
        AssertConsistent(h.Model.Profile);
        h.Model.IsDirty.Should().BeTrue();
    }

    [Fact]
    public void Edit_stack_reorders_unticks_back_to_the_panel_and_adds_loose_tools()
    {
        var h = Open(StackFixtures.Backend());
        h.Modals.Stacks.Enqueue(m =>
        {
            m.Title.Should().Be("Edit stack on “Model”");
            m.ConfirmText.Should().Be("Save stack");
            m.Items.Select(i => (i.Name, i.IsChecked, i.IsMember)).Should().Equal(
                ("Door", true, true), ("Window", true, true), ("Opening", true, true), ("Wall", false, false));
            m.Items.Single(i => i.Name == "Wall").CanCheck.Should().BeFalse("a fourth tick is refused");
            m.Items.Select(i => i.MemberNote).Should().Equal("in this stack", "in this stack", "in this stack", "");
            FakeBuilderDialogs.Untick(m, "Window");
            m.Items.Single(i => i.Name == "Window").MemberNote.Should().Be("goes back on the panel", "review F2");
            FakeBuilderDialogs.Tick(m, "Wall");
            m.Items.Single(i => i.Name == "Wall").LaterCommand.CanExecute(null).Should().BeFalse("already last");
            m.Items.Single(i => i.Name == "Wall").EarlierCommand.Execute(null);
            m.Items.Single(i => i.Name == "Door").LaterCommand.Execute(null);
            m.Items.Where(i => i.IsChecked).OrderBy(i => i.Order).Select(i => i.Name).Should().Equal("Wall", "Door", "Opening");
            return true;
        });
        h.Model.Panels[0].StackAt(1).EditCommand.Execute(null);
        Shape(h, 0).Should().Be("[Wall Door Opening],Window,[Level Grid]", "Window goes back on the panel right after the stack");
        AssertConsistent(h.Model.Profile);
    }

    [Fact]
    public void Saving_an_edited_stack_with_fewer_than_2_removes_it_and_keeps_its_tools()
    {
        var h = Open(StackFixtures.Backend());
        h.Modals.Stacks.Enqueue(m =>
        {
            FakeBuilderDialogs.Untick(m, "Grid");
            m.CanConfirm.Should().BeTrue("editing, Save stack is always available");
            m.Status.Should().Be("1 selected · saving with fewer than 2 removes the stack; its tools stay on the panel");
            return true;
        });
        h.Model.Panels[0].StackAt(2).EditCommand.Execute(null);
        Shape(h, 0).Should().Be("Wall,[Door Window Opening],Level,Grid");
        h.Model.Profile.Stacks.Keys.Should().NotContain("Datum");
        AssertConsistent(h.Model.Profile);
    }

    [Fact]
    public void X_on_a_stack_unstacks_it_and_x_on_a_row_removes_that_tool_only()
    {
        var h = Open(StackFixtures.Backend());
        h.Model.Panels[0].StackAt(1).UnstackCommand.Execute(null);
        Shape(h, 0).Should().Be("Wall,Door,Window,Opening,[Level Grid]", "its tools go back on the panel in order");

        h.Model.Panels[0].Tile(4, row: 0).RemoveCommand.Execute(null); // Level
        Shape(h, 0).Should().Be("Wall,Door,Window,Opening,Grid", "a stack left with one tool becomes that tool");
        h.Model.Panels[1].Tile(1).RemoveCommand.Execute(null); // Enscape
        Shape(h, 1).Should().Be("[Revit Help Studio Wiki]");
        h.Dialogs.ConfirmCalls.Should().BeEmpty("× removes from this panel without asking");
        AssertConsistent(h.Model.Profile);
        h.Model.Profile.Stacks.Keys.Should().Equal("Help");
    }

    [Fact]
    public async Task Save_refuses_a_stack_of_4_naming_the_panel()
    {
        var (backend, file) = RealBackendWith(StackFixtures.Profile());
        var before = File.ReadAllBytes(file);
        var h = Open(backend);
        // No Builder path makes one; a working copy that holds one anyway (decision #28) cannot be saved.
        h.Model.Profile.Stacks["Openings"].Tools.Add(new Slot { Name = "Curtain", CommandId = "ID_CURTAIN" });
        h.Model.ProfileEdited();
        h.Model.IsDirty.Should().BeTrue();

        (await h.Model.SaveAsync()).Should().BeFalse();
        h.Model.Toast.Text.Should().Be("Panel \"Model\" has a stack of 4 tools. A stack holds 2–3 tools; move some out before saving.");
        h.Model.Toast.Kind.Should().Be(BannerKind.Error);
        File.ReadAllBytes(file).Should().Equal(before);
    }

    [Fact]
    public async Task Inline_panel_name_and_colour_edits_survive_save()
    {
        var (backend, file) = RealBackendWith(StackFixtures.Profile());
        var h = Open(backend);
        h.Model.Panels[1].Name = "Help & Links";
        h.Dialogs.PickResults.Enqueue(RST.Core.Colors.Rgb.Parse("#EC4899"));
        h.Model.Panels[1].PickColorCommand.Execute(null);
        h.Model.SelectedPanel!.Name.Should().Be("Help & Links", "the swatch selects its card");
        h.Model.Panels[0].Tile(0).Name = "Big Wall";
        h.Model.Panels[0].Tile(1, row: 2).Name = "Hole";

        (await h.Model.SaveAsync()).Should().BeTrue();
        var written = ProfileSerializer.ReadString(File.ReadAllText(file));
        written.Panels[1].Name.Should().Be("Help & Links");
        written.Panels[1].Color.Should().Be("#ec4899");
        written.Panels[0].Slots[0].Name.Should().Be("Big Wall");
        written.Stacks["Openings"].Tools[2].Name.Should().Be("Hole");
    }

    [Fact]
    public void Deleting_a_panel_removes_its_stacks_at_once()
    {
        var h = Open(StackFixtures.Backend());
        h.Dialogs.ConfirmResult = true;
        h.Model.Panels[0].DeleteCommand.Execute(null); // Model: Openings and Datum
        h.Model.Profile.Stacks.Keys.Should().Equal(new[] { "Help" }, "no orphan stack stays in the working copy (review N2)");
        AssertConsistent(h.Model.Profile);
        ProfileSerializer.OrphanStacks(h.Model.Profile).Should().BeEmpty();
    }

    [Fact]
    public void Stack_actions_on_a_card_that_no_longer_matches_the_profile_are_refused_and_redrawn()
    {
        var h = Open(StackFixtures.Backend());
        var model = h.Model.Panels[0];
        var row = model.Tile(1, row: 1);      // Openings › Window
        var stack = model.StackAt(2);         // Datum
        // The working copy changes under the cards with no redraw (as WU4's edit everywhere may):
        // Wall leaves, so every slot index on the old card is off by one.
        h.Model.Profile.Panels[0].Slots.RemoveAt(0);
        var before = Shape(h, 0);

        row.RemoveCommand.Execute(null);
        Shape(h, 0).Should().Be(before, "× on a row of a moved stack is refused (review N1)");
        h.Model.Panels[0].Should().NotBeSameAs(model, "the cards are redrawn from the profile");

        stack.UnstackCommand.Execute(null);
        stack.EditCommand.Execute(null);
        h.Modals.StackDialogs.Should().BeEmpty("✎ on a stale stack opens nothing");
        Shape(h, 0).Should().Be(before);

        // A stale tile: its slot index now holds another tool.
        var links = h.Model.Panels[1];
        var enscape = links.Tile(1);
        h.Model.Profile.Panels[1].Slots.Insert(0, new Slot { Name = "New", CommandId = "ID_NEW" });
        enscape.RemoveCommand.Execute(null);
        Shape(h, 1).Should().Be("New,[Revit Help Studio Wiki],Enscape");
        AssertConsistent(h.Model.Profile);
    }

    [Fact]
    public void A_stack_dialog_refuses_when_its_panel_changed_while_it_was_open()
    {
        var h = Open(StackFixtures.Backend());
        h.Modals.Stacks.Enqueue(m =>
        {
            FakeBuilderDialogs.Untick(m, "Grid");
            h.Model.Profile.Panels[0].Slots.Insert(0, new Slot { Name = "Late", CommandId = "ID_LATE" }); // underneath
            return true;
        });
        h.Model.Panels[0].StackAt(2).EditCommand.Execute(null);
        Shape(h, 0).Should().Be("Late,Wall,[Door Window Opening],[Level Grid]", "nothing un-stacked at a moved index");
        AssertConsistent(h.Model.Profile);
    }

    [Fact]
    public void The_live_preview_draws_the_stacks_the_cards_author()
    {
        var h = Open(StackFixtures.Backend());
        h.Model.PreviewProfile.Stacks.Keys.Should().Equal("Openings", "Datum", "Help");
        var before = h.Model.PreviewRevision;
        h.Model.Panels[0].StackAt(2).UnstackCommand.Execute(null);
        h.Model.PreviewRevision.Should().BeGreaterThan(before);
        h.Model.PreviewProfile.Stacks.Keys.Should().Equal("Openings", "Help");
    }
}

/// <summary>Stacks on a real ProfileBuilderWindow.</summary>
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

    private static string Id(System.Windows.DependencyObject d) => System.Windows.Automation.AutomationProperties.GetAutomationId(d);

    [Fact]
    public void The_cards_show_stack_groups_with_editable_rows_and_the_live_preview_draws_the_stacks() => Sta(() =>
    {
        var h = BuilderHarness.Create(StackFixtures.Backend());
        h.Model.SelectedProfileOption = h.Option("Stack Studio");
        var window = new ProfileBuilderWindow(h.Model);
        WindowCapture.ShowOffScreen(window);
        try
        {
            var headers = WindowCapture.Descendants<TextBlock>(window.PanelCards).Where(t => Id(t) == "Builder.StackHeaderText").Select(t => t.Text);
            headers.Should().Equal("STACK · 3/3", "STACK · 2/3", "STACK · 2/3");
            var names = WindowCapture.Descendants<TextBox>(window.PanelCards).Where(t => Id(t) == "Builder.ToolName").ToList();
            names.Select(b => b.Text).Should().Equal("Wall", "Door", "Window", "Opening", "Level", "Grid", "Revit Help", "Studio Wiki", "Enscape");
            names.Should().OnlyContain(b => !b.IsReadOnly, "every tool's label is editable, in a stack too");
            WindowCapture.Descendants<TextBox>(window.PanelCards).Select(b => b.Text).Should().NotContain(["Openings", "Datum", "Help"],
                "the Builder never shows a stack's name");

            // The preview: 2 panels; 3 stacks drawn as rows of 16 px icons (7 tools), Window's the default.
            window.Preview.DrawnPanels.Count.Should().Be(2);
            var rows = StackFixtures.StackRowIcons(window.Preview);
            rows.Should().HaveCount(7);
            rows.Count(i => ReferenceEquals(i.Source, PackIcons.Default16)).Should().Be(1, "Window has no pack icon");
        }
        finally
        {
            h.Dialogs.ChooseAnswers.Enqueue(DialogChoice.Secondary);
            window.Close();
        }
    });

    [Fact]
    public void The_stack_dialog_creates_only_at_2_and_closes_on_confirm() => Sta(() =>
    {
        var model = StackDialogModel.ForNew("Frame", [(0, new Slot { Name = "Beam", CommandId = "B" }), (1, new Slot { Name = "Column", CommandId = "C" }), (2, new Slot { Name = "Brace", CommandId = "R" })]);
        var dialog = new StackDialog(model);
        WindowCapture.ShowOffScreen(dialog);
        try
        {
            dialog.ShowInTaskbar.Should().BeFalse();
            dialog.ConfirmButton.IsEnabled.Should().BeFalse("nothing ticked");
            var boxes = WindowCapture.Descendants<CheckBox>(dialog.PickList).ToList();
            boxes.Should().HaveCount(3);
            boxes[1].IsChecked = true;
            WindowCapture.DoEvents();
            dialog.ConfirmButton.IsEnabled.Should().BeFalse("one tool is not a stack");
            boxes[0].IsChecked = true;
            boxes[2].IsChecked = true;
            WindowCapture.DoEvents();
            dialog.ConfirmButton.IsEnabled.Should().BeTrue();
            model.Picks.Should().Equal(StackPick.Loose(1), StackPick.Loose(0), StackPick.Loose(2));
            dialog.StatusLine.Text.Should().Be("3 selected · a stack holds 2–3 tools");
        }
        finally
        {
            dialog.Close();
        }
    });
}

