// rst-c's own (not vendored): the operations on one model's rollup (spec
// #24, Rollup Store and Model Health) — apply or replace a session's pending
// contribution, remove it, seal it, keep the event window, recompute the
// per-day health inputs, and build the model's index row. Pure: no IO.
//
// The rollup's Days are always the whole truth (sealed + pending), so a
// fork reading the file needs nothing else. PendingBase holds, for each day
// a pending contribution touches, that day's sealed totals: replacing a
// contribution rebuilds the day as base ⊕ every pending share, exactly,
// with no subtraction (maxima and version sets cannot be subtracted).

using RST.Core.Health;

namespace RST.Core.Telemetry.Rollups;

internal static class RollupEditor
{
    /// <summary>Event points are kept for this many days, and always at least <see cref="EventMinimum"/>.</summary>
    public const int EventWindowDays = 90;

    public const int EventMinimum = ModelHealth.MedianWindow;

    public static ModelRollup Create(ModelKey key, DocumentIdentity defining) => new()
    {
        ModelId = key.ModelId,
        KeyKind = key.Kind,
        Keys = RollupKeys.From(defining),
    };

    public static PendingSession ToPending(ModelContribution c, string fileName, long size, DateTimeOffset lastWriteUtc) => new()
    {
        File = fileName,
        Size = size,
        LastWriteUtc = RollupJson.TruncateMs(lastWriteUtc),
        Days = c.Days.Select(d => d.CloneCounters()).ToList(),
        Opens = SortPoints(c.Opens),
        Syncs = SortPoints(c.Syncs),
    };

    /// <summary>
    /// Put <paramref name="pending"/> in as session <paramref name="sessionGuid"/>'s share,
    /// replacing any earlier share of that session — sealed or not: while the raw file exists a
    /// change always replaces, and the replacement is unsealed until a later unchanged pass.
    /// No-op (false) when the share is identical (same fingerprint and content) — re-folding an
    /// unchanged file leaves the rollup byte-identical and keeps its seal.
    /// </summary>
    public static bool ApplyPending(
        ModelRollup r, string sessionGuid, PendingSession pending,
        IEnumerable<TelemetryEvent> identityEvents, TimeZoneInfo zone, DateTimeOffset nowUtc)
    {
        r.Pending.TryGetValue(sessionGuid, out var old);
        pending.Sealed = false;
        if (old is not null && SameContent(old, pending)) return false;

        var days = r.Days.ToDictionary(d => d.Date);
        var bases = r.PendingBase.ToDictionary(d => d.Date);
        var touched = new SortedSet<DateOnly>(pending.Days.Select(d => d.Date));
        if (old is not null)
        {
            touched.UnionWith(old.Days.Select(d => d.Date));
            RemoveEvents(r, old);
        }
        foreach (var d in pending.Days)
            if (!bases.ContainsKey(d.Date))
                bases[d.Date] = days.TryGetValue(d.Date, out var cur) ? cur.CloneCounters() : new RollupDay { Date = d.Date };

        r.Pending[sessionGuid] = pending;
        AddEvents(r, pending);
        Rebuild(r, days, bases, touched);
        MergeIdentity(r, identityEvents);
        Finish(r, days, bases, touched.Count > 0 ? touched.Min : null, zone, nowUtc);
        return true;
    }

    /// <summary>Take session <paramref name="sessionGuid"/>'s pending share out (the file no
    /// longer touches this model). False when it had none.</summary>
    public static bool RemovePending(ModelRollup r, string sessionGuid, TimeZoneInfo zone, DateTimeOffset nowUtc)
    {
        if (!r.Pending.TryGetValue(sessionGuid, out var old)) return false;
        var days = r.Days.ToDictionary(d => d.Date);
        var bases = r.PendingBase.ToDictionary(d => d.Date);
        var touched = new SortedSet<DateOnly>(old.Days.Select(d => d.Date));
        RemoveEvents(r, old);
        r.Pending.Remove(sessionGuid);
        Rebuild(r, days, bases, touched);
        Finish(r, days, bases, touched.Count > 0 ? touched.Min : null, zone, nowUtc);
        return true;
    }

