// rst-c's own (not vendored): AC2 of spec #18 (feature #16, T-B2, T-B3). The planner's decision
// table (Disable on/off × required/unrequired × RST/Revit/Autodesk × already off by the user or by
// rst-c × locks × every year), the one matcher's tiers, the record clean-up, the JSON round trip,
// and the property "the plan shown is the operations executed" over random machines.

using System.IO;
using System.Text.Json;
using FluentAssertions;
using RST.Core.Activation;
using RST.Core.Profiles;
using RST.Tests.Loader;
using Xunit;
using static RST.Tests.Loader.FakeAddinSwitching;

namespace RST.Tests.Activation;

public class ActivationPlannerTests
{
    private static readonly int[] Years = [2026, 2027];

    /// <summary>The same add-ins in 2026 and 2027.</summary>
    private static InventoryItem[] Machine(int y) =>
    [
        Item(y, "RST", fileName: "RST.addin", id: InventoryItem.RstClientId, vendor: "RSTC"),
        Item(y, "Batch Print", AddinScope.Machine, vendor: "ADSK"),
        Item(y, "Add-Ins Manager", AddinScope.RevitInstall, vendor: "ADSK"),
        Item(y, "Kinship"),
        Item(y, "Twinmotion"),
        Item(y, "DiRoots.One", AddinScope.Machine, vendor: "www.diroots.com"),
        Item(y, "Lumion", off: true),
        Item(y, "Enscape", off: true),
        Item(y, "Locked On", adminLock: AdminLock.AlwaysEnabled),
        Item(y, "Locked Off", adminLock: AdminLock.AlwaysDisabled),
    ];

    private static AddinInventory Inventory() => InventoryOf([.. Years.SelectMany(Machine)]);

    /// <summary>rst-c switched Enscape off (both years); the user switched Lumion off themselves.</summary>
    private static ActivationLedger Ledger(AddinInventory inventory)
    {
        var ledger = new ActivationLedger();
        foreach (var e in inventory.Items.Where(i => i.Name == "Enscape"))
            ledger.Add(new LedgerEntry(e.Year, e.AddinIds[0], e.Name, SwitchMechanism.SettingsFile, e.ManifestPath, DateTime.UtcNow));
        return ledger;
    }

    private static readonly RequiredAddin[] RequiresKinshipAndEnscape =
    [
        new() { TabName = "Architecture" },
        new() { TabName = "Kinship Tools", AddinFile = "Kinship.addin" },
        new() { TabName = "Enscape" },
    ];

    private static Dictionary<string, (PlanAction, NotManagedReason?)> Decisions(ActivationPlan plan, int year) =>
        plan.Entries.Where(e => e.Year == year).ToDictionary(e => e.Name, e => (e.Action, e.NotManaged));

    [Fact]
    public void Disable_on_keeps_only_required_and_protected_add_ins_on_in_every_year()
    {
        var inventory = Inventory();
        var plan = ActivationPlanner.Plan(inventory, ActivationPolicy.Load(RequiresKinshipAndEnscape, disableUnused: true), Ledger(inventory));

        foreach (var y in Years)
        {
            Decisions(plan, y).Should().BeEquivalentTo(new Dictionary<string, (PlanAction, NotManagedReason?)>
            {
                ["RST"] = (PlanAction.NotManaged, NotManagedReason.RstItself),
                ["Batch Print"] = (PlanAction.NotManaged, NotManagedReason.Autodesk),
                ["Add-Ins Manager"] = (PlanAction.NotManaged, NotManagedReason.RevitInstall),
                ["Kinship"] = (PlanAction.NoChange, null),
                ["Twinmotion"] = (PlanAction.Disable, null),
                ["DiRoots.One"] = (PlanAction.Disable, null),
                ["Lumion"] = (PlanAction.NoChange, null),
                ["Enscape"] = (PlanAction.Enable, null),
                ["Locked On"] = (PlanAction.Disable, null),
                ["Locked Off"] = (PlanAction.NoChange, null),
            }, y.ToString());
        }
        plan.Entries.Single(e => e.Year == 2027 && e.Name == "Locked On").ExpectedBlock.Should().Be(ChangeReason.AdminLocked);
        plan.Entries.Single(e => e.Year == 2026 && e.Name == "Enscape").Required.Should().BeTrue();
        plan.Entries.Single(e => e.Year == 2026 && e.Name == "Twinmotion").TargetIds.Should().Equal(Item(2026, "Twinmotion").AddinIds);
        plan.Years.Should().Equal(2026, 2027);
        plan.DisableUnused.Should().BeTrue();
    }

