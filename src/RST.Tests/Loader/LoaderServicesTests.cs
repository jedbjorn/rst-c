// rst-c's own (not vendored): the Loader services extracted from LoaderBridge (spec #16, WU3):
// prefs (read, migration, write, opacity clamp), the library (list, active, import, delete), the
// load orchestration (QA, auto-restore, disable pass, schedule, restart) and unload, and the
// add-in maintenance calls (disable preview by id, restore all, the manifest scan).

using System.IO;
using FluentAssertions;
using RST.Core.AddIns;
using RST.Core.Configuration;
using RST.Core.Loader;
using RST.Core.Profiles;
using RST.Core.Scanning;
using Xunit;

namespace RST.Tests.Loader;

public sealed class LoaderPrefsServiceTests : IDisposable
{
    private readonly TempLoaderRoot _root = new();

    public void Dispose() => _root.Dispose();

    private LoaderPrefsService Service() => new(_root.Paths);

    [Theory]
    [InlineData(5, 10)]
    [InlineData(10, 10)]
    [InlineData(55, 55)]
    [InlineData(100, 100)]
    [InlineData(250, 100)]
    public void Opacity_is_clamped_to_10_100(int value, int expected) => LoaderPrefsService.ClampOpacity(value).Should().Be(expected);

    [Fact]
    public void Record_writes_the_entry_and_a_missing_id_is_refused()
    {
        Service().Record("id-1", ["Analyze"], disableUnused: true, presetsAdopted: true).Should().Be(OpResult.Success);
        var entry = UserProfilePrefs.Read(_root.Paths.PrefsFile).Get("id-1")!;
        entry.HiddenTabs.Should().Equal("Analyze");
        entry.DisableUnusedAddins.Should().BeTrue();
        entry.PresetsAdopted.Should().BeTrue();

        Service().Record("", [], false, true).Should().Be(OpResult.Fail("profileId required"));
        Service().Record(null, [], false, true).Ok.Should().BeFalse();
    }

    [Fact]
    public void Opacity_override_is_clamped_kept_beside_the_rstify_state_and_cleared_by_null()
    {
        Service().Record("id-1", ["Analyze"], true, true);
        Service().RecordOpacityOverride("id-1", 3).Ok.Should().BeTrue();
        var entry = UserProfilePrefs.Read(_root.Paths.PrefsFile).Get("id-1")!;
        entry.PanelOpacityOverride.Should().Be(10);
        entry.HiddenTabs.Should().Equal("Analyze");

        Service().RecordOpacityOverride("id-1", null);
        UserProfilePrefs.Read(_root.Paths.PrefsFile).Get("id-1")!.PanelOpacityOverride.Should().BeNull();
        Service().RecordOpacityOverride("", 50).Error.Should().Be("profileId required");
    }

    [Fact]
    public void Read_migrates_the_active_profiles_state_once_with_presets_adopted()
    {
        var ap = new ActiveProfile { ProfileName = "A", ProfileId = "id-a", ProfileFile = "A.json", HiddenTabs = ["Analyze"], DisableNonRequired = true };
        ap.Write(_root.Paths.ActiveProfileFile);

        var prefs = Service().Read();
        var entry = prefs.Get("id-a")!;
        entry.HiddenTabs.Should().Equal("Analyze");
        entry.DisableUnusedAddins.Should().BeTrue();
        entry.PresetsAdopted.Should().BeTrue();
        File.Exists(_root.Paths.PrefsFile).Should().BeTrue("the migration is written");
    }

    [Fact]
    public void Read_does_not_migrate_an_empty_or_existing_or_blank_state()
    {
        new ActiveProfile { ProfileName = "A", ProfileId = "id-a", ProfileFile = "A.json" }.Write(_root.Paths.ActiveProfileFile);
        Service().Read().Profiles.Should().BeEmpty();

        ActiveProfile.WriteBlank(_root.Paths.ActiveProfileFile);
        Service().Read().Profiles.Should().BeEmpty();
        File.Exists(_root.Paths.PrefsFile).Should().BeFalse();
    }

    [Fact]
    public void A_corrupt_prefs_file_reads_as_empty()
    {
        File.WriteAllText(_root.Paths.PrefsFile, "{ not json");
        Service().Read().Profiles.Should().BeEmpty();
    }
}

public sealed class ProfileLibraryServiceTests : IDisposable
{
    private readonly TempLoaderRoot _root = new();

    public void Dispose() => _root.Dispose();

    private ProfileLibraryService Service() => new(_root.Paths);

