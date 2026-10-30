// rst-c's own (not vendored): the Profile Builder view-model's flows (spec #16, WU4, AC5) with a
// fake backend, a manual clock and scripted dialogs: boot, the profile menu (new / existing, the
// loaded mark, the unsaved-changes prompt on a switch), branding (pick, clear, URL), the catalogue
// (count, filter, the native group, expansion, empty states, double-click), panels and slots (add,
// select, rename, move, delete with its confirm), General (Min Revit, colour strength, the tab),
// the panel colour picker, Save (validation, a rename under the same id, collision, required
// add-ins, the auto-reload), Export ZIP, Exit and the title-bar ✕. Every toast text is checked
// against profile_builder.html's. Feature #18's Rename, Duplicate and Profile pane tabs are in
// ProfileBuilderShellTests.

using FluentAssertions;
using RST.Core.Builder;
using RST.Core.Colors;
using RST.Core.Loader;
using RST.Core.Profiles;
using RST.RuntimeTests.Loader;
using RST.UI.Builder;
using RST.UI.Controls;
using RST.UI.Dialogs;
using Xunit;

namespace RST.RuntimeTests.Builder;

public class ProfileBuilderViewModelTests
{
    private static void AssertToast(BuilderHarness h, string text, BannerKind kind)
    {
        h.Model.Toast.IsOpen.Should().BeTrue();
        h.Model.Toast.Text.Should().Be(text);
        h.Model.Toast.Kind.Should().Be(kind);
        h.Clock.PendingDelays.Should().Contain(TransientMessage.ToastDuration, "the Builder's toast shows for 2.4 s");
    }

    // ── boot ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Boot_reads_branding_first_then_the_data_and_starts_a_fresh_profile_with_one_panel()
    {
        var h = BuilderHarness.Create();
        h.Backend.Calls.Take(4).Should().Equal("LoadBranding", "GetProfiles", "GetActiveProfile", "GetAddinLookup");
        h.Model.ProfileOptions.Select(o => o.Display).Should().Equal("Architecture", "Structure");
        h.Model.ProfileOptions.Select(o => o.IsLoaded).Should().Equal(true, false);
        h.Model.ProfileOptions.Should().OnlyContain(o => !o.IsCurrent, "a new profile is in the editor");
        h.Model.SelectedProfileOption!.IsNew.Should().BeTrue();
        h.Model.DisplayName.Should().Be("New profile");
        h.Model.IsLoadedProfile.Should().BeFalse();
        h.Model.IsEditMode.Should().BeFalse();
        h.Model.Panels.Should().ContainSingle().Which.Name.Should().Be("Panel 1");
        h.Model.SelectedPanel.Should().BeSameAs(h.Model.Panels[0]);
        h.Model.Panels[0].IsSelected.Should().BeTrue();
        h.Model.ProfileName.Should().Be("");
        h.Model.MinVersion.Should().Be("0.1.0");
        h.Model.PanelOpacity.Should().Be(100);
        h.Model.PanelOpacityText.Should().Be("100%");
        h.Model.DisableUnused.Should().BeFalse();
        h.Model.PresetTabs.Should().OnlyContain(t => !t.IsHidden);
        h.Model.CleanupRows.Should().HaveCount(6).And.OnlyContain(r => r.Enabled);
        h.Model.IsDirty.Should().BeFalse("the auto-added panel is part of the clean baseline");
        h.Model.SaveCommand.CanExecute(null).Should().BeFalse("Save is enabled only when there are changes");
        h.Model.CatalogCount.Should().Be(15);
    }

    [Fact]
    public void A_throwing_backend_still_opens_a_fresh_editor()
    {
        var backend = new ThrowingBackend();
        var h = BuilderHarness.Create(backend);
        h.Model.Panels.Should().ContainSingle();
        h.Model.CatalogIsEmpty.Should().BeTrue();
        h.Model.ProfileOptions.Should().BeEmpty();
    }

    // ── the profile menu ─────────────────────────────────────────────────────

    [Fact]
    public void Picking_a_profile_loads_it_for_editing_with_panel_0_selected()
    {
        var h = BuilderHarness.Create();
        h.Model.SelectedProfileOption = h.Option("Structure");
        h.Model.IsEditMode.Should().BeTrue();
        h.Model.ProfileName.Should().Be("Structure");
        h.Model.ProfileTab.Should().Be("Studio Struct");
        h.Model.MinVersion.Should().Be("2.1.0");
        h.Model.PanelOpacity.Should().Be(60);
        h.Model.Panels.Select(p => p.Name).Should().Equal("Frame", "Review", "Links");
        h.Model.SelectedPanel!.Name.Should().Be("Frame");
        h.Model.PresetTabs.Where(t => t.IsHidden).Select(t => t.Tab).Should().Equal("Massing & Site", "Collaborate");
        h.Model.DisableUnused.Should().BeTrue();
        h.Model.EditingText.Should().Be("· editing Structure");
        h.Option("Structure").IsCurrent.Should().BeTrue();
        h.Option("Structure").Note.Should().Be("tab \"Studio Struct\"");
        h.Option("Architecture").Note.Should().Be("loaded · tab \"Studio Arch\"");
        h.Model.IsLoadedProfile.Should().BeFalse("Architecture is the loaded one");
        h.Model.Profile.Branding.Should().BeNull();
        h.Model.IsDirty.Should().BeFalse();
        h.Model.PreviewProfile.Should().BeSameAs(h.Model.Profile);

        // The review row: a stack slot shows its type tag; a tool shows its icon and short id.
        var review = h.Model.Panels[1];
        review.SlotCount.Should().Be("3 slots");
        review.Slots[1].IsTool.Should().BeFalse();
        review.Slots[1].TypeTag.Should().Be("stack");
        review.Slots[0].IconLabel.Should().Be("box_open");
        review.Slots[0].ShortCommandId.Should().Be("ID_KINSHIP");
        review.Slots[2].IconLabel.Should().Be("Icon", "custom.png is not a pack icon");

        h.Model.NewProfileCommand.Execute(null);
        h.Model.IsEditMode.Should().BeFalse();
        h.Model.Panels.Should().ContainSingle().Which.Name.Should().Be("Panel 1");
        h.Model.ProfileOptions.Should().OnlyContain(o => !o.IsCurrent);

        h.Model.SwitchProfileCommand.Execute(h.Option("Architecture"));
        h.Model.ProfileName.Should().Be("Architecture");
        h.Model.IsLoadedProfile.Should().BeTrue("the editor holds the loaded profile");
    }