    [Theory]
    [InlineData(ActivationIntent.Load)]
    [InlineData(ActivationIntent.Unload)]
    [InlineData(ActivationIntent.RestoreAll)]
    public void Disable_off_unload_and_restore_all_switch_back_on_only_what_rst_c_switched_off(ActivationIntent intent)
    {
        var inventory = Inventory();
        var policy = intent switch
        {
            ActivationIntent.Load => ActivationPolicy.Load([new RequiredAddin { TabName = "Twinmotion" }], disableUnused: false),
            ActivationIntent.Unload => ActivationPolicy.Unload,
            _ => ActivationPolicy.RestoreAll,
        };
        var plan = ActivationPlanner.Plan(inventory, policy, Ledger(inventory));

        plan.Changes.Select(e => (e.Name, e.Year, e.Action)).Should().Equal(("Enscape", 2026, PlanAction.Enable), ("Enscape", 2027, PlanAction.Enable));
        // Lumion was switched off by the user in Autodesk's manager: never turned on by rst-c.
        plan.Entries.Where(e => e.Name == "Lumion").Should().OnlyContain(e => e.Action == PlanAction.NoChange);
        plan.Changes.Single(e => e.Year == 2026).TargetIds.Should().Equal(Item(2026, "Enscape").AddinIds);
    }

    [Fact]
    public void A_required_add_in_the_user_switched_off_is_flagged_not_turned_on()
    {
        var inventory = Inventory();
        var plan = ActivationPlanner.Plan(inventory, ActivationPolicy.Load([new RequiredAddin { TabName = "Lumion" }, new RequiredAddin { TabName = "Locked Off" }], true), Ledger(inventory));
        plan.Entries.Where(e => e.Name == "Lumion").Should().OnlyContain(e =>
            e.Action == PlanAction.NotManaged && e.NotManaged == NotManagedReason.SwitchedOffByUser && e.Required);
        plan.Entries.Where(e => e.Name == "Locked Off").Should().OnlyContain(e => e.NotManaged == NotManagedReason.LockedOffByAdmin);
    }

    [Fact]
    public void An_admin_lock_on_wins_over_the_users_file_so_switching_off_reports_the_lock()
    {
        // rst-c switched DiRoots off earlier; then IT locked it on. Revit loads it (the admin file
        // wins), so Disable unused must try again and report the lock, not call it "already off".
        var locked = Item(2027, "DiRoots.One", AddinScope.Machine, off: true, adminLock: AdminLock.AlwaysEnabled);
        locked.IsOff.Should().BeFalse();
        var ledger = new ActivationLedger();
        ledger.Add(new LedgerEntry(2027, locked.AddinIds[0], locked.Name, SwitchMechanism.SettingsFile, locked.ManifestPath, DateTime.UtcNow));

        var plan = ActivationPlanner.Plan(InventoryOf(locked), ActivationPolicy.Load([], true), ledger);
        plan.Changes.Should().ContainSingle().Which.Should().Match<PlanEntry>(e =>
            e.Action == PlanAction.Disable && e.ExpectedBlock == ChangeReason.AdminLocked && e.TargetIds.Count == 0);
        plan.Forget.Should().BeEmpty("the setting rst-c wrote is still there for when the lock goes");
        ActivationPlanner.Plan(InventoryOf(locked), ActivationPolicy.Unload, ledger).HasChanges.Should().BeFalse("it is on already");
    }

    [Fact]
    public void A_protected_add_in_that_v3_switched_off_is_switched_back_on()
    {
        // v3.0 could rename Autodesk's own add-ins (investigation B3): they go back on, whatever the policy.
        var renamed = Item(2026, "eTransmit", vendor: "ADSK", renamed: true);
        var plan = ActivationPlanner.Plan(InventoryOf(renamed), ActivationPolicy.Load([], true), new ActivationLedger());
        plan.Changes.Should().ContainSingle().Which.Should().Match<PlanEntry>(e => e.Action == PlanAction.Enable && e.Renamed && e.TargetIds.Count == 0);
    }

    [Fact]
    public void A_manifest_without_a_client_id_is_not_managed_in_a_settings_year()
    {
        var bare = Item(2026, "Bare") with { AddinIds = [] };
        var plan = ActivationPlanner.Plan(InventoryOf(bare), ActivationPolicy.Load([], true), new ActivationLedger());
        plan.Entries.Single().NotManaged.Should().Be(NotManagedReason.NoAddinId);
    }