    [Fact]
    public void List_returns_the_saved_profiles_and_skips_corrupt_files()
    {
        _root.Add(TempLoaderRoot.Profile("A"));
        _root.Add(TempLoaderRoot.Profile("B"));
        File.WriteAllText(Path.Combine(_root.Paths.ProfilesDir, "broken.json"), "{");
        Service().List().Select(e => e.Profile.ProfileName).Should().BeEquivalentTo(["A", "B"]);
    }

    [Fact]
    public void Get_active_reads_the_pointer_or_blank()
    {
        Service().GetActive().IsBlank.Should().BeTrue();
        new ActiveProfile { ProfileName = "A", ProfileId = "id", ProfileFile = "A.json" }.Write(_root.Paths.ActiveProfileFile);
        Service().GetActive().ProfileId.Should().Be("id");
    }

    [Fact]
    public void Import_installs_a_profile_package_with_its_logo()
    {
        var profile = TempLoaderRoot.Profile("Imported", id: Guid.NewGuid().ToString());
        var logo = Path.Combine(_root.Root, "logo.png");
        File.WriteAllBytes(logo, [0x89, 0x50, 0x4E, 0x47]);
        var zip = Path.Combine(_root.Root, "Imported.zip");
        using (var fs = File.Create(zip)) ProfileZip.Pack(profile, logo, "https://example.com", fs);

        var result = Service().Import(zip);
        result.Ok.Should().BeTrue();
        result.FileName.Should().StartWith("Imported_");
        result.Profile!.ProfileName.Should().Be("Imported");
        File.Exists(Path.Combine(_root.Paths.ProfilesDir, result.FileName!)).Should().BeTrue();
        File.Exists(Path.Combine(_root.Root, profile.Id!, "branding.png")).Should().BeTrue();
    }

    [Fact]
    public void Import_of_a_non_package_gives_the_import_failed_text()
    {
        var notZip = Path.Combine(_root.Root, "notes.zip");
        File.WriteAllText(notZip, "plain text");
        var result = Service().Import(notZip);
        result.Ok.Should().BeFalse();
        result.Error.Should().StartWith("Not a valid RST profile package: ");

        var missing = Service().Import(Path.Combine(_root.Root, "missing.zip"));
        missing.Ok.Should().BeFalse();
        missing.Error.Should().StartWith("Read failed: ");
    }

    [Fact]
    public void Delete_removes_the_file_and_blanks_the_pointer_when_it_was_active()
    {
        var a = TempLoaderRoot.Profile("A");
        var file = _root.Add(a);
        ActiveProfile.FromProfile(a, file).Write(_root.Paths.ActiveProfileFile);

        var result = Service().Delete("A", a.Id);
        result.Should().Be(new DeleteOutcome(true, null, WasActive: true));
        File.Exists(Path.Combine(_root.Paths.ProfilesDir, file)).Should().BeFalse();
        ActiveProfile.Read(_root.Paths.ActiveProfileFile).IsBlank.Should().BeTrue();
    }

    [Fact]
    public void Delete_of_an_inactive_profile_keeps_the_pointer_and_unknown_is_not_found()
    {
        var a = TempLoaderRoot.Profile("A");
        var b = TempLoaderRoot.Profile("B");
        var aFile = _root.Add(a);
        _root.Add(b);
        ActiveProfile.FromProfile(a, aFile).Write(_root.Paths.ActiveProfileFile);

        Service().Delete("B", b.Id).WasActive.Should().BeFalse();
        ActiveProfile.Read(_root.Paths.ActiveProfileFile).ProfileId.Should().Be(a.Id);
        Service().Delete("Nope", null).Should().Be(new DeleteOutcome(false, "Profile not found", false));
    }
}

