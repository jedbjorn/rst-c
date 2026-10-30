// rst-c's own (not vendored): runs a confirmed ActivationPlan (spec #18, In Scope 3; AC3).
//
//   Revit with the Add-Ins Manager (2025.3+, 2026, 2027): one read-modify-write of the year's
//     AddInsSettings.json, atomic (temp file + replace), keeping every other field and entry,
//     appending missing entries and creating the file when absent. A one-time backup
//     (AddInsSettings.json.rst-bak) is kept beside it. The file is read back afterwards.
//   Revit 2025.0–2025.2: renames X.addin ↔ X.addin.RSTdisabled.
//   A v3.0 rename in a settings-file year is undone (renamed back) after the setting is written.
//
// It runs the plan's Enable and Disable entries and nothing else, and reports each one once:
// Done, or NotChanged with AdminLocked, NeedsAdmin, InUse, Vanished, FileError or Other. Admin locks
// are re-read at execution time. No exception escapes; rst-c's record (addin_switches.json) is
// updated with what actually changed.

namespace RST.Core.Activation;

public sealed class ActivationExecutor
{
    public const string BackupSuffix = ".rst-bak";

    private readonly AddinRoots _roots;
    private readonly string _ledgerPath;
    private readonly IActivationFileSystem _fs;
    private readonly Func<DateTime> _utcNow;

    /// <param name="roots">Where the settings and lock files are.</param>
    /// <param name="ledgerPath">rst-c's record; usually <see cref="ActivationLedger.DefaultPath"/>.</param>
    /// <param name="fs">The file operations; null is the real file system.</param>
    /// <param name="utcNow">The record's timestamps; null is now.</param>
    public ActivationExecutor(AddinRoots roots, string ledgerPath, IActivationFileSystem? fs = null, Func<DateTime>? utcNow = null)
    {
        _roots = roots ?? throw new ArgumentNullException(nameof(roots));
        _ledgerPath = ledgerPath ?? throw new ArgumentNullException(nameof(ledgerPath));
        _fs = fs ?? ActivationFileSystem.Instance;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>Runs <paramref name="plan"/> against rst-c's record on disk, and saves the record.</summary>
    public ActivationReport Execute(ActivationPlan plan) => Execute(plan, ActivationLedger.Load(_ledgerPath), save: true);

    /// <summary>Runs <paramref name="plan"/> and updates <paramref name="ledger"/> (saved when <paramref name="save"/>).</summary>
    public ActivationReport Execute(ActivationPlan plan, ActivationLedger ledger, bool save = true)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(ledger);
        var outcomes = new Dictionary<PlanEntry, ActivationOutcome>(ReferenceEqualityComparer.Instance);
        var notes = new List<string>(plan.Notes.Select(n => n.Text));
        var ledgerChanged = false;

        // Clear the record entries the plan found stale; collect the settings resets they ask for.
        var resets = new Dictionary<int, List<string>>();
        foreach (var f in plan.Forget)
        {
            ledgerChanged |= ledger.Remove(f.Key);
            if (f.ResetSetting && f.Key.AddinId is { } id)
            {
                if (!resets.TryGetValue(f.Key.Year, out var list)) resets[f.Key.Year] = list = [];
                list.Add(id);
            }
        }

        var changes = plan.Changes;
        var settingsYears = changes.Where(e => e.Mechanism == SwitchMechanism.SettingsFile).Select(e => e.Year).Concat(resets.Keys).Distinct().Order();
        foreach (var year in settingsYears)
        {
            var ops = changes.Where(e => e.Year == year && e.Mechanism == SwitchMechanism.SettingsFile).ToList();
            ledgerChanged |= RunSettingsYear(year, ops, resets.GetValueOrDefault(year) ?? [], ledger, outcomes);
        }
        foreach (var e in changes.Where(e => e.Mechanism == SwitchMechanism.Rename))
            ledgerChanged |= RunRename(e, ledger, outcomes);

        if (save && ledgerChanged)
        {
            try
            {
                ledger.Save(_ledgerPath, _fs);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                notes.Add(ActivationWording.LedgerSaveFailed + " (" + ex.Message + ").");
            }
        }

        // One outcome per planned change, in plan order.
        var ordered = changes.Select(e => outcomes.TryGetValue(e, out var o) ? o : ActivationOutcome.For(e, OutcomeResult.NotChanged, ChangeReason.Other, "not run")).ToList();
        return new ActivationReport(plan.Intent, ordered, notes);
    }

