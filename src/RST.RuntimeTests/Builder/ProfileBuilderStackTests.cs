// rst-c's own (not vendored): spec #17 (Ribbon Stacks), AC3, revised 2026-10-30 (decision #19:
// stack authoring moves to feature #18). The WPF Builder's guard for the stacks a profile already
// holds: a profile with stacks opens and saves byte-stable over the real backend; a stack slot's
// name is read-only in the panel list (view-model and the real window's box); deleting a stack
// slot leaves an orphan stack that the save removes (from the file and from the editor); and the
// Builder's Live Preview draws the stacks with 16 px icons, the ribbon's default_16.png for a tool
// without a pack icon.

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
}

/// <summary>The guard through the view-model, over the real backend (the real save) where the file matters.</summary>
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
    /// The real Builder backend on a temp profiles folder holding the stack profile, written as the
    /// Builder writes a profile (its edit defaults and required add-ins), as a hand-edited file
    /// saved once by the Builder would be. Returns the backend and the file's path.
    /// </summary>
    private (ProfileBuilderBackend Backend, string File) RealBackendWithStackFile()
    {
        Directory.CreateDirectory(ProfilesDir);
        var backend = new ProfileBuilderBackend("2026", [], new FakeScheduler(), ["Architecture", "Enscape"],
            LoaderPaths.Under(_root), LoaderAssets.LookupPath);
        var profile = ProfileEditing.ForEditing(StackFixtures.Profile());
        profile.RequiredAddins = RequiredAddinDetector.Compute(profile, backend.Catalog, backend.GetAddinLookup());
        var fileName = ProfileStore.Save(profile, ProfilesDir);
        return (backend, Path.Combine(ProfilesDir, fileName));
    }

    private static BuilderHarness Open(IProfileBuilderBackend backend)
    {
        var h = BuilderHarness.Create(backend);
        h.Model.SelectedProfileOption = h.Option("Stack Studio");
        h.Model.IsDirty.Should().BeFalse();
        return h;
    }

    [Fact]
    public async Task A_profile_with_stacks_opens_and_saves_byte_stable()
    {
        var (backend, file) = RealBackendWithStackFile();
        var before = File.ReadAllBytes(file);
        var h = Open(backend);
        h.Model.Profile.Stacks.Keys.Should().Equal("Openings", "Datum", "Help");

        (await h.Model.SaveAsync()).Should().BeTrue();

        File.ReadAllBytes(file).Should().Equal(before, "opening and saving a profile with stacks changes nothing");
        Directory.GetFiles(ProfilesDir).Should().ContainSingle("saving in place adds no file");
        h.Model.IsDirty.Should().BeFalse();

        // The stacks are the hand-edited ones, field for field (sourceTab, sourcePanel, iconFile kept).
        var saved = JsonNode.Parse(File.ReadAllText(file))!["stacks"];
        var source = JsonNode.Parse(ProfileSerializer.WriteString(StackFixtures.Profile()))!["stacks"];
        JsonNode.DeepEquals(saved, source).Should().BeTrue();
    }

    [Fact]
    public void A_stack_slot_s_name_cannot_be_edited_in_the_panel_list()
    {
        var h = Open(StackFixtures.Backend());
        var row = h.Model.Panels[0].Slots[1];
        row.IsStack.Should().BeTrue();
        row.TypeTag.Should().Be("stack");
        row.NameToolTip.Should().Be("A stack's name links this row to the stack, so it can't be changed here.");

        row.Name = "Broken link";

        row.Name.Should().Be("Openings");
        h.Model.Profile.Panels[0].Slots[1].Name.Should().Be("Openings");
        h.Model.Profile.Stacks.Keys.Should().Contain("Openings");
        h.Model.IsDirty.Should().BeFalse();

        var tool = h.Model.Panels[0].Slots[0];
        tool.IsStack.Should().BeFalse();
        tool.NameToolTip.Should().BeNull();
        tool.Name = "Big Wall";
        h.Model.Profile.Panels[0].Slots[0].Name.Should().Be("Big Wall", "tool names stay editable");
    }

    [Fact]
    public async Task Deleting_a_stack_slot_leaves_an_orphan_that_the_save_removes()
    {
        var (backend, file) = RealBackendWithStackFile();
        var h = Open(backend);
        var help = h.Model.Panels[1].Slots[0];
        help.IsStack.Should().BeTrue();

        help.RemoveCommand.Execute(null);

        h.Model.Profile.Panels[1].Slots.Select(s => s.Name).Should().Equal("Enscape");
        h.Model.Profile.Stacks.Keys.Should().Contain("Help", "the Builder leaves the stack itself alone");
        ProfileSerializer.OrphanStacks(h.Model.Profile).Should().Equal("Help");
        ProfileSerializer.Validate(h.Model.Profile).Should().BeEmpty("an orphan stack is a warning, not an error");

        (await h.Model.SaveAsync()).Should().BeTrue();

        var written = ProfileSerializer.ReadString(File.ReadAllText(file));
        written.Stacks.Keys.Should().Equal("Openings", "Datum");
        ProfileSerializer.Validate(written).Should().BeEmpty();
        ProfileSerializer.Warnings(written).Should().BeEmpty();
        h.Model.Profile.Stacks.Keys.Should().Equal(new[] { "Openings", "Datum" }, "the editor matches the file");
        h.Model.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void The_Builder_has_no_stack_authoring_and_its_preview_draws_the_stacks()
    {
        typeof(ProfileBuilderViewModel).GetProperty("Stacks").Should().BeNull("stack authoring is feature #18");
        typeof(ProfileBuilderViewModel).GetProperty("AddStackCommand").Should().BeNull();
        var h = Open(StackFixtures.Backend());
        h.Model.PreviewProfile.Stacks.Keys.Should().Equal("Openings", "Datum", "Help");
    }
}

/// <summary>The guard on a real ProfileBuilderWindow.</summary>
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
    public void A_stack_slot_s_box_is_read_only_and_the_live_preview_draws_the_stacks() => Sta(() =>
    {
        var h = BuilderHarness.Create(StackFixtures.Backend());
        h.Model.SelectedProfileOption = h.Option("Stack Studio");
        var window = new ProfileBuilderWindow(h.Model);
        WindowCapture.ShowOffScreen(window);
        try
        {
            var slotBoxes = WindowCapture.Descendants<TextBox>(window.PanelCards).Where(t => Id(t) == "Builder.SlotName").ToList();
            slotBoxes.Select(b => (b.Text, b.IsReadOnly)).Should().Equal(
                ("Wall", false), ("Openings", true), ("Datum", true), ("Help", true), ("Enscape", false));
            slotBoxes[1].ToolTip.Should().Be("A stack's name links this row to the stack, so it can't be changed here.");

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
}
