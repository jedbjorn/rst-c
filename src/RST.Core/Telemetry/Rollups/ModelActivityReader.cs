// rst-c's own (not vendored): the shared read path (decision #52; spec #24,
// Revit Health "Data path"; spec #25, Model Detail "Same numbers as Revit").
// Revit's Activity tab and the tray's model detail both call it:
//
//   the model's rollup(s)
//   + closed session files the ledger has not folded yet (or that changed)
//   + optionally the live session's file
//   folded in memory with the same code the tray's fold uses — and nothing
//   is written.
//
// The current model is found with ActivityAggregator's matching ladder
// applied to each index row's keys, so every rollup the model's events
// could have folded into is found (identity-capture gaps included).

using System.IO;
using RST.Core.Configuration;
using RST.Core.Health;

namespace RST.Core.Telemetry.Rollups;

/// <summary>Where and when to read.</summary>
public sealed class ActivityReadOptions
{
    public string OutboxDir { get; init; } = AppDataPaths.TelemetryOutboxDir;
    public string RollupsDir { get; init; } = AppDataPaths.TelemetryRollupsDir;
    public TimeZoneInfo Zone { get; init; } = TimeZoneInfo.Local;
    public DateTimeOffset NowUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>The live Revit session's guid (Revit only): its file extends to now and gives
    /// Activity (Session). Null in the tray.</summary>
    public string? LiveSessionGuid { get; init; }

    public Action<string>? Log { get; init; }

    internal RollupFileIO? FileIO { get; init; }
}

/// <summary>A health dot on the trend chart.</summary>
public sealed record HealthTrendPoint(DateOnly Date, HealthInputs Inputs, ModelHealthResult Health);

/// <summary>One model's activity: every day it has data, its recent timed events, the live
/// session, and helpers that slice a range the way the charts plot it.</summary>
public sealed class ModelActivity
{
    public static readonly ModelActivity Empty = new();

    /// <summary>The ladder level the current model matched at; null when it has no usable key.</summary>
    public string? MatchedKeyKind { get; init; }

    /// <summary>The rollups read (several when an identity-capture gap split one model).</summary>
    public IReadOnlyList<string> ModelIds { get; init; } = Array.Empty<string>();

    /// <summary>Every day with data, oldest first, implausible values clamped.</summary>
    public IReadOnlyList<RollupDay> Days { get; init; } = Array.Empty<RollupDay>();

    /// <summary>Timed opens (last 90 days and at least the last 10), oldest first.</summary>
    public IReadOnlyList<DurationPoint> Opens { get; init; } = Array.Empty<DurationPoint>();

    /// <summary>Timed syncs at their start (same window), oldest first.</summary>
    public IReadOnlyList<DurationPoint> Syncs { get; init; } = Array.Empty<DurationPoint>();

    /// <summary>When the model was opened in the live session (its earliest still-open
    /// interval); null when it is not open there.</summary>
    public DateTimeOffset? LiveOpenSinceUtc { get; init; }

    /// <summary>Activity (Session): active time on the model since it was opened in the live
    /// session; null when it is not open there.</summary>
    public double? SessionActiveSeconds { get; init; }

    public ModelStatsPoint? LastStats { get; init; }

    /// <summary>The newest day's health inputs.</summary>
    public HealthInputs? CurrentHealth { get; init; }

    public RollupIdentity Identity { get; init; } = new();
    public KeysSeen KeysSeen { get; init; } = new();

    /// <summary>The zone the days are calendar days in.</summary>
    public TimeZoneInfo Zone { get; init; } = TimeZoneInfo.Utc;

    /// <summary>The oldest day with data (the footer's "since").</summary>
    public DateOnly? OldestDay => Days.Count > 0 ? Days[0].Date : null;

    public bool HasData => Days.Count > 0 || LiveOpenSinceUtc is not null;

    /// <summary>The last <paramref name="rangeDays"/> calendar days ending on <paramref name="today"/>.</summary>
    public static (DateOnly First, DateOnly Last) Range(int rangeDays, DateOnly today) =>
        (today.AddDays(-(Math.Max(1, rangeDays) - 1)), today);

    /// <summary>Days with data in [first, last].</summary>
    public IReadOnlyList<RollupDay> DaysIn(DateOnly first, DateOnly last) =>
        Days.Where(d => d.Date >= first && d.Date <= last).ToList();

