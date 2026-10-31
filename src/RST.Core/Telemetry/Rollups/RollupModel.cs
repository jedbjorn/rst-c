// rst-c's own (not vendored): the rollup store's file shapes (decision #52;
// spec #24, Rollup Store). The contract is documented in
// docs/activity-rollups.md; bump RollupSchema.Version on any breaking change
// and add a forward step to RollupMigrations.
//
//   models\<model_id>.json   one model's rollup (ModelRollup)
//   index.json               one row per model (ModelIndexRow), list-ready
//   ledger.json              one row per session file seen (LedgerRow)
//
// Durations are held in whole milliseconds in memory (so sums and
// replacements are exact and order-independent) and written as seconds.

using System.Text.Json.Serialization;
using RST.Core.Health;

namespace RST.Core.Telemetry.Rollups;

public static class RollupSchema
{
    /// <summary>The version every rollup, index and ledger file is written with.</summary>
    public const int Version = 1;
}

/// <summary>The latest <c>model_stats</c> a model recorded (a day's, or overall).</summary>
public sealed record ModelStatsPoint(DateTimeOffset Ts, int? WarningsCount, long? FileSizeBytes)
{
    /// <summary>The later of two points; a tie on ts keeps the larger values, so the merge is order-independent.</summary>
    internal static ModelStatsPoint? Latest(ModelStatsPoint? a, ModelStatsPoint? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        var c = a.Ts.CompareTo(b.Ts);
        if (c != 0) return c > 0 ? a : b;
        var w = Nullable.Compare(a.WarningsCount, b.WarningsCount);
        if (w != 0) return w > 0 ? a : b;
        return Nullable.Compare(a.FileSizeBytes, b.FileSizeBytes) >= 0 ? a : b;
    }
}

/// <summary>
/// One local calendar day of one model: active and open time, timed opens and
/// syncs, the Revit versions it was opened in, the day's latest
/// <c>model_stats</c>, and the health inputs as they stood at the end of the day.
/// </summary>
public sealed class RollupDay
{
    public const long MaxDayMs = 24L * 60 * 60 * 1000;

    public DateOnly Date { get; set; }
    public long ActiveMs { get; set; }
    public long OpenMs { get; set; }
    public int Opens { get; set; }
    public long OpenSumMs { get; set; }
    public long OpenMaxMs { get; set; }
    public int Syncs { get; set; }
    public long SyncSumMs { get; set; }
    public long SyncMaxMs { get; set; }

    /// <summary>Major versions (e.g. "2026"), sorted.</summary>
    public List<string> RevitVersions { get; set; } = new();

    /// <summary>Subversions (e.g. "2026.4"), sorted; empty for data from before v3.2.</summary>
    public List<string> RevitSubversions { get; set; } = new();

    /// <summary>The day's latest model_stats; null when none was recorded that day.</summary>
    public ModelStatsPoint? Stats { get; set; }

    /// <summary>The four health inputs at the end of the day (spec #24, Model Health).</summary>
    public HealthInputs? Health { get; set; }

    public double ActiveSeconds => ActiveMs / 1000.0;
    public double OpenSeconds => OpenMs / 1000.0;
    public double OpenSecondsSum => OpenSumMs / 1000.0;
    public double OpenSecondsMax => OpenMaxMs / 1000.0;
    public double SyncSecondsSum => SyncSumMs / 1000.0;
    public double SyncSecondsMax => SyncMaxMs / 1000.0;

    internal bool IsEmpty =>
        ActiveMs == 0 && OpenMs == 0 && Opens == 0 && Syncs == 0 && Stats is null;

    /// <summary>A copy of the counters, versions and stats (no health inputs).</summary>
    internal RollupDay CloneCounters() => new()
    {
        Date = Date,
        ActiveMs = ActiveMs,
        OpenMs = OpenMs,
        Opens = Opens,
        OpenSumMs = OpenSumMs,
        OpenMaxMs = OpenMaxMs,
        Syncs = Syncs,
        SyncSumMs = SyncSumMs,
        SyncMaxMs = SyncMaxMs,
        RevitVersions = new List<string>(RevitVersions),
        RevitSubversions = new List<string>(RevitSubversions),
        Stats = Stats,
    };

    internal RollupDay Clone()
    {
        var c = CloneCounters();
        c.Health = Health;
        return c;
    }

