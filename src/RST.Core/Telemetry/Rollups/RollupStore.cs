// rst-c's own (not vendored): the rollup folder's layout and typed access
// to its three kinds of file (spec #24, Rollup Store):
//
//   %LOCALAPPDATA%\RST\telemetry\rollups\
//     models\<model_id>.json (+ .bak.json, .corrupt.json)
//     index.json             (+ .bak.json)
//     ledger.json            (+ .bak.json)
//     fold.lock              the tray worker's exclusive lock

using System.IO;

namespace RST.Core.Telemetry.Rollups;

internal sealed class RollupStore
{
    public const string ModelsFolder = "models";
    public const string IndexFile = "index.json";
    public const string LedgerFile = "ledger.json";
    public const string LockFile = "fold.lock";

    private readonly RollupFileIO _io;
    private readonly Action<string>? _log;

    public RollupStore(string rollupsDir, RollupFileIO? io = null, Action<string>? log = null)
    {
        Root = rollupsDir;
        _io = io ?? new RollupFileIO();
        _log = log;
    }

    public string Root { get; }
    public string ModelsDir => Path.Combine(Root, ModelsFolder);
    public string IndexPath => Path.Combine(Root, IndexFile);
    public string LedgerPath => Path.Combine(Root, LedgerFile);
    public string ModelPath(string modelId) => Path.Combine(ModelsDir, modelId + ".json");
    public RollupFileIO IO => _io;

    public ReadResult<ModelRollup> LoadModel(string modelId, ReadMode mode)
    {
        if (!ModelKey.IsValidId(modelId)) return new(null, ReadOutcome.Missing);
        // Validation and normalisation run inside the recovery boundary: a structurally broken
        // rollup (for example "days": null) is corrupt, so .bak.json takes over.
        return _io.Read<ModelRollup>(ModelPath(modelId), mode, r => Valid(r, modelId));
    }

    /// <summary>Write the rollup; returns the file's length and last-write time for the index row.</summary>
    public (long Bytes, DateTimeOffset LastWriteUtc) SaveModel(ModelRollup r)
    {
        var path = ModelPath(r.ModelId);
        _io.WriteAtomic(path, RollupJson.Serialize(r));
        return Stat(path);
    }

    /// <summary>Write the current rollup's bytes again, so its .bak.json becomes a copy of it
    /// (how a seal reaches both copies when the model is not written again).</summary>
    public (long Bytes, DateTimeOffset LastWriteUtc) Recommit(string modelId, byte[] bytes)
    {
        var path = ModelPath(modelId);
        _io.WriteAtomic(path, bytes);
        return Stat(path);
    }

    /// <summary>Strict read of one copy (current or .bak.json) — no fallback, no repair.
    /// Null when missing or unreadable. Cleanup verifies seals with this.</summary>
    public ModelRollup? ReadCopy(string modelId, bool backup, out byte[]? bytes)
    {
        bytes = null;
        var path = ModelPath(modelId);
        if (backup) path = RollupFileIO.BakPath(path);
        try
        {
            bytes = _io.ReadBytes(path);
            if (bytes is null) return null;
            return _io.Parse<ModelRollup>(bytes, r => Valid(r, modelId), out var tooNew) is { } r && !tooNew ? r : null;
        }
        catch (Exception ex)
        {
            SafeLog("rollup copy unreadable " + Path.GetFileName(path) + ": " + ex.Message);
            return null;
        }
    }

