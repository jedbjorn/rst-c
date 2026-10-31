// rst-c's own (not vendored): the Profile Loader view-model's flows (spec #16, WU3, AC5) with a
// fake backend, a manual clock and scripted dialogs: boot, select, adopt presets, opacity
// override, import, load (the add-in plan, confirm, the report, Loading … and close, ok:false,
// throws), unload, delete (incl. while loaded), restore all, the RST 3.0 move and close. Message
// texts are checked against profile_loader.html's; the add-in parts follow spec #18 (AC4).
// SC-016 and SC-048 F6: selection gates, command refresh, and no-op loads without modals.

using System.IO;
using System.Windows.Controls;
using System.Windows.Threading;
using RST.RuntimeTests.Support;
using RST.Tests.Loader;
using FluentAssertions;
using RST.Core.Activation;
using RST.Core.Configuration;
using RST.Core.Loader;
using RST.Core.Profiles;
using RST.UI.Controls;
using RST.UI.Loader;
using Xunit;

namespace RST.RuntimeTests.Loader;

public class ProfileLoaderViewModelTests
{
    private readonly ManualClock _clock = new();
    private readonly LoaderTestDialogs _dialogs = new();
    private readonly List<string> _opened = [];
    private int _closed;

    private ProfileLoaderViewModel Model(IProfileLoaderBackend backend, bool initialize = true)
    {
        var vm = new ProfileLoaderViewModel(backend, _dialogs, _dialogs, _clock.Schedule, url => _opened.Add(url));
        vm.CloseRequested += () => _closed++;
        if (initialize) vm.Initialize();
        return vm;
    }

    private static ProfileCardViewModel Card(ProfileLoaderViewModel vm, string name) => vm.Profiles.Single(c => c.Name == name);

    private void AssertToast(ProfileLoaderViewModel vm, string title, string text, BannerKind kind)
    {
        vm.Toast.IsOpen.Should().BeTrue();
        vm.Toast.Title.Should().Be(title);
        vm.Toast.Text.Should().Be(text);
        vm.Toast.Kind.Should().Be(kind);
        _clock.PendingDelays.Should().Contain(TransientMessage.InformDuration);
    }