    /// <summary>Add another contribution to the same day. Sums add, maxima take the
    /// larger, version sets union, stats keep the latest — commutative and exact.</summary>
    internal void Add(RollupDay other)
    {
        ActiveMs += other.ActiveMs;
        OpenMs += other.OpenMs;
        Opens += other.Opens;
        OpenSumMs += other.OpenSumMs;
        OpenMaxMs = Math.Max(OpenMaxMs, other.OpenMaxMs);
        Syncs += other.Syncs;
        SyncSumMs += other.SyncSumMs;
        SyncMaxMs = Math.Max(SyncMaxMs, other.SyncMaxMs);
        RevitVersions = UnionSorted(RevitVersions, other.RevitVersions);
        RevitSubversions = UnionSorted(RevitSubversions, other.RevitSubversions);
        Stats = ModelStatsPoint.Latest(Stats, other.Stats);
    }

    /// <summary>Clamp implausible values (hand edits, corruption, overlapping sessions):
    /// nothing negative, no duration over 24 h. Never used for deletion decisions.</summary>
    internal void Clamp()
    {
        ActiveMs = ClampMs(ActiveMs);
        OpenMs = ClampMs(OpenMs);
        OpenSumMs = ClampMs(OpenSumMs);
        OpenMaxMs = ClampMs(OpenMaxMs);
        SyncSumMs = ClampMs(SyncSumMs);
        SyncMaxMs = ClampMs(SyncMaxMs);
        if (Opens < 0) Opens = 0;
        if (Syncs < 0) Syncs = 0;
    }

    internal static long ClampMs(long ms) => ms < 0 ? 0 : ms > MaxDayMs ? MaxDayMs : ms;

    internal static List<string> UnionSorted(List<string> a, List<string> b)
    {
        if (b.Count == 0) return a;
        var set = new SortedSet<string>(a, StringComparer.Ordinal);
        set.UnionWith(b);
        return set.ToList();
    }
}

/// <summary>The identity keys of a model, on the matching ladder (cloud pair → central
/// GUID → central path → creation GUID).</summary>
public sealed class RollupKeys
{
    public string? CloudProjectGuid { get; set; }
    public string? CloudModelGuid { get; set; }
    public string? CentralGuid { get; set; }
    public string? CentralPath { get; set; }
    public string? CreationGuid { get; set; }

    internal static RollupKeys From(DocumentIdentity id) => new()
    {
        CloudProjectGuid = id.CloudProjectGuid,
        CloudModelGuid = id.CloudModelGuid,
        CentralGuid = id.CentralGuid,
        CentralPath = id.CentralPath,
        CreationGuid = id.CreationGuid,
    };

    internal DocumentIdentity ToIdentity() => new()
    {
        CloudProjectGuid = CloudProjectGuid,
        CloudModelGuid = CloudModelGuid,
        CentralGuid = CentralGuid,
        CentralPath = CentralPath,
        CreationGuid = CreationGuid,
    };

    /// <summary>A keys-only event carrying these keys, for the matcher.</summary>
    internal TelemetryEvent ToKeysEvent()
    {
        var e = new TelemetryEvent { EventType = TelemetryEventTypes.DocOpened };
        ToIdentity().WriteKeysTo(e);
        return e;
    }
}

/// <summary>Every identity value a model's events have carried (cloud pairs, central GUIDs,
/// central paths, creation GUIDs), plus the local paths and titles it has had. Search and
/// display use these; matching uses <see cref="RollupKeys"/>.</summary>
public sealed class KeysSeen
{
    /// <summary>Local paths and titles keep the most recent this many.</summary>
    public const int MaxPathsAndTitles = 20;

    public List<string> CloudProjectGuids { get; set; } = new();
    public List<string> CloudModelGuids { get; set; } = new();
    public List<string> CentralGuids { get; set; } = new();
    public List<string> CentralPaths { get; set; } = new();
    public List<string> CreationGuids { get; set; } = new();
    public List<string> LocalPaths { get; set; } = new();
    public List<string> Titles { get; set; } = new();

    internal void Add(DocumentIdentity id)
    {
        Remember(CloudProjectGuids, id.CloudProjectGuid, int.MaxValue);
        Remember(CloudModelGuids, id.CloudModelGuid, int.MaxValue);
        Remember(CentralGuids, id.CentralGuid, int.MaxValue);
        Remember(CentralPaths, id.CentralPath, int.MaxValue);
        Remember(CreationGuids, id.CreationGuid, int.MaxValue);
        Remember(LocalPaths, id.LocalPath, MaxPathsAndTitles);
        Remember(Titles, id.Title, MaxPathsAndTitles);
    }