    public static (long Bytes, DateTimeOffset LastWriteUtc) Stat(string path)
    {
        var fi = new FileInfo(path);
        return fi.Exists
            ? (fi.Length, RollupJson.TruncateMs(new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero)))
            : (-1, default);
    }

    /// <summary>The index; when it and its backup are unreadable, rebuilt from the model files.</summary>
    public RollupIndexFile LoadIndex(ReadMode mode, DateOnly today, out bool rebuilt)
    {
        rebuilt = false;
        // The container must be sound (a "models" array) or .bak.json / a rebuild takes over;
        // inside it, a bad row is skipped and logged, never fatal (spec #25).
        var result = _io.Read<RollupIndexDocument>(IndexPath, mode, d => d.Models is not null);
        if (result.Value is { Models: { } elements } doc)
        {
            var index = new RollupIndexFile { SchemaVersion = doc.SchemaVersion, UpdatedUtc = doc.UpdatedUtc };
            var skipped = 0;
            foreach (var element in elements)
            {
                var row = TryReadRow(element);
                if (row is null) skipped++;
                else if (index.Models.All(m => m.ModelId != row.ModelId)) index.Models.Add(row);
            }
            if (skipped > 0) SafeLog("rollup index: skipped " + skipped + " unreadable row(s)");
            return index;
        }
        if (result.Outcome == ReadOutcome.Missing && !Directory.Exists(ModelsDir)) return new RollupIndexFile();
        rebuilt = true;
        if (result.Outcome != ReadOutcome.Missing) SafeLog("rollup index unreadable; rebuilding from model files");
        return RebuildIndex(today);
    }

    public RollupIndexFile RebuildIndex(DateOnly today)
    {
        var index = new RollupIndexFile();
        foreach (var id in ListModelIds())
        {
            var r = LoadModel(id, ReadMode.Reader).Value;
            if (r is null) continue;
            var (bytes, ts) = Stat(ModelPath(id));
            index.Models.Add(RollupEditor.BuildIndexRow(r, today, bytes, ts));
        }
        return index;
    }

    public void SaveIndex(RollupIndexFile index, DateTimeOffset nowUtc)
    {
        index.SchemaVersion = RollupSchema.Version;
        index.UpdatedUtc = RollupJson.TruncateMs(nowUtc);
        index.Models.Sort((a, b) => string.CompareOrdinal(a.ModelId, b.ModelId));
        _io.WriteAtomic(IndexPath, RollupJson.Serialize(index));
    }

    public FoldLedger LoadLedger(ReadMode mode)
    {
        var result = _io.Read<FoldLedger>(LedgerPath, mode, l =>
        {
            if (l.Files is null) return false;
            l.Files.RemoveAll(f => f is null || string.IsNullOrEmpty(f.Name));
            foreach (var f in l.Files)
            {
                f.SessionGuid ??= "";
                // Membership the ledger cannot vouch for (missing, or an entry that is not a
                // model) is unknown, never "touched nothing": the file re-folds, and nothing is
                // sealed or deleted on its word. An empty list stays a genuinely empty session.
                var onDisk = f.ModelsOnDisk; // null: "models" was absent or null
                var unknown = onDisk is null || onDisk.Any(m => m is null || !ModelKey.IsValidId(m.ModelId));
                f.Models = (onDisk ?? new List<LedgerModel>()).Where(m => m is not null && ModelKey.IsValidId(m.ModelId)).ToList();
                foreach (var m in f.Models)
                    if (m.Sealed is not (SealStates.No or SealStates.Current or SealStates.Both)) m.Sealed = SealStates.No;
                if (unknown && f.Unclosed != true)
                {
                    f.Size = -1;
                    foreach (var m in f.Models) m.Sealed = SealStates.No;
                }
            }
            return true;
        });
        if (result.Outcome is ReadOutcome.Lost) SafeLog("fold ledger unreadable; every session file re-folds");
        return result.Value ?? new FoldLedger();
    }

    public void SaveLedger(FoldLedger ledger)
    {
        ledger.SchemaVersion = RollupSchema.Version;
        ledger.Files.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        _io.WriteAtomic(LedgerPath, RollupJson.Serialize(ledger));
    }

    public IEnumerable<string> ListModelIds()
    {
        if (!Directory.Exists(ModelsDir)) return Array.Empty<string>();
        return Directory.GetFiles(ModelsDir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is not null && ModelKey.IsValidId(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Validate a parsed rollup and bring it into canonical shape: sorted rows and events, no
    /// implausible event values (day rows are clamped as they are read). False — or a throw,
    /// which the caller treats the same — means corrupt: the structure the fold relies on
    /// (days, events, keys) is missing.
    /// </summary>
    private static bool Valid(ModelRollup r, string modelId)
    {
        if (r.ModelId != modelId || r.Days is null || r.Opens is null || r.Syncs is null || r.Keys is null)
            return false;
        r.Days = r.Days.Where(d => d is not null).GroupBy(d => d.Date).Select(g => g.First()).OrderBy(d => d.Date).ToList();
        r.PendingBase = (r.PendingBase ?? new List<RollupDay>()).Where(d => d is not null)
            .GroupBy(d => d.Date).Select(g => g.First()).OrderBy(d => d.Date).ToList();
        r.Opens = RollupEditor.SortPoints(r.Opens.Where(Plausible));
        r.Syncs = RollupEditor.SortPoints(r.Syncs.Where(Plausible));
        r.KeyKind ??= "";
        r.KeysSeen = NormalizeSeen(r.KeysSeen);
        r.Identity ??= new RollupIdentity();
        var pending = new SortedDictionary<string, PendingSession>(StringComparer.Ordinal);
        foreach (var (guid, p) in r.Pending ?? new SortedDictionary<string, PendingSession>())
        {
            if (string.IsNullOrEmpty(guid) || p is null || p.Days is null || p.Opens is null || p.Syncs is null) return false;
            p.File ??= "";
            p.Days = p.Days.Where(d => d is not null).OrderBy(d => d.Date).ToList();
            p.Opens = RollupEditor.SortPoints(p.Opens.Where(Plausible));
            p.Syncs = RollupEditor.SortPoints(p.Syncs.Where(Plausible));
            pending[guid] = p;
        }
        r.Pending = pending;
        return true;
    }

    private static KeysSeen NormalizeSeen(KeysSeen? seen)
    {
        seen ??= new KeysSeen();
        static List<string> L(List<string>? l) => (l ?? new List<string>()).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        seen.CloudProjectGuids = L(seen.CloudProjectGuids);
        seen.CloudModelGuids = L(seen.CloudModelGuids);
        seen.CentralGuids = L(seen.CentralGuids);
        seen.CentralPaths = L(seen.CentralPaths);
        seen.CreationGuids = L(seen.CreationGuids);
        seen.LocalPaths = L(seen.LocalPaths);
        seen.Titles = L(seen.Titles);
        return seen;
    }

    /// <summary>One index row, or null when it is not a usable row.</summary>
    private static ModelIndexRow? TryReadRow(System.Text.Json.JsonElement element)
    {
        try
        {
            if (element.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            var row = System.Text.Json.JsonSerializer.Deserialize<ModelIndexRow>(element, RollupJson.Options);
            if (row is null || !ModelKey.IsValidId(row.ModelId) || row.Keys is null) return null;
            row.KeyKind ??= "";
            row.KeysSeen = NormalizeSeen(row.KeysSeen);
            row.Totals ??= new ActivityTotals();
            row.RecentDaysTotals ??= new List<ActivityTotals>();
            row.RecentWeeksTotals ??= new List<ActivityTotals>();
            row.RevitVersions = new SortedDictionary<string, DateOnly>(
                row.RevitVersions ?? new SortedDictionary<string, DateOnly>(), StringComparer.Ordinal);
            row.RevitSubversions = new SortedDictionary<string, DateOnly>(
                row.RevitSubversions ?? new SortedDictionary<string, DateOnly>(), StringComparer.Ordinal);
            return row;
        }
        catch
        {
            return null;
        }
    }

    private static bool Plausible(DurationPoint p) =>
        p.Seconds >= 0 && p.Seconds <= RollupDay.MaxDayMs / 1000.0 && p.Ts != default;

    private void SafeLog(string message)
    {
        try { _log?.Invoke(message); }
        catch { /* logging must never take the store down */ }
    }
}