    [Fact]
    public void Switching_with_unsaved_edits_asks_and_Cancel_keeps_everything()
    {
        var h = BuilderHarness.Create();
        h.Model.ProfileName = "Draft";
        h.Dialogs.ChooseAnswers.Enqueue(DialogChoice.Cancel);
        h.Model.SelectedProfileOption = h.Option("Structure");
        var ask = h.Dialogs.ChooseCalls.Single();
        ask.Title.Should().Be("Unsaved changes");
        ask.Message.Should().Be("You have unsaved changes to this profile. What would you like to do?");
        ask.PrimaryText.Should().Be("Save & Switch");
        ask.SecondaryText.Should().Be("Discard & Switch");
        h.Model.SelectedProfileOption!.IsNew.Should().BeTrue("the editor stays on the new profile");
        h.Model.ProfileName.Should().Be("Draft");
    }

    [Fact]
    public void Switching_with_unsaved_edits_and_Discard_switches()
    {
        var h = BuilderHarness.Create();
        h.Model.ProfileName = "Draft";
        h.Dialogs.ChooseAnswers.Enqueue(DialogChoice.Secondary);
        h.Model.SelectedProfileOption = h.Option("Structure");
        h.Model.ProfileName.Should().Be("Structure");
        h.Backend.Saved.Should().BeEmpty();
    }

    [Fact]
    public void Switching_with_unsaved_edits_and_Save_saves_then_switches()
    {
        var h = BuilderHarness.Create();
        h.Model.ProfileName = "Draft";
        h.Model.ProfileTab = "Draft Tab";
        h.Dialogs.ChooseAnswers.Enqueue(DialogChoice.Primary);
        h.Model.SelectedProfileOption = h.Option("Structure");
        h.Backend.Saved.Should().ContainSingle().Which.ProfileName.Should().Be("Draft");
        h.Model.ProfileName.Should().Be("Structure");
        h.Model.ProfileOptions.Select(o => o.Display).Should().Contain("Draft");
    }

    [Fact]
    public void Switching_with_unsaved_edits_and_a_failed_save_stays()
    {
        var h = BuilderHarness.Create();
        h.Model.ProfileName = "Draft"; // no tab: the save is refused
        h.Dialogs.ChooseAnswers.Enqueue(DialogChoice.Primary);
        h.Model.SelectedProfileOption = h.Option("Structure");
        AssertToast(h, "Tab name required.", BannerKind.Error);
        h.Model.ProfileName.Should().Be("Draft");
        h.Model.SelectedProfileOption!.IsNew.Should().BeTrue();
    }

    [Fact]
    public void A_clean_switch_does_not_ask()
    {
        var h = BuilderHarness.Create();
        h.Model.SelectedProfileOption = h.Option("Architecture");
        h.Model.SelectedProfileOption = h.Option("Structure");
        h.Dialogs.ChooseCalls.Should().BeEmpty();
    }

    [Fact]
    public void Editing_a_loaded_profile_never_changes_the_menu_s_copy()
    {
        var h = BuilderHarness.Create();
        h.Model.SelectedProfileOption = h.Option("Structure");
        h.Model.Panels[0].Name = "Edited";
        h.Option("Structure").Entry!.Profile.Panels[0].Name.Should().Be("Frame");
    }

    // ── branding ─────────────────────────────────────────────────────────────

    [Fact]
    public void Branding_shows_the_machine_default()
    {
        var backend = BuilderFixtures.Backend();
        backend.Branding = new BrandingState(true, "branding.png", "https://studio.example");
        var h = BuilderHarness.Create(backend);
        h.Model.HasLogo.Should().BeTrue();
        h.Model.LogoName.Should().Be("branding.png");
        h.Model.BrandingUrl.Should().Be("https://studio.example");

        var none = BuilderHarness.Create();
        none.Model.HasLogo.Should().BeFalse();
        none.Model.LogoName.Should().Be("(none)");
        none.Model.BrandingUrl.Should().Be("");
    }

    [Fact]
    public void The_settings_menu_s_Branding_opens_the_branding_dialog()
    {
        var h = BuilderHarness.Create();
        h.Model.BrandingCommand.Execute(null);
        h.Modals.Brandings.Should().ContainSingle().Which.Should().BeSameAs(h.Model);
    }

