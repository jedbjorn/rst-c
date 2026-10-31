// rst-c's own (not vendored): the Builder's editing rules (spec #16, WU4, AC5): startFresh,
// loadIntoProfile, the dirty snapshot and the save plan (a rename keeps the id since spec #19), and
// the save service (save_profile's checks and write, export_profile's package), against
// profile_builder.html and LoaderBridge.

using System.IO;
using System.IO.Compression;
using FluentAssertions;
using RST.Core.Builder;
using RST.Core.Loader;
using RST.Core.Profiles;
using Xunit;

namespace RST.Tests.Builder;

public sealed class ProfileEditingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rst-builder-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private static Profile Named(string name, string? id) => new() { Id = id, ProfileName = name, Tab = "T" };

    [Fact]
    public void A_new_profile_has_the_page_defaults()
    {
        var p = ProfileEditing.NewProfile();
        p.SchemaVersion.Should().Be(1);
        p.Id.Should().BeNull();
        p.ProfileName.Should().Be("");
        p.Tab.Should().Be("");
        p.MinVersion.Should().Be("0.1.0");
        p.ExportDate.Should().Be("");
        p.PanelOpacity.Should().Be(100);
        p.Panels.Should().BeEmpty();
        p.Branding.Should().BeNull();
        p.Presets.Should().BeEquivalentTo(new ProfilePresets());
        p.CleanupTargets.Should().BeEquivalentTo(CleanupDefaults.Build());
    }

    [Fact]
    public void Loading_for_editing_copies_and_applies_the_page_defaults()
    {
        var source = new Profile
        {
            SchemaVersion = 0,
            Id = "abc",
            ProfileName = "Arch",
            Tab = "Studio",
            MinVersion = "",
            PanelOpacity = 70,
            Branding = new Branding { LogoFile = "logo.png", Url = "https://x" },
            Presets = null,
            CleanupTargets = null,
            Panels = [new Panel { Name = "P", Color = "#fff", CornerRadius = 5, Slots = [new Slot { Name = "S", CommandId = "C", BaseName = "B" }] }],
        };
        var p = ProfileEditing.ForEditing(source);
        p.Should().NotBeSameAs(source);
        p.Panels[0].Should().NotBeSameAs(source.Panels[0], "the editor works on its own copy");
        p.SchemaVersion.Should().Be(1, "schemaVersion || 1");
        p.MinVersion.Should().Be("0.1.0", "min_version || '0.1.0'");
        p.Branding.Should().BeNull("branding is machine-wide now");
        p.Presets.Should().BeEquivalentTo(new ProfilePresets());
        p.CleanupTargets.Should().BeEquivalentTo(CleanupDefaults.Build(), "a pre-RST-031 profile gets the defaults");
        p.Panels[0].CornerRadius.Should().Be(5, "fields the Builder does not edit are kept");
        p.Panels[0].Slots[0].BaseName.Should().Be("B");
        p.PanelOpacity.Should().Be(70);

        p.Panels[0].Name = "changed";
        source.Panels[0].Name.Should().Be("P");
    }

    [Fact]
    public void Loading_keeps_a_profile_s_own_cleanup_targets_and_presets()
    {
        var source = Named("A", "1");
        source.CleanupTargets = [new CleanupTarget { Id = "rst.cleanup.temp", Name = "Temp", Path = @"%LocalAppData%\Temp", Enabled = false }];
        source.Presets = new ProfilePresets { HiddenTabs = ["Analyze"], DisableUnusedAddins = true };
        var p = ProfileEditing.ForEditing(source);
        p.CleanupTargets.Should().BeEquivalentTo(source.CleanupTargets);
        p.Presets.Should().BeEquivalentTo(source.Presets);
        p.Presets!.HiddenTabs.Should().NotBeSameAs(source.Presets.HiddenTabs);
    }

    [Fact]
    public void The_snapshot_changes_with_any_edit_and_returns_when_it_is_undone()
    {
        var p = ProfileEditing.ForEditing(Named("A", "1"));
        var clean = ProfileEditing.Snapshot(p);
        p.ProfileName = "B";
        ProfileEditing.Snapshot(p).Should().NotBe(clean);
        p.ProfileName = "A";
        ProfileEditing.Snapshot(p).Should().Be(clean);
        p.Panels.Add(BuilderRules.NewPanel(0));
        ProfileEditing.Snapshot(p).Should().NotBe(clean);
    }

    [Fact]
    public void Save_requires_a_name_then_a_tab()
    {
        ProfileEditing.PlanSave(new Profile { ProfileName = "  ", Tab = "" }, false, "", []).Should().Be(SavePlanKind.MissingName);
        ProfileEditing.PlanSave(new Profile { ProfileName = "A", Tab = " " }, false, "", []).Should().Be(SavePlanKind.MissingTab);
    }

    [Fact]
    public void A_new_profile_saves_in_place_even_on_a_taken_name()
    {
        var p = Named("Arch", null);
        ProfileEditing.PlanSave(p, editMode: false, "", [Named("arch", "1")]).Should().Be(SavePlanKind.InPlace);
    }

    [Fact]
    public void An_edit_that_keeps_its_name_or_changes_only_the_tab_saves_in_place()
    {
        var p = Named(" Arch ", "1");
        p.Tab = "New tab";
        ProfileEditing.PlanSave(p, editMode: true, "Arch", [Named("Arch", "1")]).Should().Be(SavePlanKind.InPlace);
        p.Id.Should().Be("1");
    }

    [Fact]
    public void A_rename_in_edit_mode_saves_in_place_under_its_own_id()
    {
        // Spec #19: rename-as-copy is retired; the id and export date are kept.
        var p = Named("Arch 2", "1");
        p.ExportDate = "2026-01-01";
        ProfileEditing.PlanSave(p, editMode: true, "Arch", [Named("Arch", "1"), Named("Struct", "2")]).Should().Be(SavePlanKind.InPlace);
        p.Id.Should().Be("1");
        p.ExportDate.Should().Be("2026-01-01");
    }

    [Fact]
    public void A_rename_onto_another_profile_s_name_aborts_case_insensitively()
    {
        var p = Named("  STRUCT ", "1");
        ProfileEditing.PlanSave(p, editMode: true, "Arch", [Named("Arch", "1"), Named("struct", "2")]).Should().Be(SavePlanKind.NameCollision);
        p.Id.Should().Be("1", "nothing changes on a collision");
        // The profile's own id never collides, nor a profile without an id.
        ProfileEditing.PlanSave(Named("Struct", "1"), true, "Arch", [Named("struct", "1"), Named("Struct", null)]).Should().Be(SavePlanKind.InPlace);
    }

    [Fact]
    public void Edit_mode_without_an_original_name_saves_in_place() =>
        ProfileEditing.PlanSave(Named("A", "1"), true, "", []).Should().Be(SavePlanKind.InPlace);

    // ---- the save service ----

    [Fact]
    public void Save_assigns_an_id_and_today_and_writes_a_copy()
    {
        var service = new ProfileSaveService(_dir, utcNow: () => new DateTime(2026, 10, 30, 23, 0, 0, DateTimeKind.Utc));
        var edited = ProfileEditing.NewProfile();
        edited.ProfileName = "My Profile";
        edited.Tab = "Studio";
        edited.Panels.Add(BuilderRules.NewPanel(0));
        var result = service.Save(edited);
        result.Ok.Should().BeTrue();
        result.Saved!.Id.Should().NotBeNullOrEmpty();
        result.Saved.ExportDate.Should().Be("2026-10-30");
        result.FileName.Should().Be("My_Profile_" + result.Saved.Id + ".json");
        edited.Id.Should().BeNull("the service writes its own copy; the editor takes the id from the result");
        File.Exists(Path.Combine(_dir, result.FileName!)).Should().BeTrue();
        ProfileStore.List(_dir).Single().Profile.ProfileName.Should().Be("My Profile");
    }

    [Fact]
    public void Save_keeps_an_existing_id_and_date_and_replaces_the_old_file_of_that_id()
    {
        var service = new ProfileSaveService(_dir);
        var p = Named("Old", "fixed-id");
        p.ExportDate = "2025-01-02";
        service.Save(p).Ok.Should().BeTrue();
        p.ProfileName = "New";
        var second = service.Save(p);
        second.Saved!.Id.Should().Be("fixed-id");
        second.Saved.ExportDate.Should().Be("2025-01-02");
        Directory.GetFiles(_dir).Select(Path.GetFileName).Should().Equal("New_fixed-id.json");
    }

    [Fact]
    public void Save_reports_the_bridge_s_errors()
    {
        var service = new ProfileSaveService(_dir);
        service.Save(new Profile { ProfileName = "", Tab = "T" }).Error.Should().Be("Profile name required");
        service.Save(new Profile { ProfileName = "A", Tab = "" }).Error.Should().Be("Tab name required");
        var bad = Named("A", null);
        bad.Panels.Add(new Panel { Name = "P", Color = "blue", Slots = [new Slot { SlotType = "tool", Name = "x" }] });
        var r = service.Save(bad);
        r.Ok.Should().BeFalse();
        r.Error.Should().StartWith("Profile validation failed: ")
            .And.Contain("profile.panels[0].color: invalid hex \"blue\"")
            .And.Contain("profile.panels[0].slots[0].commandId: required for type=tool");
        Directory.Exists(_dir).Should().BeFalse("nothing was written");
    }

    [Fact]
    public void The_suggested_export_name_is_the_canonical_stem_or_rst_profile()
    {
        ProfileSaveService.SuggestedExportName(Named("My Arch", "id1")).Should().Be("My_Arch_id1.zip");
        ProfileSaveService.SuggestedExportName(Named(" ", "id1")).Should().Be("rst_profile.zip");
    }

    [Fact]
    public void Export_packs_the_profile_without_touching_the_profiles_folder()
    {
        Directory.CreateDirectory(_dir);
        var zip = Path.Combine(_dir, "out.zip");
        var service = new ProfileSaveService(Path.Combine(_dir, "profiles"));
        var p = Named("Arch", "id1");
        service.Export(p, zip).Should().Be(OpResult.Success);
        using (var archive = ZipFile.OpenRead(zip))
            archive.Entries.Select(e => e.FullName).Should().Contain("profile.json");
        Directory.Exists(Path.Combine(_dir, "profiles")).Should().BeFalse();
        p.Branding.Should().BeNull("the editor's profile is not changed by the pack");

        service.Export(p, Path.Combine(_dir, "missing", "x.zip")).Error.Should().StartWith("Write failed: ");
    }

    [Fact]
    public void A_snapshot_reads_back_as_the_profile_it_was_taken_of()
    {
        var p = ProfileEditing.NewProfile();
        p.Id = "id-1";
        p.ProfileName = "Arch";
        p.Tab = "Studio";
        p.Panels.Add(new Panel { Name = "Links", Color = "#4f8ef7", Slots = [new Slot { SlotType = "tool", Name = "Wiki", BaseName = "Wiki", CommandId = "URL:https://wiki.example.com" }, PanelStacks.StackSlot("S")] });
        p.Stacks["S"] = new Stack { Tools = [new Slot { SlotType = "tool", Name = "A", CommandId = "A" }, new Slot { SlotType = "tool", Name = "B", CommandId = "B" }] };
        var snapshot = ProfileEditing.Snapshot(p);
        var back = ProfileEditing.FromSnapshot(snapshot);
        back.Should().NotBeSameAs(p);
        ProfileEditing.Snapshot(back).Should().Be(snapshot);
    }
}
