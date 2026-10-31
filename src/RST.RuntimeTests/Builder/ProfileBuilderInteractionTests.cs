// rst-c's own (not vendored): the Profile Builder's WU5 flows on the view-model (spec #16, WU5,
// AC5): the icon picker (open on the slot's value, a variant commits and redraws, None clears,
// Cancel / Escape / outside click change nothing, legacy values), drag and drop (the four kinds
// and their markers, through the view-model the window drives), and the Profile pane's Loader
// defaults and Junk cleanup tabs, which replaced the RST Presets and Junk Cleanup modals (feature
// #18, spec #19): edits are unsaved changes, saved with Save.

using System.IO;
using FluentAssertions;
using RST.Core.Builder;
using RST.Core.Profiles;
using RST.UI.Builder;
using Xunit;

namespace RST.RuntimeTests.Builder;

public class ProfileBuilderInteractionTests
{
    private static BuilderHarness Structure()
    {
        var h = BuilderHarness.Create();
        h.Model.SelectedProfileOption = h.Option("Structure");
        return h;
    }

    // ── the icon picker ──────────────────────────────────────────────────────

    [Fact]
    public void Pick_icon_opens_the_picker_on_the_slot_s_value_without_changing_it()
    {
        var h = Structure();
        var beam = h.Model.Panels[0].Slots[0]; // pack:parallel_red
        beam.PickIconCommand.Execute(null);
        var picker = h.Model.IconPicker!;
        h.Model.IsIconPickerOpen.Should().BeTrue();
        picker.Model.SelectedDesign.Should().Be("parallel");
        picker.Model.SelectedColor.Should().Be("red");
        picker.Rows[0].Tiles[0].IsNone.Should().BeTrue("the None tile comes first");
        picker.Rows.SelectMany(r => r.Tiles).Count().Should().Be(56, "None + the 55 shipped designs");
        picker.TileCommand.Execute("parallel");
        picker.Rows.Should().Contain(r => r.IsVariantRow);
        h.Model.IsDirty.Should().BeFalse("opening the picker or a variant row writes nothing");
        h.Backend.Calls.Count(c => c == "GetIconPack").Should().Be(1);
    }

    [Fact]
    public void A_variant_commits_the_value_updates_the_button_and_redraws_the_preview()
    {
        var h = Structure();
        var beam = h.Model.Panels[0].Slots[0];
        var before = h.Model.PreviewRevision;
        beam.PickIconCommand.Execute(null);
        h.Model.IconPicker!.TileCommand.Execute("move");
        h.Model.IconPicker!.VariantCommand.Execute("green");
        h.Model.IsIconPickerOpen.Should().BeFalse();
        h.Model.Profile.Panels[0].Slots[0].IconFile.Should().Be("pack:move_green");
        beam.IconLabel.Should().Be("move");
        beam.HasIconThumbnail.Should().BeTrue();
        h.Model.PreviewRevision.Should().BeGreaterThan(before);
        h.Model.IsDirty.Should().BeTrue();

        // The pack list is read once per window.
        h.Model.Panels[0].Slots[1].PickIconCommand.Execute(null);
        h.Model.IconPicker!.CancelCommand.Execute(null);
        h.Backend.Calls.Count(c => c == "GetIconPack").Should().Be(1);
    }

    [Fact]
    public void None_clears_the_icon()
    {
        var h = Structure();
        h.Model.Panels[0].Slots[0].PickIconCommand.Execute(null);
        h.Model.IconPicker!.NoneCommand.Execute(null);
        h.Model.Profile.Panels[0].Slots[0].IconFile.Should().BeNull();
        h.Model.Panels[0].Slots[0].IconLabel.Should().Be("Icon");
    }