public sealed class ProfileLoadServiceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 30, 9, 15, 0, DateTimeKind.Unspecified);

    private readonly TempLoaderRoot _root = new();
    private readonly FakeAddinPasses _passes = new();
    private readonly FakeScheduler _scheduler = new();

    public void Dispose() => _root.Dispose();

    private ProfileLoadService Service(IProfileSwitchScheduler? scheduler = null, bool noScheduler = false) =>
        new(_root.Paths, ["Kinship"], noScheduler ? null : scheduler ?? _scheduler, _passes, clock: () => Now);

    private Profile AddProfile(params RequiredAddin[] required)
    {
        var p = TempLoaderRoot.Profile("Arch", tab: "Studio");
        p.RequiredAddins = [.. required];
        _root.Add(p);
        return p;
    }

    [Fact]
    public void Load_writes_the_active_profile_and_prefs_and_schedules_the_switch()
    {
        var p = AddProfile();
        var result = Service().Load(new LoadRequest("Arch", p.Id, false, ["Analyze"]));

        result.Ok.Should().BeTrue();
        result.RestartNeeded.Should().BeFalse("the live switch is scheduled");
        result.Qa!.AllClear.Should().BeTrue();
        _scheduler.Scheduled.Should().ContainSingle().Which!.Id.Should().Be(p.Id);

        var ap = ActiveProfile.Read(_root.Paths.ActiveProfileFile);
        ap.ProfileId.Should().Be(p.Id);
        ap.HiddenTabs.Should().Equal("Analyze");
        ap.LoadedAt.Should().Be("2026-10-30T09:15:00.0000000");
        UserProfilePrefs.Read(_root.Paths.PrefsFile).Get(p.Id!)!.HiddenTabs.Should().Equal("Analyze");
    }

    [Fact]
    public void Load_keeps_the_presets_adopted_flag()
    {
        var p = AddProfile();
        new LoaderPrefsService(_root.Paths).Record(p.Id, [], false, presetsAdopted: true);
        Service().Load(new LoadRequest("Arch", p.Id, true, []));
        UserProfilePrefs.Read(_root.Paths.PrefsFile).Get(p.Id!)!.PresetsAdopted.Should().BeTrue();
    }

    [Fact]
    public void An_unknown_profile_fails_with_the_bridge_warning_and_writes_nothing()
    {
        var result = Service().Load(new LoadRequest("Ghost", null, false, []));
        result.Should().BeEquivalentTo(LoadResult.Fail("Profile not found: Ghost"));
        File.Exists(_root.Paths.ActiveProfileFile).Should().BeFalse();
        _passes.Calls.Should().BeEmpty();
        _scheduler.Scheduled.Should().BeEmpty();
    }

    [Fact]
    public void The_id_wins_over_the_name()
    {
        var p = AddProfile();
        Service().Load(new LoadRequest("Renamed", p.Id, false, [])).Ok.Should().BeTrue();
    }

    [Fact]
    public void Qa_lists_restored_and_missing_add_ins_and_a_restore_forces_a_restart()
    {
        var p = AddProfile(
            new RequiredAddin { TabName = "Kinship", AddinFile = "Kinship.addin" },
            new RequiredAddin { TabName = "Lumion", AddinFile = "Lumion.addin", Url = "https://lumion.com" },
            new RequiredAddin { TabName = "Enscape" });
        var disabledManifest = FakeAddinPasses.Manifest("KinshipTools.addin", disabled: true);
        _passes.OnClassify = req =>
        [
            new(req[0], RequiredAddinStatus.InstalledDisabled, disabledManifest),
            new(req[1], RequiredAddinStatus.NotInstalled, null),
            new(req[2], RequiredAddinStatus.InstalledActive, null),
        ];
        _passes.RestoreRequiredResult = new RestoreResult(1, 0, ["KinshipTools.addin"], []);

        var result = Service().Load(new LoadRequest("Arch", p.Id, false, []));
        result.Qa!.Restored.Should().Equal(new QaRestoredItem("Kinship", "KinshipTools.addin"));
        result.Qa.NotInstalled.Should().Equal(new QaMissingItem("Lumion", "Lumion.addin", "https://lumion.com"));
        result.Qa.AllClear.Should().BeFalse();
        result.RestartNeeded.Should().BeTrue();
        _passes.RestoredRequired.Should().ContainSingle().Which.Select(r => r.TabName).Should().Equal("Kinship");
    }

    [Fact]
    public void A_restore_that_renames_nothing_does_not_force_a_restart()
    {
        var p = AddProfile(new RequiredAddin { TabName = "Kinship" });
        _passes.OnClassify = req => [new(req[0], RequiredAddinStatus.InstalledDisabled, null)];
        var result = Service().Load(new LoadRequest("Arch", p.Id, false, []));
        result.RestartNeeded.Should().BeFalse();
        result.Qa!.Restored.Should().Equal(new QaRestoredItem("Kinship", null));
    }

    [Fact]
    public void A_qa_that_throws_still_loads()
    {
        var p = AddProfile(new RequiredAddin { TabName = "Kinship" });
        _passes.ClassifyThrows = new IOException("scan failed");
        var result = Service().Load(new LoadRequest("Arch", p.Id, false, []));
        result.Ok.Should().BeTrue();
        result.Qa!.AllClear.Should().BeTrue();
    }

    [Fact]
    public void The_disable_pass_runs_only_when_asked_and_reports_failures_and_forces_a_restart()
    {
        var p = AddProfile();
        Service().Load(new LoadRequest("Arch", p.Id, false, []));
        _passes.Calls.Should().NotContain("DisableNonRequired");

        _passes.DisableResult = new DisableResult(2, 1, 0, 1, ["A.addin", "B.addin"], ["C.addin"]);
        var result = Service().Load(new LoadRequest("Arch", p.Id, true, []));
        result.FailedDisables.Should().Equal(new FailedDisable("C.addin"));
        result.RestartNeeded.Should().BeTrue();
    }

    [Fact]
    public void A_disable_pass_that_throws_reports_the_subsystem_failure()
    {
        var p = AddProfile();
        _passes.DisableThrows = new UnauthorizedAccessException("denied");
        var result = Service().Load(new LoadRequest("Arch", p.Id, true, []));
        result.FailedDisables.Should().Equal(new FailedDisable("(disable subsystem failure)", Error: "denied"));
        result.RestartNeeded.Should().BeTrue();
    }

    [Fact]
    public void Without_a_scheduler_or_when_it_throws_revit_must_restart()
    {
        var p = AddProfile();
        Service(noScheduler: true).Load(new LoadRequest("Arch", p.Id, false, [])).RestartNeeded.Should().BeTrue();
        var failing = new FakeScheduler { Throws = new InvalidOperationException("no event") };
        var result = Service(failing).Load(new LoadRequest("Arch", p.Id, false, []));
        result.Ok.Should().BeTrue();
        result.RestartNeeded.Should().BeTrue();
    }

    [Fact]
    public async Task The_three_steps_equal_one_load_and_the_add_in_pass_can_run_on_another_thread()
    {
        var p = AddProfile();
        var service = Service();
        var start = service.Begin(new LoadRequest("Arch", p.Id, true, ["Analyze"]));
        start.Failure.Should().BeNull();
        var passes = await Task.Run(() => service.RunAddinPasses(start));
        var stepped = service.Finish(start, passes);

        var whole = Service().Load(new LoadRequest("Arch", p.Id, true, ["Analyze"]));
        stepped.Should().BeEquivalentTo(whole);
    }

    [Fact]
    public void A_failed_begin_skips_the_passes_and_finish_returns_the_failure()
    {
        var service = Service();
        var start = service.Begin(new LoadRequest("Ghost", null, true, []));
        service.RunAddinPasses(start).Should().Be(new AddinPassResult(QaCheck.Clear, false, [], false));
        service.Finish(start, new AddinPassResult(QaCheck.Clear, false, [], false)).Should().BeSameAs(start.Failure);
        _passes.Calls.Should().BeEmpty();
    }

    [Fact]
    public void Unload_blanks_the_pointer_and_schedules_the_teardown()
    {
        var p = AddProfile();
        Service().Load(new LoadRequest("Arch", p.Id, false, []));
        Service().Unload().Ok.Should().BeTrue();
        ActiveProfile.Read(_root.Paths.ActiveProfileFile).IsBlank.Should().BeTrue();
        _scheduler.Scheduled.Last().Should().BeNull();
    }

    [Fact]
    public void Unload_survives_a_failing_scheduler()
    {
        var failing = new FakeScheduler { Throws = new InvalidOperationException("x") };
        Service(failing).Unload().Ok.Should().BeTrue();
        ActiveProfile.Read(_root.Paths.ActiveProfileFile).IsBlank.Should().BeTrue();
    }
}

