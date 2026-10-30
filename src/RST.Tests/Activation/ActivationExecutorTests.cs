// rst-c's own (not vendored): AC3 of spec #18 (feature #16, T-B4, T-B5). The executor on temp
// folders: the atomic AddInsSettings.json write (unknown fields and other entries kept, one token per
// flip, a missing file created, a backup kept once), admin locks (AdminLocked, also when set after
// the plan), an unreadable file (FileError, never overwritten), the 2025.0–2025.2 rename fallback,
// partial failures (NeedsAdmin, InUse, Vanished, FileError), idempotence, rst-c's own record, and
// the one-time move of v3.0's renamed manifests.

using System.IO;
using System.Text;
using FluentAssertions;
using RST.Core.Activation;
using RST.Core.Profiles;
using Xunit;

namespace RST.Tests.Activation;

public sealed class ActivationExecutorTests : IDisposable
{
    // The add-ins of the spike's own orig-2026.json, so the real capture is what gets edited.
    private const string DiRoots = "b4ad7489-2899-46b3-aca6-c0def7135528";
    private const string Kinship = "a47e7259-a459-4fdd-8435-fb0bc0f86268";
    private const string AlignTag = "5a9e8c41-09d6-4cdf-8556-38f8119137a4";
    private const string Probe = "d4c6e3e0-0000-4000-8000-525354500001";

    private readonly FakeAddinTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private static readonly RevitYear Y2026 = FakeAddinTree.Year(2026);
    private static readonly RevitYear Y2027 = FakeAddinTree.Year(2027);
    private static readonly RevitYear Y2025Rename = FakeAddinTree.Year(2025, SwitchMechanism.Rename);

    private static ActivationPolicy DisableAllBut(params string[] required) =>
        ActivationPolicy.Load(required.Select(r => new RequiredAddin { TabName = r, AddinFile = r + ".addin" }).ToList(), disableUnused: true);

    private void Spike2026()
    {
        _tree.Manifest(_tree.UserAddins(2026), "Kinship.addin", "Kinship", Kinship, "ANDK");
        _tree.Manifest(_tree.MachineAddins(2026), "DiRoots.One.addin", "DiRoots.One", DiRoots, "www.diroots.com");
        _tree.Manifest(_tree.UserAddins(2026), "RST.addin", "RST", InventoryItem.RstClientId, "RSTC");
        _tree.Manifest(_tree.MachineAddins(2026), "Autodesk.BatchPrint.addin", "Batch Print", "9d508589-48b6-4ff6-a50d-d16d77c585a1", "ADSK");
        _tree.Settings(2026, FakeAddinTree.Fixture("orig-2026.json"));
    }

    [Fact]
    public void Disabling_flips_exactly_those_entries_atomically_and_keeps_everything_else()
    {
        Spike2026();
        var original = FakeAddinTree.Fixture("orig-2026.json");

        var (plan, report) = _tree.Run(DisableAllBut(), null, Y2026);

        plan.Changes.Select(e => e.Name).Should().BeEquivalentTo("Kinship", "DiRoots.One");
        report.Outcomes.Should().HaveCount(2).And.OnlyContain(o => o.Result == OutcomeResult.Done && o.Action == PlanAction.Disable);
        var expected = original
            .Replace("{\"Disabled\":false,\"GUID\":\"" + DiRoots + "\"", "{\"Disabled\":true,\"GUID\":\"" + DiRoots + "\"")
            .Replace("{\"Disabled\":false,\"GUID\":\"" + Kinship + "\"", "{\"Disabled\":true,\"GUID\":\"" + Kinship + "\"");
        _tree.SettingsBytes(2026).Should().Equal(Encoding.UTF8.GetBytes(expected), "two tokens change; no BOM, no newline");
        File.ReadAllText(_tree.Roots.SettingsFile(2026) + ActivationExecutor.BackupSuffix).Should().Be(original, "a one-time backup");
        File.Exists(_tree.Roots.SettingsFile(2026) + ActivationFileSystem.TempSuffix).Should().BeFalse("the temp file is gone");
        // Machine-wide DiRoots is switched off for this user without touching ProgramData.
        File.Exists(Path.Combine(_tree.MachineAddins(2026), "DiRoots.One.addin")).Should().BeTrue();

        _tree.Ledger().Entries.Select(e => (e.Year, e.AddinId, e.Mechanism)).Should().BeEquivalentTo(new[]
        {
            (2026, (string?)Kinship, SwitchMechanism.SettingsFile),
            (2026, (string?)DiRoots, SwitchMechanism.SettingsFile),
        });
    }

