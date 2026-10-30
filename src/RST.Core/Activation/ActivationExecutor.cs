// rst-c's own (not vendored): runs a confirmed ActivationPlan (spec #18, In Scope 3; AC3).
//
// Every supported year (2025, 2026, 2027; decision #21) switches through its AddInsSettings.json:
// one read-modify-write per year, atomic (temp file + replace), keeping every other field and
// entry, appending missing entries and creating the file when absent. A one-time backup
// (AddInsSettings.json.rst-bak) is kept beside it, and the file is read back afterwards.
//
// The only rename left is undoing RST 3.0's X.addin.RSTdisabled: after the setting is written (the
// one-time move), or before it is cleared (switching such an add-in back on).
//
// It runs the plan's Enable and Disable entries and its record clean-up, nothing else, and reports
// each change once: Done, or NotChanged with AdminLocked, NeedsAdmin (a v3.0 undo of a manifest
// installed for all users), InUse, Vanished, FileError or Other. The clean-up is one "Tidied up"
// note. Admin locks are re-read at execution time. No exception escapes. RST's record
// (addin_switches.json) is updated with what actually changed, merged with what is on disk.

namespace RST.Core.Activation;

public sealed class ActivationExecutor
{
    public const string BackupSuffix = ".rst-bak";

    private readonly AddinRoots _roots;
    private readonly string _ledgerPath;
    private readonly IActivationFileSystem _fs;
    private readonly Func<DateTime> _utcNow;