    [Fact]
    public void The_URL_is_normalized_and_saved_on_change_only()
    {
        var h = BuilderHarness.Create();
        h.Model.BrandingUrl = "  studio.example ";
        h.Backend.BrandingUrls.Should().Equal("https://studio.example");
        h.Model.BrandingUrl.Should().Be("https://studio.example");
        h.Model.BrandingUrl = "https://studio.example";
        h.Backend.BrandingUrls.Should().ContainSingle("an unchanged value is not saved again");
        h.Model.BrandingUrl = "";
        h.Backend.BrandingUrls.Should().Equal("https://studio.example", "");
        h.Model.BrandingUrl.Should().Be("");
    }

    [Fact]
    public void A_failed_URL_save_toasts_and_keeps_the_old_value()
    {
        var h = BuilderHarness.Create();
        h.Backend.UrlResult = OpResult.Fail("Write failed: denied");
        h.Model.BrandingUrl = "x.org";
        AssertToast(h, "Save URL failed: Write failed: denied", BannerKind.Error);
        h.Model.BrandingUrl.Should().Be("");
    }

    [Fact]
    public void Pick_logo_opens_the_image_dialog_stores_the_file_and_cancel_is_silent()
    {
        var h = BuilderHarness.Create();
        h.Model.PickLogoCommand.Execute(null);
        h.Dialogs.OpenCalls.Should().Be(1);
        h.Backend.Logos.Should().BeEmpty();
        h.Model.Toast.IsOpen.Should().BeFalse();

        h.Dialogs.OpenResult = @"C:\logo.png";
        h.Model.PickLogoCommand.Execute(null);
        h.Backend.Logos.Should().Equal(@"C:\logo.png");
        h.Model.HasLogo.Should().BeTrue();
        h.Model.LogoName.Should().Be("branding.png");

        h.Backend.LogoResult = LogoOutcome.Fail("Copy failed: busy");
        h.Model.PickLogoCommand.Execute(null);
        AssertToast(h, "Logo pick failed: Copy failed: busy", BannerKind.Error);
    }

    [Fact]
    public void Clear_logo_clears_it_or_toasts()
    {
        var backend = BuilderFixtures.Backend();
        backend.Branding = new BrandingState(true, "branding.png", null);
        var h = BuilderHarness.Create(backend);
        h.Model.ClearLogoCommand.Execute(null);
        h.Model.HasLogo.Should().BeFalse();
        h.Model.LogoName.Should().Be("(none)");
        h.Backend.ClearResult = OpResult.Fail("Delete failed: in use");
        h.Model.ClearLogoCommand.Execute(null);
        AssertToast(h, "Clear logo failed: Delete failed: in use", BannerKind.Error);
    }

    [Fact]
    public void Branding_never_enters_the_profile()
    {
        var h = BuilderHarness.Create();
        h.Model.BrandingUrl = "studio.example";
        h.Model.IsDirty.Should().BeFalse();
        h.Model.Profile.Branding.Should().BeNull();
    }

    // ── the catalogue ────────────────────────────────────────────────────────

    [Fact]
    public void The_tree_shows_the_native_group_collapsed_then_the_vendors_sorted()
    {
        var h = BuilderHarness.Create();
        h.Model.CatalogRoots.Select(r => r.Label).Should().Equal("Autodesk Native", "Enscape", "Kinship", "pyRevit");
        var native = h.Model.CatalogRoots[0];
        native.IsExpanded.Should().BeFalse("collapsed by default");
        native.Count.Should().Be(8);
        native.Children.Select(c => c.Label).Should().Equal("Architecture", "Structure", "View");
        var enscape = h.Model.CatalogRoots[1];
        enscape.IsExpanded.Should().BeTrue();
        enscape.Count.Should().Be(3);
        enscape.Children.Select(c => c.Label + ":" + c.Count).Should().Equal("Assets:1", "Enscape:2");
        var kinship = h.Command("CustomCtrl_%Add-Ins%Kinship%KinshipUploadThis2Library");
        kinship.Label.Should().Be("Upload to Library");
        kinship.ShortId.Should().Be("CustomCt…2Library");
        kinship.ToolTip.Should().Be("CustomCtrl_%Add-Ins%Kinship%KinshipUploadThis2Library");
    }

    [Fact]
    public void Filtering_counts_matches_opens_the_native_group_and_keeps_expansion()
    {
        var h = BuilderHarness.Create();
        var enscape = h.Model.CatalogRoots[1];
        enscape.Children[1].IsExpanded = false; // collapse the Enscape panel

        h.Model.FilterText = "LI";
        h.Model.CatalogRoots.Select(r => r.Label + ":" + r.Count).Should().Equal("Enscape:2", "Kinship:2");
        h.Model.AllCatalogRoots[0].IsExpanded.Should().BeTrue("the native group opens while filtering (even with no match)");
        h.Model.HasNoMatches.Should().BeFalse();

        h.Model.FilterText = "wall";
        var native = h.Model.CatalogRoots.Single();
        native.Label.Should().Be("Autodesk Native");
        native.IsExpanded.Should().BeTrue();
        native.Count.Should().Be(1);

        h.Model.FilterText = "";
        h.Model.CatalogRoots.Should().HaveCount(4);
        h.Model.CatalogRoots[0].IsExpanded.Should().BeFalse("collapsed again once the filter is cleared");
        enscape.Children[1].IsExpanded.Should().BeFalse("per-node expansion is kept across filters");
        enscape.Children.Should().HaveCount(2, "the same node objects come back: the tree is not rebuilt");
    }