    [Fact]
    public void A_rename_year_disables_by_renaming_and_re_enables_its_renamed_manifests()
    {
        var on = Item(2025, "Twinmotion", mechanism: SwitchMechanism.Rename, scope: AddinScope.Machine);
        var renamed = Item(2025, "Enscape", mechanism: SwitchMechanism.Rename, renamed: true);
        var inventory = new AddinInventory(
            [new InventoryYear(new RevitYear(2025, null, "25.2.0.0", SwitchMechanism.Rename), "s", SettingsFileState.Missing, null, false, "a", false)],
            [on, renamed]);

        var disable = ActivationPlanner.Plan(inventory, ActivationPolicy.Load([], true), new ActivationLedger());
        disable.Changes.Should().ContainSingle().Which.Should().Match<PlanEntry>(e =>
            e.Name == "Twinmotion" && e.Action == PlanAction.Disable && e.Mechanism == SwitchMechanism.Rename && e.TargetIds.Count == 0 && e.MayNeedAdmin);

        var unload = ActivationPlanner.Plan(inventory, ActivationPolicy.Unload, new ActivationLedger());
        unload.Changes.Should().ContainSingle().Which.Name.Should().Be("Enscape");
    }

    [Fact]
    public void An_unreadable_settings_file_is_known_to_block_and_noted()
    {
        var inventory = new AddinInventory(
            [new InventoryYear(FakeAddinTree.Year(2026), "s", SettingsFileState.Unreadable, "bad", false, "a", false)],
            [Item(2026, "Twinmotion")]);
        var plan = ActivationPlanner.Plan(inventory, ActivationPolicy.Load([], true), new ActivationLedger());
        plan.Changes.Single().ExpectedBlock.Should().Be(ChangeReason.FileError);
        plan.Notes.Should().ContainSingle().Which.Text.Should().StartWith("Revit 2026: its add-in settings file could not be read");
    }

    [Fact]
    public void Stale_record_entries_are_forgotten_and_an_uninstalled_add_ins_setting_is_reset()
    {
        var reEnabledByUser = Item(2026, "Twinmotion");
        var inventory = InventoryOf(reEnabledByUser);
        var ledger = new ActivationLedger();
        ledger.Add(new LedgerEntry(2026, reEnabledByUser.AddinIds[0], "Twinmotion", SwitchMechanism.SettingsFile, reEnabledByUser.ManifestPath, DateTime.UtcNow));
        ledger.Add(new LedgerEntry(2026, DeterministicId("Gone"), "Gone", SwitchMechanism.SettingsFile, "x", DateTime.UtcNow));
        ledger.Add(new LedgerEntry(2025, null, "Old", SwitchMechanism.Rename, "y", DateTime.UtcNow));

        var plan = ActivationPlanner.Plan(inventory, ActivationPolicy.Unload, ledger);

        plan.Forget.Should().BeEquivalentTo(new[]
        {
            new LedgerForget(new LedgerKey(2026, SwitchMechanism.SettingsFile, reEnabledByUser.AddinIds[0], reEnabledByUser.ManifestPath), false),
            new LedgerForget(new LedgerKey(2026, SwitchMechanism.SettingsFile, DeterministicId("Gone"), "x"), true),
            new LedgerForget(new LedgerKey(2025, SwitchMechanism.Rename, null, "y"), false),
        });
        plan.HasChanges.Should().BeFalse();
    }

    [Fact]
    public void The_user_s_disable_all_and_the_admin_s_are_noted()
    {
        var inventory = new AddinInventory(
            [new InventoryYear(FakeAddinTree.Year(2026), "s", SettingsFileState.Ok, null, true, "a", true)], []);
        ActivationPlanner.Plan(inventory, ActivationPolicy.Unload, new ActivationLedger()).Notes.Select(n => n.Text).Should().Equal(
            "Revit 2026: “Disable all Add-Ins” is ticked in Autodesk's Add-Ins Manager, so no add-in loads there.",
            "Revit 2026: your administrator has switched off all add-ins.");
    }

    [Fact]
    public void The_migration_plan_moves_v3_renames_in_settings_years_only()
    {
        var inventory = new AddinInventory(
            [
                new InventoryYear(FakeAddinTree.Year(2025, SwitchMechanism.Rename), "s", SettingsFileState.Missing, null, false, "a", false),
                new InventoryYear(FakeAddinTree.Year(2026), "s", SettingsFileState.Ok, null, false, "a", false),
            ],
            [
                Item(2025, "Old", mechanism: SwitchMechanism.Rename, renamed: true),
                Item(2026, "Old", renamed: true),
                Item(2026, "Locked", renamed: true, adminLock: AdminLock.AlwaysEnabled),
                Item(2026, "Fine"),
            ]);
        var plan = ActivationPlanner.PlanMigration(inventory, new ActivationLedger());
        plan.Changes.Should().ContainSingle().Which.Should().Match<PlanEntry>(e =>
            e.Year == 2026 && e.Name == "Old" && e.Migrate && e.Action == PlanAction.Disable && e.Renamed);
        plan.Intent.Should().Be(ActivationIntent.MigrateLegacy);
    }

