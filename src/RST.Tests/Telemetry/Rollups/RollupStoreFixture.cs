// RollupStoreFixture.cs — a temp outbox + rollups folder, a fixed clock and a
// fold engine over them, shared by the rollup tests (spec #24).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RST.Core.Telemetry;
using RST.Core.Telemetry.Rollups;

namespace RST.Tests.Telemetry.Rollups;

internal sealed class RollupStoreFixture : IDisposable
{
    public RollupStoreFixture(DateTimeOffset now)
    {
        Root = Path.Combine(Path.GetTempPath(), "RST-Rollups-" + Guid.NewGuid().ToString("N"));
        Outbox = Path.Combine(Root, "outbox");
        Rollups = Path.Combine(Root, "rollups");
        Directory.CreateDirectory(Outbox);
        Now = now;
    }

    public string Root { get; }
    public string Outbox { get; }
    public string Rollups { get; }
    public DateTimeOffset Now { get; set; }
    public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Utc;
    public HashSet<string> LiveFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Logs { get; } = new();
    public IReadOnlyList<IRetentionGate> Gates { get; set; } = Array.Empty<IRetentionGate>();
    public Action<string>? FaultHook { get; set; }
    public IFileTaker? FileTaker { get; set; }

    public RollupStoreOptions Options => new()
    {
        OutboxDir = Outbox,
        RollupsDir = Rollups,
        Zone = Zone,
        Clock = () => Now,
        Log = m => { lock (Logs) Logs.Add(m); },
        IsLive = p => LiveFiles.Contains(p),
        Gates = Gates,
        FaultHook = FaultHook,
        FileTaker = FileTaker,
    };

    public ActivityFoldEngine Engine => new(Options);

    public FoldPassResult Pass() => Engine.RunPass();

    /// <summary>Run passes until one reports nothing changed (at most <paramref name="max"/>).</summary>
    public int PassUntilQuiet(int max = 10)
    {
        for (var i = 1; i <= max; i++)
            if (Pass().NothingChanged) return i;
        throw new InvalidOperationException("the store never went quiet");
    }

    public ActivityReadOptions ReadOptions(string? liveSessionGuid = null) => new()
    {
        OutboxDir = Outbox,
        RollupsDir = Rollups,
        Zone = Zone,
        NowUtc = Now,
        LiveSessionGuid = liveSessionGuid,
    };

    public string ModelPath(DocumentIdentity id) => Path.Combine(Rollups, "models", ModelKey.From(id)!.Value.ModelId + ".json");

    public string BakPath(DocumentIdentity id) => Path.ChangeExtension(ModelPath(id), null) + ".bak.json";

    public ModelRollup? Rollup(DocumentIdentity id) =>
        new RollupStore(Rollups).LoadModel(ModelKey.From(id)!.Value.ModelId, ReadMode.Reader).Value;

    public FoldLedger Ledger() => new RollupStore(Rollups).LoadLedger(ReadMode.Reader);

    public IReadOnlyList<ModelIndexRow> Index() => ModelActivityReader.ReadIndex(Rollups, Now, Zone);

    /// <summary>Every file under the rollups folder with its bytes (the lock file excluded).</summary>
    public Dictionary<string, byte[]> Snapshot(string? dir = null)
    {
        dir ??= Rollups;
        if (!Directory.Exists(dir)) return new Dictionary<string, byte[]>();
        return Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .Where(p => !p.EndsWith(RollupStore.LockFile, StringComparison.Ordinal))
            .ToDictionary(p => Path.GetRelativePath(dir, p), File.ReadAllBytes);
    }

    public string[] OutboxNames() => Directory.GetFiles(Outbox).Select(Path.GetFileName).OrderBy(n => n).ToArray()!;

    public void Dispose()
    {
        try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
        catch { /* best-effort */ }
    }
}
