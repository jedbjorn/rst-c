// rst-c's own (not vendored): the pure planner (spec #18, In Scope 2; AC2). It compares the
// inventory with a declarative target state and rst-c's own record, for every installed year
// (decision #16.1), and decides one action per add-in per year:
//
//   protected (RST itself, Revit's own add-ins, Autodesk-vendor add-ins; decision #16.3)
//       → never switched off; if rst-c switched one off before (v3.0 could), it goes back on.
//   Load with "Disable unused" on, not required
//       → Disable, unless it is already off (by anyone).
//   everything else (Load with Disable off, Load and required, Unload, Restore All; decision #16.2)
//       → Enable what rst-c switched off; leave alone what the user switched off in Autodesk's
//         manager (a required one is flagged so the Add-in Check can say so).
//
// The executor runs exactly the Enable and Disable entries of the plan the user confirmed.

using RST.Core.Profiles;

namespace RST.Core.Activation;

/// <summary>The target state: what is being done, the Disable switch, and the profile's required add-ins.</summary>
public sealed record ActivationPolicy(ActivationIntent Intent, bool DisableUnused, IReadOnlyList<RequiredAddin> Required)
{
    public static ActivationPolicy Load(IReadOnlyList<RequiredAddin>? required, bool disableUnused) =>
        new(ActivationIntent.Load, disableUnused, required ?? []);

    public static readonly ActivationPolicy Unload = new(ActivationIntent.Unload, false, []);

    public static readonly ActivationPolicy RestoreAll = new(ActivationIntent.RestoreAll, false, []);
}

public static class ActivationPlanner
{
    public static ActivationPlan Plan(AddinInventory inventory, ActivationPolicy policy, ActivationLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(ledger);
        if (policy.Intent == ActivationIntent.MigrateLegacy) return PlanMigration(inventory, ledger);

        var required = RequiredAddinMatch.RequiredItems(policy.Required, inventory);
        var switchOffUnused = policy.Intent == ActivationIntent.Load && policy.DisableUnused;
        var entries = new List<PlanEntry>();
        foreach (var item in inventory.Items)
        {
            var year = inventory.Year(item.Year);
            var unreadable = item.Mechanism == SwitchMechanism.SettingsFile && year?.Settings == SettingsFileState.Unreadable;
            var oursIds = OursIds(item, ledger);
            var ours = oursIds.Count > 0 || item.Renamed;
            var isRequired = required.Contains(item);

            var protectedReason = item.IsRst ? NotManagedReason.RstItself
                : item.Scope == AddinScope.RevitInstall ? NotManagedReason.RevitInstall
                : item.IsAutodesk ? NotManagedReason.Autodesk
                : (NotManagedReason?)null;

            if (protectedReason is not null)
            {
                if (ours && item.IsOff && item.Scope != AddinScope.RevitInstall)
                    entries.Add(Enable(item, oursIds, isRequired, unreadable));
                else
                    entries.Add(Entry(item, [], PlanAction.NotManaged, protectedReason, isRequired, null));
                continue;
            }

            if (switchOffUnused && !isRequired)
            {
                if (item.IsOff)
                    entries.Add(Entry(item, [], PlanAction.NoChange, null, false, null));
                else if (item.Mechanism == SwitchMechanism.SettingsFile && item.AddinIds.Count == 0)
                    entries.Add(Entry(item, [], PlanAction.NotManaged, NotManagedReason.NoAddinId, false, null));
                else
                {
                    var ids = item.Mechanism == SwitchMechanism.SettingsFile
                        ? item.AddinIds.Where(id => !item.DisabledIds.Contains(id)).ToList()
                        : [];
                    var block = item.Lock == AdminLock.AlwaysEnabled ? ChangeReason.AdminLocked
                        : unreadable ? ChangeReason.FileError
                        : (ChangeReason?)null;
                    entries.Add(Entry(item, ids, PlanAction.Disable, null, false, block));
                }
                continue;
            }

            if (!item.IsOff)
                entries.Add(Entry(item, [], PlanAction.NoChange, null, isRequired, null));
            else if (ours)
                entries.Add(Enable(item, oursIds, isRequired, unreadable));
            else if (isRequired)
                entries.Add(Entry(item, [], PlanAction.NotManaged,
                    item.Lock == AdminLock.AlwaysDisabled ? NotManagedReason.LockedOffByAdmin : NotManagedReason.SwitchedOffByUser, true, null));
            else
                entries.Add(Entry(item, [], PlanAction.NoChange, null, false, null));
        }

        return new ActivationPlan(policy.Intent, switchOffUnused, entries, Forget(inventory, ledger), Notes(inventory));
    }