    internal void Add(KeysSeen other)
    {
        foreach (var v in other.CloudProjectGuids) Remember(CloudProjectGuids, v, int.MaxValue);
        foreach (var v in other.CloudModelGuids) Remember(CloudModelGuids, v, int.MaxValue);
        foreach (var v in other.CentralGuids) Remember(CentralGuids, v, int.MaxValue);
        foreach (var v in other.CentralPaths) Remember(CentralPaths, v, int.MaxValue);
        foreach (var v in other.CreationGuids) Remember(CreationGuids, v, int.MaxValue);
        foreach (var v in other.LocalPaths) Remember(LocalPaths, v, MaxPathsAndTitles);
        foreach (var v in other.Titles) Remember(Titles, v, MaxPathsAndTitles);
    }

    /// <summary>Most recent last; a value seen again moves to the end (case-insensitive).</summary>
    private static void Remember(List<string> list, string? value, int cap)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var i = list.FindIndex(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase));
        if (i == list.Count - 1 && i >= 0) return;
        if (i >= 0) list.RemoveAt(i);
        list.Add(value);
        while (list.Count > cap) list.RemoveAt(0);
    }
}

/// <summary>The model's latest descriptive identity (from its newest doc_opened, doc_saved or doc_saved_as).</summary>
public sealed class RollupIdentity
{
    public string? Title { get; set; }
    public string? LocalPath { get; set; }
    public string? CentralPath { get; set; }
    public bool? IsWorkshared { get; set; }
    public bool? IsCloud { get; set; }

    /// <summary>The ts of the event these values came from.</summary>
    public DateTimeOffset? AsOf { get; set; }
}

/// <summary>One session's not-yet-sealed share of a rollup (spec #24: held while the raw
/// file exists, so a changed file re-folds by replacement).</summary>
internal sealed class PendingSession
{
    public string File { get; set; } = "";
    public long Size { get; set; }
    public DateTimeOffset LastWriteUtc { get; set; }

    [JsonConverter(typeof(DayTableConverter))]
    public List<RollupDay> Days { get; set; } = new();

    public List<DurationPoint> Opens { get; set; } = new();
    public List<DurationPoint> Syncs { get; set; } = new();
}

/// <summary><c>models\&lt;model_id&gt;.json</c>.</summary>
internal sealed class ModelRollup
{
    public int SchemaVersion { get; set; } = RollupSchema.Version;
    public string ModelId { get; set; } = "";

    /// <summary>The ladder level the model is keyed at (<see cref="ActivityMatchKinds"/>).</summary>
    public string KeyKind { get; set; } = "";

    /// <summary>The keys of the doc_opened that defined the model; the matcher reads these.</summary>
    public RollupKeys Keys { get; set; } = new();

    public KeysSeen KeysSeen { get; set; } = new();
    public RollupIdentity Identity { get; set; } = new();

    /// <summary>Every day with data, oldest first — sealed and pending together.</summary>
    [JsonConverter(typeof(DayTableConverter))]
    public List<RollupDay> Days { get; set; } = new();

    /// <summary>Timed opens (doc_opening → doc_opened), oldest first: the last 90 days and
    /// always the last 10.</summary>
    public List<DurationPoint> Opens { get; set; } = new();

    /// <summary>Timed syncs, plotted at their start, oldest first: same window as opens.</summary>
    public List<DurationPoint> Syncs { get; set; } = new();

    /// <summary>The newest open the window dropped; null = none dropped yet.</summary>
    public DateTimeOffset? OpensTrimmedThrough { get; set; }

    public DateTimeOffset? SyncsTrimmedThrough { get; set; }

    /// <summary>Per session GUID, its unsealed contribution.</summary>
    public SortedDictionary<string, PendingSession> Pending { get; set; } = new(StringComparer.Ordinal);

    /// <summary>For each day a pending contribution touches, that day's sealed totals.</summary>
    [JsonConverter(typeof(DayTableConverter))]
    public List<RollupDay> PendingBase { get; set; } = new();

    /// <summary>Sessions sealed into the permanent rows whose raw file may still exist.</summary>
    public List<string> SealedSessions { get; set; } = new();

    internal bool Contains(string sessionGuid) =>
        Pending.ContainsKey(sessionGuid) || IsSealed(sessionGuid);

    internal bool IsSealed(string sessionGuid) =>
        SealedSessions.BinarySearch(sessionGuid, StringComparer.Ordinal) >= 0;
}

/// <summary>A day's or a week's totals in an index row.</summary>
public sealed class ActivityTotals
{
    /// <summary>The day, or the Monday that starts the ISO week. Default for all-time totals.</summary>
    public DateOnly Date { get; set; }
    public long ActiveMs { get; set; }
    public long OpenMs { get; set; }
    public int Opens { get; set; }
    public long OpenSumMs { get; set; }
    public int Syncs { get; set; }
    public long SyncSumMs { get; set; }

