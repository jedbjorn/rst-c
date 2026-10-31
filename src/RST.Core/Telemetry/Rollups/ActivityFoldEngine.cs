// rst-c's own (not vendored): the fold engine the tray worker drives
// (decision #52; spec #24, Fold & Self-Healing, Tray Worker, Cleanup). The
// tray is the only writer of rollups; this class holds every rule, the tray
// only schedules it (WU3).
//
// A pass, under an exclusive lock file in the rollups folder:
//   1. list the outbox and compare it with the ledger; verify each rollup
//      against its index row (length + last write), restoring a corrupt one
//      from .bak.json and re-folding the sessions it lost;
//   2. fold one changed or new closed session file per step, oldest first:
//      write each touched rollup, then index.json, then ledger.json — the
//      ledger last, so a crash between writes re-folds that file next pass.
//      A changed file replaces its share even when it was sealed; the seal
//      is cleared and comes back only on a later unchanged pass;
//   3. maintenance and sealing: age event windows that expired, seal files
//      folded in an earlier pass and unchanged since, carry a seal into
//      .bak.json when the model is not written again, absorb the shares of
//      files that are gone;
//   4. clean up: delete a raw file only when — re-checked under a handle
//      that blocks writers, right before the delete — it is unchanged since
//      it was folded, closed and not live, its share is sealed for exactly
//      that file in every model it touched in both the rollup and its
//      .bak.json, it is not one of the 3 newest closed files, and every
//      registered retention gate allows it.
// Every failure is per file: logged, left for the next pass, never thrown.
// A failed index or ledger write stops the pass before cleanup.

using System.IO;
using RST.Core.Configuration;

namespace RST.Core.Telemetry.Rollups;

/// <summary>Where the store lives and how it decides; defaults are production's.</summary>
public sealed class RollupStoreOptions
{
    public string OutboxDir { get; init; } = AppDataPaths.TelemetryOutboxDir;
    public string RollupsDir { get; init; } = AppDataPaths.TelemetryRollupsDir;

    /// <summary>The zone that defines a calendar day — the user's local zone.</summary>
    public TimeZoneInfo Zone { get; init; } = TimeZoneInfo.Local;

    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;
    public Action<string>? Log { get; init; }

    /// <summary>True when a session file is live (a Revit instance holds it). Default: the
    /// outbox lock probe (<see cref="OutboxFiles.IsLocked"/>).</summary>
    public Func<string, bool> IsLive { get; init; } = OutboxFiles.IsLocked;

    /// <summary>Retention gates; null = <see cref="RetentionGates.Registered"/>.</summary>
    public IReadOnlyList<IRetentionGate>? Gates { get; init; }

    /// <summary>The newest closed session files cleanup always keeps (a re-fold buffer).</summary>
    public int KeepNewestClosed { get; init; } = 3;

    /// <summary>Test seam, called at named points ("models" and "index" in a fold step,
    /// "delete:&lt;file name&gt;" just before cleanup takes a file); throwing there simulates a
    /// crash, writing there simulates a concurrent change.</summary>
    internal Action<string>? FaultHook { get; init; }

    internal RollupFileIO? FileIO { get; init; }

    /// <summary>Test seam: how cleanup takes a raw file before deleting it.</summary>
    internal IFileTaker? FileTaker { get; init; }
}

public sealed record FoldPassResult(
    bool Skipped,
    bool NothingChanged,
    int Folded,
    int Failed,
    int Sealed,
    int Deleted);

public sealed class ActivityFoldEngine
{
    private readonly RollupStoreOptions _options;

    public ActivityFoldEngine(RollupStoreOptions? options = null)
    {
        _options = options ?? new RollupStoreOptions();
    }

    public RollupStoreOptions Options => _options;