    [Fact]
    public void Clicking_the_native_group_toggles_it()
    {
        var h = BuilderHarness.Create();
        var native = h.Model.CatalogRoots[0];
        native.ToggleCommand.Execute(null);
        native.IsExpanded.Should().BeTrue();
        native.ToggleCommand.Execute(null);
        native.IsExpanded.Should().BeFalse();
        h.Model.CatalogRoots[1].ToggleCommand.Execute(null);
        h.Model.CatalogRoots[1].IsExpanded.Should().BeFalse();
    }

    [Fact]
    public void No_match_and_no_catalogue_show_their_empty_states()
    {
        var h = BuilderHarness.Create();
        h.Model.FilterText = "  ZZZ ";
        h.Model.HasNoMatches.Should().BeTrue();
        h.Model.NoMatchesText.Should().Be("No matches for \"zzz\".");
        h.Model.CatalogRoots.Should().BeEmpty();

        var empty = BuilderHarness.Create(new FakeBuilderBackend());
        empty.Model.CatalogIsEmpty.Should().BeTrue();
        empty.Model.HasNoMatches.Should().BeFalse();
        empty.Model.CatalogCount.Should().Be(0);
        empty.Model.FilterText = "x";
        empty.Model.HasNoMatches.Should().BeFalse("the empty catalogue's message wins");
    }

    [Fact]
    public void Double_clicking_a_command_adds_it_to_the_selected_panel()
    {
        var h = BuilderHarness.Create();
        var wall = h.Command("ID_OBJECTS_WALL");
        h.Model.AddCatalogCommand.CanExecute(wall).Should().BeTrue();
        h.Model.AddCatalogCommand.CanExecute(h.Model.CatalogRoots[0]).Should().BeFalse("groups are not added");
        h.Model.AddCatalogCommand.Execute(wall);
        var slot = h.Model.Profile.Panels[0].Slots.Single();
        slot.Should().BeEquivalentTo(new Slot { SlotType = "tool", Name = "Wall", CommandId = "ID_OBJECTS_WALL", SourceTab = "Architecture", SourcePanel = "Build" });
        h.Model.Panels[0].SlotCount.Should().Be("1 slot");
        h.Model.IsDirty.Should().BeTrue();
    }

    [Fact]
    public void Adding_with_no_panel_selected_adds_a_panel_first()
    {
        var h = BuilderHarness.Create();
        h.Dialogs.ConfirmResult = true;
        h.Model.Panels[0].DeleteCommand.Execute(null);
        h.Model.Panels.Should().BeEmpty();
        h.Model.HasPanels.Should().BeFalse();
        h.Model.AddToSelectedPanel(h.Command("Enscape.Start").Command!);
        h.Model.Panels.Should().ContainSingle().Which.Name.Should().Be("Panel 1");
        h.Model.Profile.Panels[0].Slots.Single().SourcePanel.Should().Be("Enscape");
    }

    // ── panels and slots ─────────────────────────────────────────────────────

    [Fact]
    public void Add_panel_appends_Panel_N_in_blue_and_selects_it()
    {
        var h = BuilderHarness.Create();
        h.Model.AddPanelCommand.Execute(null);
        h.Model.Panels.Select(p => p.Name).Should().Equal("Panel 1", "Panel 2");
        h.Model.Profile.Panels[1].Color.Should().Be("#4f8ef7");
        h.Model.SelectedPanelIndex.Should().Be(1);
        h.Model.Panels[1].IsSelected.Should().BeTrue();
        h.Model.Panels[0].IsSelected.Should().BeFalse();
        h.Model.Panels[0].SelectCommand.Execute(null);
        h.Model.SelectedPanel!.Name.Should().Be("Panel 1");
    }

    [Fact]
    public void Panel_names_are_shared_by_the_card_and_the_properties_pane_and_redraw_the_preview()
    {
        var h = BuilderHarness.Create();
        var before = h.Model.PreviewRevision;
        h.Model.SelectedPanel!.Name = "Modelling";
        h.Model.Profile.Panels[0].Name.Should().Be("Modelling");
        h.Model.Panels[0].Name.Should().Be("Modelling");
        h.Model.PreviewRevision.Should().BeGreaterThan(before);
    }

    [Fact]
    public void Moving_panels_swaps_them_and_follows_the_selection()
    {
        var h = BuilderHarness.Create();
        h.Model.SelectedProfileOption = h.Option("Structure");
        h.Model.Panels[0].MoveUpCommand.Execute(null); // first: no-op
        h.Model.Panels.Select(p => p.Name).Should().Equal("Frame", "Review", "Links");
        h.Model.Panels[0].MoveDownCommand.Execute(null);
        h.Model.Panels.Select(p => p.Name).Should().Equal("Review", "Frame", "Links");
        h.Model.SelectedPanel!.Name.Should().Be("Frame", "the selection moves with the panel");
        h.Model.Panels[0].MoveDownCommand.Execute(null);
        h.Model.SelectedPanel!.Name.Should().Be("Frame", "the other panel of a swap keeps the selection on Frame");
        h.Model.Panels.Select(p => p.Name).Should().Equal("Frame", "Review", "Links");
        h.Model.Panels[2].MoveDownCommand.Execute(null); // last: no-op
        h.Model.Panels.Select(p => p.Name).Should().Equal("Frame", "Review", "Links");
    }