    /// <summary>Seal session <paramref name="sessionGuid"/>'s share for exactly this file
    /// fingerprint. True when it was sealed now; false when it was already sealed or the share
    /// does not match the file (it must re-fold first). Totals are unchanged.</summary>
    public static bool Seal(ModelRollup r, string sessionGuid, long size, DateTimeOffset lastWriteUtc)
    {
        if (!r.Pending.TryGetValue(sessionGuid, out var p) || !p.Matches(size, lastWriteUtc) || p.Sealed) return false;
        p.Sealed = true;
        return true;
    }

    /// <summary>
    /// Move the shares of sessions whose raw file is gone into the permanent totals — nothing can
    /// replace them any more. Days, events and health inputs are unchanged; only the bookkeeping
    /// moves. True when anything moved.
    /// </summary>
    public static bool AbsorbGone(ModelRollup r, Func<string, bool> sessionFilePresent)
    {
        var gone = r.Pending.Keys.Where(g => !sessionFilePresent(g)).ToList();
        if (gone.Count == 0) return false;
        var bases = r.PendingBase.ToDictionary(d => d.Date);
        foreach (var guid in gone)
        {
            foreach (var d in r.Pending[guid].Days)
            {
                if (!bases.TryGetValue(d.Date, out var b)) bases[d.Date] = b = new RollupDay { Date = d.Date };
                b.Add(d);
            }
            r.Pending.Remove(guid);
        }
        DropUntouchedBases(r, bases);
        r.PendingBase = bases.Values.OrderBy(d => d.Date).ToList();
        return true;
    }

    /// <summary>
    /// Recompute the health inputs of every day from <paramref name="fromDay"/> on: the latest
    /// size and warning count recorded by the end of the day, and the medians of the last 10
    /// opens and syncs up to then. A median is recomputed only while the event window still
    /// holds what it needs (10 events up to that day, or none ever dropped); otherwise the
    /// stored value stays.
    /// </summary>
    public static void RecomputeHealth(ModelRollup r, DateOnly fromDay, TimeZoneInfo zone)
    {
        var opens = r.Opens;
        var syncs = r.Syncs;
        var openDays = opens.Select(p => ActiveTime.LocalDay(p.Ts, zone)).ToArray();
        var syncDays = syncs.Select(p => ActiveTime.LocalDay(p.Ts, zone)).ToArray();
        int oi = 0, si = 0;
        ModelStatsPoint? latest = null;
        foreach (var row in r.Days)
        {
            latest = ModelStatsPoint.Latest(latest, row.Stats);
            while (oi < openDays.Length && openDays[oi] <= row.Date) oi++;
            while (si < syncDays.Length && syncDays[si] <= row.Date) si++;
            if (row.Date < fromDay) continue;
            var open = MedianUpTo(opens, oi, r.OpensTrimmedThrough, row.Health?.OpenMedianSeconds);
            var sync = MedianUpTo(syncs, si, r.SyncsTrimmedThrough, row.Health?.SyncMedianSeconds);
            row.Health = new HealthInputs(latest?.FileSizeBytes, latest?.WarningsCount, open, sync);
        }
    }

    /// <summary>Keep events of the last 90 days and always the newest 10; remember the newest dropped.</summary>
    public static void TrimEvents(ModelRollup r, DateTimeOffset nowUtc)
    {
        var cutoff = nowUtc == DateTimeOffset.MaxValue ? DateTimeOffset.MinValue : nowUtc.AddDays(-EventWindowDays);
        r.OpensTrimmedThrough = Trim(r.Opens, cutoff, r.OpensTrimmedThrough);
        r.SyncsTrimmedThrough = Trim(r.Syncs, cutoff, r.SyncsTrimmedThrough);
    }

