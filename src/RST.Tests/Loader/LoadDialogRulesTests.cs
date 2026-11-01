// rst-c's own (not vendored): SC-048 F6. No-op loads avoid modals; blocked changes, notes and
// missing/off required add-ins still reach the report. These decisions run on the Linux CI leg.

using RST.Core.Activation;
using RST.Core.Loader;
using Xunit;

namespace RST.Tests.Loader;

public class LoadDialogRulesTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void No_changes_need_neither_confirmation_nor_report(bool disableUnused)
    {
        var inventory = FakeAddinSwitching.InventoryOf(FakeAddinSwitching.Item(2026, "Probe", off: true));
        var plan = ActivationPlanner.Plan(inventory, ActivationPolicy.Load([], disableUnused), new ActivationLedger());
        Assert.False(LoadDialogRules.NeedsConfirmation(plan));
        Assert.False(LoadDialogRules.NeedsReport(FakeAddinSwitching.AllDone(plan), QaCheck.Clear));
    }

    [Theory]
    [InlineData(AdminLock.None, true)]
    [InlineData(AdminLock.AlwaysEnabled, false)]
    public void Admin_locked_changes_need_only_the_report(AdminLock adminLock, bool confirm)
    {
        var inventory = FakeAddinSwitching.InventoryOf(FakeAddinSwitching.Item(2026, "Probe", adminLock: adminLock));
        var plan = ActivationPlanner.Plan(inventory, ActivationPolicy.Load([], true), new ActivationLedger());
        Assert.True(plan.HasChanges);
        Assert.Equal(confirm, LoadDialogRules.NeedsConfirmation(plan));
        Assert.True(LoadDialogRules.NeedsReport(FakeAddinSwitching.AllDone(plan), QaCheck.Clear));
    }

    [Fact]
    public void Ledger_cleanup_needs_no_confirmation_but_its_note_is_reported()
    {
        var plan = ActivationPlan.Nothing with { Forget = [new(new LedgerKey(2026, LedgerKind.Setting, "probe", "Probe.addin"), false)] };
        Assert.True(plan.HasWork);
        Assert.False(LoadDialogRules.NeedsConfirmation(plan));
        var report = ActivationReport.Empty(ActivationIntent.Load, ActivationWording.TidiedUpNote(plan.Forget.Count, [])!);
        Assert.True(LoadDialogRules.NeedsReport(report, QaCheck.Clear));
    }

    [Fact]
    public void An_unreadable_settings_file_still_needs_confirmation_because_execution_can_retry_it()
    {
        var inventory = FakeAddinSwitching.InventoryOf(FakeAddinSwitching.Item(2026, "Probe"));
        inventory = new AddinInventory(inventory.Years.Select(y => y with { Settings = SettingsFileState.Unreadable }).ToList(), inventory.Items);
        var plan = ActivationPlanner.Plan(inventory, ActivationPolicy.Load([], true), new ActivationLedger());
        Assert.Equal(ChangeReason.FileError, Assert.Single(plan.Changes).ExpectedBlock);
        Assert.True(LoadDialogRules.NeedsConfirmation(plan));
    }

    [Fact]
    public void Missing_or_switched_off_requirements_still_need_a_report()
    {
        var report = ActivationReport.Empty(ActivationIntent.Load);
        Assert.True(LoadDialogRules.NeedsReport(report, new QaCheck([new("Probe", "Probe.addin", "")], [])));
        Assert.True(LoadDialogRules.NeedsReport(report, new QaCheck([], ["Probe is switched off"])));
    }

    [Fact]
    public void Notes_including_plan_failures_still_need_a_report()
    {
        Assert.False(LoadDialogRules.NeedsConfirmation(null));
        Assert.True(LoadDialogRules.NeedsReport(ActivationReport.Empty(ActivationIntent.Load, "plan failed"), QaCheck.Clear));
    }
}