    /// <summary>Every calendar day in [first, last], oldest first, zero days included (the
    /// daily chart's points).</summary>
    public IReadOnlyList<RollupDay> PerDay(DateOnly first, DateOnly last)
    {
        var map = Days.Where(d => d.Date >= first && d.Date <= last).ToDictionary(d => d.Date);
        var list = new List<RollupDay>();
        for (var d = first; d <= last; d = d.AddDays(1))
            list.Add(map.TryGetValue(d, out var row) ? row : new RollupDay { Date = d });
        return list;
    }

    /// <summary>ISO weeks with data among the days in [first, last] (the 6M / 1Y points).</summary>
    public IReadOnlyList<ActivityWeek> WeeksIn(DateOnly first, DateOnly last) => ActivityWeeks.Derive(DaysIn(first, last));

    public IReadOnlyList<DurationPoint> OpensIn(DateOnly first, DateOnly last) => EventsIn(Opens, first, last);

    public IReadOnlyList<DurationPoint> SyncsIn(DateOnly first, DateOnly last) => EventsIn(Syncs, first, last);

    /// <summary>Activity (Total) over [first, last], in seconds.</summary>
    public double ActiveSecondsIn(DateOnly first, DateOnly last) => DaysIn(first, last).Sum(d => d.ActiveMs) / 1000.0;

    /// <summary>The Longest Open card: the largest timed open in [first, last]; null with none.</summary>
    public double? LongestOpenSecondsIn(DateOnly first, DateOnly last)
    {
        var days = DaysIn(first, last).Where(d => d.Opens > 0).ToList();
        return days.Count == 0 ? null : days.Max(d => d.OpenMaxMs) / 1000.0;
    }

    /// <summary>The Longest Sync card: the largest sync in [first, last]; null with none.</summary>
    public double? LongestSyncSecondsIn(DateOnly first, DateOnly last)
    {
        var days = DaysIn(first, last).Where(d => d.Syncs > 0).ToList();
        return days.Count == 0 ? null : days.Max(d => d.SyncMaxMs) / 1000.0;
    }

    /// <summary>The Health trend chart: one dot per day used, or per week (its last day used).</summary>
    public IReadOnlyList<HealthTrendPoint> HealthTrend(DateOnly first, DateOnly last, bool weekly)
    {
        var points = new List<HealthTrendPoint>();
        if (weekly)
        {
            foreach (var w in WeeksIn(first, last))
                if (w.Health is { } h) points.Add(new HealthTrendPoint(w.LastDayUsed, h, ModelHealth.Evaluate(h)));
        }
        else
        {
            foreach (var d in DaysIn(first, last))
                if (d.Health is { } h && (d.OpenMs > 0 || d.ActiveMs > 0))
                    points.Add(new HealthTrendPoint(d.Date, h, ModelHealth.Evaluate(h)));
        }
        return points;
    }

    private IReadOnlyList<DurationPoint> EventsIn(IReadOnlyList<DurationPoint> points, DateOnly first, DateOnly last) =>
        points.Where(p =>
        {
            var day = ActiveTime.LocalDay(p.Ts, Zone);
            return day >= first && day <= last;
        }).ToList();
}

public static class ModelActivityReader
{
    /// <summary>The Revit side: the current model (null = no model open → empty).</summary>
    public static ModelActivity ReadCurrentModel(DocumentIdentity? currentModel, ActivityReadOptions options)
    {
        try
        {
            if (currentModel is null || SessionReplay.BuildMatcher(currentModel) is not { } matcher)
                return ModelActivity.Empty;
            var store = new RollupStore(options.RollupsDir, options.FileIO, options.Log);
            var index = store.LoadIndex(ReadMode.Reader, ActiveTime.LocalDay(options.NowUtc, options.Zone), out _);
            return Read(store, Resolve(index, matcher.Matches), currentModel, matcher.Kind, options);
        }
        catch (Exception ex)
        {
            SafeLog(options.Log, "activity read failed: " + ex.Message);
            return ModelActivity.Empty;
        }
    }