    /// <summary>Start a pass; null when another process (a second tray) holds the lock,
    /// or the store cannot be opened (logged).</summary>
    public FoldPass? TryBeginPass()
    {
        var handle = TryLock(_options.RollupsDir, _options.Log);
        if (handle is null) return null;
        try
        {
            return new FoldPass(_options, handle);
        }
        catch (Exception ex)
        {
            handle.Dispose();
            SafeLog(_options.Log, "activity fold: pass could not start: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// One whole pass: when the outbox matches the ledger and nothing awaits sealing,
    /// maintenance or cleanup it ends at once; otherwise it folds every pending file (calling
    /// <paramref name="betweenSteps"/> after each — the worker's pause), then seals and cleans
    /// up. Never throws: a failure is logged and counted in <see cref="FoldPassResult.Failed"/>.
    /// </summary>
    public FoldPassResult RunPass(Action? betweenSteps = null, CancellationToken cancel = default)
    {
        FoldPass? pass = null;
        int sealedCount = 0, deleted = 0;
        try
        {
            pass = TryBeginPass();
            if (pass is null) return new FoldPassResult(true, false, 0, 0, 0, 0);
            if (!pass.HasWork) return new FoldPassResult(false, true, 0, 0, 0, 0);
            while (!cancel.IsCancellationRequested && pass.FoldNext())
            {
                try { betweenSteps?.Invoke(); } catch { /* the pause must not end the pass */ }
            }
            if (!cancel.IsCancellationRequested)
            {
                sealedCount = pass.Seal();
                deleted = pass.Cleanup();
            }
            return new FoldPassResult(false, false, pass.Folded, pass.Failed, sealedCount, deleted);
        }
        catch (Exception ex)
        {
            SafeLog(_options.Log, "activity fold: pass failed (retried next pass): " + ex.Message);
            return new FoldPassResult(false, false, pass?.Folded ?? 0, (pass?.Failed ?? 0) + 1, sealedCount, deleted);
        }
        finally
        {
            try { pass?.Dispose(); }
            catch (Exception ex) { SafeLog(_options.Log, "activity fold: pass end failed: " + ex.Message); }
        }
    }

    /// <summary>The exclusive lock file; null when held elsewhere.</summary>
    internal static IDisposable? TryLock(string rollupsDir, Action<string>? log)
    {
        try
        {
            Directory.CreateDirectory(rollupsDir);
            return new FileStream(Path.Combine(rollupsDir, RollupStore.LockFile),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException ex)
        {
            SafeLog(log, "activity fold: lock refused: " + ex.Message);
            return null;
        }
    }

    internal static void SafeLog(Action<string>? log, string message)
    {
        try { log?.Invoke(message); }
        catch { /* logging must never take the fold down */ }
    }
}

/// <summary>One fold pass, holding the lock until disposed. Steps: <see cref="FoldNext"/>
/// until false, then <see cref="Seal"/>, then <see cref="Cleanup"/>.</summary>
public sealed class FoldPass : IDisposable
{
    private readonly RollupStoreOptions _o;
    private readonly IDisposable _lock;
    private readonly RollupStore _store;
    private readonly FoldLedger _ledger;
    private readonly RollupIndexFile _index;
    private readonly long _pass;
    private readonly DateTimeOffset _now;
    private readonly DateOnly _today;
    private readonly Dictionary<string, OutboxFile> _outbox;
    private readonly HashSet<string> _outboxGuids;
    private readonly List<OutboxFile> _toFold;
    private readonly HashSet<string> _closedSeen = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _currentAtStart;
    private readonly List<string> _expiring;
    private int _cursor;
    private bool _indexDirty;
    private bool _ledgerDirty;
    private bool _persistFailed;
    private bool _disposed;

    private sealed record OutboxFile(string Name, string Path, string? SessionGuid, long Size, DateTimeOffset LastWriteUtc);

    internal FoldPass(RollupStoreOptions options, IDisposable lockHandle)
    {
        _o = options;
        _lock = lockHandle;
        _store = new RollupStore(options.RollupsDir, options.FileIO, options.Log);
        _now = options.Clock();
        _today = ActiveTime.LocalDay(_now, options.Zone);
        _ledger = _store.LoadLedger(ReadMode.Writer);
        _pass = _ledger.Pass + 1;
        _index = _store.LoadIndex(ReadMode.Writer, _today, out var rebuilt);
        _indexDirty = rebuilt;

        _outbox = ListOutbox();
        _outboxGuids = new HashSet<string>(_outbox.Values.Select(f => f.SessionGuid).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var row in _ledger.Files)
            if (_outbox.ContainsKey(row.Name) && row.SessionGuid.Length > 0) _outboxGuids.Add(row.SessionGuid);
        _ledgerDirty = _ledger.Files.Any(r => !_outbox.ContainsKey(r.Name));

        _toFold = new List<OutboxFile>();
        VerifyRollups();

        foreach (var f in _outbox.Values
            .Where(f => LedgerRow(f.Name) is not { } row || !SameFingerprint(row, f))
            .OrderBy(f => f.LastWriteUtc).ThenBy(f => f.Name, StringComparer.Ordinal))
            if (!_toFold.Contains(f)) _toFold.Add(f);
        _currentAtStart = new HashSet<string>(_ledger.Files
            .Where(r => r.Models.Any(m => m.Sealed == SealStates.Current))
            .Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
        _expiring = _index.Models
            .Where(m => m.NextEventExpiryUtc is { } at && at <= _now)
            .Select(m => m.ModelId)
            .ToList();
    }

    public int Folded { get; private set; }
    public int Failed { get; private set; }

    /// <summary>Session files not yet folded (or changed since) that this pass will try.</summary>
    public int RemainingToFold => _toFold.Count - _cursor;

    /// <summary>False when the outbox matches the ledger and nothing awaits sealing, carrying
    /// a seal into .bak.json, event-window maintenance or cleanup — the pass can end at once.</summary>
    public bool HasWork =>
        RemainingToFold > 0 || _indexDirty || _ledgerDirty || _expiring.Count > 0 ||
        _ledger.Files.Any(r => r.Unclosed != true && r.Models.Any(m => m.Sealed != SealStates.Both)) ||
        DeletionCandidates().Any();

    /// <summary>Fold the oldest pending closed session file. True when a file was folded or
    /// failed (one step); false when none remain this pass. Live and unrecovered files are
    /// skipped, never folded.</summary>
    public bool FoldNext()
    {
        while (_cursor < _toFold.Count)
        {
            var f = _toFold[_cursor++];
            try
            {
                if (_o.IsLive(f.Path))
                {
                    RememberUnclosed(f);
                    continue;
                }
                var (size, lastWrite) = RollupStore.Stat(f.Path);
                if (size < 0) continue;
                var events = OutboxFiles.ReadEvents(f.Path);
                var info = SessionInfo.Read(events, f.Name);
                if (!info.Closed || info.SessionGuid is null)
                {
                    RememberUnclosed(f with { Size = size, LastWriteUtc = lastWrite }); // recovery's job first
                    continue;
                }
                _closedSeen.Add(f.Name);
                FoldFile(f with { Size = size, LastWriteUtc = lastWrite }, events, info);
                Folded++;
                return true;
            }
            catch (Exception ex)
            {
                Failed++;
                Log("activity fold failed for " + f.Name + " (retried next pass): " + ex.Message);
                return true;
            }
        }
        return false;
    }

    /// <summary>Maintenance and sealing: age expired event windows; absorb the shares of files
    /// that vanished (pruned by age or by hand); seal every file folded in an earlier pass and
    /// unchanged since; carry seals made in an earlier pass into .bak.json. Returns the number of
    /// seals made.</summary>
    public int Seal()
    {
        foreach (var id in _expiring)
        {
            try { Maintain(id); }
            catch (Exception ex) { Log("activity event-window maintenance failed for " + id + ": " + ex.Message); }
        }
        _expiring.Clear();

        var count = 0;
        foreach (var row in _ledger.Files.ToList())
        {
            try
            {
                if (!_outbox.TryGetValue(row.Name, out var file))
                {
                    // The raw file is gone: nothing can replace its share any more.
                    foreach (var m in row.Models) Maintain(m.ModelId);
                    _ledger.Files.Remove(row);
                    _ledgerDirty = true;
                    continue;
                }
                if (row.Unclosed == true || !SameFingerprint(row, file) || row.FoldedPass >= _pass) continue;
                foreach (var m in row.Models)
                {
                    if (m.Sealed == SealStates.No)
                    {
                        if (SealInModel(m.ModelId, row) is { } sealedNow)
                        {
                            m.Sealed = SealStates.Current;
                            _ledgerDirty = true;
                            if (sealedNow) count++;
                        }
                        else
                        {
                            MarkForRefold(row);
                        }
                    }
                    else if (m.Sealed == SealStates.Current && _currentAtStart.Contains(row.Name))
                    {
                        m.Sealed = CarryToBackup(m.ModelId, row);
                        if (m.Sealed == SealStates.No) MarkForRefold(row);
                        _ledgerDirty = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("activity seal failed for " + row.Name + " (retried next pass): " + ex.Message);
            }
        }
        TryFlush();
        return count;
    }

    /// <summary>Delete raw files that pass every cleanup rule. Returns the number deleted. Does
    /// nothing after a failed index or ledger write in this pass.</summary>
    public int Cleanup()
    {
        if (_persistFailed) return 0;
        var deleted = 0;
        var gates = _o.Gates ?? RetentionGates.Registered;
        var touched = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var row in DeletionCandidates().ToList())
        {
            try
            {
                if (TryDelete(row, gates))
                {
                    _outbox.Remove(row.Name);
                    _outboxGuids.Remove(row.SessionGuid);
                    _ledger.Files.Remove(row);
                    _ledgerDirty = true;
                    foreach (var m in row.Models) touched.Add(m.ModelId);
                    deleted++;
                }
            }
            catch (Exception ex)
            {
                Log("activity cleanup skipped " + row.Name + ": " + ex.Message);
            }
        }
        // The deleted files' shares move into the permanent totals; .bak.json keeps the
        // sealed shares until the model's next write.
        foreach (var id in touched)
        {
            try { Maintain(id); }
            catch (Exception ex) { Log("activity cleanup: absorbing into " + id + " failed (retried next pass): " + ex.Message); }
        }
        TryFlush();
        return deleted;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { TryFlush(); }
        finally { _lock.Dispose(); }
    }

    // ---- folding ----------------------------------------------------------

    private void FoldFile(OutboxFile f, List<TelemetryEvent> events, SessionInfo info)
    {
        var guid = info.SessionGuid!;
        var contributions = ActivityFold.FoldSession(events, info, _o.Zone, DateTimeOffset.MaxValue);
        var oldRow = LedgerRow(f.Name);
        var newIds = new HashSet<string>(contributions.Select(c => c.Key.ModelId), StringComparer.Ordinal);
        var written = new Dictionary<string, (ModelRollup Rollup, long Bytes, DateTimeOffset Ts)>(StringComparer.Ordinal);
        var states = new List<LedgerModel>();

        // Load everything first: a rollup this build may not write stops the step before any write.
        var loaded = new List<(ModelContribution C, ModelRollup R, bool Repaired)>();
        foreach (var c in contributions)
        {
            var (r, outcome) = _store.LoadModel(c.Key.ModelId, ReadMode.Writer);
            if (outcome is ReadOutcome.Restored or ReadOutcome.Lost) Heal(c.Key.ModelId, f.Name);
            loaded.Add((c, r ?? RollupEditor.Create(c.Key, c.Defining), outcome != ReadOutcome.Ok));
        }

        foreach (var (c, r, repaired) in loaded)
        {
            var pending = RollupEditor.ToPending(c, f.Name, f.Size, f.LastWriteUtc);
            var changed = RollupEditor.ApplyPending(r, guid, pending, c.IdentityEvents, _o.Zone, _now);
            changed |= RollupEditor.AbsorbGone(r, SessionPresent);
            var path = _store.ModelPath(r.ModelId);
            var (bytes, ts) = changed || repaired || !File.Exists(path) ? _store.SaveModel(r) : RollupStore.Stat(path);
            written[r.ModelId] = (r, bytes, ts);
            states.Add(new LedgerModel
            {
                ModelId = r.ModelId,
                Sealed = r.IsSealed(guid, f.Size, f.LastWriteUtc) ? SealStates.Current : SealStates.No,
            });
        }

        // Models the file no longer touches (it changed after folding) lose its share.
        foreach (var gone in oldRow?.Models.Select(m => m.ModelId).Where(id => !newIds.Contains(id)) ?? Enumerable.Empty<string>())
        {
            var (r, _) = _store.LoadModel(gone, ReadMode.Writer);
            if (r is null) continue;
            if (RollupEditor.RemovePending(r, guid, _o.Zone, _now))
            {
                var (bytes, ts) = _store.SaveModel(r);
                written[r.ModelId] = (r, bytes, ts);
            }
        }
        _o.FaultHook?.Invoke("models");

        foreach (var (r, bytes, ts) in written.Values) UpsertIndexRow(RollupEditor.BuildIndexRow(r, _today, bytes, ts));
        _indexDirty = true;
        _store.SaveIndex(_index, _now);
        _indexDirty = false;
        _o.FaultHook?.Invoke("index");

        var row = oldRow ?? new LedgerRow { Name = f.Name };
        if (oldRow is null) _ledger.Files.Add(row);
        row.SessionGuid = guid;
        row.Size = f.Size;
        row.LastWriteUtc = f.LastWriteUtc;
        row.FoldedPass = _pass;
        row.Unclosed = null;
        row.Models = states.OrderBy(s => s.ModelId, StringComparer.Ordinal).ToList();
        _ledgerDirty = true;
        SaveLedger();
    }

    // ---- maintenance and sealing ------------------------------------------

    /// <summary>Age the event window and absorb shares whose raw file is gone; write when
    /// anything changed.</summary>
    private void Maintain(string modelId)
    {
        var (r, outcome) = _store.LoadModel(modelId, ReadMode.Writer);
        if (outcome is ReadOutcome.Restored or ReadOutcome.Lost) Heal(modelId, null);
        if (r is null) return;
        var opens = r.Opens.Count;
        var syncs = r.Syncs.Count;
        RollupEditor.TrimEvents(r, _now);
        var changed = r.Opens.Count != opens || r.Syncs.Count != syncs;
        changed |= RollupEditor.AbsorbGone(r, SessionPresent);
        if (!changed && outcome == ReadOutcome.Ok) return;
        var (bytes, ts) = _store.SaveModel(r);
        UpsertIndexRow(RollupEditor.BuildIndexRow(r, _today, bytes, ts));
        _indexDirty = true;
    }

    /// <summary>Seal the session in one model, for the ledger row's file fingerprint. True =
    /// sealed now, false = already sealed, null = the rollup does not hold this file's share
    /// (re-fold it).</summary>
    private bool? SealInModel(string modelId, LedgerRow row)
    {
        var (r, outcome) = _store.LoadModel(modelId, ReadMode.Writer);
        if (outcome is ReadOutcome.Restored or ReadOutcome.Lost) Heal(modelId, row.Name);
        if (r is null || !r.Holds(row.SessionGuid, row.Size, row.LastWriteUtc)) return null;
        if (r.IsSealed(row.SessionGuid, row.Size, row.LastWriteUtc)) return false;
        RollupEditor.Seal(r, row.SessionGuid, row.Size, row.LastWriteUtc);
        RollupEditor.AbsorbGone(r, SessionPresent);
        var (bytes, ts) = _store.SaveModel(r);
        UpsertIndexRow(RollupEditor.BuildIndexRow(r, _today, bytes, ts));
        _indexDirty = true;
        return true;
    }

    /// <summary>A seal made in an earlier pass: when .bak.json lacks it (the model was not
    /// written since), write the verified current rollup again so .bak.json becomes a copy.</summary>
    private string CarryToBackup(string modelId, LedgerRow row)
    {
        var bak = _store.ReadCopy(modelId, backup: true, out _);
        if (bak is not null && bak.IsSealed(row.SessionGuid, row.Size, row.LastWriteUtc)) return SealStates.Both;
        var current = _store.ReadCopy(modelId, backup: false, out var bytes);
        if (current is null || bytes is null || !current.IsSealed(row.SessionGuid, row.Size, row.LastWriteUtc))
            return SealStates.No;
        var (size, ts) = _store.Recommit(modelId, bytes);
        UpsertIndexRow(RollupEditor.BuildIndexRow(current, _today, size, ts));
        _indexDirty = true;
        return SealStates.Both;
    }

    // ---- cleanup ----------------------------------------------------------

    private IEnumerable<LedgerRow> DeletionCandidates()
    {
        // Closed files: everything folded (closed by construction, unchanged since) plus the
        // closed files this pass found. The newest few are a re-fold buffer.
        var closed = _outbox.Values
            .Where(f => (LedgerRow(f.Name) is { } r && r.Unclosed != true && SameFingerprint(r, f)) || _closedSeen.Contains(f.Name))
            .OrderByDescending(f => f.LastWriteUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal)
            .ToList();
        var buffer = new HashSet<string>(closed.Take(Math.Max(0, _o.KeepNewestClosed)).Select(f => f.Name),
            StringComparer.OrdinalIgnoreCase);
        foreach (var f in closed)
        {
            if (buffer.Contains(f.Name)) continue;
            if (LedgerRow(f.Name) is not { } row || row.Unclosed == true || !SameFingerprint(row, f)) continue;
            if (row.FoldedPass >= _pass) continue;
            if (row.Models.All(m => m.Sealed == SealStates.Both)) yield return row;
        }
    }

    /// <summary>
    /// Delete one candidate, re-checking everything against the file itself: it is held
    /// exclusively (a live Revit session's writer refuses that, and nobody can write, rename or
    /// replace it while held); through that handle its length and last write must still be the
    /// folded ones, it must still be closed, and re-folding its content must touch exactly the
    /// models the ledger lists; both copies of every touched rollup must hold the seal for
    /// exactly that file; then it is deleted through the same handle — the file verified, never
    /// one that took its path.
    /// </summary>
    private bool TryDelete(LedgerRow row, IReadOnlyList<IRetentionGate> gates)
    {
        if (!_outbox.TryGetValue(row.Name, out var file)) return false;
        if (_o.IsLive(file.Path)) return false;
        _o.FaultHook?.Invoke("delete:" + row.Name);

        using var taken = (_o.FileTaker ?? OutboxFileTaker.Default).TryTake(file.Path);
        if (taken is null) return false; // live, locked or gone: not now
        if (taken.Length != row.Size || taken.LastWriteUtc != RollupJson.TruncateMs(row.LastWriteUtc))
        {
            Log("activity cleanup: " + row.Name + " changed since it was folded; re-folding first");
            MarkForRefold(row);
            return false;
        }
        if (!SameMembership(row, taken.ReadAll()))
        {
            Log("activity cleanup: " + row.Name + " does not match its ledger row; re-folding first");
            MarkForRefold(row);
            return false;
        }
        if (!SealVerified(row)) return false;
        var candidate = new RetentionCandidate(file.Path, row.SessionGuid,
            row.Models.Select(m => m.ModelId).ToList(), row.Size, row.LastWriteUtc);
        if (!RetentionGates.AllAllow(gates, candidate, _o.Log)) return false;
        _o.FaultHook?.Invoke("verified:" + row.Name);
        return taken.Delete();
    }

    /// <summary>The held content is a closed session whose fold touches exactly the ledger's
    /// models (a session that touched none is a genuinely empty contribution).</summary>
    private bool SameMembership(LedgerRow row, byte[] content)
    {
        var events = new List<TelemetryEvent>();
        using (var reader = new StreamReader(new MemoryStream(content)))
        {
            string? line;
            while ((line = reader.ReadLine()) is not null)
                if (TelemetryJson.TryParseLine(line) is { } e) events.Add(e);
        }
        var info = SessionInfo.Read(events, row.Name);
        if (!info.Closed || !string.Equals(info.SessionGuid, row.SessionGuid, StringComparison.OrdinalIgnoreCase)) return false;
        var ids = ActivityFold.FoldSession(events, info, _o.Zone, DateTimeOffset.MaxValue)
            .Select(c => c.Key.ModelId).OrderBy(i => i, StringComparer.Ordinal);
        return ids.SequenceEqual(row.Models.Select(m => m.ModelId).OrderBy(i => i, StringComparer.Ordinal));
    }

    /// <summary>Re-read both copies of every touched rollup: each must hold the seal for exactly
    /// this file. The ledger's word alone never deletes anything.</summary>
    private bool SealVerified(LedgerRow row)
    {
        var ok = true;
        foreach (var m in row.Models)
        {
            var current = _store.ReadCopy(m.ModelId, backup: false, out _);
            var bak = _store.ReadCopy(m.ModelId, backup: true, out _);
            var inCurrent = current?.IsSealed(row.SessionGuid, row.Size, row.LastWriteUtc) == true;
            var inBak = bak?.IsSealed(row.SessionGuid, row.Size, row.LastWriteUtc) == true;
            if (inCurrent && inBak) continue;
            ok = false;
            m.Sealed = inCurrent ? SealStates.Current : SealStates.No;
            _ledgerDirty = true;
            if (!inCurrent) MarkForRefold(row);
        }
        return ok;
    }

    // ---- self-healing -----------------------------------------------------

    /// <summary>Compare each rollup with its index row; a changed or missing file is loaded in
    /// writer mode, which restores a corrupt one from .bak.json.</summary>
    private void VerifyRollups()
    {
        foreach (var row in _index.Models.ToList())
        {
            try
            {
                var (bytes, ts) = RollupStore.Stat(_store.ModelPath(row.ModelId));
                if (bytes >= 0 && bytes == row.RollupBytes && ts == row.RollupLastWriteUtc) continue;
                var (r, outcome) = _store.LoadModel(row.ModelId, ReadMode.Writer);
                if (outcome is ReadOutcome.Restored or ReadOutcome.Lost or ReadOutcome.Missing) Heal(row.ModelId, null);
                if (r is null)
                {
                    _index.Models.Remove(row);
                }
                else
                {
                    var (b2, t2) = RollupStore.Stat(_store.ModelPath(row.ModelId));
                    UpsertIndexRow(RollupEditor.BuildIndexRow(r, _today, b2, t2));
                }
                _indexDirty = true;
            }
            catch (Exception ex)
            {
                Log("activity rollup check failed for " + row.ModelId + ": " + ex.Message);
            }
        }
    }

    /// <summary>A rollup came back from .bak.json (or not at all): every session file still in
    /// the outbox that touched it folds again; the fold replaces or skips what it already holds.</summary>
    private void Heal(string modelId, string? exceptFile)
    {
        Log("activity rollup " + modelId + " restored; re-folding its session files");
        foreach (var row in _ledger.Files)
        {
            if (!row.Models.Any(m => m.ModelId == modelId)) continue;
            if (exceptFile is not null && string.Equals(row.Name, exceptFile, StringComparison.OrdinalIgnoreCase)) continue;
            MarkForRefold(row);
        }
    }

    private void MarkForRefold(LedgerRow row)
    {
        row.Size = -1; // fingerprint can never match: the file folds again
        foreach (var m in row.Models) m.Sealed = SealStates.No;
        _ledgerDirty = true;
        if (_outbox.TryGetValue(row.Name, out var f) && !_toFold.Skip(_cursor).Contains(f))
            _toFold.Add(f);
    }

    // ---- helpers ----------------------------------------------------------

    private Dictionary<string, OutboxFile> ListOutbox()
    {
        var files = new Dictionary<string, OutboxFile>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(_o.OutboxDir)) return files;
        foreach (var path in Directory.GetFiles(_o.OutboxDir, "*" + OutboxFiles.Extension))
        {
            var name = Path.GetFileName(path);
            var (size, ts) = RollupStore.Stat(path);
            if (size < 0) continue;
            files[name] = new OutboxFile(name, path, OutboxFiles.TryParseSessionGuid(name), size, ts);
        }
        return files;
    }

    /// <summary>Note a live or unrecovered file so later passes skip it until it changes.</summary>
    private void RememberUnclosed(OutboxFile f)
    {
        var row = LedgerRow(f.Name);
        if (row is not null && row.Unclosed != true) return; // folded before: keep its row and shares
        if (row is null) _ledger.Files.Add(row = new LedgerRow { Name = f.Name });
        row.SessionGuid = f.SessionGuid ?? "";
        row.Size = f.Size;
        row.LastWriteUtc = f.LastWriteUtc;
        row.Unclosed = true;
        _ledgerDirty = true;
    }

    private bool SessionPresent(string sessionGuid) => _outboxGuids.Contains(sessionGuid);

    private LedgerRow? LedgerRow(string name) =>
        _ledger.Files.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

    private static bool SameFingerprint(LedgerRow row, OutboxFile f) =>
        row.Size == f.Size && RollupJson.TruncateMs(row.LastWriteUtc) == f.LastWriteUtc;

    private void UpsertIndexRow(ModelIndexRow row)
    {
        var i = _index.Models.FindIndex(m => m.ModelId == row.ModelId);
        if (i >= 0) _index.Models[i] = row; else _index.Models.Add(row);
    }

    private void SaveLedger()
    {
        _ledger.Pass = _pass;
        _ledger.Files.RemoveAll(r => !_outbox.ContainsKey(r.Name) && r.Models.Count == 0);
        _store.SaveLedger(_ledger);
        _ledgerDirty = false;
    }

    /// <summary>Write what is dirty: index, then ledger. A failure is logged and stops the pass
    /// before cleanup; the in-memory state is not trusted further, and the next pass retries
    /// from what is on disk.</summary>
    private void TryFlush()
    {
        try
        {
            if (_indexDirty)
            {
                _store.SaveIndex(_index, _now);
                _indexDirty = false;
            }
            if (_ledgerDirty) SaveLedger();
        }
        catch (Exception ex)
        {
            _persistFailed = true;
            _indexDirty = _ledgerDirty = false; // never retried from this pass's memory
            Failed++;
            Log("activity fold: writing the index or ledger failed (retried next pass): " + ex.Message);
        }
    }

    private void Log(string message) => ActivityFoldEngine.SafeLog(_o.Log, message);
}