    [Fact]
    public void A_missing_settings_file_is_created_and_unknown_fields_survive_a_rewrite()
    {
        _tree.Manifest(_tree.UserAddins(2027), "RstProbe.addin", "RST-Probe", Probe, "RSTC");
        _tree.Run(DisableAllBut(), null, Y2027);
        _tree.ReadSettings(2027).Should().Be(
            "{\"DisableAllAddIns\":false,\"AddInItemSettings\":[{\"Disabled\":true,\"GUID\":\"" + Probe + "\",\"LoadTime\":0,\"Name\":\"RST-Probe\",\"Vendor\":\"RSTC\"}]}");
        File.Exists(_tree.Roots.SettingsFile(2027) + ActivationExecutor.BackupSuffix).Should().BeFalse("nothing to back up");

        _tree.Settings(2027, "{\"DisableAllAddIns\":false,\"NewInRevit2028\":[1],\"AddInItemSettings\":[{\"Disabled\":true,\"GUID\":\"" + Probe + "\",\"LoadTime\":12,\"Name\":\"RST-Probe\",\"Vendor\":\"RSTC\",\"Pinned\":true},{\"Disabled\":false,\"GUID\":\"" + Kinship + "\",\"LoadTime\":3,\"Name\":\"Kinship\",\"Vendor\":\"ANDK\"}]}");
        _tree.Run(ActivationPolicy.Unload, null, Y2027).Report.Outcomes.Should().ContainSingle().Which.IsDone.Should().BeTrue();
        _tree.ReadSettings(2027).Should().Be(
            "{\"DisableAllAddIns\":false,\"NewInRevit2028\":[1],\"AddInItemSettings\":[{\"Disabled\":false,\"GUID\":\"" + Probe + "\",\"LoadTime\":12,\"Name\":\"RST-Probe\",\"Vendor\":\"RSTC\",\"Pinned\":true},{\"Disabled\":false,\"GUID\":\"" + Kinship + "\",\"LoadTime\":3,\"Name\":\"Kinship\",\"Vendor\":\"ANDK\"}]}");
        _tree.Ledger().Entries.Should().BeEmpty("an entry is cleared once the add-in is back on");
    }

    [Fact]
    public void Re_enabling_touches_only_what_rst_c_switched_off()
    {
        Spike2026();
        _tree.Manifest(_tree.UserAddins(2026), "AlignTag.addin", "Application AlignTag", AlignTag, "BM42");
        // The user switched AlignTag off in Autodesk's Add-Ins Manager.
        _tree.Settings(2026, FakeAddinTree.Fixture("orig-2026.json").Replace("{\"Disabled\":false,\"GUID\":\"" + AlignTag + "\"", "{\"Disabled\":true,\"GUID\":\"" + AlignTag + "\""));

        _tree.Run(DisableAllBut(), null, Y2026);
        var (_, report) = _tree.Run(ActivationPolicy.Unload, null, Y2026);

        report.Outcomes.Select(o => o.Name).Should().BeEquivalentTo("Kinship", "DiRoots.One");
        var settings = AddinsSettingsDocument.TryParse(_tree.ReadSettings(2026)!, out _)!;
        settings.IsDisabled(Kinship).Should().BeFalse();
        settings.IsDisabled(DiRoots).Should().BeFalse();
        settings.IsDisabled(AlignTag).Should().BeTrue("the user's own choice stays");
    }