    /// <summary>The model's row for index.json, with the recent windows relative to <paramref name="today"/>.</summary>
    public static ModelIndexRow BuildIndexRow(ModelRollup r, DateOnly today, long rollupBytes, DateTimeOffset? rollupLastWriteUtc)
    {
        var row = new ModelIndexRow
        {
            ModelId = r.ModelId,
            KeyKind = r.KeyKind,
            Keys = r.Keys,
            KeysSeen = r.KeysSeen,
            Title = r.Identity.Title,
            LocalPath = r.Identity.LocalPath,
            CentralPath = r.Identity.CentralPath ?? r.Keys.CentralPath,
            IsWorkshared = r.Identity.IsWorkshared,
            IsCloud = r.Identity.IsCloud,
            RollupBytes = rollupBytes,
            RollupLastWriteUtc = rollupLastWriteUtc is { } written ? RollupJson.TruncateMs(written) : null,
            NextEventExpiryUtc = NextEventExpiry(r),
        };
        var firstRecentDay = today.AddDays(-(ModelIndexRow.RecentDays - 1));
        var firstRecentWeek = ActivityWeeks.WeekStart(today).AddDays(-7 * (ModelIndexRow.RecentWeeks - 1));
        var weeks = new SortedDictionary<DateOnly, ActivityTotals>();
        foreach (var d in r.Days)
        {
            row.Totals.Add(d);
            if (d.OpenMs > 0 || d.ActiveMs > 0)
            {
                row.FirstActiveDay ??= d.Date;
                row.LastActiveDay = d.Date;
                foreach (var v in d.RevitVersions) row.RevitVersions[v] = d.Date;
                foreach (var v in d.RevitSubversions) row.RevitSubversions[v] = d.Date;
            }
            row.LastStats = ModelStatsPoint.Latest(row.LastStats, d.Stats);
            if (d.Health is not null) row.Health = d.Health;
            if (d.Date >= firstRecentDay && d.Date <= today)
            {
                var t = new ActivityTotals { Date = d.Date };
                t.Add(d);
                row.RecentDaysTotals.Add(t);
            }
            if (d.Date >= firstRecentWeek && d.Date <= today)
            {
                var ws = ActivityWeeks.WeekStart(d.Date);
                if (!weeks.TryGetValue(ws, out var wt)) weeks[ws] = wt = new ActivityTotals { Date = ws };
                wt.Add(d);
            }
        }
        row.RecentWeeksTotals = weeks.Values.ToList();
        return row;
    }

    /// <summary>
    /// A copy of the combined materialized view of several rollups of one model (an
    /// identity-capture gap can split a document across ladder levels; the read path
    /// matches them all, as the aggregator always has). The first rollup is the primary:
    /// its keys and, where it has the day, its stored health inputs win.
    /// </summary>
    public static ModelRollup MergeViews(IReadOnlyList<ModelRollup> rollups, TimeZoneInfo zone)
    {
        if (rollups.Count == 0) return new ModelRollup();
        var primary = rollups[0];
        var merged = new ModelRollup
        {
            ModelId = primary.ModelId,
            KeyKind = primary.KeyKind,
            Keys = primary.Keys,
            Identity = primary.Identity,
            OpensTrimmedThrough = primary.OpensTrimmedThrough,
            SyncsTrimmedThrough = primary.SyncsTrimmedThrough,
            Days = primary.Days.Select(d => d.Clone()).ToList(),
            Opens = new List<DurationPoint>(primary.Opens),
            Syncs = new List<DurationPoint>(primary.Syncs),
        };
        merged.KeysSeen.Add(primary.KeysSeen);
        if (rollups.Count == 1) return merged;

        var days = merged.Days.ToDictionary(d => d.Date);
        DateOnly? from = null;
        foreach (var other in rollups.Skip(1))
        {
            foreach (var d in other.Days)
            {
                if (days.TryGetValue(d.Date, out var row)) row.Add(d);
                else days[d.Date] = d.Clone();
                if (from is null || d.Date < from) from = d.Date;
            }
            merged.Opens.AddRange(other.Opens);
            merged.Syncs.AddRange(other.Syncs);
            merged.OpensTrimmedThrough = Max(merged.OpensTrimmedThrough, other.OpensTrimmedThrough);
            merged.SyncsTrimmedThrough = Max(merged.SyncsTrimmedThrough, other.SyncsTrimmedThrough);
            merged.KeysSeen.Add(other.KeysSeen);
            if (other.Identity.AsOf is { } asOf && (merged.Identity.AsOf is null || asOf > merged.Identity.AsOf))
                merged.Identity = other.Identity;
        }
        foreach (var d in days.Values) d.Clamp();
        merged.Days = days.Values.OrderBy(d => d.Date).ToList();
        merged.Opens = SortPoints(merged.Opens);
        merged.Syncs = SortPoints(merged.Syncs);
        if (from is { } f) RecomputeHealth(merged, f, zone);
        return merged;
    }