    [Fact]
    public void Deleting_a_panel_asks_the_destructive_confirm()
    {
        var h = BuilderHarness.Create();
        h.Model.SelectedProfileOption = h.Option("Structure");
        h.Model.Panels[2].SelectCommand.Execute(null);
        h.Dialogs.ConfirmResult = false;
        h.Model.Panels[2].DeleteCommand.Execute(null);
        var ask = h.Dialogs.ConfirmCalls.Single();
        ask.Title.Should().Be("Delete panel \"Links\"?");
        ask.ConfirmText.Should().Be("Delete");
        ask.Danger.Should().BeTrue();
        ask.AutomationId.Should().Be(ProfileBuilderViewModel.DeletePanelDialogId);
        h.Model.Panels.Should().HaveCount(3);

        h.Dialogs.ConfirmResult = true;
        h.Model.Panels[2].DeleteCommand.Execute(null);
        h.Model.Panels.Select(p => p.Name).Should().Equal("Frame", "Review");
        h.Model.SelectedPanel!.Name.Should().Be("Review", "the selection falls back to the last panel");
    }

    [Fact]
    public void Slots_move_rename_and_remove_without_a_confirm()
    {
        var h = BuilderHarness.Create();
        h.Model.SelectedProfileOption = h.Option("Structure");
        var frame = h.Model.Panels[0];
        frame.Slots[0].MoveDownCommand.Execute(null);
        h.Model.Profile.Panels[0].Slots.Select(s => s.Name).Should().Equal("Column", "Beam", "Brace");
        h.Model.Panels[0].Slots[0].MoveUpCommand.Execute(null); // first: no-op
        h.Model.Panels[0].Slots[2].MoveDownCommand.Execute(null); // last: no-op
        h.Model.Profile.Panels[0].Slots.Select(s => s.Name).Should().Equal("Column", "Beam", "Brace");
        h.Model.Panels[0].Slots[1].Name = "Girder";
        h.Model.Profile.Panels[0].Slots[1].Name.Should().Be("Girder");
        h.Model.Panels[0].Slots[2].RemoveCommand.Execute(null);
        h.Model.Profile.Panels[0].Slots.Select(s => s.Name).Should().Equal("Column", "Girder");
        h.Dialogs.ConfirmCalls.Should().BeEmpty();
        h.Model.Panels[0].SlotCount.Should().Be("2 slots");
    }

    // ── General, and the panel colour picker ──────────────────────────────────

    [Fact]
    public void Min_revit_and_opacity_edit_the_profile_and_opacity_redraws_the_preview()
    {
        var h = BuilderHarness.Create();
        h.Model.MinVersion = "2.3.0";
        h.Model.Profile.MinVersion.Should().Be("2.3.0");
        var before = h.Model.PreviewRevision;
        h.Model.PanelOpacity = 55;
        h.Model.Profile.PanelOpacity.Should().Be(55);
        h.Model.PreviewRevision.Should().BeGreaterThan(before);
        h.Model.PanelOpacity = 5;
        h.Model.PanelOpacity.Should().Be(10);
    }

    [Fact]
    public void The_tab_name_redraws_the_preview_pill_as_you_type()
    {
        var h = BuilderHarness.Create();
        var before = h.Model.PreviewRevision;
        h.Model.ProfileTab = "Studio";
        h.Model.Profile.Tab.Should().Be("Studio");
        h.Model.PreviewRevision.Should().BeGreaterThan(before);
    }

    [Fact]
    public void Colour_strength_shows_a_percentage_in_steps_and_bounds()
    {
        var h = BuilderHarness.Create();
        h.Model.PanelOpacity = 60;
        h.Model.PanelOpacityText.Should().Be("60%");
        h.Model.PanelOpacity = 5;
        h.Model.PanelOpacity.Should().Be(10);
        h.Model.PanelOpacity = 120;
        h.Model.PanelOpacity.Should().Be(100);
        h.Model.IsDirty.Should().BeFalse("100 is where it started");
        h.Model.MinVersion = "3.1.0";
        h.Model.IsDirty.Should().BeTrue();
        h.Model.SaveCommand.CanExecute(null).Should().BeTrue("Save is enabled by changes; its checks refuse a nameless profile");
    }

    [Fact]
    public void The_color_picker_opens_the_family_picker_and_writes_lower_case_hex()
    {
        var h = BuilderHarness.Create();
        h.Dialogs.PickResults.Enqueue(Rgb.Parse("#10B981"));
        h.Model.PickColorCommand.Execute(null);
        var call = h.Dialogs.PickCalls.Single();
        call.Current.Should().Be(Rgb.Parse("#4f8ef7"));
        call.Title.Should().Be("Panel color");
        call.Palette.Should().Equal(ProfileBuilderViewModel.PanelPalette);
        h.Model.Profile.Panels[0].Color.Should().Be("#10b981");
        h.Model.SelectedPanel!.ColorHex.Should().Be("#10b981");

        h.Model.PickColorCommand.Execute(null); // cancelled
        h.Model.Profile.Panels[0].Color.Should().Be("#10b981");
    }

    [Fact]
    public void The_hex_box_applies_a_valid_color_and_snaps_back_otherwise()
    {
        var h = BuilderHarness.Create();
        var panel = h.Model.SelectedPanel!;
        panel.ColorHex = "#ABC";
        h.Model.Profile.Panels[0].Color.Should().Be("#aabbcc");
        panel.ColorHex = "not a color";
        h.Model.Profile.Panels[0].Color.Should().Be("#aabbcc");
        panel.ColorHex.Should().Be("#aabbcc");

        h.Model.Profile.Panels[0].Color = "#FFF";
        panel.ColorHex.Should().Be("#ffffff", "the picker starts from normalizeHex(panel.color)");
        h.Model.Profile.Panels[0].Color = "transparent";
        panel.ColorHex.Should().Be("#4f8ef7", "… or the default blue");
    }