    public double ActiveSeconds => ActiveMs / 1000.0;
    public double OpenSeconds => OpenMs / 1000.0;
    public double OpenSecondsSum => OpenSumMs / 1000.0;
    public double SyncSecondsSum => SyncSumMs / 1000.0;

    internal void Add(RollupDay d)
    {
        ActiveMs += d.ActiveMs;
        OpenMs += d.OpenMs;
        Opens += d.Opens;
        OpenSumMs += d.OpenSumMs;
        Syncs += d.Syncs;
        SyncSumMs += d.SyncSumMs;
    }
}

/// <summary>One row of <c>index.json</c>: everything the tray's Models list needs (spec #25)
/// in one read.</summary>
public sealed class ModelIndexRow
{
    /// <summary>Recent days kept in a row: the last 14 calendar days.</summary>
    public const int RecentDays = 14;

    /// <summary>Recent weeks kept in a row: the last 53 ISO weeks, the current one included.</summary>
    public const int RecentWeeks = 53;

    public string ModelId { get; set; } = "";
    public string KeyKind { get; set; } = "";
    public RollupKeys Keys { get; set; } = new();
    public KeysSeen KeysSeen { get; set; } = new();
    public string? Title { get; set; }
    public string? LocalPath { get; set; }
    public string? CentralPath { get; set; }
    public bool? IsWorkshared { get; set; }
    public bool? IsCloud { get; set; }

    /// <summary>First and last day with any open time.</summary>
    public DateOnly? FirstActiveDay { get; set; }
    public DateOnly? LastActiveDay { get; set; }

    /// <summary>Major version → the last day the model was open in it.</summary>
    public SortedDictionary<string, DateOnly> RevitVersions { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Subversion ("2026.4") → the last day the model was open in it.</summary>
    public SortedDictionary<string, DateOnly> RevitSubversions { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The latest model_stats, with its ts ("last known").</summary>
    public ModelStatsPoint? LastStats { get; set; }

    /// <summary>The current health inputs (the last day's); score them with <see cref="ModelHealth"/>.</summary>
    public HealthInputs? Health { get; set; }

    [JsonConverter(typeof(TotalsObjectConverter))]
    public ActivityTotals Totals { get; set; } = new();

    /// <summary>The last 14 days relative to the day the row was written, days with data only.</summary>
    [JsonConverter(typeof(TotalsTableConverter))]
    public List<ActivityTotals> RecentDaysTotals { get; set; } = new();

    /// <summary>The last 53 ISO weeks (Monday start, local zone) relative to the day the row was
    /// written, weeks with data only; each row's date is its Monday.</summary>
    [JsonConverter(typeof(TotalsTableConverter))]
    public List<ActivityTotals> RecentWeeksTotals { get; set; } = new();

    /// <summary>The rollup file's length and last-write time when this row was written; a
    /// mismatch makes the next pass verify the rollup.</summary>
    public long RollupBytes { get; set; }
    public DateTimeOffset? RollupLastWriteUtc { get; set; }
}

/// <summary><c>index.json</c>.</summary>
internal sealed class RollupIndexFile
{
    public int SchemaVersion { get; set; } = RollupSchema.Version;
    public DateTimeOffset? UpdatedUtc { get; set; }
    public List<ModelIndexRow> Models { get; set; } = new();
}

/// <summary>Seal state of one session in one model.</summary>
internal static class SealStates
{
    public const string No = "no";
    public const string Current = "current";
    public const string Both = "both";
}

internal sealed class LedgerModel
{
    public string ModelId { get; set; } = "";

    /// <summary><see cref="SealStates"/>: not sealed, sealed in the current rollup only, or in
    /// the rollup and its .bak.json.</summary>
    public string Sealed { get; set; } = SealStates.No;
}

/// <summary>One session file the fold has seen.</summary>
internal sealed class LedgerRow
{
    public string Name { get; set; } = "";
    public string SessionGuid { get; set; } = "";
    public long Size { get; set; }
    public DateTimeOffset LastWriteUtc { get; set; }
    public long FoldedPass { get; set; }
    public List<LedgerModel> Models { get; set; } = new();

    /// <summary>True for a file seen live or without session_end (recovery has not closed it):
    /// nothing is folded from it, and it is looked at again only once it changes.</summary>
    public bool? Unclosed { get; set; }
}

/// <summary><c>ledger.json</c> — written last in every fold step.</summary>
internal sealed class FoldLedger
{
    public int SchemaVersion { get; set; } = RollupSchema.Version;
    public long Pass { get; set; }
    public List<LedgerRow> Files { get; set; } = new();
}
