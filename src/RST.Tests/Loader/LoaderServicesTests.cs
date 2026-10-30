// rst-c's own (not vendored): the Loader services extracted from LoaderBridge (spec #16, WU3):
// prefs (read, migration, write, opacity clamp), the library (list, active, import, delete), the
// load orchestration (the confirmed add-in plan, schedule, restart), the ribbon-only reload and
// unload, the required-add-in check, and the add-in maintenance calls (plan by id, restore all, migration).

using System.IO;
using FluentAssertions;
using RST.Core.Activation;
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
    private readonly FakeAddinSwitching _switching = new();
    private readonly FakeScheduler _scheduler = new();

    public void Dispose() => _root.Dispose();

    private ProfileLoadService Service(IProfileSwitchScheduler? scheduler = null, bool noScheduler = false) =>
        new(_root.Paths, noScheduler ? null : scheduler ?? _scheduler, _switching, clock: () => Now);

    private Profile AddProfile(params RequiredAddin[] required)
    {
        var p = TempLoaderRoot.Profile("Arch", tab: "Studio");
        p.RequiredAddins = [.. required];
        _root.Add(p);
        return p;
    }

    private ActivationPlan PlanFor(Profile p, bool disable) => _switching.Plan(_switching.InventoryResult, ActivationPolicy.Load(p.RequiredAddins, disable));

    [Fact]
    public void Load_writes_the_active_profile_and_prefs_and_schedules_the_switch()
    {
        var p = AddProfile();
        var result = Service().Load(new LoadRequest("Arch", p.Id, false, ["Analyze"]));

        result.Ok.Should().BeTrue();
        result.RestartNeeded.Should().BeFalse("the live switch is scheduled and no add-in changed");
        result.AddinChanges.Should().BeEmpty();
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
        var result = Service().Load(new LoadRequest("Ghost", null, false, [], ActivationPlan.Nothing));
        result.Should().BeEquivalentTo(LoadResult.Fail("Profile not found: Ghost"));
        File.Exists(_root.Paths.ActiveProfileFile).Should().BeFalse();
        _switching.Calls.Should().BeEmpty();
        _scheduler.Scheduled.Should().BeEmpty();
    }

    [Fact]
    public void The_id_wins_over_the_name()
    {
        var p = AddProfile();
        Service().Load(new LoadRequest("Renamed", p.Id, false, [])).Ok.Should().BeTrue();
    }

    [Fact]
    public void Load_runs_exactly_the_confirmed_plan_and_any_change_forces_a_restart()
    {
        _switching.InventoryResult = FakeAddinSwitching.InventoryOf(
            FakeAddinSwitching.Item(2026, "Kinship"), FakeAddinSwitching.Item(2026, "Twinmotion"), FakeAddinSwitching.Item(2027, "Twinmotion"));
        var p = AddProfile(new RequiredAddin { TabName = "Kinship", AddinFile = "Kinship.addin" });
        var plan = PlanFor(p, disable: true);

        var result = Service().Load(new LoadRequest("Arch", p.Id, true, [], plan));

        _switching.Executed.Should().ContainSingle().Which.Should().BeSameAs(plan);
        result.AddinChanges.Select(o => (o.Name, o.Year, o.Action, o.Result)).Should().Equal(
            ("Twinmotion", 2026, PlanAction.Disable, OutcomeResult.Done),
            ("Twinmotion", 2027, PlanAction.Disable, OutcomeResult.Done));
        result.RestartNeeded.Should().BeTrue("add-in changes apply at the next start");
        result.Report.Intent.Should().Be(ActivationIntent.Load);
    }

    [Fact]
    public void A_request_without_a_plan_or_a_plan_without_changes_touches_no_add_in()
    {
        _switching.InventoryResult = FakeAddinSwitching.InventoryOf(FakeAddinSwitching.Item(2026, "Kinship", off: true));
        var p = AddProfile();
        Service().Load(new LoadRequest("Arch", p.Id, true, []));
        Service().Load(new LoadRequest("Arch", p.Id, true, [], PlanFor(p, disable: true)));
        _switching.Executed.Should().BeEmpty();
    }

    [Fact]
    public void An_executor_that_throws_reports_every_change_as_not_changed()
    {
        _switching.InventoryResult = FakeAddinSwitching.InventoryOf(FakeAddinSwitching.Item(2026, "Twinmotion"));
        _switching.ExecuteThrows = new IOException("disk gone");
        var p = AddProfile();
        var result = Service().Load(new LoadRequest("Arch", p.Id, true, [], PlanFor(p, disable: true)));
        result.Ok.Should().BeTrue();
        result.AddinChanges.Should().ContainSingle().Which.Should().Match<ActivationOutcome>(o =>
            o.Result == OutcomeResult.NotChanged && o.Reason == ChangeReason.Other && o.Detail == "disk gone");
    }

    [Fact]
    public void Reload_is_ribbon_only_even_with_a_plan()
    {
        _switching.InventoryResult = FakeAddinSwitching.InventoryOf(FakeAddinSwitching.Item(2026, "Twinmotion"));
        var p = AddProfile();
        var result = Service().Reload(new LoadRequest("Arch", p.Id, true, ["Analyze"], PlanFor(p, disable: true)));
        result.Ok.Should().BeTrue();
        result.AddinChanges.Should().BeEmpty();
        _switching.Executed.Should().BeEmpty();
        _scheduler.Scheduled.Should().ContainSingle();
        ActiveProfile.Read(_root.Paths.ActiveProfileFile).DisableNonRequired.Should().BeTrue("the flag is kept for the next Load Profile");
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
        _switching.InventoryResult = FakeAddinSwitching.InventoryOf(FakeAddinSwitching.Item(2026, "Twinmotion"));
        var p = AddProfile();
        var plan = PlanFor(p, disable: true);
        var service = Service();
        var start = service.Begin(new LoadRequest("Arch", p.Id, true, ["Analyze"], plan));
        start.Failure.Should().BeNull();
        var passes = await Task.Run(() => service.RunAddinPasses(start));
        _switching.ExecuteThreadId.Should().NotBe(Environment.CurrentManagedThreadId);
        var stepped = service.Finish(start, passes);

        var whole = Service().Load(new LoadRequest("Arch", p.Id, true, ["Analyze"], plan));
        stepped.Should().BeEquivalentTo(whole);
    }

    [Fact]
    public void A_failed_begin_skips_the_passes_and_finish_returns_the_failure()
    {
        var service = Service();
        var start = service.Begin(new LoadRequest("Ghost", null, true, [], ActivationPlan.Nothing));
        service.RunAddinPasses(start).Should().BeSameAs(AddinPassResult.None);
        service.Finish(start, AddinPassResult.None).Should().BeSameAs(start.Failure);
        _switching.Calls.Should().BeEmpty();
    }

    [Fact]
    public void Unload_blanks_the_pointer_schedules_the_teardown_and_switches_back_on_what_rst_c_switched_off()
    {
        var mine = FakeAddinSwitching.Item(2026, "Twinmotion", off: true);
        var users = FakeAddinSwitching.Item(2026, "Lumion", off: true);
        _switching.InventoryResult = FakeAddinSwitching.InventoryOf(mine, users);
        _switching.Ledger.Add(new LedgerEntry(2026, mine.AddinIds[0], "Twinmotion", SwitchMechanism.SettingsFile, mine.ManifestPath, DateTime.UtcNow));
        var p = AddProfile();
        Service().Load(new LoadRequest("Arch", p.Id, false, []));

        var result = Service().Unload();

        result.Ok.Should().BeTrue();
        ActiveProfile.Read(_root.Paths.ActiveProfileFile).IsBlank.Should().BeTrue();
        _scheduler.Scheduled.Last().Should().BeNull();
        _switching.Policies.Last().Intent.Should().Be(ActivationIntent.Unload);
        result.Addins.Outcomes.Should().ContainSingle().Which.Should().Match<ActivationOutcome>(o =>
            o.Name == "Twinmotion" && o.Action == PlanAction.Enable && o.Result == OutcomeResult.Done);
    }

    [Fact]
    public void Unload_survives_a_failing_scheduler_and_a_failing_scan()
    {
        var failing = new FakeScheduler { Throws = new InvalidOperationException("x") };
        _switching.InventoryThrows = new IOException("scan");
        var result = Service(failing).Unload();
        result.Ok.Should().BeTrue();
        result.Addins.Notes.Should().ContainSingle().Which.Should().Contain("scan");
        ActiveProfile.Read(_root.Paths.ActiveProfileFile).IsBlank.Should().BeTrue();
    }
}