    private bool RunSettingsYear(int year, List<PlanEntry> ops, List<string> resetIds, ActivationLedger ledger, Dictionary<PlanEntry, ActivationOutcome> outcomes)
    {
        var ledgerChanged = false;
        var admin = AdminSettings.Read(_roots.AdminSettingsFile(year));
        var pending = new List<PlanEntry>();
        foreach (var e in ops)
        {
            if (e.ExpectedBlock == ChangeReason.AdminLocked || IsLocked(e, admin))
                outcomes[e] = ActivationOutcome.For(e, OutcomeResult.NotChanged, ChangeReason.AdminLocked);
            else if (!_fs.FileExists(e.ManifestPath) && !_fs.FileExists(e.ManifestPath + InventoryItem.RenamedSuffix))
                outcomes[e] = ActivationOutcome.For(e, OutcomeResult.NotChanged, ChangeReason.Vanished);
            else
                pending.Add(e);
        }

        // Turning on a v3.0-renamed add-in: rename it back first, so a failed rename leaves the setting alone.
        foreach (var e in pending.Where(e => e.Action == PlanAction.Enable && e.Renamed).ToList())
        {
            if (TryMove(e.ManifestPath + InventoryItem.RenamedSuffix, e.ManifestPath, e.Scope) is { } failure)
            {
                outcomes[e] = ActivationOutcome.For(e, OutcomeResult.NotChanged, failure.Reason, failure.Detail);
                pending.Remove(e);
            }
        }

        var flips = pending.Where(e => e.TargetIds.Count > 0).ToList();
        if (flips.Count > 0 || resetIds.Count > 0)
        {
            var path = _roots.SettingsFile(year);
            var written = WriteSettings(path, flips, resetIds, out var failure);
            if (!written)
            {
                foreach (var e in flips)
                {
                    outcomes[e] = ActivationOutcome.For(e, OutcomeResult.NotChanged, failure!.Reason, failure.Detail);
                    pending.Remove(e);
                }
            }
            else
            {
                var check = AddinInventory.ReadSettings(path).Document;
                foreach (var e in flips)
                {
                    var want = e.Action == PlanAction.Disable;
                    if (check is null || e.TargetIds.Any(id => check.IsDisabled(id) != want))
                    {
                        outcomes[e] = ActivationOutcome.For(e, OutcomeResult.NotChanged, ChangeReason.Other, "Revit's settings file did not keep the change");
                        pending.Remove(e);
                        continue;
                    }
                    foreach (var id in e.TargetIds)
                    {
                        if (want) ledger.Add(new LedgerEntry(year, id, e.Name, SwitchMechanism.SettingsFile, e.ManifestPath, _utcNow()));
                        else ledger.Remove(new LedgerKey(year, SwitchMechanism.SettingsFile, id, e.ManifestPath));
                        ledgerChanged = true;
                    }
                }
            }
        }

        // The v3.0 move: the setting now keeps it off; put the manifest back under its own name.
        foreach (var e in pending.Where(e => e.Migrate).ToList())
        {
            if (TryMove(e.ManifestPath + InventoryItem.RenamedSuffix, e.ManifestPath, e.Scope) is { } failure)
            {
                outcomes[e] = ActivationOutcome.For(e, OutcomeResult.NotChanged, failure.Reason, failure.Detail);
                // Still renamed, and still rst-c's: record it so the move is not retried (and reported) every time.
                ledger.Add(new LedgerEntry(year, null, e.Name, SwitchMechanism.Rename, e.ManifestPath, _utcNow()));
                ledgerChanged = true;
                pending.Remove(e);
            }
        }

        foreach (var e in pending)
        {
            if (e.Action == PlanAction.Enable && e.Renamed)
            {
                ledgerChanged |= ledger.Remove(new LedgerKey(year, SwitchMechanism.Rename, null, e.ManifestPath));
            }
            outcomes[e] = ActivationOutcome.For(e, OutcomeResult.Done);
        }
        return ledgerChanged;
    }

    /// <summary>The live lock check: IT's file is re-read when the plan runs.</summary>
    private static bool IsLocked(PlanEntry e, AdminSettings admin)
    {
        var blocking = e.Action == PlanAction.Disable ? AdminLock.AlwaysEnabled : AdminLock.AlwaysDisabled;
        if (e.Action == PlanAction.Enable && admin.DisableAll) return true;
        return e.TargetIds.Any(id => admin.LockFor(id, e.ManifestPath) == blocking)
               || (e.Migrate && e.TargetIds.Any(id => admin.LockFor(id, e.ManifestPath) == AdminLock.AlwaysEnabled));
    }

