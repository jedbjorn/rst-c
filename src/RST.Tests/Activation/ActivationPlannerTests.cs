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
            ledger.Add(new LedgerEntry(e.Year, e.AddinIds[0], e.Name, LedgerKind.Setting, e.ManifestPath, DateTime.UtcNow));
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
        ledger.Add(new LedgerEntry(2027, locked.AddinIds[0], locked.Name, LedgerKind.Setting, locked.ManifestPath, DateTime.UtcNow));

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
    public void Revit_2025_takes_the_settings_file_path_like_2026_and_never_a_rename()
    {
        // Decision #21: no rename fallback. A machine-wide 2025 add-in is switched through the user's file.
        var machine = Item(2025, "Twinmotion", scope: AddinScope.Machine);
        var plan = ActivationPlanner.Plan(InventoryOf(machine, Item(2026, "Twinmotion", scope: AddinScope.Machine)), ActivationPolicy.Load([], true), new ActivationLedger());
        plan.Changes.Should().HaveCount(2).And.OnlyContain(e =>
            e.Action == PlanAction.Disable && !e.Renamed && e.TargetIds.SequenceEqual(machine.AddinIds));
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
    public void An_unreadable_settings_file_keeps_that_years_record_and_says_so()
    {
        // Review N1: with the file unreadable every add-in looks on; forgetting would orphan RST's own entries.
        var item = Item(2027, "Twinmotion");
        var inventory = new AddinInventory(
            [new InventoryYear(FakeAddinTree.Year(2027), "s", SettingsFileState.Unreadable, "in use", false, "a", false)], [item]);
        var ledger = new ActivationLedger();
        ledger.Add(new LedgerEntry(2027, item.AddinIds[0], item.Name, LedgerKind.Setting, item.ManifestPath, DateTime.UtcNow));
        ledger.Add(new LedgerEntry(2027, DeterministicId("Gone"), "Gone", LedgerKind.Setting, "x", DateTime.UtcNow));

        var plan = ActivationPlanner.Plan(inventory, ActivationPolicy.RestoreAll, ledger);

        plan.Forget.Should().BeEmpty();
        plan.HasWork.Should().BeFalse();
        plan.Notes.Single().Text.Should().Contain("could not be read").And.Contain("kept its record of the add-ins it switched off there");
    }

    [Fact]
    public void Stale_record_entries_are_forgotten_and_an_uninstalled_add_ins_setting_is_reset()
    {
        var reEnabledByUser = Item(2026, "Twinmotion");
        var inventory = InventoryOf(reEnabledByUser);
        var ledger = new ActivationLedger();
        ledger.Add(new LedgerEntry(2026, reEnabledByUser.AddinIds[0], "Twinmotion", LedgerKind.Setting, reEnabledByUser.ManifestPath, DateTime.UtcNow));
        ledger.Add(new LedgerEntry(2026, DeterministicId("Gone"), "Gone", LedgerKind.Setting, "x", DateTime.UtcNow));
        ledger.Add(new LedgerEntry(2026, null, "Old", LedgerKind.V30Rename, "y", DateTime.UtcNow));

        var plan = ActivationPlanner.Plan(inventory, ActivationPolicy.Unload, ledger);

        plan.Forget.Should().BeEquivalentTo(new[]
        {
            new LedgerForget(new LedgerKey(2026, LedgerKind.Setting, reEnabledByUser.AddinIds[0], reEnabledByUser.ManifestPath), false),
            new LedgerForget(new LedgerKey(2026, LedgerKind.Setting, DeterministicId("Gone"), "x"), true),
            new LedgerForget(new LedgerKey(2026, LedgerKind.V30Rename, null, "y"), false),
        });
        plan.HasChanges.Should().BeFalse();
        plan.HasWork.Should().BeTrue("a plan whose only work is the clean-up still runs (review B1)");
    }

    [Fact]
    public void An_add_in_whose_manifest_failed_to_parse_keeps_its_record_entry_and_setting()
    {
        // Review N5: an unparsable manifest is still installed; its setting must not be reset.
        var path = Path.Combine("C:", "User", "2026", "Broken.addin");
        var inventory = new AddinInventory(
            [new InventoryYear(FakeAddinTree.Year(2026), "s", SettingsFileState.Ok, null, false, "a", false, [path])], []);
        var ledger = new ActivationLedger();
        ledger.Add(new LedgerEntry(2026, DeterministicId("Broken"), "Broken", LedgerKind.Setting, path, DateTime.UtcNow));
        ActivationPlanner.Plan(inventory, ActivationPolicy.Unload, ledger).Forget.Should().BeEmpty();
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
    public void The_migration_plan_moves_v3_renames_but_not_while_a_settings_file_is_unreadable()
    {
        var inventory = new AddinInventory(
            [
                new InventoryYear(FakeAddinTree.Year(2025), "s", SettingsFileState.Missing, null, false, "a", false),
                new InventoryYear(FakeAddinTree.Year(2026), "s", SettingsFileState.Ok, null, false, "a", false),
                new InventoryYear(FakeAddinTree.Year(2027), "s", SettingsFileState.Unreadable, "bad", false, "a", false),
            ],
            [
                Item(2025, "Old", renamed: true),
                Item(2026, "Old", renamed: true),
                Item(2026, "Locked", renamed: true, adminLock: AdminLock.AlwaysEnabled),
                Item(2026, "Fine"),
                Item(2027, "Old", renamed: true),
            ]);
        var plan = ActivationPlanner.PlanMigration(inventory, new ActivationLedger());
        // 2025 migrates like 2026 (decision #21); 2027 waits for a readable file (review N7).
        plan.Changes.Select(e => (e.Year, e.Name)).Should().Equal((2025, "Old"), (2026, "Old"));
        plan.Changes.Should().OnlyContain(e => e.Migrate && e.Action == PlanAction.Disable && e.Renamed);
        plan.Intent.Should().Be(ActivationIntent.MigrateLegacy);
    }

    [Fact]
    public void A_native_requirement_is_never_an_add_in_and_an_add_in_id_matches()
    {
        // Review N2: the planner honours `native` and `addinId` exactly as the pills do.
        var probe = Item(2026, "Probe", id: "d4c6e3e0-0000-4000-8000-525354500001", fileName: "Whatever.addin");
        var probeTab = Item(2026, "RSTProbe Tools", fileName: "RSTProbeTools.addin");
        var inventory = InventoryOf(probe, probeTab);
        var byId = new RequiredAddin { TabName = "Elsewhere", AddinId = "D4C6E3E0-0000-4000-8000-525354500001" };
        var native = new RequiredAddin { TabName = "RSTProbe", Native = true };

        var plan = ActivationPlanner.Plan(inventory, ActivationPolicy.Load([byId, native], true), new ActivationLedger());

        plan.Entries.Single(e => e.Name == "Probe").Should().Match<PlanEntry>(e => e.Required && e.Action == PlanAction.NoChange);
        plan.Entries.Single(e => e.Name == "RSTProbe Tools").Action.Should().Be(PlanAction.Disable, "a native requirement keeps no add-in on");
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

    /// <summary>Every entry of a year's settings file: GUID → Disabled (empty when there is no file).</summary>
    private static Dictionary<string, bool> SettingsOnDisk(FakeAddinTree tree, int year)
    {
        var json = tree.ReadSettings(year);
        if (json is null) return [];
        return System.Text.Json.Nodes.JsonNode.Parse(json)!["AddInItemSettings"]!.AsArray()
            .ToDictionary(n => n!["GUID"]!.GetValue<string>(), n => n!["Disabled"]!.GetValue<bool>());
    }

    [Fact]
    public void The_plan_shown_is_exactly_the_operations_executed_on_disk_and_a_second_run_changes_nothing()
    {
        int[] years = [2025, 2026, 2027];
        var random = new Random(16018);
        for (var run = 0; run < 40; run++)
        {
            using var tree = new FakeAddinTree();
            var names = Enumerable.Range(0, random.Next(1, 8)).Select(i => "Addin" + i).ToList();
            foreach (var y in years)
            {
                var settings = new List<string>();
                foreach (var n in names)
                {
                    if (random.Next(4) == 0) continue;
                    var id = DeterministicId(n);
                    var dir = random.Next(3) == 0 ? tree.MachineAddins(y) : tree.UserAddins(y);
                    tree.Manifest(dir, n + ".addin", n, id, random.Next(6) == 0 ? "ADSK" : "ACME", renamed: random.Next(10) == 0);
                    if (random.Next(3) == 0) settings.Add("{\"Disabled\":true,\"GUID\":\"" + id + "\",\"LoadTime\":7,\"Name\":\"" + n + "\",\"Vendor\":\"X\"}");
                }
                if (settings.Count > 0) tree.Settings(y, "{\"DisableAllAddIns\":false,\"AddInItemSettings\":[" + string.Join(",", settings) + "]}");
            }
            var required = names.Where(_ => random.Next(3) == 0).Select(n => new RequiredAddin { TabName = n, AddinFile = n + ".addin" }).ToList();
            var policy = random.Next(3) switch
            {
                0 => ActivationPolicy.Load(required, true),
                1 => ActivationPolicy.Load(required, false),
                _ => ActivationPolicy.Unload,
            };
            var revitYears = years.Select(y => FakeAddinTree.Year(y)).ToArray();

            // A first Disable pass so RST's own record has entries; then the user re-enables some by hand.
            tree.Run(ActivationPolicy.Load([], true), null, revitYears);
            foreach (var y in years)
            {
                var json = tree.ReadSettings(y);
                if (json is null) continue;
                var doc = AddinsSettingsDocument.TryParse(json, out _)!;
                foreach (var id in doc.DisabledIds().Where(_ => random.Next(4) == 0).ToList()) doc.SetDisabled(id, false, null, null);
                tree.Settings(y, doc.ToJson());
            }

            var before = years.ToDictionary(y => y, y => SettingsOnDisk(tree, y));
            var (plan, report) = tree.Run(policy, null, revitYears);

            report.Outcomes.Select(o => o.OperationKey).Should().BeEquivalentTo(plan.Changes.Select(e => e.OperationKey), $"run {run}");
            report.Outcomes.Select(o => o.OperationKey).Should().OnlyHaveUniqueItems();
            report.Outcomes.Where(o => !o.IsDone).Should().BeEmpty($"run {run}: nothing blocks on a plain disk");

            // On disk (review N3): exactly the planned tokens flipped, to the planned value; nothing else moved.
            foreach (var y in years)
            {
                var after = SettingsOnDisk(tree, y);
                var flipped = after.Where(kv => !before[y].TryGetValue(kv.Key, out var was) || was != kv.Value)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
                var planned = plan.Changes.Where(e => e.Year == y)
                    .SelectMany(e => e.TargetIds.Select(id => (id, Disabled: e.Action == PlanAction.Disable)))
                    .ToDictionary(t => t.id, t => t.Disabled);
                flipped.Should().BeEquivalentTo(planned, $"run {run}, {y}");
                before[y].Keys.Should().BeSubsetOf(after.Keys, $"run {run}, {y}: no entry is dropped");
            }
            // Every record entry the user re-enabled by hand is gone (In Scope 4).
            foreach (var e in tree.Ledger().Entries.Where(e => e.Kind == LedgerKind.Setting))
                SettingsOnDisk(tree, e.Year).GetValueOrDefault(e.AddinId!).Should().BeTrue($"run {run}: the record only lists add-ins that are off");

            var again = ActivationPlanner.Plan(tree.Scan(revitYears), policy, tree.Ledger());
            again.HasWork.Should().BeFalse($"run {run}: the target state was reached and the record is tidy");
        }
    }
}