public sealed class QaCheckTests
{
    [Fact]
    public void The_check_lists_missing_add_ins_of_the_running_year_and_required_add_ins_left_off()
    {
        var kinship = FakeAddinSwitching.Item(2026, "Kinship", off: true);
        var inventory = FakeAddinSwitching.InventoryOf(kinship, FakeAddinSwitching.Item(2027, "Lumion"));
        var profile = TempLoaderRoot.Profile("Arch");
        profile.RequiredAddins =
        [
            new RequiredAddin { TabName = "Architecture" },
            new RequiredAddin { TabName = "Kinship", AddinFile = "Kinship.addin" },
            new RequiredAddin { TabName = "Lumion", AddinFile = "Lumion.addin" },
            new RequiredAddin { TabName = "Enscape" },
        ];
        var lookup = new Dictionary<string, AddinLookupEntry> { ["Lumion"] = new("Lumion LiveSync", "Lumion.addin", "https://lumion.com") };
        var session = AddinSessionContext.For("2026", inventory, ["Enscape"], lookup);
        var plan = ActivationPlanner.Plan(inventory, ActivationPolicy.Load(profile.RequiredAddins, false), new ActivationLedger());

        var qa = QaCheck.For(profile, session, plan);

        qa.NotInstalled.Should().Equal(new QaMissingItem("Lumion", "Lumion.addin", "https://lumion.com"));
        qa.RequiredOff.Should().ContainSingle().Which.Should().StartWith("Kinship (2026) is required by this profile, but it is switched off in Autodesk's Add-Ins Manager");
        qa.AllClear.Should().BeFalse();
        QaCheck.Clear.AllClear.Should().BeTrue();
    }
}