public sealed class AddinMaintenanceServiceTests : IDisposable
{
    private readonly TempLoaderRoot _root = new();
    private readonly FakeAddinPasses _passes = new();

    public void Dispose() => _root.Dispose();

    private AddinMaintenanceService Service() => new(_root.Paths, _passes);

    [Fact]
    public void Disable_preview_resolves_by_id_before_name()
    {
        // Two profiles share a name (Planner default 5): the id picks the right one.
        var first = TempLoaderRoot.Profile("Same");
        first.RequiredAddins = [new RequiredAddin { TabName = "First" }];
        var second = TempLoaderRoot.Profile("Same");
        second.RequiredAddins = [new RequiredAddin { TabName = "Second" }];
        _root.Add(first);
        _root.Add(second);
        IReadOnlyList<RequiredAddin>? asked = null;
        var entry = new AddinPreviewEntry("Kinship.addin", @"C:\Addins\Kinship.addin", "k.dll", "id", AddinPathKind.UserAddins);
        _passes.PreviewResult = new DisablePreview([entry], [entry with { FileName = "Other.addin" }], [], []);
        var passes = new RecordingPreview(_passes, r => asked = r);

        var result = new AddinMaintenanceService(_root.Paths, passes).Preview("Same", second.Id);
        asked!.Single().TabName.Should().Be("Second");
        result.Error.Should().BeNull();
        result.Staying.Should().Equal(new DisablePreviewItem("Kinship", "Kinship.addin", @"C:\Addins\Kinship.addin", "id", "k.dll", "UserAddins"));
        result.Disabling.Single().DisplayName.Should().Be("Other");
    }