    [Fact]
    public void Load_and_delete_need_a_selection_and_idle_state()
    {
        var vm = Model(new FakeLoaderBackend().With(LoaderFixtures.Mep()));
        vm.LoadCommand.CanExecute(null).Should().BeFalse();
        vm.DeleteCommand.CanExecute(null).Should().BeFalse();
        vm.UnloadCommand.CanExecute(null).Should().BeTrue();

        vm.SelectedCard = vm.Profiles.Single();
        vm.LoadCommand.CanExecute(null).Should().BeTrue();
        vm.DeleteCommand.CanExecute(null).Should().BeTrue();
        vm.Busy.Show("Working");
        vm.LoadCommand.CanExecute(null).Should().BeFalse();
        vm.DeleteCommand.CanExecute(null).Should().BeFalse();
        vm.Busy.Hide();
        vm.SelectedCard = null;
        vm.LoadCommand.CanExecute(null).Should().BeFalse();
        vm.DeleteCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void Selection_refreshes_bound_buttons_without_an_input_event() => Screenshots.RunSta(() =>
    {
        try
        {
            var vm = Model(new FakeLoaderBackend().With(LoaderFixtures.Mep()));
            var load = new Button { Command = vm.LoadCommand };
            var delete = new Button { Command = vm.DeleteCommand };
            void DrainRequery() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            DrainRequery();
            load.IsEnabled.Should().BeFalse();
            delete.IsEnabled.Should().BeFalse();
            vm.SelectedCard = vm.Profiles.Single();
            DrainRequery();
            load.IsEnabled.Should().BeTrue();
            delete.IsEnabled.Should().BeTrue();
            vm.SelectedCard = null;
            DrainRequery();
            load.IsEnabled.Should().BeFalse();
            delete.IsEnabled.Should().BeFalse();
        }
        finally
        {
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    });

    [Fact]
    public void Reopening_after_unload_disables_load_and_delete()
    {
        var profile = LoaderFixtures.Mep();
        var backend = new FakeLoaderBackend().With(profile).ActiveIs(profile);
        backend.OnUnload = () =>
        {
            backend.Active = new ActiveProfile { Blank = true };
            return new UnloadOutcome(true, null, ActivationReport.Empty(ActivationIntent.Unload));
        };
        var vm = Model(backend);
        vm.UnloadCommand.Execute(null);
        backend.Calls.Should().Contain("Unload");
        _clock.Advance(ProfileLoaderViewModel.CloseDelay);
        var reopened = Model(backend);
        reopened.SelectedCard.Should().BeNull();
        reopened.LoadCommand.CanExecute(null).Should().BeFalse();
        reopened.DeleteCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task No_change_with_disable_on_skips_both_dialogs_and_reports_in_the_closing_summary()
    {
        var vm = Model(new FakeLoaderBackend().With(LoaderFixtures.Mep()));
        vm.SelectedCard = vm.Profiles.Single();
        vm.DisableUnused = true;
        await vm.LoadAsync();
        _dialogs.Log.Should().BeEmpty();
        vm.Busy.Message.Should().Contain("No add-in changes.");
        _clock.PendingDelays.Should().Contain(ProfileLoaderViewModel.CloseDelay);
    }

    [Fact]
    public async Task No_change_with_a_missing_requirement_skips_confirm_but_keeps_the_report()
    {
        var profile = LoaderFixtures.Mep();
        profile.RequiredAddins = [new RequiredAddin { TabName = "Missing", AddinFile = "Missing.addin" }];
        var vm = Model(new FakeLoaderBackend().With(profile));
        vm.SelectedCard = vm.Profiles.Single();
        vm.DisableUnused = true;
        await vm.LoadAsync();
        _dialogs.AddinChanges.Should().BeEmpty();
        _dialogs.AddinReports.Single().Missing.Should().ContainSingle().Which.Text.Should().Be("Missing");
        vm.Busy.Message.Should().NotContain("No add-in changes.");
    }

    [Fact]
    public async Task Locked_only_changes_skip_confirm_but_are_reported_after_loading()
    {
        var backend = new FakeLoaderBackend
        {
            Inventory = FakeAddinSwitching.InventoryOf(FakeAddinSwitching.Item(2026, "Probe", adminLock: AdminLock.AlwaysEnabled)),
        }.With(LoaderFixtures.Mep());
        var vm = Model(backend);
        vm.SelectedCard = vm.Profiles.Single();
        vm.DisableUnused = true;
        await vm.LoadAsync();
        _dialogs.AddinChanges.Should().BeEmpty();
        backend.LoadRequests.Should().ContainSingle();
        _dialogs.AddinReports.Single().NotChanged.Should().Equal("Probe (2026): locked on by your administrator");
    }

    // ── boot ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Boot_reads_everything_then_selects_the_active_profile()
    {
        var backend = LoaderFixtures.Backend();
        var vm = Model(backend, initialize: false);
        vm.LoadData();
        vm.Profiles.Select(c => c.Name).Should().Equal("Architecture", "Structure", "MEP Coordination");
        vm.ProfileCount.Should().Be(3);
        vm.RevitVersionDisplay.Should().Be("Revit 2026");
        Card(vm, "Architecture").IsLoaded.Should().BeTrue();
        Card(vm, "Structure").IsLoaded.Should().BeFalse();
        vm.SelectedCard.Should().BeNull("nothing is selected until the window shows");

        vm.SelectActiveProfile();
        vm.SelectedCard.Should().BeSameAs(Card(vm, "Architecture"));
        // Planner default 6: the tabs and prefs were in, so the RSTify list and slider are right.
        vm.TabToggles.Should().Contain(t => t.Tab == "Analyze" && t.IsHidden);
        vm.OpacityOverride.Should().Be(70);
        backend.Calls.Should().BeEquivalentTo(["GetAddinLookup", "GetAddinInventory", "GetPrefs", "GetProfiles", "GetActiveProfile", "MigrateLegacy"]);
    }

    [Fact]
    public void No_revit_version_shows_not_detected_and_no_active_profile_selects_nothing()
    {
        var backend = new FakeLoaderBackend { RevitVersion = "" }.With(LoaderFixtures.Mep());
        var vm = Model(backend);
        vm.RevitVersionDisplay.Should().Be("Not detected");
        vm.SelectedCard.Should().BeNull();
        vm.HasSelection.Should().BeFalse();
        vm.DetailName.Should().Be("—");
    }

    [Fact]
    public void An_empty_library_shows_the_empty_state()
    {
        var vm = Model(new FakeLoaderBackend());
        vm.HasProfiles.Should().BeFalse();
        vm.ProfileCount.Should().Be(0);
    }

    [Fact]
    public void A_failing_boot_read_falls_back_instead_of_throwing()
    {
        var backend = new ThrowingProfilesBackend();
        var vm = Model(backend);
        vm.HasProfiles.Should().BeFalse();
    }

    // ── select ───────────────────────────────────────────────────────────────

    [Fact]
    public void Selecting_a_card_fills_the_detail_pane()
    {
        var vm = Model(LoaderFixtures.Backend());
        vm.SelectedCard = Card(vm, "Structure");

        vm.DetailName.Should().Be("Structure");
        vm.IsSelectionLoaded.Should().BeFalse();
        vm.DetailTab.Should().Be("Studio Struct");
        vm.DetailMinRevit.Should().Be("2.1.0+");
        vm.DetailUpdated.Should().Be("2026-08-02");
        vm.SelectedProfile!.Id.Should().Be(LoaderFixtures.StructId);
        vm.OpacityOverride.Should().Be(60, "no saved override: the profile's opacity");
        vm.PreviewOpacity.Should().Be(60);
        vm.DisableUnused.Should().BeTrue("from Structure's saved prefs");

        // RSTify: core, source (Structure, Kinship Tools) and own tabs are not offered.
        vm.TabToggles.Select(t => t.Tab).Should().Equal(
            "Architecture", "Systems", "Insert", "Analyze", "Massing & Site", "Collaborate", "Enscape", "Studio Arch");
        vm.TabToggles.Where(t => t.IsHidden).Select(t => t.Tab).Should().Equal("Massing & Site");
        vm.SelectedHiddenTabs.Should().Equal("Massing & Site");

        // Required add-ins in every state (spec #18, In Scope 7): from the running year's inventory.
        vm.RequiredAddins.Select(r => (r.TabName, r.StatusLabel)).Should().Equal(
            ("Structure", "Native"), ("Kinship", "Loaded"), ("pyRevit", "Loaded"), ("Lumion", "Disabled"), ("Guardian", "Locked"), ("Twinmotion", "Not Found"));
        var pyrevit = vm.RequiredAddins.Single(r => r.TabName == "pyRevit");
        pyrevit.StatusTooltip.Should().Be("Installed and on for Revit 2026 (PyRevitLoader)");
        pyrevit.HasInfoUrl.Should().BeTrue();
        vm.RequiredAddins.Single(r => r.TabName == "Kinship").StatusTooltip.Should().Be("Loaded in current Revit session");
        vm.RequiredAddins.Single(r => r.TabName == "Lumion").StatusTooltip.Should().StartWith("Lumion LiveSync is switched off for Revit 2026.");
        vm.RequiredAddins.Single(r => r.TabName == "Guardian").StatusTooltip.Should().Be("Guardian is locked off by your administrator for Revit 2026.");
        vm.RequiredAddins.Single(r => r.TabName == "Twinmotion").StatusTooltip.Should().Be("Click to download");
        vm.RequiredAddins.Single(r => r.TabName == "Structure").FileColumn.Should().Be("Revit Built-in");
    }

    [Fact]
    public void A_profile_without_required_add_ins_shows_none_and_without_tabs_shows_the_empty_state()
    {
        var backend = LoaderFixtures.Backend();
        backend.AllTabs = ["Modify", "Systems", "Studio MEP"];
        var vm = Model(backend);
        vm.SelectedCard = Card(vm, "MEP Coordination");
        vm.RequiredAddinsEmpty.Should().BeTrue();
        vm.TabToggles.Should().BeEmpty();
        vm.TabTogglesEmpty.Should().BeTrue("Systems is a source tab, Studio MEP its own, Modify a core tab");
    }

    [Fact]
    public void No_captured_ribbon_tabs_leaves_the_rstify_list_blank_not_empty()
    {
        var backend = LoaderFixtures.Backend();
        backend.AllTabs = [];
        var vm = Model(backend);
        vm.TabToggles.Should().BeEmpty();
        vm.TabTogglesEmpty.Should().BeFalse();
    }

    [Fact]
    public void The_loaded_badge_goes_by_id_not_name()
    {
        // Planner default 5: a second profile with the active profile's name is not "Loaded".
        var twin = LoaderFixtures.Architecture();
        twin.Id = "99999999-0000-0000-0000-000000000000";
        var backend = LoaderFixtures.Backend().With(twin);
        var vm = Model(backend);
        var cards = vm.Profiles.Where(c => c.Name == "Architecture").ToList();
        cards.Should().HaveCount(2);
        cards.Count(c => c.IsLoaded).Should().Be(1);
        vm.SelectedCard = cards.Single(c => c.Id == twin.Id);
        vm.IsSelectionLoaded.Should().BeFalse();
        vm.SelectedCard = cards.Single(c => c.Id == LoaderFixtures.ArchId);
        vm.IsSelectionLoaded.Should().BeTrue();
    }

    [Fact]
    public void Without_an_active_id_the_name_decides_as_before()
    {
        var backend = LoaderFixtures.Backend();
        backend.Active = new ActiveProfile { ProfileName = "Structure", ProfileFile = "Structure.json" };
        var vm = Model(backend);
        Card(vm, "Structure").IsLoaded.Should().BeTrue();
        vm.SelectedCard.Should().BeSameAs(Card(vm, "Structure"));
    }

    // ── adopt presets ────────────────────────────────────────────────────────

    [Fact]
    public void Adopting_presets_saves_them_and_switches_the_list()
    {
        var backend = LoaderFixtures.Backend();
        backend.Prefs.Profiles.Remove(LoaderFixtures.StructId);
        var vm = Model(backend);
        _dialogs.ConfirmAnswers.Enqueue(true);
        vm.SelectedCard = Card(vm, "Structure");

        _dialogs.Confirms.Should().ContainSingle();
        var (title, message, confirm, danger, id) = _dialogs.Confirms[0];
        title.Should().Be("Adopt RSTify Presets?");
        message.Should().Contain("Admin defaults: 2 tabs to hide, disable unused add-ins on.");
        message.Should().EndWith("Adopt these defaults for \"Structure\"? You can change them anytime.");
        confirm.Should().Be("Continue");
        danger.Should().BeFalse();
        id.Should().Be(ProfileLoaderViewModel.AdoptPresetsDialogId);

        var write = backend.PrefsWrites.Should().ContainSingle().Which;
        write.Id.Should().Be(LoaderFixtures.StructId);
        write.Hidden.Should().Equal("Massing & Site", "Collaborate");
        write.Disable.Should().BeTrue();
        write.Adopted.Should().BeTrue();
        vm.SelectedHiddenTabs.Should().Equal("Massing & Site", "Collaborate");
        vm.DisableUnused.Should().BeTrue();

        // Answered once: picking the card again does not ask.
        vm.SelectedCard = Card(vm, "Architecture");
        vm.SelectedCard = Card(vm, "Structure");
        _dialogs.Confirms.Should().HaveCount(1);
        vm.SelectedHiddenTabs.Should().Equal("Massing & Site", "Collaborate");
    }

    [Fact]
    public void Declining_presets_saves_empty_defaults_with_the_answer_recorded()
    {
        var backend = LoaderFixtures.Backend();
        backend.Prefs.Profiles.Remove(LoaderFixtures.StructId);
        var vm = Model(backend);
        _dialogs.ConfirmAnswers.Enqueue(false);
        vm.SelectedCard = Card(vm, "Structure");

        var write = backend.PrefsWrites.Should().ContainSingle().Which;
        write.Id.Should().Be(LoaderFixtures.StructId);
        write.Hidden.Should().BeEmpty();
        write.Disable.Should().BeFalse();
        write.Adopted.Should().BeTrue("the answer is recorded either way");
        vm.SelectedHiddenTabs.Should().BeEmpty();
        vm.DisableUnused.Should().BeFalse();
    }

    [Fact]
    public void The_active_profiles_prompt_fires_at_boot_once_the_window_shows()
    {
        var backend = LoaderFixtures.Backend();
        backend.Prefs.Profiles.Clear();
        var vm = Model(backend, initialize: false);
        vm.LoadData();
        _dialogs.Confirms.Should().BeEmpty();
        _dialogs.ConfirmAnswers.Enqueue(true);
        vm.SelectActiveProfile();
        _dialogs.Confirms.Should().ContainSingle().Which.Title.Should().Be("Adopt RSTify Presets?");
        vm.SelectedHiddenTabs.Should().Equal("Analyze");
    }

    [Fact]
    public void A_profile_without_presets_or_id_never_asks()
    {
        var noId = LoaderFixtures.Structure();
        noId.Id = null;
        noId.ProfileName = "No Id";
        var backend = LoaderFixtures.Backend().With(noId);
        var vm = Model(backend);
        vm.SelectedCard = Card(vm, "MEP Coordination");
        vm.SelectedCard = Card(vm, "No Id");
        _dialogs.Confirms.Should().BeEmpty();
    }

    // ── opacity override ─────────────────────────────────────────────────────

    [Fact]
    public void The_opacity_slider_previews_live_and_saves_on_release_only_when_changed()
    {
        var backend = LoaderFixtures.Backend();
        var vm = Model(backend);
        vm.OpacityOverride = 40;
        vm.PreviewOpacity.Should().Be(40);
        backend.OpacityWrites.Should().BeEmpty("dragging only previews");

        vm.CommitOpacityOverride();
        backend.OpacityWrites.Should().Equal((LoaderFixtures.ArchId, (int?)40));
        vm.CommitOpacityOverride();
        backend.OpacityWrites.Should().HaveCount(1, "the same value is not saved twice");

        // The saved override seeds the slider next time the card is picked.
        vm.SelectedCard = Card(vm, "Structure");
        vm.SelectedCard = Card(vm, "Architecture");
        vm.OpacityOverride.Should().Be(40);

        vm.OpacityOverride = 300;
        vm.OpacityOverride.Should().Be(100);
        vm.OpacityOverride = 2;
        vm.OpacityOverride.Should().Be(10);
    }

    [Fact]
    public void Selecting_a_card_never_saves_the_seeded_opacity()
    {
        var backend = LoaderFixtures.Backend();
        var vm = Model(backend);
        vm.SelectedCard = Card(vm, "Structure");
        vm.CommitOpacityOverride();
        backend.OpacityWrites.Should().BeEmpty();
    }

    // ── import ───────────────────────────────────────────────────────────────

    [Fact]
    public void Import_opens_the_zip_dialog_and_a_cancel_does_nothing()
    {
        var backend = LoaderFixtures.Backend();
        var vm = Model(backend);
        vm.ImportCommand.Execute(null);
        _dialogs.Opens.Should().Equal(("Import RST Profile (.zip)", "RST Profile Package (*.zip)|*.zip|All Files (*.*)|*.*"));
        backend.Calls.Should().NotContain("Import");
        vm.Toast.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void Import_appends_a_new_profile_and_replaces_one_with_the_same_id()
    {
        var backend = LoaderFixtures.Backend();
        var vm = Model(backend);
        _dialogs.OpenResult = @"C:\in\x.zip";
        var fresh = TempProfile("Landscape", "aaaaaaaa-0000-0000-0000-000000000001");
        backend.OnImport = _ => new ImportOutcome(true, null, "Landscape_x.json", fresh);
        vm.ImportCommand.Execute(null);
        vm.Profiles.Select(c => c.Name).Should().EndWith("Landscape");
        vm.ProfileCount.Should().Be(4);

        var updated = LoaderFixtures.Structure();
        updated.Tab = "Struct v2";
        backend.OnImport = _ => new ImportOutcome(true, null, "Structure_new.json", updated);
        vm.ImportCommand.Execute(null);
        vm.ProfileCount.Should().Be(4);
        Card(vm, "Structure").Tab.Should().Be("Struct v2");
    }

    [Fact]
    public void Import_failures_show_import_failed()
    {
        var backend = LoaderFixtures.Backend();
        var vm = Model(backend);
        _dialogs.OpenResult = @"C:\in\bad.zip";
        backend.OnImport = _ => ImportOutcome.Fail("Not a valid RST profile package: no profile.json");
        vm.ImportCommand.Execute(null);
        AssertToast(vm, "Import Failed", "Not a valid RST profile package: no profile.json", BannerKind.Error);

        backend.OnImport = _ => throw new IOException("disk gone");
        vm.ImportCommand.Execute(null);
        AssertToast(vm, "Import Failed", "Error importing profile: disk gone", BannerKind.Error);
        vm.ProfileCount.Should().Be(3);
    }

    // ── load ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Load_sends_the_selection_shows_the_check_then_loading_for_3s_then_closes()
    {
        var backend = LoaderFixtures.Backend();
        backend.OnLoad = _ => Task.FromResult(new LoadResult(true, [], true, [],
            new QaCheck([new("Twinmotion", null, "https://twinmotion.com")], [])));
        var vm = Model(backend);
        vm.SelectedCard = Card(vm, "Structure");
        vm.DisableUnused = false;
        vm.TabToggles.Single(t => t.Tab == "Analyze").IsHidden = true;

        await vm.LoadAsync();

        backend.LoadRequests.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new LoadRequest("Structure", LoaderFixtures.StructId, false, ["Analyze", "Massing & Site"], backend.Plans.Single()));
        backend.Plans.Single().HasChanges.Should().BeFalse("Disable off and nothing rst-c switched off: no confirm");
        _dialogs.Log.Should().Equal("AddinReport:Add-in Check");
        var check = _dialogs.AddinReports.Single();
        check.Missing.Should().Equal(new AddinCheckLine("Twinmotion", "https://twinmotion.com"));
        check.IsEmpty.Should().BeFalse();
        check.NextStartNote.Should().Be("Add-in changes apply the next time Revit starts.");

        vm.Busy.IsBusy.Should().BeTrue();
        vm.Busy.Title.Should().Be("Loading Structure");
        vm.Busy.Message.Should().Be(
            "Add-in changes apply the next time Revit starts. Tab \"Studio Struct\" will be built. "
            + "Twinmotion not found. If you experience issues with using tools from this add-in please install for Revit 2026 and retry.");
        vm.IsIdle.Should().BeFalse();
        vm.LoadCommand.CanExecute(null).Should().BeFalse();
        Card(vm, "Structure").IsLoaded.Should().BeTrue();
        Card(vm, "Architecture").IsLoaded.Should().BeFalse();

        _clock.Advance(TimeSpan.FromSeconds(2.9));
        _closed.Should().Be(0);
        _clock.Advance(TimeSpan.FromSeconds(0.1));
        _closed.Should().Be(1);
        vm.Busy.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task Load_with_nothing_selected_does_nothing()
    {
        var backend = new FakeLoaderBackend().With(LoaderFixtures.Mep());
        var vm = Model(backend);
        await vm.LoadAsync();
        backend.Calls.Should().NotContain("Load");
        _dialogs.Log.Should().BeEmpty();
    }

    [Fact]
    public async Task Load_with_disable_on_confirms_the_plan_per_year_and_runs_exactly_that_plan()
    {
        var backend = LoaderFixtures.Backend();
        (string Name, string? Id, bool Disable)? asked = null;
        backend.OnPlan = (name, id, disable) =>
        {
            asked = (name, id, disable);
            backend.OnPlan = null;
            return backend.PlanLoad(name, id, disable);
        };
        var vm = Model(backend);
        vm.SelectedCard = Card(vm, "Structure");
        vm.DisableUnused.Should().BeTrue();

        await vm.LoadAsync();

        asked.Should().Be(("Structure", LoaderFixtures.StructId, true));
        _dialogs.Log.Should().Equal("ConfirmAddinChanges", "AddinReport:Add-in Check");
        var model = _dialogs.AddinChanges.Single();
        model.Subtitle.Should().Be("For your Windows user, in Revit 2026 and 2027.");
        model.SwitchingOff.Should().Equal("Enscape (2026, 2027)", "DiRoots.One (2026, 2027)", "Ideate BIMLink (2026, 2027)");
        model.SwitchingOn.Should().BeEmpty();
        model.Staying.Should().Equal("PyRevitLoader (2026)");
        model.CannotChange.Should().Equal("Revizto (2027): locked on by your administrator");
        model.HasCannotChange.Should().BeTrue();

        // Preview = commit (spec #18, AC2): the load runs the very plan the dialog showed.
        var request = backend.LoadRequests.Single();
        request.DisableNonRequired.Should().BeTrue();
        request.Plan.Should().BeSameAs(backend.Plans.Single());
        ConfirmAddinChangesModel.From(request.Plan!).SwitchingOff.Should().Equal(model.SwitchingOff);
    }

    [Fact]
    public async Task Disable_off_with_add_ins_rst_c_switched_off_confirms_switching_them_back_on()
    {
        var backend = LoaderFixtures.Backend();
        LoaderFixtures.IdeateSwitchedOffByRst(backend);
        var vm = Model(backend);
        vm.SelectedCard = Card(vm, "Architecture");
        vm.DisableUnused.Should().BeFalse();
        await vm.LoadAsync();
        var model = _dialogs.AddinChanges.Single();
        model.SwitchingOn.Should().Equal("Ideate BIMLink (2026, 2027)");
        model.SwitchingOff.Should().BeEmpty();
        _dialogs.AddinReports.Single().SwitchedOn.Should().Equal("Ideate BIMLink (2026, 2027)");
    }

    [Fact]
    public async Task Cancelling_the_confirm_does_not_load()
    {
        var backend = LoaderFixtures.Backend();
        var vm = Model(backend);
        vm.SelectedCard = Card(vm, "Structure");
        _dialogs.ConfirmAddinChangesAnswer = false;
        await vm.LoadAsync();
        backend.Calls.Should().NotContain("Load");
        vm.Busy.IsBusy.Should().BeFalse();
        _closed.Should().Be(0);
    }

    [Fact]
    public async Task A_plan_error_shows_error_and_stops()
    {
        var backend = LoaderFixtures.Backend();
        backend.OnPlan = (name, _, _) => new LoadPlanOutcome("Profile not found: " + name, null);
        var vm = Model(backend);
        vm.SelectedCard = Card(vm, "Structure");
        await vm.LoadAsync();
        AssertToast(vm, "Error", "Profile not found: Structure", BannerKind.Error);
        backend.Calls.Should().NotContain("Load");
    }

    [Fact]
    public async Task A_plan_that_throws_loads_the_ribbon_changes_no_add_in_and_says_so()
    {
        // v3.0 went ahead with Disable on and no confirmation (D15); now no add-in changes, and the report says why.
        var backend = LoaderFixtures.Backend();
        backend.OnPlan = (_, _, _) => throw new InvalidOperationException("scan failed");
        var vm = Model(backend);
        vm.SelectedCard = Card(vm, "Structure");
        await vm.LoadAsync();
        _dialogs.AddinChanges.Should().BeEmpty();
        backend.LoadRequests.Should().ContainSingle().Which.Plan.Should().BeNull();
        _dialogs.AddinReports.Single().Notes.Should().Equal("RST could not read the installed add-ins, so no add-in was changed: scan failed");
    }

    [Fact]
    public async Task Load_ok_false_shows_load_failed_and_stays_open()
    {
        var backend = LoaderFixtures.Backend();
        backend.OnLoad = r => Task.FromResult(LoadResult.Fail("Profile not found: " + r.Name));
        var vm = Model(backend);
        await vm.LoadAsync();
        AssertToast(vm, "Load Failed", "Failed to load profile.", BannerKind.Error);
        _dialogs.AddinReports.Should().BeEmpty();
        vm.Busy.IsBusy.Should().BeFalse();
        vm.IsIdle.Should().BeTrue();
        _clock.Advance(TimeSpan.FromSeconds(5));
        _closed.Should().Be(0);
    }

    [Fact]
    public async Task Load_that_throws_shows_the_error()
    {
        var backend = LoaderFixtures.Backend();
        backend.OnLoad = _ => throw new IOException("locked");
        var vm = Model(backend);
        await vm.LoadAsync();
        AssertToast(vm, "Error", "Error loading profile: locked", BannerKind.Error);
        vm.IsWorking.Should().BeFalse();
        vm.Busy.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task The_add_in_pass_keeps_the_window_busy_and_every_command_disabled()
    {
        var backend = LoaderFixtures.Backend();
        var pending = new TaskCompletionSource<LoadResult>();
        backend.OnLoad = _ => pending.Task;
        var vm = Model(backend);
        var load = vm.LoadAsync();

        vm.IsWorking.Should().BeTrue();
        vm.Busy.IsBusy.Should().BeTrue();
        vm.Busy.Title.Should().Be("Loading Architecture");
        vm.IsIdle.Should().BeFalse();
        foreach (var command in new[] { vm.ImportCommand, vm.LoadCommand, vm.UnloadCommand, vm.DeleteCommand, vm.RestoreCommand, vm.CloseCommand })
            command.CanExecute(null).Should().BeFalse();

        pending.SetResult(new LoadResult(true, [], false, [], QaCheck.Clear));
        await load;
        vm.IsWorking.Should().BeFalse();
    }

    [Fact]
    public async Task The_report_lists_what_changed_what_could_not_and_the_required_add_ins_missing_or_off()
    {
        var backend = LoaderFixtures.Backend();
        var vm = Model(backend);
        vm.SelectedCard = Card(vm, "Structure");
        await vm.LoadAsync();

        var report = _dialogs.AddinReports.Single();
        report.Title.Should().Be("Add-in Check");
        report.Subtitle.Should().Be("What changed for \"Structure\", and what could not.");
        report.SwitchedOff.Should().Equal("Enscape (2026, 2027)", "DiRoots.One (2026, 2027)", "Ideate BIMLink (2026, 2027)");
        report.NotChanged.Should().Equal("Revizto (2027): locked on by your administrator");
        report.Missing.Should().Equal(new AddinCheckLine("Twinmotion", "https://twinmotion.com"));
        report.RequiredOff.Should().Equal(
            "Lumion LiveSync (2026) is required by this profile, but it is switched off in Autodesk's Add-Ins Manager. Turn it on there (Manage tab › Add-Ins Manager).",
            "Guardian (2026) is required by this profile, but your administrator has switched it off.");
        report.IsEmpty.Should().BeFalse();
        report.OpenUrlCommand.Execute("https://twinmotion.com");
        _opened.Should().Equal("https://twinmotion.com");
        vm.Busy.Message.Should().StartWith("Add-in changes apply the next time Revit starts.");
    }

    [Fact]
    public async Task The_report_shows_before_loading_and_nothing_else_interrupts()
    {
        var backend = LoaderFixtures.Backend();
        var vm = Model(backend);
        vm.SelectedCard = Card(vm, "Structure");
        _dialogs.DuringAddinReport = () => vm.Busy.IsBusy.Should().BeFalse("the report shows before Loading …");
        await vm.LoadAsync();
        _dialogs.Log.Should().Equal("ConfirmAddinChanges", "AddinReport:Add-in Check");
        _dialogs.Messages.Should().BeEmpty("v3.0's separate failed-disable message is part of the report now");
        vm.Busy.IsBusy.Should().BeTrue();
    }

    // ── unload ───────────────────────────────────────────────────────────────

    [Fact]
    public void Unload_with_nothing_loaded_says_no_profile()
    {
        var vm = Model(new FakeLoaderBackend().With(LoaderFixtures.Mep()));
        vm.UnloadCommand.Execute(null);
        AssertToast(vm, "No Profile", "No profile is currently loaded.", BannerKind.Info);
    }

    [Fact]
    public void Unload_clears_the_active_profile_and_closes_after_3s()
    {
        var backend = LoaderFixtures.Backend();
        var vm = Model(backend);
        vm.UnloadCommand.Execute(null);

        backend.Calls.Should().Contain("Unload");
        _dialogs.AddinReports.Should().BeEmpty("rst-c had switched nothing off");
        // Nothing is silent (spec #18, In Scope 5): a short non-modal note instead of a dialog.
        vm.Toast.IsOpen.Should().BeTrue();
        vm.Toast.Text.Should().Be("Unloaded. No add-in changes were needed.");
        vm.Toast.Kind.Should().Be(BannerKind.Info);
        vm.Profiles.Should().OnlyContain(c => !c.IsLoaded);
        vm.Busy.Title.Should().Be("Unloading");
        vm.Busy.Message.Should().Be("A blank RST tab will replace the current one.");
        vm.SelectedCard.Should().BeSameAs(vm.Profiles[0]);
        _clock.Advance(ProfileLoaderViewModel.CloseDelay);
        _closed.Should().Be(1);
    }

    [Fact]
    public void Unload_reports_the_add_ins_it_switched_back_on()
    {
        var backend = LoaderFixtures.Backend();
        var item = backend.Inventory.Items.First(i => i.Name == "Ideate BIMLink");
        var entry = new PlanEntry(item.Year, item.Name, null, item.Scope, item.ManifestPath, item.FileName, item.AddinIds,
            PlanAction.Enable, null, false, false, false, null);
        backend.OnUnload = () => new UnloadOutcome(true, null, new ActivationReport(ActivationIntent.Unload,
            [ActivationOutcome.For(entry, OutcomeResult.Done), ActivationOutcome.For(entry with { Year = 2027 }, OutcomeResult.NotChanged, ChangeReason.InUse)], []));
        var vm = Model(backend);
        vm.UnloadCommand.Execute(null);

        _dialogs.Log.Should().Equal("AddinReport:Add-ins After Unloading");
        vm.Toast.IsOpen.Should().BeFalse("the report already says what changed");
        var report = _dialogs.AddinReports.Single();
        report.SwitchedOn.Should().Equal("Ideate BIMLink (2026)");
        report.NotChanged.Should().Equal("Ideate BIMLink (2027): a file was in use by another program; try again");
        vm.Busy.Title.Should().Be("Unloading");
    }

    [Fact]
    public void Unload_failures_show_their_message()
    {
        var backend = LoaderFixtures.Backend();
        backend.OnUnload = () => UnloadOutcome.Fail("x");
        var vm = Model(backend);
        vm.UnloadCommand.Execute(null);
        AssertToast(vm, "Unload Failed", "Failed to unload profile.", BannerKind.Error);
        Card(vm, "Architecture").IsLoaded.Should().BeTrue();

        backend.OnUnload = () => throw new IOException("denied");
        vm.UnloadCommand.Execute(null);
        AssertToast(vm, "Error", "Error unloading profile: denied", BannerKind.Error);
        vm.Busy.IsBusy.Should().BeFalse();
    }

    // ── migration ────────────────────────────────────────────────────────────

    [Fact]
    public void Rst_3_renames_are_moved_once_at_boot_and_reported_before_anything_else()
    {
        var backend = LoaderFixtures.Backend();
        var item = backend.Inventory.Items.First(i => i.Name == "Ideate BIMLink");
        var moved = new PlanEntry(2026, "Old Tool", null, AddinScope.User, item.ManifestPath, "OldTool.addin", item.AddinIds,
            PlanAction.Disable, null, false, true, true, null);
        backend.OnMigrate = () => new ActivationReport(ActivationIntent.MigrateLegacy, [ActivationOutcome.For(moved, OutcomeResult.Done)], []);
        backend.ActiveIs(LoaderFixtures.Structure());
        backend.Prefs.Profiles.Remove(LoaderFixtures.StructId);
        var vm = Model(backend);

        _dialogs.Log.Should().Equal("AddinReport:Add-ins Switched Off by RST 3.0", "Confirm:Adopt RSTify Presets?");
        _dialogs.AddinReports.Single().Moved.Should().Equal("Old Tool (2026)");
        backend.Calls.Count(c => c == "GetAddinInventory").Should().Be(2, "the pills are redrawn after the move");
        vm.SelectedCard.Should().BeSameAs(Card(vm, "Structure"));
    }

    // ── delete ───────────────────────────────────────────────────────────────

    [Fact]
    public void Delete_asks_a_destructive_confirm_and_cancel_keeps_the_profile()
    {
        var backend = LoaderFixtures.Backend();
        var vm = Model(backend);
        vm.SelectedCard = Card(vm, "Structure");
        vm.DeleteCommand.Execute(null);

        var (title, message, confirm, danger, id) = _dialogs.Confirms.Single();
        title.Should().Be("Delete \"Structure\"?");
        message.Should().Be("This deletes the local copy. The original file shared by the admin is not affected.");
        confirm.Should().Be("Continue");
        danger.Should().BeTrue("a destructive confirm defaults to Cancel");
        id.Should().Be(ProfileLoaderViewModel.DeleteProfileDialogId);
        backend.Calls.Should().NotContain("Delete");
        vm.ProfileCount.Should().Be(3);
    }

    [Fact]
    public void Delete_of_an_inactive_profile_removes_it_and_selects_the_first_card()
    {
        var backend = LoaderFixtures.Backend();
        var vm = Model(backend);
        vm.SelectedCard = Card(vm, "Structure");
        _dialogs.ConfirmAnswers.Enqueue(true);
        vm.DeleteCommand.Execute(null);

        backend.Calls.Should().Contain("Delete").And.NotContain("Unload");
        vm.Profiles.Select(c => c.Name).Should().Equal("Architecture", "MEP Coordination");
        vm.SelectedCard.Should().BeSameAs(Card(vm, "Architecture"));
        vm.Busy.IsBusy.Should().BeFalse();
        _clock.Advance(TimeSpan.FromSeconds(5));
        _closed.Should().Be(0);
    }

    [Fact]
    public void Delete_while_loaded_unloads_first_then_closes_after_unloading()
    {
        var backend = LoaderFixtures.Backend();
        var vm = Model(backend);
        vm.SelectedCard.Should().BeSameAs(Card(vm, "Architecture"));
        _dialogs.ConfirmAnswers.Enqueue(true);
        vm.DeleteCommand.Execute(null);

        _dialogs.Confirms.Single().Message.Should().Be(
            "This deletes the local copy. The original file shared by the admin is not affected. "
            + "This is the currently loaded profile — a blank RST tab will replace it.");
        backend.Calls.SkipWhile(c => c != "Unload").Take(2).Should().Equal("Unload", "Delete");
        vm.Profiles.Select(c => c.Name).Should().Equal("Structure", "MEP Coordination");
        vm.Profiles.Should().OnlyContain(c => !c.IsLoaded);
        vm.Busy.Title.Should().Be("Unloading");
        _clock.Advance(ProfileLoaderViewModel.CloseDelay);
        _closed.Should().Be(1);
    }

    [Fact]
    public void A_failed_unload_during_delete_stops_before_deleting()
    {
        var backend = LoaderFixtures.Backend();
        backend.OnUnload = () => UnloadOutcome.Fail("x");
        var vm = Model(backend);
        _dialogs.ConfirmAnswers.Enqueue(true);
        vm.DeleteCommand.Execute(null);
        AssertToast(vm, "Unload Failed", "Failed to unload profile.", BannerKind.Error);
        backend.Calls.Should().NotContain("Delete");
        vm.ProfileCount.Should().Be(3);
    }

    [Fact]
    public void Delete_failures_show_their_message()
    {
        var backend = LoaderFixtures.Backend();
        backend.OnDelete = (_, _) => new DeleteOutcome(false, "Profile not found", false);
        var vm = Model(backend);
        vm.SelectedCard = Card(vm, "Structure");
        _dialogs.ConfirmAnswers.Enqueue(true);
        vm.DeleteCommand.Execute(null);
        AssertToast(vm, "Delete Failed", "Failed to delete profile.", BannerKind.Error);

        backend.OnDelete = (_, _) => throw new UnauthorizedAccessException("read only");
        _dialogs.ConfirmAnswers.Enqueue(true);
        vm.DeleteCommand.Execute(null);
        AssertToast(vm, "Error", "Error deleting profile: read only", BannerKind.Error);
        vm.ProfileCount.Should().Be(3);
    }

    [Fact]
    public void Deleting_the_last_profile_clears_the_detail_pane()
    {
        var backend = new FakeLoaderBackend().With(LoaderFixtures.Mep());
        var vm = Model(backend);
        vm.SelectedCard = vm.Profiles[0];
        _dialogs.ConfirmAnswers.Enqueue(true);
        vm.DeleteCommand.Execute(null);
        vm.HasProfiles.Should().BeFalse();
        vm.HasSelection.Should().BeFalse();
        vm.DetailName.Should().Be("—");
    }

    [Fact]
    public void Delete_with_nothing_selected_asks_nothing()
    {
        var vm = Model(new FakeLoaderBackend().With(LoaderFixtures.Mep()));
        vm.DeleteCommand.Execute(null);
        _dialogs.Confirms.Should().BeEmpty();
    }

    // ── restore, close, links ────────────────────────────────────────────────

    [Fact]
    public void Restore_all_shows_the_report_and_redraws_the_add_in_rows()
    {
        var backend = LoaderFixtures.Backend();
        var vm = Model(backend);
        vm.SelectedCard = Card(vm, "Structure");
        vm.RequiredAddins.Single(r => r.TabName == "Lumion").StatusLabel.Should().Be("Disabled");

        var lumion = backend.Inventory.Items.Single(i => i.Name == "Lumion LiveSync");
        var entry = new PlanEntry(2026, lumion.Name, null, lumion.Scope, lumion.ManifestPath, lumion.FileName, lumion.AddinIds,
            PlanAction.Enable, null, false, false, false, null);
        backend.OnRestore = () =>
        {
            backend.Inventory = new AddinInventory(backend.Inventory.Years,
                backend.Inventory.Items.Select(i => i.Name == lumion.Name ? i with { DisabledIds = [] } : i).ToList());
            return new RestoreOutcome(true, null, new ActivationReport(ActivationIntent.RestoreAll, [ActivationOutcome.For(entry, OutcomeResult.Done)], []));
        };
        vm.RestoreCommand.Execute(null);

        _dialogs.Log.Should().Equal("AddinReport:Restore All Add-ins");
        _dialogs.AddinReports.Single().SwitchedOn.Should().Equal("Lumion LiveSync (2026)");
        vm.RequiredAddins.Single(r => r.TabName == "Lumion").StatusLabel.Should().Be("Loaded", "the rows show the new state (v3.0 left them stale)");

        backend.OnRestore = () => new RestoreOutcome(true, null, ActivationReport.Empty(ActivationIntent.RestoreAll));
        vm.RestoreCommand.Execute(null);
        _dialogs.AddinReports.Last().IsEmpty.Should().BeTrue();
        _dialogs.AddinReports.Last().NothingText.Should().Be("No add-in needed changing.");
    }

    [Fact]
    public void Restore_failures_show_their_message()
    {
        var backend = LoaderFixtures.Backend();
        var vm = Model(backend);
        backend.OnRestore = () => new RestoreOutcome(false, "access denied", null);
        vm.RestoreCommand.Execute(null);
        AssertToast(vm, "Restore Failed", "Failed to restore add-ins: access denied", BannerKind.Error);

        backend.OnRestore = () => new RestoreOutcome(false, null, null);
        vm.RestoreCommand.Execute(null);
        vm.Toast.Text.Should().Be("Failed to restore add-ins: Unknown error");

        backend.OnRestore = () => throw new IOException("busy");
        vm.RestoreCommand.Execute(null);
        AssertToast(vm, "Error", "Error restoring add-ins: busy", BannerKind.Error);
        _dialogs.AddinReports.Should().BeEmpty();
    }

    [Fact]
    public void Close_requests_the_window_close()
    {
        var vm = Model(LoaderFixtures.Backend());
        vm.CloseCommand.Execute(null);
        _closed.Should().Be(1);
    }

    [Fact]
    public void The_info_link_opens_through_the_link_opener()
    {
        var vm = Model(LoaderFixtures.Backend());
        vm.SelectedCard = Card(vm, "Structure");
        var lumion = vm.RequiredAddins.Single(r => r.TabName == "Lumion");
        lumion.OpenUrlCommand.Execute(lumion.InfoUrl);
        _opened.Should().Equal("https://lumion.com");
    }

    [Fact]
    public void Links_that_would_not_open_are_hidden()
    {
        // Review N3: no dead "info" or "download" links.
        var backend = LoaderFixtures.Backend();
        var odd = LoaderFixtures.Mep();
        odd.RequiredAddins =
        [
            new RequiredAddin { TabName = "Local", Url = @"C:\tools\setup.exe" },
            new RequiredAddin { TabName = "Script", Url = "javascript:alert(1)" },
            new RequiredAddin { TabName = "Web", Url = "https://example.com" },
        ];
        backend.Profiles.Clear();
        backend.With(odd);
        var vm = Model(backend);
        vm.SelectedCard = vm.Profiles[0];
        vm.RequiredAddins.Select(r => (r.TabName, r.HasInfoUrl)).Should().Equal(("Local", false), ("Script", false), ("Web", true));

        var check = AddinReportModel.ForLoad("MEP", ActivationReport.Empty(ActivationIntent.Load),
            new QaCheck([new QaMissingItem("Local", null, "file:///C:/x.exe"), new QaMissingItem("Web", null, "https://example.com")], []), _ => { });
        check.Missing.Select(m => m.HasUrl).Should().Equal(false, true);
    }

    [Theory]
    [InlineData("https://lumion.com", true)]
    [InlineData("http://example.com/x", true)]
    [InlineData("file:///C:/Windows/notepad.exe", false)]
    [InlineData(@"C:\Windows\notepad.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_http_links_open(string? url, bool openable) => ExternalLinks.IsOpenable(url).Should().Be(openable);

    [Fact]
    public void A_toast_hides_after_3s()
    {
        var vm = Model(new FakeLoaderBackend().With(LoaderFixtures.Mep()));
        vm.UnloadCommand.Execute(null);
        vm.Toast.IsOpen.Should().BeTrue();
        _clock.Advance(TransientMessage.InformDuration);
        vm.Toast.IsOpen.Should().BeFalse();
    }

    private static Profile TempProfile(string name, string id) => new() { Id = id, ProfileName = name, Tab = name, ExportDate = "2026-10-30" };

    private sealed class ThrowingProfilesBackend : IProfileLoaderBackend
    {
        private readonly FakeLoaderBackend _inner = new();

        public string RevitVersion => "2026";

        public IReadOnlyList<string> AllTabs => [];

        public IReadOnlyList<ProfileEntry> GetProfiles() => throw new IOException("profiles folder unreadable");

        public ActiveProfile GetActiveProfile() => throw new IOException("x");

        public UserProfilePrefs GetPrefs() => throw new IOException("x");

        public IReadOnlyDictionary<string, AddinLookupEntry> GetAddinLookup() => throw new IOException("x");

        public AddinInventory GetAddinInventory() => throw new IOException("x");

        public OpResult RecordPrefs(string profileId, IReadOnlyList<string> hiddenTabs, bool disableUnused, bool presetsAdopted) => _inner.RecordPrefs(profileId, hiddenTabs, disableUnused, presetsAdopted);

        public OpResult RecordOpacityOverride(string profileId, int? opacity) => _inner.RecordOpacityOverride(profileId, opacity);

        public ImportOutcome Import(string zipPath) => _inner.Import(zipPath);

        public LoadPlanOutcome PlanLoad(string name, string? id, bool disableUnused) => _inner.PlanLoad(name, id, disableUnused);

        public Task<LoadResult> LoadAsync(LoadRequest request) => _inner.LoadAsync(request);

        public UnloadOutcome Unload() => _inner.Unload();

        public DeleteOutcome Delete(string name, string? id) => _inner.Delete(name, id);

        public RestoreOutcome RestoreAll() => _inner.RestoreAll();

        public ActivationReport? MigrateLegacy() => throw new IOException("x");
    }
}