    [Fact]
    public void Cancel_escape_and_an_outside_click_change_nothing()
    {
        var h = Structure();
        var slot = h.Model.Panels[0].Slots[0];
        slot.PickIconCommand.Execute(null);
        h.Model.IconPicker!.CancelCommand.Execute(null);
        h.Model.IsIconPickerOpen.Should().BeFalse();

        slot.PickIconCommand.Execute(null);
        h.Model.IconPicker!.OutsideClick();
        h.Model.IsIconPickerOpen.Should().BeFalse();

        slot.PickIconCommand.Execute(null);
        var picker = h.Model.IconPicker!;
        picker.TileCommand.Execute("move");
        picker.Escape();
        h.Model.IsIconPickerOpen.Should().BeTrue("the first Escape closes only the variant row");
        picker.Model.FocusedTile.Should().Be("move");
        picker.Escape();
        h.Model.IsIconPickerOpen.Should().BeFalse();

        h.Model.Profile.Panels[0].Slots[0].IconFile.Should().Be("pack:parallel_red");
        h.Model.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void A_legacy_bare_value_restores_as_blue_and_is_kept_until_a_variant_is_picked()
    {
        var h = Structure();
        var wiki = h.Model.Panels[2].Slots[0]; // pack:link_external (bare)
        wiki.PickIconCommand.Execute(null);
        var picker = h.Model.IconPicker!;
        picker.Model.SelectedDesign.Should().Be("link_external");
        picker.Model.SelectedColor.Should().Be("blue");
        picker.TileCommand.Execute("link_external");
        picker.Model.FocusedColor.Should().Be("blue");
        picker.Rows.Single(r => r.IsVariantRow).Variants.Single(v => v.IsSelected).Color.Should().Be("blue");
        picker.CancelCommand.Execute(null);
        h.Model.Profile.Panels[2].Slots[0].IconFile.Should().Be("pack:link_external");
    }

    [Fact]
    public void The_variant_row_follows_the_keyboard_and_enter_commits_the_focused_color()
    {
        var h = Structure();
        h.Model.Panels[0].Slots[0].PickIconCommand.Execute(null);
        var picker = h.Model.IconPicker!;
        picker.TileCommand.Execute("parallel");
        picker.Model.FocusedColor.Should().Be("red");
        picker.Key(PickerKey.Home);
        picker.Key(PickerKey.Right);
        picker.ActivateFocused();
        h.Model.Profile.Panels[0].Slots[0].IconFile.Should().Be("pack:parallel_dark_grey");
    }

    [Fact]
    public void A_throwing_pack_read_leaves_only_None_and_is_not_retried()
    {
        var backend = BuilderFixtures.Backend();
        backend.OnIconPack = () => throw new IOException("pack gone");
        var h = BuilderHarness.Create(backend);
        h.Model.SelectedProfileOption = h.Option("Structure");
        h.Model.Panels[0].Slots[0].PickIconCommand.Execute(null);
        var tiles = h.Model.IconPicker!.Rows.SelectMany(r => r.Tiles).ToList();
        tiles.Should().ContainSingle().Which.IsNone.Should().BeTrue("only the None tile shows");
        h.Model.IconPicker!.CancelCommand.Execute(null);
        h.Model.Panels[0].Slots[1].PickIconCommand.Execute(null);
        h.Backend.Calls.Count(c => c == "GetIconPack").Should().Be(1, "the empty list is cached for the window");
    }

    [Fact]
    public void A_broken_variant_image_is_disabled_and_logged_once()
    {
        var events = new List<RST.Core.Logging.LogEvent>();
        var previous = RST.Core.Logging.Log.Logger;
        RST.Core.Logging.Log.Logger = new RST.Core.Logging.Logger(RST.Core.Logging.LogLevel.Verbose, new ListSink(events));
        var empty = Path.Combine(Path.GetTempPath(), "rst-pack-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            var design = "zz_broken_" + Guid.NewGuid().ToString("N")[..8];
            var picker = new RST.UI.Controls.IconPickerViewModel(
                [new RST.Core.Ribbon.IconPackEntry(design, IconPickerModel.PackColors)], null, empty, _ => { });
            picker.TileCommand.Execute(design);
            picker.Rows.Single(r => r.IsVariantRow).Variants.Should().OnlyContain(v => !v.IsEnabled, "no image file: every variant is disabled");
            picker.Key(PickerKey.Right); // re-render: still logged once per variant
            var logged = events.Where(e => e.Level == RST.Core.Logging.LogLevel.Error && e.RenderMessage().Contains(design, StringComparison.Ordinal)).ToList();
            logged.Should().HaveCount(7, "each broken variant is logged once");
            picker.VariantCommand.Execute("orange");
            picker.Model.Outcome.Should().BeNull("a disabled variant never applies");
        }
        finally
        {
            RST.Core.Logging.Log.Logger = previous;
            Directory.Delete(empty, recursive: true);
        }
    }

    private sealed class ListSink(List<RST.Core.Logging.LogEvent> events) : RST.Core.Logging.ILogSink
    {
        public void Emit(RST.Core.Logging.LogEvent logEvent, IReadOnlyDictionary<string, object?> properties)
        {
            lock (events) events.Add(logEvent);
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public void Switching_profiles_closes_the_picker()
    {
        var h = Structure();
        h.Model.Panels[0].Slots[0].PickIconCommand.Execute(null);
        h.Model.NewProfileCommand.Execute(null);
        h.Model.IsIconPickerOpen.Should().BeFalse();
    }

    // ── drag and drop ────────────────────────────────────────────────────────

    [Fact]
    public void Dragging_a_command_onto_a_card_highlights_it_and_appends_the_slot()
    {
        var h = Structure();
        var wall = h.Command("ID_OBJECTS_WALL").Command!;
        h.Model.BeginDrag(BuilderDrag.FromCatalog(wall));
        h.Model.DragOverPanel(2, upperHalf: true).Should().BeTrue();
        h.Model.Panels[2].DropMarker.Should().Be(DropMarker.Over);
        h.Model.Panels[0].DropMarker.Should().Be(DropMarker.None);
        h.Model.DropOnPanel(2, upperHalf: true);
        h.Model.Profile.Panels[2].Slots.Select(s => s.Name).Should().Equal("Wiki", "Help", "Wall");
        h.Model.SelectedPanelIndex.Should().Be(2);
        h.Model.Panels.Should().OnlyContain(p => p.DropMarker == DropMarker.None);
        h.Model.EndDrag();
        h.Model.CurrentDrag.Should().BeNull();
    }

    [Fact]
    public void Dragging_a_command_onto_a_slot_inserts_it_before_or_after()
    {
        var h = Structure();
        var wall = h.Command("ID_OBJECTS_WALL").Command!;
        h.Model.BeginDrag(BuilderDrag.FromCatalog(wall));
        h.Model.DragOverSlot(0, 1, upperHalf: false).Should().BeTrue();
        h.Model.Panels[0].Slots[1].DropMarker.Should().Be(DropMarker.After);
        h.Model.DropOnSlot(0, 1, upperHalf: false);
        h.Model.Profile.Panels[0].Slots.Select(s => s.Name).Should().Equal("Beam", "Column", "Wall", "Brace");
    }

    [Fact]
    public void Dragging_a_slot_reorders_it_marks_the_source_and_moves_it_across_panels()
    {
        var h = Structure();
        h.Model.BeginDrag(BuilderDrag.FromSlot(0, 0));
        h.Model.Panels[0].Slots[0].IsDragSource.Should().BeTrue();
        h.Model.DragOverSlot(0, 2, upperHalf: true).Should().BeTrue();
        h.Model.Panels[0].Slots[2].DropMarker.Should().Be(DropMarker.Before);
        h.Model.DropOnSlot(0, 2, upperHalf: true);
        h.Model.Profile.Panels[0].Slots.Select(s => s.Name).Should().Equal("Column", "Beam", "Brace");

        h.Model.BeginDrag(BuilderDrag.FromSlot(0, 2));
        h.Model.DropOnPanel(1, upperHalf: false);
        h.Model.Profile.Panels[1].Slots.Select(s => s.Name).Should().Equal("Kinship", "Checks", "Notes", "Brace");
        h.Model.SelectedPanelIndex.Should().Be(1);
    }

    [Fact]
    public void Dragging_a_panel_header_reorders_the_panels_with_the_before_and_after_markers()
    {
        var h = Structure();
        h.Model.BeginDrag(BuilderDrag.FromPanel(0));
        h.Model.DragOverPanel(0, upperHalf: true).Should().BeFalse("a panel never drops on itself");
        h.Model.DragOverPanel(2, upperHalf: false).Should().BeTrue();
        h.Model.Panels[2].DropMarker.Should().Be(DropMarker.After);
        h.Model.DragOverSlot(1, 0, upperHalf: true).Should().BeFalse("a panel never drops on a slot");
        h.Model.DropOnPanel(2, upperHalf: false);
        h.Model.Panels.Select(p => p.Name).Should().Equal("Review", "Links", "Frame");
        h.Model.SelectedPanel!.Name.Should().Be("Frame");
    }

    [Fact]
    public void A_cancelled_or_no_op_drag_changes_nothing_and_clears_the_markers()
    {
        var h = Structure();
        h.Model.BeginDrag(BuilderDrag.FromSlot(0, 0));
        h.Model.DragOverPanel(0, upperHalf: true);
        h.Model.DropOnPanel(0, upperHalf: true); // own card: nothing
        h.Model.Profile.Panels[0].Slots.Select(s => s.Name).Should().Equal("Beam", "Column", "Brace");
        h.Model.Panels.SelectMany(p => p.Slots).Should().OnlyContain(s => !s.IsDragSource && s.DropMarker == DropMarker.None);
        h.Model.IsDirty.Should().BeFalse();

        h.Model.BeginDrag(BuilderDrag.FromPanel(1));
        h.Model.DragOverPanel(0, upperHalf: true);
        h.Model.EndDrag(); // released outside a target
        h.Model.Panels.Should().OnlyContain(p => p.DropMarker == DropMarker.None);
        h.Model.Panels.Select(p => p.Name).Should().Equal("Frame", "Review", "Links");
    }

    [Fact]
    public void The_drag_sources_map_to_their_payloads()
    {
        var h = Structure();
        ProfileBuilderWindow.DragFor(h.Command("ID_OBJECTS_WALL")).Should().Be(BuilderDrag.FromCatalog(h.Command("ID_OBJECTS_WALL").Command!));
        ProfileBuilderWindow.DragFor(h.Model.CatalogRoots[0]).Should().BeNull("groups are not draggable");
        ProfileBuilderWindow.DragFor(h.Model.Panels[1]).Should().Be(BuilderDrag.FromPanel(1));
        ProfileBuilderWindow.DragFor(h.Model.Panels[1].Slots[2]).Should().Be(BuilderDrag.FromSlot(1, 2));
    }

    // ── Loader defaults (the RST Presets, inline) ─────────────────────────────

    [Fact]
    public void Loader_defaults_lists_the_hideable_tabs_and_a_switch_rewrites_the_presets()
    {
        var h = Structure(); // presets: Massing & Site, Collaborate hidden; disable on; tab "Studio Struct"
        h.Model.PresetTabs.Select(t => t.Tab).Should().Equal("Architecture", "Structure", "Systems", "Insert", "Analyze", "Massing & Site",
            "Collaborate", "Enscape", "Kinship Tools", "Studio Arch");
        h.Model.PresetTabs.Where(t => t.IsHidden).Select(t => t.Tab).Should().Equal("Massing & Site", "Collaborate");
        h.Model.DisableUnused.Should().BeTrue();

        h.Model.PresetTabs.Single(t => t.Tab == "Enscape").IsHidden = true;
        h.Model.PresetTabs.Single(t => t.Tab == "Collaborate").IsHidden = false;
        h.Model.DisableUnused = false;
        h.Model.Profile.Presets!.HiddenTabs.Should().Equal("Massing & Site", "Enscape");
        h.Model.Profile.Presets.DisableUnusedAddins.Should().BeFalse();
        h.Model.IsDirty.Should().BeTrue("a Loader default is an unsaved change");

        h.Model.PresetTabs.Single(t => t.Tab == "Enscape").IsHidden = false;
        h.Model.PresetTabs.Single(t => t.Tab == "Collaborate").IsHidden = true;
        h.Model.DisableUnused = true;
        h.Model.IsDirty.Should().BeFalse("back as it was saved");
    }

    [Fact]
    public void Loader_defaults_keeps_preset_tabs_off_the_ribbon_and_never_offers_the_profile_s_own_tab()
    {
        var h = Structure();
        h.Model.Profile.Presets!.HiddenTabs.Add("Lumion");
        h.Model.ProfileTab = "Enscape"; // rebuilds the list
        h.Model.PresetTabs.Select(t => t.Tab).Should().NotContain("Enscape").And.EndWith("Lumion");
        h.Model.PresetTabs.Single(t => t.Tab == "Lumion").IsHidden.Should().BeTrue();
        h.Model.ProfileTab = "Studio Struct";
        h.Model.PresetTabs.Select(t => t.Tab).Should().Contain("Enscape");
    }

    [Fact]
    public void Loader_defaults_with_no_tabs_to_hide()
    {
        var backend = BuilderFixtures.Backend();
        backend.AllTabs = ["Modify", "View"];
        var h = BuilderHarness.Create(backend);
        h.Model.PresetTabs.Should().BeEmpty();
        h.Model.HasPresetTabs.Should().BeFalse();
    }

    // ── Junk cleanup (inline) ────────────────────────────────────────────────

    [Fact]
    public void Junk_cleanup_edits_the_targets_in_place()
    {
        var h = Structure(); // a legacy profile: the defaults are seeded
        h.Model.CleanupRows.Should().HaveCount(6);
        h.Model.CleanupRows[0].Enabled = false;
        h.Model.CleanupRows[1].Name = "Package Cache";
        h.Model.CleanupRows[5].RemoveCommand.Execute(null);
        var targets = h.Model.Profile.CleanupTargets!;
        targets.Select(t => t.Name).Should().Equal("Temp", "Package Cache", "Journals", "Collaboration Cache", "Recent File List");
        targets[0].Enabled.Should().BeFalse();
        targets.Should().OnlyContain(t => CleanupDefaults.IsPresetPath(t.Path), "paths are never edited");
        h.Model.CleanupRows.Should().HaveCount(5);
        h.Model.CleanupRows[1].PathTip.Should().StartWith(targets[1].Path!);
        h.Model.IsDirty.Should().BeTrue();
    }

    [Fact]
    public void Junk_cleanup_restore_defaults_and_the_empty_state()
    {
        var h = Structure();
        foreach (var row in h.Model.CleanupRows.ToList()) row.RemoveCommand.Execute(null);
        h.Model.HasCleanupTargets.Should().BeFalse();
        h.Model.Profile.CleanupTargets.Should().BeEmpty("the admin removed every target");
        h.Model.RestoreCleanupDefaultsCommand.Execute(null);
        h.Model.CleanupRows.Should().HaveCount(6);
        h.Model.HasCleanupTargets.Should().BeTrue();
        h.Model.Profile.CleanupTargets.Should().BeEquivalentTo(CleanupDefaults.Build());
        h.Model.IsDirty.Should().BeFalse("the defaults are what the legacy profile was seeded with");
    }

    [Fact]
    public void Junk_cleanup_shows_an_emptied_list_as_empty()
    {
        var backend = BuilderFixtures.Backend();
        var structure = backend.Library.Single(e => e.Profile.ProfileName == "Structure").Profile;
        structure.CleanupTargets = [];
        var h = BuilderHarness.Create(backend);
        h.Model.SelectedProfileOption = h.Option("Structure");
        h.Model.CleanupRows.Should().BeEmpty("the inline list shows what the profile holds; Restore defaults brings them back");
        h.Model.IsDirty.Should().BeFalse();
    }
}