    [Fact]
    public void Disable_preview_of_an_unknown_profile_is_an_error()
    {
        var result = Service().Preview("Ghost", null);
        result.Error.Should().Be("Profile not found: Ghost");
        result.Staying.Should().BeEmpty();
    }

    [Fact]
    public void Restore_all_reports_the_restored_files_and_whether_to_restart()
    {
        _passes.RestoreAllResult = new RestoreResult(2, 0, ["A.addin", "B.addin"], []);
        Service().RestoreAll().Should().BeEquivalentTo(new RestoreOutcome(true, null, true, ["A.addin", "B.addin"], []));

        _passes.RestoreAllResult = new RestoreResult(0, 0, [], []);
        Service().RestoreAll().RestartNeeded.Should().BeFalse();

        _passes.RestoreAllThrows = new IOException("busy");
        Service().RestoreAll().Should().BeEquivalentTo(new RestoreOutcome(false, "busy", false, [], []));
    }

    [Fact]
    public void Scan_maps_manifests_to_the_loaded_add_in_shape()
    {
        _passes.ScanResult =
        [
            FakeAddinPasses.Manifest("Kinship.addin", name: "Kinship Tools", id: "k-id"),
            new AddinManifest(@"C:\x\Bare.addin", "Bare.addin", true, []),
        ];
        var loaded = Service().ScanLoaded();
        loaded[0].Should().Be(new LoadedAddin("Kinship.addin", _passes.ScanResult[0].FilePath, false, "Kinship Tools", "k-id", "x.dll"));
        loaded[1].DisplayName.Should().Be("Bare", "no entry name: the file stem");
        loaded[1].IsDisabled.Should().BeTrue();
    }

    [Fact]
    public void User_config_is_the_empty_stub()
    {
        Service().GetUserConfig().Addins.Should().BeEmpty();
    }

    private sealed class RecordingPreview(FakeAddinPasses inner, Action<IReadOnlyList<RequiredAddin>> onPreview) : IAddinPasses
    {
        public IReadOnlyList<RequiredAddinQaResult> Classify(IReadOnlyList<RequiredAddin> required, IReadOnlyList<string> ribbonTabs) => inner.Classify(required, ribbonTabs);

        public RestoreResult RestoreRequired(IReadOnlyList<RequiredAddin> disabledRequired, Action<string, Exception> onError) => inner.RestoreRequired(disabledRequired, onError);

        public DisableResult DisableNonRequired(IReadOnlyList<RequiredAddin> required, Action<string, Exception> onError) => inner.DisableNonRequired(required, onError);

        public DisablePreview Preview(IReadOnlyList<RequiredAddin> required)
        {
            onPreview(required);
            return inner.Preview(required);
        }

        public RestoreResult RestoreAll(Action<string, Exception> onError) => inner.RestoreAll(onError);

        public IReadOnlyList<AddinManifest> Scan(Action<string, Exception> onSkip) => inner.Scan(onSkip);
    }
}

public sealed class AddinLookupServiceTests : IDisposable
{
    private readonly TempLoaderRoot _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public void The_shipped_lookup_parses_and_the_raw_text_passes_through()
    {
        var shipped = Path.Combine(RepoRoot(), "src", "RST.UI", "Assets", "lookup", "addin_lookup.json");
        var service = new AddinLookupService(shipped);
        service.ReadRawJson().Should().Be(File.ReadAllText(shipped));
        var lookup = service.Read();
        lookup.Should().ContainKey("Kinship");
        lookup["Lumion"].Should().Be(new AddinLookupEntry("Lumion LiveSync", "Lumion.addin", "https://lumion.com"));
    }

    [Fact]
    public void A_missing_or_broken_file_gives_an_empty_registry()
    {
        new AddinLookupService(Path.Combine(_root.Root, "none.json")).ReadRawJson().Should().Be("{}");
        var broken = Path.Combine(_root.Root, "broken.json");
        File.WriteAllText(broken, "[1,2]");
        new AddinLookupService(broken).Read().Should().BeEmpty();
    }

    internal static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "RST-C.sln"))) return dir.FullName;
        throw new InvalidOperationException("RST-C.sln not found");
    }
}