    private sealed record Failure(ChangeReason Reason, string? Detail);

    private bool WriteSettings(string path, IReadOnlyList<PlanEntry> flips, IReadOnlyList<string> resetIds, out Failure? failure)
    {
        failure = null;
        AddinsSettingsDocument? doc;
        var exists = false;
        try
        {
            exists = _fs.FileExists(path);
            if (exists)
            {
                doc = AddinsSettingsDocument.TryParse(_fs.ReadAllText(path), out var error);
                if (doc is null)
                {
                    // Never overwrite a file rst-c cannot read: Revit's own rebuild is the user's call.
                    failure = new Failure(ChangeReason.FileError, error);
                    return false;
                }
            }
            else
            {
                doc = AddinsSettingsDocument.CreateEmpty();
            }
        }
        catch (Exception ex)
        {
            failure = MapSettings(ex);
            return false;
        }

        var changed = false;
        foreach (var e in flips)
            foreach (var id in e.TargetIds)
                changed |= doc.SetDisabled(id, e.Action == PlanAction.Disable, e.Name, e.VendorId);
        foreach (var id in resetIds)
            if (doc.IsDisabled(id) == true) changed |= doc.SetDisabled(id, false, null, null);
        if (!changed) return true;

        try
        {
            if (exists && !_fs.FileExists(path + BackupSuffix)) _fs.Copy(path, path + BackupSuffix);
            _fs.WriteAtomic(path, doc.ToBytes());
            return true;
        }
        catch (Exception ex)
        {
            failure = MapSettings(ex);
            return false;
        }
    }

    private bool RunRename(PlanEntry e, ActivationLedger ledger, Dictionary<PlanEntry, ActivationOutcome> outcomes)
    {
        var renamed = e.ManifestPath + InventoryItem.RenamedSuffix;
        var (source, destination) = e.Action == PlanAction.Disable ? (e.ManifestPath, renamed) : (renamed, e.ManifestPath);
        if (!_fs.FileExists(source))
        {
            outcomes[e] = _fs.FileExists(destination)
                ? ActivationOutcome.For(e, OutcomeResult.Done)
                : ActivationOutcome.For(e, OutcomeResult.NotChanged, ChangeReason.Vanished);
            if (!_fs.FileExists(destination)) return false;
        }
        else if (TryMove(source, destination, e.Scope) is { } failure)
        {
            outcomes[e] = ActivationOutcome.For(e, OutcomeResult.NotChanged, failure.Reason, failure.Detail);
            return false;
        }
        else
        {
            outcomes[e] = ActivationOutcome.For(e, OutcomeResult.Done);
        }

        if (e.Action == PlanAction.Disable)
        {
            ledger.Add(new LedgerEntry(e.Year, null, e.Name, SwitchMechanism.Rename, e.ManifestPath, _utcNow()));
            return true;
        }
        return ledger.Remove(new LedgerKey(e.Year, SwitchMechanism.Rename, null, e.ManifestPath));
    }

    private Failure? TryMove(string source, string destination, AddinScope scope)
    {
        try
        {
            if (_fs.FileExists(destination))
                return new Failure(ChangeReason.Other, "both " + Path.GetFileName(destination) + " and " + Path.GetFileName(source) + " exist");
            _fs.Move(source, destination);
            return null;
        }
        catch (Exception ex)
        {
            return MapRename(ex, scope);
        }
    }

    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);

    private static bool IsInUse(Exception ex) => ex is IOException && (ex.HResult == SharingViolation || ex.HResult == LockViolation || ex.HResult == 32 || ex.HResult == 33);

    private static Failure MapRename(Exception ex, AddinScope scope) => ex switch
    {
        UnauthorizedAccessException => scope == AddinScope.Machine
            ? new Failure(ChangeReason.NeedsAdmin, null)
            : new Failure(ChangeReason.FileError, ex.Message),
        FileNotFoundException or DirectoryNotFoundException => new Failure(ChangeReason.Vanished, null),
        _ when IsInUse(ex) => new Failure(ChangeReason.InUse, null),
        _ => new Failure(ChangeReason.Other, ex.Message),
    };

    private static Failure MapSettings(Exception ex) => ex switch
    {
        _ when IsInUse(ex) => new Failure(ChangeReason.InUse, null),
        IOException or UnauthorizedAccessException => new Failure(ChangeReason.FileError, ex.Message),
        _ => new Failure(ChangeReason.Other, ex.Message),
    };
}