public sealed class AddinMaintenanceServiceTests : IDisposable
{
    private readonly TempLoaderRoot _root = new();
    private readonly FakeAddinSwitching _switching = new();

    public void Dispose() => _root.Dispose();

    private AddinMaintenanceService Service() => new(_root.Paths, _switching);

    [Fact]
    public void The_load_plan_resolves_the_profile_by_id_before_name()
    {
        // Two profiles share a name (Planner default 5): the id picks the right one.
        var first = TempLoaderRoot.Profile("Same");
        first.RequiredAddins = [new RequiredAddin { TabName = "First" }];
        var second = TempLoaderRoot.Profile("Same");
        second.RequiredAddins = [new RequiredAddin { TabName = "Second" }];
        _root.Add(first);
        _root.Add(second);
        _switching.InventoryResult = FakeAddinSwitching.InventoryOf(FakeAddinSwitching.Item(2026, "Second"), FakeAddinSwitching.Item(2026, "Other"));

        var result = Service().PlanLoad("Same", second.Id, disableUnused: true);

        result.Error.Should().BeNull();
        _switching.Policies.Single().Required.Single().TabName.Should().Be("Second");
        result.Plan!.Changes.Select(e => e.Name).Should().Equal("Other");
    }

    [Fact]
    public void The_load_plan_of_an_unknown_profile_is_an_error()
    {
        var result = Service().PlanLoad("Ghost", null, true);
        result.Error.Should().Be("Profile not found: Ghost");
        result.Plan.Should().BeNull();
    }

    [Fact]
    public void Restore_all_switches_back_on_what_rst_c_switched_off_and_reports_it()
    {
        var mine = FakeAddinSwitching.Item(2027, "Twinmotion", off: true);
        _switching.InventoryResult = FakeAddinSwitching.InventoryOf(mine, FakeAddinSwitching.Item(2027, "Lumion", off: true));
        _switching.Ledger.Add(new LedgerEntry(2027, mine.AddinIds[0], "Twinmotion", SwitchMechanism.SettingsFile, mine.ManifestPath, DateTime.UtcNow));

        var result = Service().RestoreAll();

        result.Ok.Should().BeTrue();
        result.RestartNeeded.Should().BeTrue();
        result.Addins!.Outcomes.Select(o => (o.Name, o.Action)).Should().Equal(("Twinmotion", PlanAction.Enable));

        _switching.InventoryResult = FakeAddinSwitching.InventoryOf();
        Service().RestoreAll().RestartNeeded.Should().BeFalse();

        _switching.ExecuteThrows = new IOException("busy");
        _switching.InventoryResult = FakeAddinSwitching.InventoryOf(mine);
        Service().RestoreAll().Should().BeEquivalentTo(new RestoreOutcome(false, "busy", null));
    }

    [Fact]
    public void The_inventory_falls_back_to_empty_and_the_migration_reports_only_when_something_moved()
    {
        _switching.InventoryThrows = new IOException("x");
        Service().Inventory().Should().BeSameAs(AddinInventory.Empty);
        Service().MigrateLegacy().Should().BeNull();

        _switching.InventoryThrows = null;
        _switching.InventoryResult = FakeAddinSwitching.InventoryOf(FakeAddinSwitching.Item(2026, "Old", renamed: true));
        var report = Service().MigrateLegacy();
        report!.Intent.Should().Be(ActivationIntent.MigrateLegacy);
        report.Outcomes.Should().ContainSingle().Which.Migrated.Should().BeTrue();
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