    // ---- internals --------------------------------------------------------

    private static void Rebuild(ModelRollup r, Dictionary<DateOnly, RollupDay> days,
        Dictionary<DateOnly, RollupDay> bases, IEnumerable<DateOnly> touched)
    {
        var shares = r.Pending.Values.Select(p => p.Days.ToDictionary(d => d.Date)).ToList();
        foreach (var date in touched)
        {
            var row = bases.TryGetValue(date, out var b) ? b.CloneCounters() : new RollupDay { Date = date };
            foreach (var share in shares)
                if (share.TryGetValue(date, out var part)) row.Add(part);
            var previousHealth = days.TryGetValue(date, out var prev) ? prev.Health : null;
            if (row.IsEmpty)
            {
                days.Remove(date);
            }
            else
            {
                // Overlapping sessions can sum past a day; the stored view is capped like
                // every reader's (the shares keep their own values for replacement).
                row.Clamp();
                row.Health = previousHealth;
                days[date] = row;
            }
        }
        DropUntouchedBases(r, bases);
    }

    private static void Finish(ModelRollup r, Dictionary<DateOnly, RollupDay> days,
        Dictionary<DateOnly, RollupDay> bases, DateOnly? fromDay, TimeZoneInfo zone, DateTimeOffset nowUtc)
    {
        r.Days = days.Values.OrderBy(d => d.Date).ToList();
        r.PendingBase = bases.Values.OrderBy(d => d.Date).ToList();
        if (fromDay is { } f) RecomputeHealth(r, f, zone);
        TrimEvents(r, nowUtc);
    }

    private static void DropUntouchedBases(ModelRollup r, Dictionary<DateOnly, RollupDay> bases)
    {
        var stillTouched = new HashSet<DateOnly>(r.Pending.Values.SelectMany(p => p.Days.Select(d => d.Date)));
        foreach (var date in bases.Keys.ToList())
            if (!stillTouched.Contains(date)) bases.Remove(date);
    }

    private static void AddEvents(ModelRollup r, PendingSession p)
    {
        r.Opens = SortPoints(r.Opens.Concat(p.Opens));
        r.Syncs = SortPoints(r.Syncs.Concat(p.Syncs));
    }

    private static void RemoveEvents(ModelRollup r, PendingSession p)
    {
        RemoveEach(r.Opens, p.Opens);
        RemoveEach(r.Syncs, p.Syncs);
    }

    /// <summary>Multiset removal; a point the window already dropped is simply absent.</summary>
    private static void RemoveEach(List<DurationPoint> list, List<DurationPoint> remove)
    {
        foreach (var p in remove)
        {
            var i = list.IndexOf(p);
            if (i >= 0) list.RemoveAt(i);
        }
    }