    [Fact]
    public void An_admin_lock_is_reported_and_the_file_is_not_touched_even_when_set_after_the_plan()
    {
        Spike2026();
        _tree.Lock(2026, Path.Combine(_tree.MachineAddins(2026), "DiRoots.One.addin"), DiRoots, AdminLock.AlwaysEnabled);
        var before = _tree.ReadSettings(2026);

        var plan = ActivationPlanner.Plan(_tree.Scan(Y2026), DisableAllBut("Kinship"), _tree.Ledger());
        plan.Changes.Single().ExpectedBlock.Should().Be(ChangeReason.AdminLocked);
        var report = _tree.Executor().Execute(plan);
        report.Outcomes.Single().Should().Match<ActivationOutcome>(o => o.Result == OutcomeResult.NotChanged && o.Reason == ChangeReason.AdminLocked && o.Scope == AddinScope.Machine);
        ActivationWording.NotChangedLine(report.Outcomes.Single()).Should().Be("DiRoots.One (2026): locked on by your administrator");
        _tree.ReadSettings(2026).Should().Be(before);

        // Locked by IT between the Confirm dialog and the write: the lock file is read again.
        File.Delete(_tree.Roots.AdminSettingsFile(2026));
        var unlocked = ActivationPlanner.Plan(_tree.Scan(Y2026), DisableAllBut("Kinship"), _tree.Ledger());
        unlocked.Changes.Single().ExpectedBlock.Should().BeNull();
        _tree.Lock(2026, Path.Combine(_tree.MachineAddins(2026), "DiRoots.One.addin"), DiRoots, AdminLock.AlwaysEnabled);
        _tree.Executor().Execute(unlocked).Outcomes.Single().Reason.Should().Be(ChangeReason.AdminLocked);
        _tree.ReadSettings(2026).Should().Be(before);
        _tree.Ledger().Entries.Should().BeEmpty();
    }

    [Fact]
    public void An_unreadable_settings_file_is_a_file_error_and_is_never_overwritten()
    {
        _tree.Manifest(_tree.UserAddins(2026), "Kinship.addin", "Kinship", Kinship);
        _tree.Manifest(_tree.UserAddins(2027), "Kinship.addin", "Kinship", Kinship);
        var corrupt = FakeAddinTree.Fixture("s9-input-seed-corrupt-2026.json");
        _tree.Settings(2026, corrupt);

        var (_, report) = _tree.Run(DisableAllBut(), null, Y2026, Y2027);

        report.Outcomes.Single(o => o.Year == 2026).Reason.Should().Be(ChangeReason.FileError);
        report.Outcomes.Single(o => o.Year == 2027).IsDone.Should().BeTrue("one year's broken file does not stop another's");
        _tree.ReadSettings(2026).Should().Be(corrupt);
        report.Notes.Should().ContainSingle().Which.Should().StartWith("Revit 2026: its add-in settings file could not be read");
    }

    [Fact]
    public void Revit_2025_0_to_2025_2_fall_back_to_renaming_and_rename_back()
    {
        var path = _tree.Manifest(_tree.UserAddins(2025), "Kinship.addin", "Kinship", Kinship);

        var (_, off) = _tree.Run(DisableAllBut(), null, Y2025Rename);
        off.Outcomes.Single().Should().Match<ActivationOutcome>(o => o.IsDone && o.Action == PlanAction.Disable);
        File.Exists(path).Should().BeFalse();
        File.Exists(path + InventoryItem.RenamedSuffix).Should().BeTrue();
        File.Exists(_tree.Roots.SettingsFile(2025)).Should().BeFalse("no settings file without the Add-Ins Manager");
        _tree.Ledger().HasRename(2025, path).Should().BeTrue();

        var (_, on) = _tree.Run(ActivationPolicy.Unload, null, Y2025Rename);
        on.Outcomes.Single().Should().Match<ActivationOutcome>(o => o.IsDone && o.Action == PlanAction.Enable);
        File.Exists(path).Should().BeTrue();
        _tree.Ledger().Entries.Should().BeEmpty();
    }