    [Fact]
    public void The_plan_round_trips_through_json_for_the_tray()
    {
        var inventory = Inventory();
        var plan = ActivationPlanner.Plan(inventory, ActivationPolicy.Load(RequiresKinshipAndEnscape, true), Ledger(inventory));
        var json = plan.ToJson();
        json.Should().Contain("\"Disable\"").And.Contain("\"version\":1");
        ActivationPlan.FromJson(json).Should().BeEquivalentTo(plan);
        FluentActions.Invoking(() => ActivationPlan.FromJson("{\"version\":2,\"plan\":null}")).Should().Throw<JsonException>();
    }

    // ── the one matcher ──────────────────────────────────────────────────────

    [Fact]
    public void The_matcher_tries_the_file_then_the_id_then_the_tab_name_and_keeps_every_hit()
    {
        var items = new[]
        {
            Item(2026, "Lumion LiveSync", fileName: "LumionLiveSync.addin"),
            Item(2026, "Lumion Extras", fileName: "Lumion.addin"),
            Item(2026, "Kinship"),
            Item(2026, "Kinship Library", fileName: "KinshipLibrary.addin"),
        };
        RequiredAddinMatch.Match(new RequiredAddin { TabName = "Lumion", AddinFile = "lumion.ADDIN" }, items).Select(i => i.Name)
            .Should().Equal(["Lumion Extras"], "tier 1 ends the search");
        RequiredAddinMatch.Match(new RequiredAddin { TabName = "x", AddinId = items[2].AddinIds[0].ToUpperInvariant() }, items).Select(i => i.Name)
            .Should().Equal("Kinship");
        RequiredAddinMatch.Match(new RequiredAddin { TabName = "Kinship" }, items).Select(i => i.Name)
            .Should().Equal("Kinship", "Kinship Library");
        RequiredAddinMatch.Match(new RequiredAddin { TabName = "Architecture" }, items).Should().BeEmpty("a built-in tab is never an add-in");
        RequiredAddinMatch.Match(new RequiredAddin { TabName = "" }, items).Should().BeEmpty();
    }

    // ── the property: the plan shown is the operations executed (T-B3) ───────

    [Fact]
    public void The_plan_shown_is_exactly_the_operations_executed_and_a_second_run_changes_nothing()
    {
        var random = new Random(16018);
        for (var run = 0; run < 40; run++)
        {
            using var tree = new FakeAddinTree();
            var names = Enumerable.Range(0, random.Next(1, 8)).Select(i => "Addin" + i).ToList();
            var settings = new Dictionary<int, List<string>>();
            foreach (var y in Years)
            {
                settings[y] = [];
                foreach (var n in names)
                {
                    if (random.Next(4) == 0) continue;
                    var id = DeterministicId(n);
                    var dir = random.Next(3) == 0 ? tree.MachineAddins(y) : tree.UserAddins(y);
                    tree.Manifest(dir, n + ".addin", n, id, random.Next(6) == 0 ? "ADSK" : "ACME");
                    if (random.Next(3) == 0) settings[y].Add("{\"Disabled\":true,\"GUID\":\"" + id + "\"}");
                }
                if (settings[y].Count > 0) tree.Settings(y, "{\"DisableAllAddIns\":false,\"AddInItemSettings\":[" + string.Join(",", settings[y]) + "]}");
            }
            var required = names.Where(_ => random.Next(3) == 0).Select(n => new RequiredAddin { TabName = n, AddinFile = n + ".addin" }).ToList();
            var policy = random.Next(3) switch
            {
                0 => ActivationPolicy.Load(required, true),
                1 => ActivationPolicy.Load(required, false),
                _ => ActivationPolicy.Unload,
            };
            var years = Years.Select(y => FakeAddinTree.Year(y)).ToArray();

            // A first Disable pass so rst-c's own record has entries to re-enable.
            tree.Run(ActivationPolicy.Load([], true), null, years);
            var (plan, report) = tree.Run(policy, null, years);

            report.Outcomes.Select(o => o.OperationKey).Should().BeEquivalentTo(plan.Changes.Select(e => e.OperationKey), $"run {run}");
            report.Outcomes.Select(o => o.OperationKey).Should().OnlyHaveUniqueItems();
            report.Outcomes.Where(o => !o.IsDone).Should().BeEmpty($"run {run}: nothing blocks on a plain disk");

            var again = ActivationPlanner.Plan(tree.Scan(years), policy, tree.Ledger());
            again.HasChanges.Should().BeFalse($"run {run}: the target state was reached");
        }
    }
}