    /// <summary>
    /// Fill a key level the model was first seen without, from an identity the model's matcher
    /// accepted (for example a creation GUID first captured in a later session). Only empty
    /// levels fill — a known key is never replaced, so the ladder's rejection rules hold and the
    /// model id never changes.
    /// </summary>
    private static void LearnKeys(RollupKeys keys, DocumentIdentity id)
    {
        if (keys.CloudProjectGuid is null && keys.CloudModelGuid is null &&
            !string.IsNullOrWhiteSpace(id.CloudProjectGuid) && !string.IsNullOrWhiteSpace(id.CloudModelGuid))
        {
            keys.CloudProjectGuid = id.CloudProjectGuid;
            keys.CloudModelGuid = id.CloudModelGuid;
        }
        if (keys.CentralGuid is null && !string.IsNullOrWhiteSpace(id.CentralGuid)) keys.CentralGuid = id.CentralGuid;
        if (string.IsNullOrEmpty(keys.CentralPath) && !string.IsNullOrWhiteSpace(id.CentralPath)) keys.CentralPath = id.CentralPath;
        if (keys.CreationGuid is null && !string.IsNullOrWhiteSpace(id.CreationGuid)) keys.CreationGuid = id.CreationGuid;
    }

    private static void MergeIdentity(ModelRollup r, IEnumerable<TelemetryEvent> identityEvents)
    {
        foreach (var e in identityEvents)
        {
            var id = DocumentIdentity.ReadFrom(e);
            r.KeysSeen.Add(id);
            LearnKeys(r.Keys, id);
            var ts = RollupJson.TruncateMs(e.Ts);
            if (r.Identity.AsOf is { } asOf && ts < asOf) continue;
            r.Identity = new RollupIdentity
            {
                Title = id.Title ?? r.Identity.Title,
                LocalPath = id.LocalPath ?? r.Identity.LocalPath,
                CentralPath = id.CentralPath ?? r.Identity.CentralPath,
                IsWorkshared = id.IsWorkshared ?? r.Identity.IsWorkshared,
                IsCloud = id.IsCloud ?? r.Identity.IsCloud,
                AsOf = ts,
            };
        }
    }

    /// <summary>Same fingerprint and content, the seal aside.</summary>
    private static bool SameContent(PendingSession a, PendingSession b)
    {
        bool sa = a.Sealed, sb = b.Sealed;
        a.Sealed = b.Sealed = false;
        try { return RollupJson.Serialize(a).AsSpan().SequenceEqual(RollupJson.Serialize(b)); }
        finally { a.Sealed = sa; b.Sealed = sb; }
    }

    /// <summary>When the next stored event leaves the window: the oldest point beyond the newest
    /// 10 of each list, plus 90 days.</summary>
    public static DateTimeOffset? NextEventExpiry(ModelRollup r)
    {
        DateTimeOffset? next = null;
        foreach (var list in new[] { r.Opens, r.Syncs })
            if (list.Count > EventMinimum)
            {
                var at = list[0].Ts.AddDays(EventWindowDays);
                if (next is null || at < next) next = at;
            }
        return next;
    }

    internal static List<DurationPoint> SortPoints(IEnumerable<DurationPoint> points) =>
        points.OrderBy(p => p.Ts).ThenBy(p => p.Seconds).ToList();

    private static double? MedianUpTo(List<DurationPoint> points, int count, DateTimeOffset? trimmedThrough, double? stored)
    {
        if (count >= EventMinimum)
            return ModelHealth.Median(points.GetRange(count - EventMinimum, EventMinimum).Select(p => p.Seconds).ToList());
        if (trimmedThrough is not null) return stored; // the window no longer reaches back far enough
        return count == 0 ? null : ModelHealth.Median(points.GetRange(0, count).Select(p => p.Seconds).ToList());
    }

    private static DateTimeOffset? Trim(List<DurationPoint> points, DateTimeOffset cutoff, DateTimeOffset? trimmedThrough)
    {
        var keepNewest = points.Count - EventMinimum;
        var kept = new List<DurationPoint>(points.Count);
        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            if (i >= keepNewest || p.Ts >= cutoff) kept.Add(p);
            else trimmedThrough = Max(trimmedThrough, p.Ts);
        }
        if (kept.Count != points.Count)
        {
            points.Clear();
            points.AddRange(kept);
        }
        return trimmedThrough;
    }

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : (a > b ? a : b);
}