    [Fact]
    public void With_no_panel_selected_the_colour_picker_is_off()
    {
        var h = BuilderHarness.Create();
        h.Dialogs.ConfirmResult = true;
        h.Model.Panels[0].DeleteCommand.Execute(null);
        h.Model.SelectedPanel.Should().BeNull();
        h.Model.PickColorCommand.CanExecute(null).Should().BeFalse();
    }

    // ── save ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Save_requires_a_name_then_a_tab()
    {
        var h = BuilderHarness.Create();
        (await h.Model.SaveAsync()).Should().BeFalse();
        AssertToast(h, "Profile name required.", BannerKind.Error);
        h.Model.ProfileName = "New";
        (await h.Model.SaveAsync()).Should().BeFalse();
        AssertToast(h, "Tab name required.", BannerKind.Error);
        h.Backend.Saved.Should().BeEmpty();
    }

    [Fact]
    public async Task Saving_a_new_profile_assigns_it_refreshes_the_menu_and_resets_the_baseline()
    {
        var h = BuilderHarness.Create();
        h.Model.ProfileName = "Studio MEP";
        h.Model.ProfileTab = "MEP";
        h.Model.AddToSelectedPanel(h.Command("Enscape.Start").Command!);
        h.Model.AddToSelectedPanel(h.Command("ID_OBJECTS_WALL").Command!);
        (await h.Model.SaveAsync()).Should().BeTrue();

        var saved = h.Backend.Saved.Single();
        saved.Id.Should().Be("new-1");
        saved.RequiredAddins.Select(r => (r.TabName, r.AddinFile, r.Url)).Should().Equal(("Enscape", "Enscape.addin", "https://enscape3d.com"));
        h.Model.Profile.Id.Should().Be("new-1");
        h.Model.Profile.ExportDate.Should().Be("2026-10-30");
        h.Model.IsEditMode.Should().BeTrue();
        h.Model.IsDirty.Should().BeFalse();
        h.Model.SelectedProfileOption!.Key.Should().Be("new-1");
        h.Model.ProfileOptions.Single(o => o.IsCurrent).Display.Should().Be("Studio MEP");
        h.Model.SaveCommand.CanExecute(null).Should().BeFalse("nothing left to save");
        AssertToast(h, "Saved as Studio_MEP_new-1.json", BannerKind.Success);
        h.Backend.Loads.Should().BeEmpty("a profile that is not active is not reloaded");
    }

    [Fact]
    public async Task Saving_an_edit_keeps_its_id_and_resaves_in_place()
    {
        var h = BuilderHarness.Create();
        h.Model.SelectedProfileOption = h.Option("Structure");
        h.Model.ProfileTab = "Renamed tab";
        (await h.Model.SaveAsync()).Should().BeTrue();
        h.Backend.Saved.Single().Id.Should().Be(BuilderFixtures.StructId);
        h.Backend.Saved.Single().ExportDate.Should().Be("2026-08-02");
        AssertToast(h, "Saved as Structure_" + BuilderFixtures.StructId + ".json", BannerKind.Success);
    }

    [Fact]
    public async Task Renaming_in_edit_mode_saves_under_the_same_id()
    {
        // Spec #19: rename-as-copy is retired; a changed name is a rename of the same profile.
        var h = BuilderHarness.Create();
        h.Model.SelectedProfileOption = h.Option("Structure");
        h.Model.ProfileName = "Structure v2";
        (await h.Model.SaveAsync()).Should().BeTrue();
        var renamed = h.Backend.Saved.Single();
        renamed.Id.Should().Be(BuilderFixtures.StructId);
        renamed.ExportDate.Should().Be("2026-08-02", "the export date is kept");
        h.Backend.Library.Select(e => e.Profile.ProfileName).Should().Contain("Structure v2").And.NotContain("Structure");
        AssertToast(h, "Saved as Structure_v2_" + BuilderFixtures.StructId + ".json", BannerKind.Success);

        // The saved name is the new baseline: saving again stays on the same id.
        h.Model.MinVersion = "3.0.0";
        (await h.Model.SaveAsync()).Should().BeTrue();
        h.Backend.Saved[1].Id.Should().Be(BuilderFixtures.StructId);
    }

    [Fact]
    public async Task Renaming_onto_another_profile_aborts()
    {
        var h = BuilderHarness.Create();
        h.Model.SelectedProfileOption = h.Option("Structure");
        h.Model.ProfileName = " architecture ";
        (await h.Model.SaveAsync()).Should().BeFalse();
        AssertToast(h, "A profile named \" architecture \" already exists. Choose a different name.", BannerKind.Error);
        h.Backend.Saved.Should().BeEmpty();
        h.Model.Profile.Id.Should().Be(BuilderFixtures.StructId);
    }

    [Fact]
    public async Task A_new_profile_named_like_a_saved_one_is_refused_on_its_first_save()
    {
        // WU1 review F5: Rename… refuses a taken name; the first save of a new profile checks again
        // (here the name was set without the dialog, as a name taken since would be).
        var h = BuilderHarness.Create();
        h.Model.ProfileName = "STRUCTURE ";
        h.Model.ProfileTab = "T";
        (await h.Model.SaveAsync()).Should().BeFalse();
        AssertToast(h, "A profile named \"STRUCTURE \" already exists. Choose a different name.", BannerKind.Error);
        h.Backend.Saved.Should().BeEmpty();
        h.Model.IsEditMode.Should().BeFalse();
    }

