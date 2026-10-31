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
        var result = _io.Read<ModelRollup>(ModelPath(modelId), mode, r => r.ModelId == modelId);
        if (result.Value is { } r) Normalize(r);
        return result;
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
            var r = RollupJson.Deserialize<ModelRollup>(bytes);
            if (r is null || r.ModelId != modelId || r.SchemaVersion < 1) return null;
            Normalize(r);
            return r;
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
        var result = _io.Read<RollupIndexFile>(IndexPath, mode);
        if (result.Value is { } index)
        {
            index.Models.RemoveAll(m => !ModelKey.IsValidId(m.ModelId));
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
        var result = _io.Read<FoldLedger>(LedgerPath, mode);
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

    /// <summary>Bring a loaded rollup into canonical shape: sorted rows and events, no
    /// implausible event values (day rows are clamped as they are read).</summary>
    private static void Normalize(ModelRollup r)
    {
        r.Days = r.Days.GroupBy(d => d.Date).Select(g => g.First()).OrderBy(d => d.Date).ToList();
        r.PendingBase = r.PendingBase.GroupBy(d => d.Date).Select(g => g.First()).OrderBy(d => d.Date).ToList();
        r.Opens = RollupEditor.SortPoints(r.Opens.Where(Plausible));
        r.Syncs = RollupEditor.SortPoints(r.Syncs.Where(Plausible));
        r.SealedSessions = r.SealedSessions.Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal).ToList();
        r.KeysSeen ??= new KeysSeen();
        r.Identity ??= new RollupIdentity();
        r.Keys ??= new RollupKeys();
        r.Pending = new SortedDictionary<string, PendingSession>(
            r.Pending ?? new SortedDictionary<string, PendingSession>(), StringComparer.Ordinal);
        foreach (var p in r.Pending.Values)
        {
            p.Days = p.Days.OrderBy(d => d.Date).ToList();
            p.Opens = RollupEditor.SortPoints(p.Opens.Where(Plausible));
            p.Syncs = RollupEditor.SortPoints(p.Syncs.Where(Plausible));
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