    [Fact]
    public void Partial_failures_are_each_reported_once_with_their_reason_and_nothing_throws()
    {
        var machine = _tree.Manifest(_tree.MachineAddins(2025), "DiRoots.One.addin", "DiRoots.One", DiRoots);
        var inUse = _tree.Manifest(_tree.UserAddins(2025), "Kinship.addin", "Kinship", Kinship);
        var vanishing = _tree.Manifest(_tree.UserAddins(2025), "AlignTag.addin", "AlignTag", AlignTag);
        _tree.Manifest(_tree.UserAddins(2025), "Fine.addin", "Fine", Probe);
        _tree.Manifest(_tree.UserAddins(2026), "Kinship.addin", "Kinship", Kinship);
        _tree.Manifest(_tree.UserAddins(2027), "Kinship.addin", "Kinship", Kinship);
        var fs = new FailingFileSystem();
        fs.MoveThrows[machine] = new UnauthorizedAccessException("Access to the path is denied.");
        fs.MoveThrows[inUse] = FailingFileSystem.SharingViolation();
        fs.WriteThrows[_tree.Roots.SettingsFile(2026)] = FailingFileSystem.SharingViolation();
        fs.WriteThrows[_tree.Roots.SettingsFile(2027)] = new UnauthorizedAccessException("denied");

        var plan = ActivationPlanner.Plan(_tree.Scan(Y2025Rename, Y2026, Y2027), DisableAllBut(), _tree.Ledger());
        File.Delete(vanishing);
        var report = _tree.Executor(fs).Execute(plan);

        string Reason(int year, string name) => report.Outcomes.Single(o => o.Year == year && o.Name == name) is { IsDone: true } ? "Done" : report.Outcomes.Single(o => o.Year == year && o.Name == name).Reason.ToString()!;
        Reason(2025, "DiRoots.One").Should().Be(nameof(ChangeReason.NeedsAdmin));
        Reason(2025, "Kinship").Should().Be(nameof(ChangeReason.InUse));
        Reason(2025, "AlignTag").Should().Be(nameof(ChangeReason.Vanished));
        Reason(2025, "Fine").Should().Be("Done");
        Reason(2026, "Kinship").Should().Be(nameof(ChangeReason.InUse));
        Reason(2027, "Kinship").Should().Be(nameof(ChangeReason.FileError));
        report.Outcomes.Should().HaveCount(plan.Changes.Count);
        report.Outcomes.Select(o => o.OperationKey).Should().OnlyHaveUniqueItems();
        _tree.Ledger().Entries.Should().ContainSingle().Which.Name.Should().Be("Fine", "only what changed is recorded");
        ActivationWording.NotChangedLine(report.Outcomes.Single(o => o.Name == "DiRoots.One"))
            .Should().Be("DiRoots.One (2025): installed for all users; changing it needs administrator rights");
    }

    [Fact]
    public void Running_twice_is_idempotent()
    {
        Spike2026();
        var plan = ActivationPlanner.Plan(_tree.Scan(Y2026), DisableAllBut(), _tree.Ledger());
        _tree.Executor().Execute(plan);
        var once = _tree.ReadSettings(2026);
        var ledgerOnce = File.ReadAllText(_tree.LedgerPath);

        var again = _tree.Executor().Execute(plan);
        again.Outcomes.Should().OnlyContain(o => o.IsDone);
        _tree.ReadSettings(2026).Should().Be(once);
        File.ReadAllText(_tree.LedgerPath).Should().Be(ledgerOnce);
        ActivationPlanner.Plan(_tree.Scan(Y2026), DisableAllBut(), _tree.Ledger()).HasChanges.Should().BeFalse();
    }

    [Fact]
    public void An_uninstalled_add_in_is_cleared_from_the_record_and_its_setting_reset()
    {
        var path = _tree.Manifest(_tree.UserAddins(2026), "RstProbe.addin", "RST-Probe", Probe);
        _tree.Run(DisableAllBut(), null, Y2026);
        File.Delete(path);

        _tree.Run(ActivationPolicy.Unload, null, Y2026).Report.Outcomes.Should().BeEmpty();
        _tree.Ledger().Entries.Should().BeEmpty();
        AddinsSettingsDocument.TryParse(_tree.ReadSettings(2026)!, out _)!.IsDisabled(Probe).Should().BeFalse("a reinstall must not come back switched off");
    }