    /// <summary>
    /// The one-time move of v3.0's renamed manifests (<c>X.addin.RSTdisabled</c>) in years with the
    /// Add-Ins Manager: the same add-in, switched off in the settings file, under its own name again
    /// (spec #18, In Scope 9). An add-in IT locks on stays renamed (renaming it back would load it),
    /// and so does one whose earlier move could not be finished (rst-c recorded it as a rename).
    /// </summary>
    public static ActivationPlan PlanMigration(AddinInventory inventory, ActivationLedger ledger)
    {
        var entries = new List<PlanEntry>();
        foreach (var item in inventory.Items)
        {
            if (item.Mechanism != SwitchMechanism.SettingsFile || !item.Renamed) continue;
            if (item.Scope == AddinScope.RevitInstall || item.AddinIds.Count == 0) continue;
            if (item.Lock == AdminLock.AlwaysEnabled || ledger.HasRename(item.Year, item.ManifestPath)) continue;
            var unreadable = inventory.Year(item.Year)?.Settings == SettingsFileState.Unreadable;
            var ids = item.AddinIds.Where(id => !item.DisabledIds.Contains(id)).ToList();
            entries.Add(new PlanEntry(item.Year, item.Name, item.VendorId, item.Scope, item.Mechanism, item.ManifestPath, item.FileName,
                ids, PlanAction.Disable, null, false, Renamed: true, Migrate: true, unreadable ? ChangeReason.FileError : null));
        }
        return new ActivationPlan(ActivationIntent.MigrateLegacy, false, entries, [], []);
    }

    /// <summary>The ClientIds of <paramref name="item"/> that rst-c switched off and are still off.</summary>
    private static IReadOnlyList<string> OursIds(InventoryItem item, ActivationLedger ledger) =>
        item.Mechanism == SwitchMechanism.SettingsFile
            ? item.AddinIds.Where(id => item.DisabledIds.Contains(id) && ledger.HasSetting(item.Year, id)).ToList()
            : [];

    private static PlanEntry Enable(InventoryItem item, IReadOnlyList<string> oursIds, bool required, bool unreadable)
    {
        var block = item.Lock == AdminLock.AlwaysDisabled ? ChangeReason.AdminLocked
            : unreadable && oursIds.Count > 0 ? ChangeReason.FileError
            : (ChangeReason?)null;
        return Entry(item, oursIds, PlanAction.Enable, null, required, block);
    }

    private static PlanEntry Entry(InventoryItem item, IReadOnlyList<string> ids, PlanAction action, NotManagedReason? reason, bool required, ChangeReason? block) =>
        new(item.Year, item.Name, item.VendorId, item.Scope, item.Mechanism, item.ManifestPath, item.FileName,
            ids, action, reason, required, item.Renamed, Migrate: false, block);

    /// <summary>
    /// Ledger entries that no longer describe an add-in rst-c switched off: it is on again (the user
    /// re-enabled it in Autodesk's manager), or it (or its Revit year) is gone. For an uninstalled
    /// add-in, the settings entry rst-c left at Disabled:true is reset as the record is cleared, so a
    /// later reinstall does not come back switched off (addins-manager-spike.md §7).
    /// </summary>
    private static IReadOnlyList<LedgerForget> Forget(AddinInventory inventory, ActivationLedger ledger)
    {
        var forget = new List<LedgerForget>();
        foreach (var e in ledger.Entries)
        {
            var yearItems = inventory.For(e.Year).ToList();
            if (e.Mechanism == SwitchMechanism.SettingsFile)
            {
                var id = e.AddinId ?? "";
                var holders = yearItems.Where(i => i.Mechanism == SwitchMechanism.SettingsFile && i.AddinIds.Contains(id)).ToList();
                if (holders.Count == 0)
                {
                    var year = inventory.Year(e.Year);
                    var reset = year is { Mechanism: SwitchMechanism.SettingsFile, Settings: SettingsFileState.Ok } && AddinIds.Normalize(id) is not null;
                    forget.Add(new LedgerForget(ActivationLedger.KeyOf(e), reset));
                }
                else if (!holders.Any(i => i.DisabledIds.Contains(id)))
                {
                    forget.Add(new LedgerForget(ActivationLedger.KeyOf(e), ResetSetting: false));
                }
            }
            else if (!yearItems.Any(i => i.Renamed && string.Equals(i.ManifestPath, e.ManifestPath, StringComparison.OrdinalIgnoreCase)))
            {
                forget.Add(new LedgerForget(ActivationLedger.KeyOf(e), ResetSetting: false));
            }
        }
        return forget;
    }

    private static IReadOnlyList<YearNote> Notes(AddinInventory inventory)
    {
        var notes = new List<YearNote>();
        foreach (var y in inventory.Years)
        {
            if (y.Mechanism != SwitchMechanism.SettingsFile) continue;
            if (y.Settings == SettingsFileState.Unreadable)
                notes.Add(new YearNote(y.Year, ActivationWording.SettingsUnreadableNote(y.Year)));
            if (y.UserDisableAll)
                notes.Add(new YearNote(y.Year, ActivationWording.UserDisableAllNote(y.Year)));
            if (y.AdminDisableAll)
                notes.Add(new YearNote(y.Year, ActivationWording.AdminDisableAllNote(y.Year)));
        }
        return notes;
    }
}