    /// <summary>
    /// The tray's model detail: one model by id, plus whatever is not folded yet. The model is
    /// resolved exactly as <see cref="ReadCurrentModel"/> resolves it — the same rollups through
    /// the same ladder — so for the same range the tray and Revit show the same numbers
    /// (spec #25 AC 4), Revit's live session aside.
    /// </summary>
    public static ModelActivity ReadModel(string modelId, ActivityReadOptions options)
    {
        try
        {
            var store = new RollupStore(options.RollupsDir, options.FileIO, options.Log);
            var index = store.LoadIndex(ReadMode.Reader, ActiveTime.LocalDay(options.NowUtc, options.Zone), out _);
            var keys = index.Models.FirstOrDefault(m => m.ModelId == modelId)?.Keys
                ?? store.LoadModel(modelId, ReadMode.Reader).Value?.Keys;
            if (keys is null) return ModelActivity.Empty;
            var identity = keys.ToIdentity();
            if (SessionReplay.BuildMatcher(identity) is not { } matcher) return ModelActivity.Empty;
            var ids = Resolve(index, matcher.Matches);
            if (!ids.Contains(modelId)) ids.Add(modelId);
            return Read(store, ids, identity, matcher.Kind, options);
        }
        catch (Exception ex)
        {
            SafeLog(options.Log, "activity read failed for " + modelId + ": " + ex.Message);
            return ModelActivity.Empty;
        }
    }

    /// <summary>index.json's rows (spec #25's Models list), from its .bak.json or rebuilt from the
    /// model files when unreadable; a bad row is skipped. Never throws.</summary>
    public static IReadOnlyList<ModelIndexRow> ReadIndex(string? rollupsDir = null, DateTimeOffset? nowUtc = null,
        TimeZoneInfo? zone = null, Action<string>? log = null)
    {
        try
        {
            var store = new RollupStore(rollupsDir ?? AppDataPaths.TelemetryRollupsDir, null, log);
            var today = ActiveTime.LocalDay(nowUtc ?? DateTimeOffset.UtcNow, zone ?? TimeZoneInfo.Local);
            return store.LoadIndex(ReadMode.Reader, today, out _).Models;
        }
        catch (Exception ex)
        {
            SafeLog(log, "activity index read failed: " + ex.Message);
            return Array.Empty<ModelIndexRow>();
        }
    }

    /// <summary>
    /// Every rollup the matcher accepts: each index row is tried with its keys and with each
    /// identity value it has since learned (keys_seen) substituted at its level — so a key first
    /// seen after the model was keyed still finds it — under the ladder's usual rules: a present
    /// key that differs rejects. Highest-keyed rollup first.
    /// </summary>
    internal static List<string> Resolve(RollupIndexFile index, Func<TelemetryEvent, bool> matches) =>
        index.Models
            .Where(row => KeyVariants(row.Keys, row.KeysSeen).Any(matches))
            .OrderBy(row => new ModelKey(row.KeyKind, "").Rank)
            .ThenBy(row => row.ModelId, StringComparer.Ordinal)
            .Select(row => row.ModelId)
            .ToList();

    private static IEnumerable<TelemetryEvent> KeyVariants(RollupKeys keys, KeysSeen seen)
    {
        yield return keys.ToKeysEvent();
        foreach (var v in seen.CreationGuids) yield return With(keys, k => k.CreationGuid = v);
        foreach (var v in seen.CentralPaths) yield return With(keys, k => k.CentralPath = v);
        foreach (var v in seen.CentralGuids) yield return With(keys, k => k.CentralGuid = v);
        foreach (var v in seen.CloudModelGuids) yield return With(keys, k => k.CloudModelGuid = v);
        foreach (var v in seen.CloudProjectGuids) yield return With(keys, k => k.CloudProjectGuid = v);
    }

    private static TelemetryEvent With(RollupKeys keys, Action<RollupKeys> set)
    {
        var copy = new RollupKeys
        {
            CloudProjectGuid = keys.CloudProjectGuid,
            CloudModelGuid = keys.CloudModelGuid,
            CentralGuid = keys.CentralGuid,
            CentralPath = keys.CentralPath,
            CreationGuid = keys.CreationGuid,
        };
        set(copy);
        return copy.ToKeysEvent();
    }

