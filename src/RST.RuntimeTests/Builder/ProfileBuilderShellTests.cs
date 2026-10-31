// rst-c's own (not vendored): feature #18 (spec #19), WU2, AC5 for the window shell, through the
// view-model over the real Builder backend on a temp profiles folder, where the files matter:
// Rename then Save keeps the id and leaves one file; Duplicate writes a new id from the working copy
// (unsaved edits included), switches to it and leaves the original's file as it was; both dialogs
// refuse a missing or taken name (case-insensitive) and accept the next one typed; and edits on
// the Loader defaults and Junk cleanup tabs survive a Save and a reopen. The real dialogs and the
// window's menus are driven in ProfileBuilderWindowFlowTests and ProfileBuilderShellScreenshotTests.

using System.IO;
using FluentAssertions;
using RST.Core.Builder;
using RST.Core.Loader;
using RST.Core.Profiles;
using RST.RuntimeTests.Loader;
using RST.Tests.Loader;
using RST.UI.Builder;
using RST.UI.Controls;
using RST.UI.Loader;
using Xunit;

namespace RST.RuntimeTests.Builder;

public sealed class ProfileBuilderShellTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rst-shell-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string ProfilesDir => Path.Combine(_root, "profiles");

    private string[] Files() => Directory.GetFiles(ProfilesDir, "*.json").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    /// <summary>The real backend on a temp folder holding Architecture and Structure, as the Builder writes them.</summary>
    private ProfileBuilderBackend RealBackend(FakeScheduler? scheduler = null)
    {
        Directory.CreateDirectory(ProfilesDir);
        foreach (var p in new[] { LoaderFixtures.Architecture(), LoaderFixtures.Structure() })
            ProfileStore.Save(ProfileEditing.ForEditing(p), ProfilesDir);
        return Reopen(scheduler);
    }

    /// <param name="scheduler">The live ribbon switch the auto-reload calls (recorded).</param>
    private ProfileBuilderBackend Reopen(FakeScheduler? scheduler = null) =>
        new("2026", [], scheduler ?? new FakeScheduler(), LoaderFixtures.Ribbon, LoaderPaths.Under(_root), LoaderAssets.LookupPath);

    private static BuilderHarness OpenStructure(IProfileBuilderBackend backend)
    {
        var h = BuilderHarness.Create(backend);
        h.Model.SelectedProfileOption = h.Option("Structure");
        h.Model.IsDirty.Should().BeFalse();
        return h;
    }

    private static Profile Read(string path) => ProfileSerializer.ReadString(File.ReadAllText(path));

    [Fact]
    public async Task Rename_then_Save_keeps_the_id_and_leaves_one_file()
    {
        var h = OpenStructure(RealBackend());
        var before = Files();
        before.Should().HaveCount(2);

        h.Modals.Names.Enqueue("Structure 2027");
        h.Model.RenameCommand.Execute(null);
        h.Modals.NameDialogs.Single().Suggested.Should().Be("Structure", "the dialog opens on the current name");
        h.Model.ProfileName.Should().Be("Structure 2027");
        h.Model.DisplayName.Should().Be("Structure 2027");
        h.Model.EditingText.Should().Be("· editing Structure 2027");
        h.Model.IsDirty.Should().BeTrue("a rename is an unsaved change");
        Files().Should().Equal(before, "nothing is written until Save");

        (await h.Model.SaveAsync()).Should().BeTrue();
        var files = Files();
        files.Should().HaveCount(2, "the rename replaced the old file: one file per profile");
        files.Should().Contain("Structure_2027_" + BuilderFixtures.StructId + ".json").And.NotContain(f => f.StartsWith("Structure_" + BuilderFixtures.StructId, StringComparison.Ordinal));
        var saved = Read(Path.Combine(ProfilesDir, "Structure_2027_" + BuilderFixtures.StructId + ".json"));
        saved.Id.Should().Be(BuilderFixtures.StructId);
        saved.ProfileName.Should().Be("Structure 2027");
        h.Model.ProfileOptions.Select(o => o.Display).Should().Equal("Architecture", "Structure 2027");
    }

    [Fact]
    public void Duplicate_writes_a_new_id_switches_to_the_copy_and_leaves_the_original_file()
    {
        var h = OpenStructure(RealBackend());
        var original = Directory.GetFiles(ProfilesDir, "Structure_*.json").Single();
        var originalBytes = File.ReadAllBytes(original);

        h.Model.PanelOpacity = 80; // an unsaved edit: it goes into the copy, not the original
        h.Modals.Names.Enqueue(""); // refused: required
        h.Modals.Names.Enqueue("Structure Site");
        h.Model.DuplicateCommand.Execute(null);

        h.Modals.NameDialogs.Single().Suggested.Should().Be("Structure copy");
        h.Modals.Refusals.Should().Equal("Profile name required.");
        Files().Should().HaveCount(3);
        File.ReadAllBytes(original).Should().Equal(originalBytes, "the original stays as last saved");

        var copyFile = Directory.GetFiles(ProfilesDir, "Structure_Site_*.json").Single();
        var copy = Read(copyFile);
        copy.Id.Should().NotBeNullOrEmpty().And.NotBe(BuilderFixtures.StructId);
        copy.PanelOpacity.Should().Be(80, "the copy is made from the working copy, unsaved edits included");
        copy.Panels.Select(p => p.Name).Should().Equal("Frame", "Review", "Links");

        h.Model.Profile.Id.Should().Be(copy.Id, "the editor switched to the copy");
        h.Model.ProfileName.Should().Be("Structure Site");
        h.Model.IsEditMode.Should().BeTrue();
        h.Model.IsDirty.Should().BeFalse();
        h.Model.SelectedProfileOption!.Key.Should().Be(copy.Id);
        h.Model.ProfileOptions.Single(o => o.IsCurrent).Display.Should().Be("Structure Site");
        h.Model.Toast.Text.Should().Be("Duplicated as \"Structure Site\"; the original is kept.");
        h.Model.Toast.Kind.Should().Be(BannerKind.Success);
    }

    [Fact]
    public void Rename_refuses_a_missing_or_taken_name_and_accepts_its_own_in_another_case()
    {
        var h = OpenStructure(RealBackend());
        h.Modals.Names.Enqueue("  ");
        h.Modals.Names.Enqueue("architecture");
        h.Modals.Names.Enqueue("STRUCTURE");
        h.Model.RenameCommand.Execute(null);
        h.Modals.Refusals.Should().Equal(
            "Profile name required.",
            "A profile named \"architecture\" already exists. Choose a different name.");
        h.Model.ProfileName.Should().Be("STRUCTURE", "the profile's own name is not taken by itself");

        h.Model.RenameCommand.Execute(null); // cancelled: nothing typed
        h.Model.ProfileName.Should().Be("STRUCTURE");
    }

    [Fact]
    public void Duplicate_refuses_every_saved_name_including_the_original_s()
    {
        var h = OpenStructure(RealBackend());
        h.Modals.Names.Enqueue("structure");
        h.Modals.Names.Enqueue(" Architecture ");
        h.Model.DuplicateCommand.Execute(null);
        h.Modals.Refusals.Should().Equal(
            "A profile named \"structure\" already exists. Choose a different name.",
            "A profile named \"Architecture\" already exists. Choose a different name.");
        Files().Should().HaveCount(2, "a cancelled Duplicate writes nothing");
        h.Model.Profile.Id.Should().Be(BuilderFixtures.StructId);
    }

    [Fact]
    public void Duplicate_is_only_for_a_saved_profile()
    {
        var h = BuilderHarness.Create(RealBackend());
        h.Model.DuplicateCommand.CanExecute(null).Should().BeFalse("a new profile has no saved original");
        h.Model.SelectedProfileOption = h.Option("Structure");
        h.Model.DuplicateCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task A_new_profile_is_named_with_Rename_and_its_first_save_writes_it()
    {
        var h = BuilderHarness.Create(RealBackend());
        h.Modals.Names.Enqueue("Structure"); // taken
        h.Modals.Names.Enqueue("Interiors");
        h.Model.RenameCommand.Execute(null);
        h.Modals.Refusals.Should().ContainSingle();
        h.Model.ProfileTab = "Studio Int";
        (await h.Model.SaveAsync()).Should().BeTrue();
        Files().Should().HaveCount(3);
        h.Model.IsEditMode.Should().BeTrue();
    }

    [Fact]
    public async Task Loader_defaults_and_Junk_cleanup_edits_survive_Save_and_a_reopen()
    {
        var h = OpenStructure(RealBackend());
        h.Model.PresetTabs.Single(t => t.Tab == "Enscape").IsHidden = true;
        h.Model.DisableUnused = false;
        h.Model.CleanupRows[1].Name = "Package Cache";
        h.Model.CleanupRows[3].Enabled = false;
        h.Model.CleanupRows[5].RemoveCommand.Execute(null);
        h.Model.SaveCommand.CanExecute(null).Should().BeTrue();
        (await h.Model.SaveAsync()).Should().BeTrue();

        var again = OpenStructure(Reopen());
        again.Model.PresetTabs.Where(t => t.IsHidden).Select(t => t.Tab).Should().Equal("Massing & Site", "Collaborate", "Enscape");
        again.Model.DisableUnused.Should().BeFalse();
        again.Model.CleanupRows.Select(r => r.Name).Should().Equal("Temp", "Package Cache", "Journals", "Collaboration Cache", "Recent File List");
        again.Model.CleanupRows.Select(r => r.Enabled).Should().Equal(true, true, true, false, true);
    }

    // ── a profile read from a file with no id (WU1 review F6) ────────────────

    [Fact]
    public async Task Saving_a_profile_read_from_an_id_less_file_replaces_that_file()
    {
        var backend = RealBackend();
        var legacy = ProfileEditing.ForEditing(LoaderFixtures.Structure());
        legacy.Id = null;
        legacy.ProfileName = "Legacy";
        using (var fs = File.Create(Path.Combine(ProfilesDir, "Legacy_2024-05-01.json")))
            ProfileSerializer.Write(legacy, fs);

        var h = BuilderHarness.Create(backend);
        h.Model.SelectedProfileOption = h.Option("Legacy");
        h.Model.Profile.Id.Should().BeNullOrEmpty();
        h.Model.MinVersion = "3.1.0";
        (await h.Model.SaveAsync()).Should().BeTrue();

        Files().Should().HaveCount(3).And.NotContain("Legacy_2024-05-01.json", "the id-less original is replaced, not kept beside the copy");
        var saved = Read(Path.Combine(ProfilesDir, Files().Single(f => f.StartsWith("Legacy_", StringComparison.Ordinal))));
        saved.Id.Should().NotBeNullOrEmpty().And.Be(h.Model.Profile.Id);
        h.Model.ProfileOptions.Count(o => o.Display == "Legacy").Should().Be(1, "listed once");
        h.Model.Toast.Kind.Should().Be(BannerKind.Success);

        // Saved again: the profile has its id now; nothing else is removed.
        h.Model.MinVersion = "3.2.0";
        (await h.Model.SaveAsync()).Should().BeTrue();
        Files().Should().HaveCount(3);
    }

    [Fact]
    public async Task A_failed_removal_of_the_id_less_file_warns_and_keeps_the_save()
    {
        var backend = BuilderFixtures.Backend();
        var legacy = LoaderFixtures.Structure();
        legacy.Id = null;
        legacy.ProfileName = "Legacy";
        backend.Library.Add(new ProfileEntry("Legacy.json", legacy));
        backend.RemoveResult = OpResult.Fail("The file is in use");
        var h = BuilderHarness.Create(backend);
        h.Model.SelectedProfileOption = h.Option("Legacy");
        h.Model.MinVersion = "3.1.0";

        var events = new List<RST.Core.Logging.LogEvent>();
        var previous = RST.Core.Logging.Log.Logger;
        RST.Core.Logging.Log.Logger = new RST.Core.Logging.Logger(RST.Core.Logging.LogLevel.Verbose, new ListSink(events));
        try
        {
            (await h.Model.SaveAsync()).Should().BeTrue("the save stands");
        }
        finally
        {
            RST.Core.Logging.Log.Logger = previous;
        }
        h.Backend.Removed.Should().Equal("Legacy.json");
        h.Model.IsDirty.Should().BeFalse();
        h.Model.Profile.Id.Should().Be("new-1");
        h.Model.Toast.Kind.Should().Be(BannerKind.Warning);
        h.Model.Toast.Text.Should().Be("Saved as Legacy_new-1.json, but the old file Legacy.json could not be removed (The file is in use). Delete it to avoid a duplicate.");
        lock (events)
            events.Should().Contain(e => e.Level == RST.Core.Logging.LogLevel.Warning
                                         && e.RenderMessage().Contains("Legacy.json could not be removed", StringComparison.Ordinal)
                                         && e.RenderMessage().Contains("The file is in use", StringComparison.Ordinal),
                "the failed removal is logged");

        // A profile that had an id from the start never removes anything.
        h.Model.SelectedProfileOption = h.Option("Structure");
        h.Model.MinVersion = "3.1.0";
        (await h.Model.SaveAsync()).Should().BeTrue();
        h.Backend.Removed.Should().ContainSingle();
    }

    [Fact]
    public async Task The_file_just_written_is_never_removed_even_when_it_has_the_old_name()
    {
        // The fake save names the copy Legacy_new-1.json; the id-less original already has that name.
        var backend = BuilderFixtures.Backend();
        var legacy = LoaderFixtures.Structure();
        legacy.Id = null;
        legacy.ProfileName = "Legacy";
        backend.Library.Add(new ProfileEntry("Legacy_new-1.json", legacy));
        var h = BuilderHarness.Create(backend);
        h.Model.SelectedProfileOption = h.Option("Legacy");
        h.Model.MinVersion = "3.1.0";
        (await h.Model.SaveAsync()).Should().BeTrue();
        h.Backend.Removed.Should().BeEmpty("the save wrote that very file");
        h.Model.Toast.Kind.Should().Be(BannerKind.Success);
        h.Model.ProfileOptions.Count(o => o.Display == "Legacy").Should().Be(1);
    }

    [Fact]
    public async Task Saving_the_loaded_id_less_profile_repoints_the_active_profile_and_reloads_the_ribbon()
    {
        var scheduler = new FakeScheduler();
        var backend = RealBackend(scheduler);
        var legacy = ProfileEditing.ForEditing(LoaderFixtures.Structure());
        legacy.Id = null;
        legacy.ProfileName = "Legacy";
        using (var fs = File.Create(Path.Combine(ProfilesDir, "Legacy_2024-05-01.json")))
            ProfileSerializer.Write(legacy, fs);
        var paths = LoaderPaths.Under(_root);
        ActiveProfile.FromProfile(legacy, "Legacy_2024-05-01.json", ["Analyze"], disableNonRequired: true).Write(paths.ActiveProfileFile);

        var h = BuilderHarness.Create(backend);
        h.Model.SelectedProfileOption = h.Option("Legacy");
        h.Modals.Names.Enqueue("Legacy Studio"); // renamed too: the old pointer's name would match nothing
        h.Model.RenameCommand.Execute(null);
        (await h.Model.SaveAsync()).Should().BeTrue();

        var id = h.Model.Profile.Id;
        id.Should().NotBeNullOrEmpty();
        var file = Files().Single(f => f.StartsWith("Legacy_Studio_", StringComparison.Ordinal));
        Files().Should().NotContain("Legacy_2024-05-01.json");
        var active = ActiveProfile.Read(paths.ActiveProfileFile);
        active.ProfileId.Should().Be(id, "the pointer follows the profile to its new id");
        active.ProfileFile.Should().Be(file);
        active.ProfileName.Should().Be("Legacy Studio");
        active.HiddenTabs.Should().Equal("Analyze");
        active.DisableNonRequired.Should().BeTrue();
        ProfileStore.Resolve(active.ProfileName, active.ProfileId, ProfilesDir).Should().NotBeNull("the pointer resolves to a file on disk");
        scheduler.Scheduled.Should().ContainSingle().Which!.Id.Should().Be(id, "the ribbon was rebuilt from the saved profile");
        h.Model.Toast.Text.Should().Be("Saved. The ribbon updates when you close the Builder.");
        h.Model.IsLoadedProfile.Should().BeTrue();
    }

    [Fact]
    public async Task An_id_less_profile_can_be_saved_with_no_edit_and_is_then_clean_with_one_file()
    {
        // Review N7: "open it in the Builder and save it once" needs Save enabled with no edit.
        var backend = RealBackend();
        var legacy = ProfileEditing.ForEditing(LoaderFixtures.Structure());
        legacy.Id = null;
        legacy.ProfileName = "Legacy";
        using (var fs = File.Create(Path.Combine(ProfilesDir, "Legacy_2024-05-01.json")))
            ProfileSerializer.Write(legacy, fs);

        var h = BuilderHarness.Create(backend);
        h.Model.SelectedProfileOption = h.Option("Legacy");
        h.Model.IsDirty.Should().BeTrue("a profile with no id counts as changed until it is saved");
        h.Model.SaveCommand.CanExecute(null).Should().BeTrue("no edit is needed");

        // Switching away asks first, as for any unsaved change.
        h.Model.SelectedProfileOption = h.Option("Structure"); // Cancel (no answer scripted)
        h.Dialogs.ChooseCalls.Should().ContainSingle();
        h.Model.ProfileName.Should().Be("Legacy");

        (await h.Model.SaveAsync()).Should().BeTrue();
        h.Model.Profile.Id.Should().NotBeNullOrEmpty();
        h.Model.IsDirty.Should().BeFalse("the id the save assigned clears it");
        h.Model.SaveCommand.CanExecute(null).Should().BeFalse();
        Files().Should().HaveCount(3).And.ContainSingle(f => f.StartsWith("Legacy_", StringComparison.Ordinal));
        Read(Path.Combine(ProfilesDir, Files().Single(f => f.StartsWith("Legacy_", StringComparison.Ordinal)))).Id.Should().Be(h.Model.Profile.Id);
    }

    /// <summary>The fixture backend with a loaded legacy profile: no id, and the active pointer names its file.</summary>
    private static FakeBuilderBackend LoadedLegacy()
    {
        var backend = BuilderFixtures.Backend();
        var legacy = LoaderFixtures.Structure();
        legacy.Id = null;
        legacy.ProfileName = "Legacy";
        backend.Library.Add(new ProfileEntry("Legacy.json", legacy));
        backend.Active = ActiveProfile.FromProfile(legacy, "Legacy.json", ["Analyze"], true);
        return backend;
    }

    [Fact]
    public async Task The_loaded_legacy_file_is_removed_only_after_the_reload_has_re_pointed_the_active_profile()
    {
        // Review N8a: the reload first, then the removal.
        var h = BuilderHarness.Create(LoadedLegacy());
        h.Model.SelectedProfileOption = h.Option("Legacy");
        (await h.Model.SaveAsync()).Should().BeTrue();
        h.Backend.Calls.SkipWhile(c => c != "Save").Should().ContainInOrder("Save", "Load", "RemoveSupersededFile");
        h.Backend.Loads.Single().Id.Should().Be(h.Model.Profile.Id);
        h.Backend.Removed.Should().Equal("Legacy.json");
        h.Model.Toast.Text.Should().Be("Saved. The ribbon updates when you close the Builder.");
        h.Model.ProfileOptions.Count(o => o.Display == "Legacy").Should().Be(1);
        h.Model.IsLoadedProfile.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_reload_keeps_the_loaded_legacy_file_and_warns(bool throws)
    {
        var backend = LoadedLegacy();
        backend.OnLoad = _ => throws
            ? Task.FromException<LoadResult>(new IOException("active_profile.json is locked"))
            : Task.FromResult(LoadResult.Fail("Failed to write active profile: locked"));
        var h = BuilderHarness.Create(backend);
        h.Model.SelectedProfileOption = h.Option("Legacy");

        var events = new List<RST.Core.Logging.LogEvent>();
        var previous = RST.Core.Logging.Log.Logger;
        RST.Core.Logging.Log.Logger = new RST.Core.Logging.Logger(RST.Core.Logging.LogLevel.Verbose, new ListSink(events));
        try
        {
            (await h.Model.SaveAsync()).Should().BeTrue("the save stands");
        }
        finally
        {
            RST.Core.Logging.Log.Logger = previous;
        }
        h.Backend.Removed.Should().BeEmpty("the pointer may still name the old file");
        h.Model.Toast.Kind.Should().Be(BannerKind.Warning);
        h.Model.Toast.Text.Should().Be("Saved as Legacy_new-1.json, but the ribbon could not be reloaded, so the old file Legacy.json was kept: "
                                       + "the loaded profile may still point at it. Load the profile again in the Loader, then delete the old file.");
        lock (events)
            events.Should().Contain(e => e.Level == RST.Core.Logging.LogLevel.Warning
                                         && e.RenderMessage().Contains("kept Legacy.json", StringComparison.Ordinal));
        h.Model.IsDirty.Should().BeFalse();
        h.Model.IsWorking.Should().BeFalse();
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
}
