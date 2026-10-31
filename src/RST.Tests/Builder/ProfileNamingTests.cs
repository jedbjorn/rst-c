// rst-c's own (not vendored): Rename and Duplicate (feature #18, spec #19, AC3), which replace
// rename-as-copy: a rename saved gives one file under the same id and leaves the active profile
// pointer valid; Duplicate writes a new file with a new id from the working copy, unsaved edits
// included, and the original's file is unchanged; both names are checked for uniqueness.

using System.IO;
using FluentAssertions;
using RST.Core.Builder;
using RST.Core.Profiles;
using Xunit;

namespace RST.Tests.Builder;

public sealed class ProfileNamingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rst-naming-" + Guid.NewGuid().ToString("N"));

    private string ProfilesDir => Path.Combine(_dir, "profiles");

    private string ActiveFile => Path.Combine(_dir, "active_profile.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private static Profile Named(string name, string? id) => new()
    {
        Id = id,
        ProfileName = name,
        Tab = "Studio",
        ExportDate = "2026-01-01",
        Panels = [new Panel { Name = "P", Color = "#4f8ef7", Slots = [new Slot { SlotType = "tool", Name = "Wall", CommandId = "ID_WALL" }] }],
    };

    private IReadOnlyList<Profile> Library() => ProfileStore.List(ProfilesDir).Select(e => e.Profile).ToList();

    private string[] Files() => Directory.GetFiles(ProfilesDir).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    [Fact]
    public void A_rename_saved_gives_one_file_under_the_same_id_and_the_loaded_profile_stays_loaded()
    {
        var file = ProfileStore.Save(Named("Arch", "id-arch"), ProfilesDir);
        ProfileStore.Save(Named("Struct", "id-struct"), ProfilesDir);
        ActiveProfile.FromProfile(Named("Arch", "id-arch"), file).Write(ActiveFile);

        var working = ProfileEditing.ForEditing(Library().Single(p => p.Id == "id-arch"));
        ProfileNaming.Rename(working, "  Architecture ", Library()).Should().Be(NameCheck.Ok);
        working.ProfileName.Should().Be("Architecture");
        working.Id.Should().Be("id-arch");
        ProfileEditing.PlanSave(working, editMode: true, "Arch", Library()).Should().Be(SavePlanKind.InPlace);

        var saved = new ProfileSaveService(ProfilesDir).Save(working);
        saved.Ok.Should().BeTrue();
        saved.Saved!.Id.Should().Be("id-arch");
        saved.Saved.ExportDate.Should().Be("2026-01-01");
        Files().Should().Equal("Architecture_id-arch.json", "Struct_id-struct.json");

        var active = ActiveProfile.Read(ActiveFile);
        active.ProfileId.Should().Be("id-arch", "active_profile.json is keyed by profile_id and is not rewritten");
        ProfileStore.Resolve(active.ProfileName, active.ProfileId, ProfilesDir)!.Profile.ProfileName
            .Should().Be("Architecture", "the pointer still resolves, by id, to the renamed profile");
    }

    [Fact]
    public void A_rename_of_a_profile_stored_under_a_legacy_name_leaves_one_file_with_the_same_id()
    {
        Directory.CreateDirectory(ProfilesDir);
        using (var fs = File.Create(Path.Combine(ProfilesDir, "Arch_2024-05-01.json")))
            ProfileSerializer.Write(Named("Arch", "id-arch"), fs);

        var working = ProfileEditing.ForEditing(Library().Single());
        ProfileNaming.Rename(working, "Architecture", Library()).Should().Be(NameCheck.Ok);
        var saved = new ProfileSaveService(ProfilesDir).Save(working);

        saved.Ok.Should().BeTrue();
        Files().Should().Equal("Architecture_id-arch.json");
        Library().Single().Id.Should().Be("id-arch");
    }

    [Fact]
    public void A_rename_must_be_given_and_unique_among_the_others_case_insensitively()
    {
        ProfileStore.Save(Named("Arch", "id-arch"), ProfilesDir);
        ProfileStore.Save(Named("Struct", "id-struct"), ProfilesDir);
        var working = ProfileEditing.ForEditing(Library().Single(p => p.Id == "id-arch"));

        ProfileNaming.Rename(working, " ", Library()).Should().Be(NameCheck.Missing);
        ProfileNaming.Rename(working, " STRUCT ", Library()).Should().Be(NameCheck.Taken);
        working.ProfileName.Should().Be("Arch", "a refused rename changes nothing");
        ProfileNaming.Rename(working, "ARCH", Library()).Should().Be(NameCheck.Ok, "its own name, in another case, is not taken");
        working.ProfileName.Should().Be("ARCH");

        // The Save check agrees: a rename typed onto another profile's name aborts.
        working.ProfileName = "struct";
        ProfileEditing.PlanSave(working, editMode: true, "Arch", Library()).Should().Be(SavePlanKind.NameCollision);
    }

    [Fact]
    public void Duplicate_writes_a_new_id_from_the_working_copy_and_leaves_the_original_file_alone()
    {
        var file = ProfileStore.Save(Named("Arch", "id-arch"), ProfilesDir);
        var originalBytes = File.ReadAllText(Path.Combine(ProfilesDir, file));
        var working = ProfileEditing.ForEditing(Library().Single());
        working.Tab = "Unsaved tab";
        working.Panels[0].Slots.Add(new Slot { SlotType = "tool", Name = "Door", CommandId = "ID_DOOR" });
        var workingBefore = ProfileEditing.Snapshot(working);

        ProfileNaming.DefaultDuplicateName(working.ProfileName, Library()).Should().Be("Arch copy");
        var outcome = ProfileNaming.Duplicate(working, "Arch copy", Library(), new ProfileSaveService(ProfilesDir, utcNow: () => new DateTime(2026, 10, 30)));

        outcome.Ok.Should().BeTrue();
        var copy = outcome.Save!.Saved!;
        copy.Id.Should().NotBeNullOrEmpty().And.NotBe("id-arch");
        copy.ProfileName.Should().Be("Arch copy");
        copy.ExportDate.Should().Be("2026-10-30");
        Files().Should().BeEquivalentTo(file, outcome.Save.FileName);

        var onDisk = Library().Single(p => p.Id == copy.Id);
        onDisk.Tab.Should().Be("Unsaved tab", "the copy includes the unsaved edits");
        onDisk.Panels[0].Slots.Select(s => s.CommandId).Should().Equal("ID_WALL", "ID_DOOR");
        File.ReadAllText(Path.Combine(ProfilesDir, file)).Should().Be(originalBytes, "the original stays as last saved");
        ProfileEditing.Snapshot(working).Should().Be(workingBefore, "the working copy is not changed; the editor switches to the copy");
    }

    [Fact]
    public void Duplicate_names_are_given_unique_and_default_to_the_next_free_copy_name()
    {
        ProfileStore.Save(Named("Arch", "id-arch"), ProfilesDir);
        ProfileStore.Save(Named("arch COPY", "id-c1"), ProfilesDir);
        ProfileStore.Save(Named("Arch copy 2", "id-c2"), ProfilesDir);
        var working = ProfileEditing.ForEditing(Library().Single(p => p.Id == "id-arch"));
        var saver = new ProfileSaveService(ProfilesDir);

        ProfileNaming.DefaultDuplicateName("Arch", Library()).Should().Be("Arch copy 3");
        ProfileNaming.Duplicate(working, " ", Library(), saver).Check.Should().Be(NameCheck.Missing);
        ProfileNaming.Duplicate(working, "ARCH", Library(), saver).Check.Should().Be(NameCheck.Taken, "the original's own name is taken too");
        ProfileNaming.Duplicate(working, "Arch Copy", Library(), saver).Check.Should().Be(NameCheck.Taken);
        Files().Should().HaveCount(3, "a refused duplicate writes nothing");
    }

    [Fact]
    public void Profiles_without_an_id_are_not_compared()
    {
        ProfileNaming.IsTaken("Arch", "x", [Named("Arch", null)]).Should().BeFalse();
        ProfileNaming.IsTaken("Arch", null, [Named(" arch ", "y")]).Should().BeTrue();
        ProfileNaming.IsTaken("Arch", "y", [Named(" arch ", "y")]).Should().BeFalse();
    }
}