    /// <param name="roots">Where the settings and lock files are.</param>
    /// <param name="ledgerPath">RST's record; usually <see cref="ActivationLedger.DefaultPath"/>.</param>
    /// <param name="fs">The file operations; null is the real file system.</param>
    /// <param name="utcNow">The record's timestamps; null is now.</param>
    public ActivationExecutor(AddinRoots roots, string ledgerPath, IActivationFileSystem? fs = null, Func<DateTime>? utcNow = null)
    {
        _roots = roots ?? throw new ArgumentNullException(nameof(roots));
        _ledgerPath = ledgerPath ?? throw new ArgumentNullException(nameof(ledgerPath));
        _fs = fs ?? ActivationFileSystem.Instance;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>Runs <paramref name="plan"/> against RST's record on disk, and saves the record.</summary>
    public ActivationReport Execute(ActivationPlan plan) => Execute(plan, ActivationLedger.Load(_ledgerPath), save: true);

    /// <summary>
    /// Runs <paramref name="plan"/> and updates <paramref name="ledger"/>. When <paramref name="save"/>,
    /// the same changes are applied to the record on disk, re-read under a lock (review N9).
    /// </summary>
    public ActivationReport Execute(ActivationPlan plan, ActivationLedger ledger, bool save = true)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(ledger);
        var outcomes = new Dictionary<PlanEntry, ActivationOutcome>(ReferenceEqualityComparer.Instance);
        var notes = new List<string>(plan.Notes.Select(n => n.Text));
        var record = new RecordChanges(ledger);

        // Clear the record entries the plan found stale; collect the settings resets they ask for.
        var resets = new Dictionary<int, List<string>>();
        foreach (var f in plan.Forget)
        {
            record.Remove(f.Key);
            if (f.ResetSetting && f.Key.AddinId is { } id)
            {
                if (!resets.TryGetValue(f.Key.Year, out var list)) resets[f.Key.Year] = list = [];
                list.Add(id);
            }
        }

        var changes = plan.Changes;
        var resetDone = new List<int>();
        foreach (var year in changes.Select(e => e.Year).Concat(resets.Keys).Distinct().Order())
        {
            var ops = changes.Where(e => e.Year == year).ToList();
            if (RunYear(year, ops, resets.GetValueOrDefault(year) ?? [], record, outcomes)) resetDone.Add(year);
        }

        if (ActivationWording.TidiedUpNote(plan.Forget.Count, resetDone) is { } tidied) notes.Add(tidied);

        if (save && record.Count > 0)
        {
            try
            {
                ActivationLedger.SaveMerged(_ledgerPath, record.Changes, _fs);
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

    /// <summary>Runs one year's changes and resets; true when a reset was written.</summary>
    private bool RunYear(int year, List<PlanEntry> ops, List<string> resetIds, RecordChanges record, Dictionary<PlanEntry, ActivationOutcome> outcomes)
    {
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

        // Switching a v3.0-renamed add-in back on: rename it back first, so a failed rename leaves the setting alone.
        foreach (var e in pending.Where(e => e.Action == PlanAction.Enable && e.Renamed).ToList())
        {
            if (TryMove(e.ManifestPath + InventoryItem.RenamedSuffix, e.ManifestPath, e.Scope) is { } failure)
            {
                outcomes[e] = ActivationOutcome.For(e, OutcomeResult.NotChanged, failure.Reason, failure.Detail);
                pending.Remove(e);
            }
        }

        var flips = pending.Where(e => e.TargetIds.Count > 0).ToList();
        var resetWritten = false;
        if (flips.Count > 0 || resetIds.Count > 0)
        {
            var path = _roots.SettingsFile(year);
            var written = WriteSettings(path, flips, resetIds, out var failure, out resetWritten);
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
                var check = ReadBack(path);
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
                        if (want) record.Add(new LedgerEntry(year, id, e.Name, LedgerKind.Setting, e.ManifestPath, _utcNow()));
                        else record.Remove(new LedgerKey(year, LedgerKind.Setting, id, e.ManifestPath));
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
                // Still renamed, and still RST's: record it so the move is not retried (and reported) every time.
                record.Add(new LedgerEntry(year, null, e.Name, LedgerKind.V30Rename, e.ManifestPath, _utcNow()));
                pending.Remove(e);
            }
        }

        foreach (var e in pending)
        {
            if (e.Action == PlanAction.Enable && e.Renamed)
                record.Remove(new LedgerKey(year, LedgerKind.V30Rename, null, e.ManifestPath));
            outcomes[e] = ActivationOutcome.For(e, OutcomeResult.Done);
        }
        return resetWritten;
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

    /// <summary>Reads the file back through the same seam it was written with.</summary>
    private AddinsSettingsDocument? ReadBack(string path)
    {
        try
        {
            return _fs.FileExists(path) ? AddinsSettingsDocument.TryParse(_fs.ReadAllText(path), out _) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private bool WriteSettings(string path, IReadOnlyList<PlanEntry> flips, IReadOnlyList<string> resetIds, out Failure? failure, out bool resetWritten)
    {
        failure = null;
        resetWritten = false;
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
                    // Never overwrite a file RST cannot read: Revit's own rebuild is the user's call.
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
        var resets = false;
        foreach (var id in resetIds)
            if (doc.IsDisabled(id) == true) resets |= doc.SetDisabled(id, false, null, null);
        if (!changed && !resets) return true;

        try
        {
            if (exists && !_fs.FileExists(path + BackupSuffix)) _fs.Copy(path, path + BackupSuffix);
            _fs.WriteAtomic(path, doc.ToBytes());
            resetWritten = resets;
            return true;
        }
        catch (Exception ex)
        {
            failure = MapSettings(ex);
            return false;
        }
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

    /// <summary>The record changes of one run: applied to the in-memory record now, and replayed on the file when it is saved.</summary>
    private sealed class RecordChanges(ActivationLedger ledger)
    {
        private readonly List<Action<ActivationLedger>> _changes = [];

        public IReadOnlyList<Action<ActivationLedger>> Changes => _changes;

        public int Count => _changes.Count;

        public void Add(LedgerEntry entry) => Apply(l => l.Add(entry));

        public void Remove(LedgerKey key) => Apply(l => l.Remove(key));

        private void Apply(Action<ActivationLedger> change)
        {
            change(ledger);
            _changes.Add(change);
        }
    }
}