    [Fact]
    public async Task A_failed_save_toasts_the_error()
    {
        var h = BuilderHarness.Create();
        h.Backend.SaveError = "Profile validation failed: profile.panels[0].name: required";
        h.Model.ProfileName = "A";
        h.Model.ProfileTab = "B";
        (await h.Model.SaveAsync()).Should().BeFalse();
        AssertToast(h, "Save failed: Profile validation failed: profile.panels[0].name: required", BannerKind.Error);
        h.Model.IsDirty.Should().BeTrue();
    }

    [Fact]
    public async Task Saving_the_active_profile_reloads_the_ribbon_with_its_hidden_tabs_and_disable_flag()
    {
        var h = BuilderHarness.Create();
        h.Model.SelectedProfileOption = h.Option("Architecture");
        h.Model.PanelOpacity = 90;
        var pending = new TaskCompletionSource<LoadResult>();
        h.Backend.OnLoad = _ => pending.Task;
        var save = h.Model.SaveAsync();
        h.Model.IsWorking.Should().BeTrue();
        h.Model.IsIdle.Should().BeFalse();
        h.Model.SaveCommand.CanExecute(null).Should().BeFalse("the commands wait for the reload");
        h.Model.CanClose().Should().BeFalse("the window stays open while the reload runs");
        pending.SetResult(new LoadResult(true, [], false, [], QaCheck.Clear));
        (await save).Should().BeTrue();

        h.Backend.Loads.Single().Should().BeEquivalentTo(new LoadRequest("Architecture", BuilderFixtures.ArchId, true, ["Analyze"]));
        AssertToast(h, "Saved & ribbon reloaded.", BannerKind.Success);
        h.Model.IsWorking.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_or_throwing_reload_still_counts_as_saved(bool throws)
    {
        var h = BuilderHarness.Create();
        h.Model.SelectedProfileOption = h.Option("Architecture");
        h.Model.PanelOpacity = 90;
        h.Backend.OnLoad = _ => throws
            ? Task.FromException<LoadResult>(new InvalidOperationException("boom"))
            : Task.FromResult(LoadResult.Fail("Profile not found: Architecture"));
        (await h.Model.SaveAsync()).Should().BeTrue();
        AssertToast(h, "Saved (auto-reload failed).", BannerKind.Error);
    }

    // ── export ───────────────────────────────────────────────────────────────

    [Fact]
    public void Export_requires_a_name_opens_the_save_dialog_and_toasts_the_path()
    {
        var h = BuilderHarness.Create();
        h.Model.ExportCommand.Execute(null);
        AssertToast(h, "Profile name required.", BannerKind.Error);
        h.Dialogs.SaveCalls.Should().Be(0);

        h.Model.SelectedProfileOption = h.Option("Structure");
        h.Model.ExportCommand.Execute(null); // cancelled: silent
        h.Dialogs.SaveCalls.Should().Be(1);
        h.Backend.Exports.Should().BeEmpty();

        h.Dialogs.SaveResult = @"C:\out\Structure.zip";
        h.Model.ExportCommand.Execute(null);
        h.Backend.Exports.Single().Path.Should().Be(@"C:\out\Structure.zip");
        h.Backend.Exports.Single().Profile.ProfileName.Should().Be("Structure");
        AssertToast(h, @"Exported to C:\out\Structure.zip", BannerKind.Success);

        h.Backend.ExportResult = OpResult.Fail("Write failed: denied");
        h.Model.ExportCommand.Execute(null);
        AssertToast(h, "Export failed: Write failed: denied", BannerKind.Error);
        h.Backend.Saved.Should().BeEmpty("exporting does not save");
    }

    // ── exit and the title-bar ✕ ──────────────────────────────────────────────

    [Fact]
    public async Task Exit_closes_at_once_when_clean()
    {
        var h = BuilderHarness.Create();
        var closed = 0;
        h.Model.CloseRequested += () => closed++;
        await h.Model.ExitAsync();
        closed.Should().Be(1);
        h.Dialogs.ChooseCalls.Should().BeEmpty();
        h.Model.CanClose().Should().BeTrue();
    }

    [Theory]
    [InlineData(DialogChoice.Cancel, 0)]
    [InlineData(DialogChoice.Secondary, 1)]
    public async Task Exit_with_unsaved_edits_asks_then_cancels_or_discards(DialogChoice answer, int expectedCloses)
    {
        var h = BuilderHarness.Create();
        var closed = 0;
        h.Model.CloseRequested += () => closed++;
        h.Model.ProfileName = "Draft";
        h.Dialogs.ChooseAnswers.Enqueue(answer);
        await h.Model.ExitAsync();
        var ask = h.Dialogs.ChooseCalls.Single();
        ask.Title.Should().Be("Unsaved changes");
        ask.PrimaryText.Should().Be("Save & Exit");
        ask.SecondaryText.Should().Be("Discard & Exit");
        ask.AutomationId.Should().Be(MessageDialog.DiscardChangesId);
        closed.Should().Be(expectedCloses);
        h.Backend.Saved.Should().BeEmpty();
    }

    [Fact]
    public async Task Save_and_exit_closes_only_when_the_save_succeeds()
    {
        var h = BuilderHarness.Create();
        var closed = 0;
        h.Model.CloseRequested += () => closed++;
        h.Model.ProfileName = "Draft";
        h.Dialogs.ChooseAnswers.Enqueue(DialogChoice.Primary);
        await h.Model.ExitAsync();
        closed.Should().Be(0, "no tab: the save failed and the window stays");
        AssertToast(h, "Tab name required.", BannerKind.Error);

        h.Model.ProfileTab = "Tab";
        h.Dialogs.ChooseAnswers.Enqueue(DialogChoice.Primary);
        await h.Model.ExitAsync();
        closed.Should().Be(1);
        h.Backend.Saved.Should().ContainSingle();
    }

    [Fact]
    public void The_title_bar_close_asks_as_Exit_does()
    {
        var h = BuilderHarness.Create();
        h.Model.ProfileName = "Draft";

        h.Dialogs.ChooseAnswers.Enqueue(DialogChoice.Cancel);
        h.Model.CanClose().Should().BeFalse();

        h.Dialogs.ChooseAnswers.Enqueue(DialogChoice.Secondary);
        h.Model.CanClose().Should().BeTrue();
        h.Model.CanClose().Should().BeTrue("once approved, the close is not asked again");
        h.Dialogs.ChooseCalls.Should().HaveCount(2);
        h.Dialogs.ChooseCalls.Should().OnlyContain(c => c.PrimaryText == "Save & Exit");
    }

    [Fact]
    public void Save_and_exit_from_the_title_bar_saves_after_the_closing_event_then_closes()
    {
        var h = BuilderHarness.Create(deferPosts: true);
        var closed = 0;
        h.Model.CloseRequested += () => closed++;
        h.Model.ProfileName = "Draft";
        h.Model.ProfileTab = "Tab";
        h.Dialogs.ChooseAnswers.Enqueue(DialogChoice.Primary);
        h.Model.CanClose().Should().BeFalse("this close is cancelled; the save closes the window");
        h.Backend.Saved.Should().BeEmpty("nothing runs inside the Closing event (WPF refuses Close() there)");
        closed.Should().Be(0);
        h.RunPosted();
        h.Backend.Saved.Should().ContainSingle();
        closed.Should().Be(1);
        h.Model.CanClose().Should().BeTrue();
    }

    [Fact]
    public void Save_and_switch_runs_after_the_menu_click()
    {
        var h = BuilderHarness.Create(deferPosts: true);
        h.Model.ProfileName = "Draft";
        h.Model.ProfileTab = "Tab";
        var optionsBefore = h.Model.ProfileOptions.ToList();
        h.Dialogs.ChooseAnswers.Enqueue(DialogChoice.Primary);
        h.Model.SelectedProfileOption = h.Option("Structure");
        h.Backend.Saved.Should().BeEmpty("the save waits for the selection change to finish");
        h.Model.ProfileOptions.Should().Equal(optionsBefore, "the menu's list is not rebuilt inside the click");
        h.RunPosted();
        h.Backend.Saved.Should().ContainSingle();
        h.Model.ProfileName.Should().Be("Structure");
        h.Model.SelectedProfileOption!.Entry!.Profile.ProfileName.Should().Be("Structure");
    }

    [Fact]
    public void Discard_and_switch_runs_after_the_menu_click()
    {
        var h = BuilderHarness.Create(deferPosts: true);
        h.Model.ProfileName = "Draft";
        h.Dialogs.ChooseAnswers.Enqueue(DialogChoice.Secondary);
        h.Model.SelectedProfileOption = h.Option("Structure");
        h.Model.ProfileName.Should().Be("Draft");
        h.RunPosted();
        h.Model.ProfileName.Should().Be("Structure");
    }

    [Fact]
    public void The_profile_option_shows_loaded_only_for_the_active_id()
    {
        var entry = new ProfileEntry("x.json", new Profile { Id = "a", ProfileName = "Arch", Tab = "Studio" });
        ProfileOptionViewModel.For(entry, "a").Should().Match<ProfileOptionViewModel>(o => o.IsLoaded && o.Display == "Arch" && o.Note == "loaded · tab \"Studio\"");
        ProfileOptionViewModel.For(entry, "b").Should().Match<ProfileOptionViewModel>(o => !o.IsLoaded && o.Note == "tab \"Studio\"");
        ProfileOptionViewModel.For(new ProfileEntry("y.json", new Profile { ProfileName = "NoId" }), null).Key.Should().Be("NoId");
    }

    private sealed class ThrowingBackend : IProfileBuilderBackend
    {
        public string RevitVersion => "";
        public IReadOnlyList<string> AllTabs => [];
        public IReadOnlyList<CatalogCommand> Catalog => throw new InvalidOperationException();
        public IReadOnlyList<ProfileEntry> GetProfiles() => throw new InvalidOperationException();
        public ActiveProfile GetActiveProfile() => throw new InvalidOperationException();
        public IReadOnlyDictionary<string, AddinLookupEntry> GetAddinLookup() => throw new InvalidOperationException();
        public SaveOutcome Save(Profile profile) => throw new InvalidOperationException();
        public OpResult Export(Profile profile, string path) => throw new InvalidOperationException();
        public BrandingState LoadBranding() => throw new InvalidOperationException();
        public OpResult SaveBrandingUrl(string url) => throw new InvalidOperationException();
        public LogoOutcome StoreLogo(string sourcePath) => throw new InvalidOperationException();
        public OpResult ClearLogo() => throw new InvalidOperationException();
        public Task<LoadResult> LoadAsync(LoadRequest request) => throw new InvalidOperationException();
        public IReadOnlyList<RST.Core.Ribbon.IconPackEntry> GetIconPack() => throw new InvalidOperationException();
    }
}