    [Fact]
    public void A_record_that_cannot_be_saved_is_noted_in_the_report()
    {
        _tree.Manifest(_tree.UserAddins(2026), "Kinship.addin", "Kinship", Kinship);
        var fs = new FailingFileSystem();
        fs.WriteThrows[_tree.LedgerPath] = new IOException("disk full");
        var (_, report) = _tree.Run(DisableAllBut(), fs, Y2026);
        report.Outcomes.Single().IsDone.Should().BeTrue();
        report.Notes.Should().ContainSingle().Which.Should().Be(ActivationWording.LedgerSaveFailed + " (disk full).");
    }

    // ── the v3.0 migration ───────────────────────────────────────────────────

    [Fact]
    public void A_v3_renamed_manifest_is_moved_into_the_settings_file_once()
    {
        var path = _tree.Manifest(_tree.UserAddins(2026), "Kinship.addin", "Kinship", Kinship, renamed: true);
        var stays = _tree.Manifest(_tree.UserAddins(2025), "Kinship.addin", "Kinship", Kinship, renamed: true);
        var switching = new AddinSwitching(_tree.Roots, () => [Y2025Rename, Y2026], _tree.LedgerPath);

        var report = switching.MigrateLegacy();

        report!.Outcomes.Should().ContainSingle().Which.Should().Match<ActivationOutcome>(o => o.IsDone && o.Migrated && o.Year == 2026);
        File.Exists(path).Should().BeTrue("renamed back");
        File.Exists(path + InventoryItem.RenamedSuffix).Should().BeFalse();
        AddinsSettingsDocument.TryParse(_tree.ReadSettings(2026)!, out _)!.IsDisabled(Kinship).Should().BeTrue("still switched off, now through the settings file");
        _tree.Ledger().HasSetting(2026, Kinship).Should().BeTrue("rst-c's own, so Unload turns it back on");
        File.Exists(stays + InventoryItem.RenamedSuffix).Should().BeTrue("a rename year keeps the rename");
        switching.MigrateLegacy().Should().BeNull("reported once");

        var back = switching.Execute(switching.Plan(switching.Inventory(), ActivationPolicy.Unload));
        back.Outcomes.Select(o => (o.Year, o.IsDone)).Should().BeEquivalentTo(new[] { (2025, true), (2026, true) });
        File.Exists(stays).Should().BeTrue();
    }

    [Fact]
    public void A_migration_that_cannot_rename_back_is_reported_once_and_stays_off()
    {
        var path = _tree.Manifest(_tree.MachineAddins(2026), "DiRoots.One.addin", "DiRoots.One", DiRoots, renamed: true);
        var fs = new FailingFileSystem();
        fs.MoveThrows[path + InventoryItem.RenamedSuffix] = new UnauthorizedAccessException("denied");
        var switching = new AddinSwitching(_tree.Roots, () => [Y2026], _tree.LedgerPath, fs);

        var report = switching.MigrateLegacy()!;
        report.Outcomes.Single().Should().Match<ActivationOutcome>(o => !o.IsDone && o.Reason == ChangeReason.NeedsAdmin && o.Migrated);
        ActivationWording.NotChangedLine(report.Outcomes.Single()).Should().Be(
            "DiRoots.One (2026): could not finish moving it from RST 3.0: installed for all users; changing it needs administrator rights");
        File.Exists(path + InventoryItem.RenamedSuffix).Should().BeTrue("still off");
        switching.MigrateLegacy().Should().BeNull("not retried and reported every time");
    }

    [Fact]
    public void Turning_on_a_v3_renamed_add_in_renames_it_back_first()
    {
        var path = _tree.Manifest(_tree.UserAddins(2026), "Kinship.addin", "Kinship", Kinship, renamed: true);
        var fs = new FailingFileSystem();
        fs.MoveThrows[path + InventoryItem.RenamedSuffix] = FailingFileSystem.SharingViolation();
        _tree.Run(ActivationPolicy.Unload, fs, Y2026).Report.Outcomes.Single().Reason.Should().Be(ChangeReason.InUse);
        _tree.Run(ActivationPolicy.Unload, null, Y2026).Report.Outcomes.Single().IsDone.Should().BeTrue();
        File.Exists(path).Should().BeTrue();
        File.Exists(_tree.Roots.SettingsFile(2026)).Should().BeFalse("nothing to flip: rst-c never wrote a setting for it");
    }
}