    private static ModelActivity Read(
        RollupStore store, IReadOnlyList<string> ids, DocumentIdentity identity, string kind, ActivityReadOptions o)
    {
        var rollups = new List<ModelRollup>();
        foreach (var id in ids)
            if (store.LoadModel(id, ReadMode.Reader).Value is { } r) rollups.Add(r);
        rollups = rollups.OrderBy(r => new ModelKey(r.KeyKind, "").Rank).ThenBy(r => r.ModelId, StringComparer.Ordinal).ToList();

        // Raw files not folded yet are replayed with the caller's own keys — the current model's
        // in Revit, the requested model's in the tray — never a weaker rollup's: a
        // creation-only rollup must not let a sibling central's events in (the ladder rejects
        // a present key that differs).
        if (SessionReplay.BuildMatcher(identity) is not { } matcher) return ModelActivity.Empty;
        var key = ModelKey.From(identity) ?? new ModelKey(matcher.Kind, "");

        // The requested ids, not only the ones that loaded: a folded file of a model whose
        // rollup is unreadable here is folded in memory instead.
        var idSet = new HashSet<string>(ids, StringComparer.Ordinal);
        var ledger = store.LoadLedger(ReadMode.Reader);
        var ledgerRows = ledger.Files
            .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // What the rollups do not hold for exactly this file, folded in memory.
        var extra = new List<(string Guid, ModelContribution C, FileFingerprint F)>();
        foreach (var path in ListOutbox(o.OutboxDir, o.Log))
        {
            try
            {
                var name = Path.GetFileName(path);
                var (size, ts) = RollupStore.Stat(path);
                if (size < 0) continue;
                var guid = OutboxFiles.TryParseSessionGuid(name);
                if (guid is not null && rollups.Any(r => r.Holds(guid, size, ts))) continue; // this very file is in
                if (ledgerRows.TryGetValue(name, out var row) && row.Unclosed != true &&
                    row.Size == size && RollupJson.TruncateMs(row.LastWriteUtc) == ts &&
                    !row.Models.Any(m => idSet.Contains(m.ModelId)))
                    continue; // folded, and it touched other models only

                var events = OutboxFiles.ReadEvents(path);
                if (events.Count == 0) continue;
                var info = SessionInfo.Read(events, name);
                var sessionGuid = info.SessionGuid ?? guid;
                if (sessionGuid is null) continue;
                if (rollups.Any(r => r.Holds(sessionGuid, size, ts))) continue;
                var c = ActivityFold.Contribute(events, matcher.Matches, key, identity, info, o.Zone,
                    info.Closed ? DateTimeOffset.MaxValue : o.NowUtc, o.LiveSessionGuid);
                // Replace, never add to, a share the rollups hold for another version of this file.
                foreach (var r in rollups) RollupEditor.RemovePending(r, sessionGuid, o.Zone, o.NowUtc);
                if (!c.IsEmpty) extra.Add((sessionGuid, c, new FileFingerprint(name, size, ts)));
            }
            catch (Exception ex)
            {
                SafeLog(o.Log, "activity read skipped " + Path.GetFileName(path) + ": " + ex.Message);
            }
        }

        var view = rollups.Count > 0 ? RollupEditor.MergeViews(rollups, o.Zone) : RollupEditor.Create(key, identity);
        double? sessionActive = null;
        DateTimeOffset? liveSince = null;
        foreach (var (guid, c, f) in extra)
        {
            RollupEditor.ApplyPending(view, guid, RollupEditor.ToPending(c, f.Name, f.Size, f.LastWriteUtc),
                c.IdentityEvents, o.Zone, o.NowUtc);
            if (c.LiveOpenSince is { } since)
            {
                liveSince = liveSince is null || since < liveSince ? since : liveSince;
                sessionActive = (sessionActive ?? 0) + c.LiveActiveMs / 1000.0;
            }
        }
        RollupEditor.TrimEvents(view, o.NowUtc); // the event window ages even while nothing folds

        foreach (var d in view.Days) d.Clamp();
        ModelStatsPoint? last = null;
        foreach (var d in view.Days) last = ModelStatsPoint.Latest(last, d.Stats);
        return new ModelActivity
        {
            MatchedKeyKind = kind,
            ModelIds = rollups.Select(r => r.ModelId).ToList(),
            Days = view.Days,
            Opens = view.Opens,
            Syncs = view.Syncs,
            LiveOpenSinceUtc = liveSince,
            SessionActiveSeconds = sessionActive,
            LastStats = last,
            CurrentHealth = view.Days.LastOrDefault(d => d.Health is not null)?.Health,
            Identity = view.Identity,
            KeysSeen = view.KeysSeen,
            Zone = o.Zone,
        };
    }

    private readonly record struct FileFingerprint(string Name, long Size, DateTimeOffset LastWriteUtc);

    private static string[] ListOutbox(string dir, Action<string>? log)
    {
        try
        {
            if (!Directory.Exists(dir)) return Array.Empty<string>();
            var files = Directory.GetFiles(dir, "*" + OutboxFiles.Extension);
            Array.Sort(files, StringComparer.Ordinal);
            return files;
        }
        catch (Exception ex)
        {
            SafeLog(log, "activity read: outbox scan failed: " + ex.Message);
            return Array.Empty<string>();
        }
    }

    private static void SafeLog(Action<string>? log, string message)
    {
        try { log?.Invoke(message); }
        catch { /* logging must never take the read down */ }
    }
}
