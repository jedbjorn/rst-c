// rst-c's own (not vendored): turns one session file's events into each
// model's contribution — daily rows and timed events (spec #24, Fold &
// Self-Healing). The tray's fold and the shared read path both call it, so
// the numbers Revit shows and the numbers the tray stores come from one
// piece of code (decision #52).
//
// Which events belong to a model is decided by SessionReplay's ladder and
// join rules, unchanged; this file only picks the models a session touched
// and sums what the replay found per local day.


namespace RST.Core.Telemetry.Rollups;

/// <summary>Session-level facts every model in the file shares.</summary>
internal sealed class SessionInfo
{
    public string? SessionGuid { get; init; }
    public string? RevitVersion { get; init; }
    public string? RevitSubversion { get; init; }
    public bool Closed { get; init; }

    public static SessionInfo Read(List<TelemetryEvent> events, string? fileName)
    {
        var start = events.FirstOrDefault(e => e.EventType == TelemetryEventTypes.SessionStart);
        var guid = fileName is null ? null : OutboxFiles.TryParseSessionGuid(fileName);
        guid ??= events.Count > 0 && Guid.TryParse(events[0].SessionGuid, out var g) ? g.ToString() : null;
        return new SessionInfo
        {
            SessionGuid = guid,
            RevitVersion = Clean(start?.GetString(TelemetryFields.RevitVersion)),
            RevitSubversion = Clean(start?.GetString(TelemetryFields.RevitSubversion)),
            Closed = events.Any(e => e.EventType == TelemetryEventTypes.SessionEnd),
        };
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}

/// <summary>One model's share of one session.</summary>
internal sealed class ModelContribution
{
    public ModelKey Key { get; init; }

    /// <summary>The identity of the doc_opened that defined the model in this session.</summary>
    public DocumentIdentity Defining { get; init; } = new();

    public List<RollupDay> Days { get; init; } = new();
    public List<DurationPoint> Opens { get; init; } = new();
    public List<DurationPoint> Syncs { get; init; } = new();
    public List<TelemetryEvent> IdentityEvents { get; init; } = new();

    /// <summary>Active time inside the intervals still open at the end of the live session.</summary>
    public long LiveActiveMs { get; init; }

    public DateTimeOffset? LiveOpenSince { get; init; }

    public bool IsEmpty => Days.Count == 0 && Opens.Count == 0 && Syncs.Count == 0 && LiveOpenSince is null;
}

internal static class ActivityFold
{
    /// <summary>
    /// Every model a closed session touched, with its contribution. A model is
    /// keyed by the highest ladder key its doc_opened (or doc_saved_as) identity
    /// carries. A candidate that only an identity-capture gap separates from a
    /// higher one — the two matchers accept each other's identity, and no other
    /// candidate does — is the same document and folds into the higher one, so
    /// one session never counts one model twice.
    /// </summary>
    public static List<ModelContribution> FoldSession(
        List<TelemetryEvent> events, SessionInfo info, TimeZoneInfo zone,
        DateTimeOffset nowUtc, string? liveSessionGuid = null)
    {
        events.Sort((a, b) => a.Seq.CompareTo(b.Seq));
        var candidates = new List<(ModelKey Key, DocumentIdentity Id, TelemetryEvent E, Func<TelemetryEvent, bool> Matches)>();
        foreach (var e in events)
        {
            if (e.EventType is not (TelemetryEventTypes.DocOpened or TelemetryEventTypes.DocSavedAs)) continue;
            var id = DocumentIdentity.ReadFrom(e);
            if (ModelKey.From(id) is not { } key) continue;
            if (candidates.Any(c => c.Key == key)) continue;
            if (SessionReplay.BuildMatcher(id) is not { } m) continue;
            candidates.Add((key, id, e, m.Matches));
        }

        var kept = new List<(ModelKey Key, DocumentIdentity Id, TelemetryEvent E, Func<TelemetryEvent, bool> Matches)>();
        foreach (var c in candidates.OrderBy(c => c.Key.Rank))
        {
            var targets = kept.Count(o => o.Key.Rank < c.Key.Rank && o.Matches(c.E) && c.Matches(o.E));
            var others = candidates.Count(o => o.Key != c.Key && o.Key.Rank < c.Key.Rank && o.Matches(c.E) && c.Matches(o.E));
            if (targets == 1 && others == 1) continue; // the same document seen through a capture gap
            kept.Add(c);
        }

        var result = new List<ModelContribution>(kept.Count);
        foreach (var c in kept)
        {
            var contribution = Contribute(events, c.Matches, c.Key, c.Id, info, zone, nowUtc, liveSessionGuid);
            if (!contribution.IsEmpty) result.Add(contribution);
        }
        return result;
    }

    /// <summary>
    /// The contribution of the model <paramref name="matches"/> accepts. A closed file
    /// is replayed with <paramref name="nowUtc"/> = <see cref="DateTimeOffset.MaxValue"/>
    /// so the result depends on the file alone; the live session's file is replayed at
    /// now with its guid, so its open intervals extend to now.
    /// </summary>
    public static ModelContribution Contribute(
        List<TelemetryEvent> events, Func<TelemetryEvent, bool> matches,
        ModelKey key, DocumentIdentity defining, SessionInfo info, TimeZoneInfo zone,
        DateTimeOffset nowUtc, string? liveSessionGuid)
    {
        var sink = new ReplaySink();
        SessionReplay.Replay(events, matches, nowUtc, liveSessionGuid, sink);

        var days = new SortedDictionary<DateOnly, RollupDay>();
        RollupDay Row(DateOnly d)
        {
            if (!days.TryGetValue(d, out var r)) days[d] = r = new RollupDay { Date = d };
            return r;
        }

        foreach (var (day, ms) in ActiveTime.MillisecondsByDay(SessionReplay.MergeIntervals(sink.Intervals), zone))
            Row(day).OpenMs += ms;
        foreach (var (day, ms) in ActiveTime.MillisecondsByDay(ActiveTime.Spans(sink.ActiveIntervals, sink.Pulses), zone))
            Row(day).ActiveMs += ms;

        var opens = new List<DurationPoint>();
        foreach (var p in sink.OpenPoints)
        {
            var ms = ToMs(p.Seconds);
            var r = Row(ActiveTime.LocalDay(p.Ts, zone));
            r.Opens++;
            r.OpenSumMs += ms;
            r.OpenMaxMs = Math.Max(r.OpenMaxMs, ms);
            opens.Add(new DurationPoint(p.Ts, ms / 1000.0));
        }

        var syncs = new List<DurationPoint>();
        foreach (var p in sink.SyncPoints)
        {
            var ms = ToMs(p.Seconds);
            var r = Row(ActiveTime.LocalDay(p.Ts, zone));
            r.Syncs++;
            r.SyncSumMs += ms;
            r.SyncMaxMs = Math.Max(r.SyncMaxMs, ms);
            syncs.Add(new DurationPoint(p.Ts, ms / 1000.0));
        }

        foreach (var e in sink.Stats)
        {
            var warnings = e.GetInt64(TelemetryFields.WarningsCount);
            var size = e.GetInt64(TelemetryFields.FileSizeBytes);
            var point = new ModelStatsPoint(
                RollupJson.TruncateMs(e.Ts),
                warnings is { } w && w >= 0 ? (int)Math.Min(w, int.MaxValue) : null,
                size is { } s && s >= 0 ? s : null);
            var r = Row(ActiveTime.LocalDay(e.Ts, zone));
            r.Stats = ModelStatsPoint.Latest(r.Stats, point);
        }

        foreach (var r in days.Values)
        {
            if (info.RevitVersion is { } v) r.RevitVersions = new List<string> { v };
            if (info.RevitSubversion is { } sv) r.RevitSubversions = new List<string> { sv };
        }

        var live = sink.LiveIntervals.Count > 0
            ? ActiveTime.TotalMilliseconds(ActiveTime.Spans(sink.LiveIntervals, sink.Pulses))
            : 0;

        return new ModelContribution
        {
            Key = key,
            Defining = defining,
            Days = days.Values.Where(d => !d.IsEmpty).ToList(),
            Opens = opens,
            Syncs = syncs,
            IdentityEvents = sink.IdentityEvents,
            LiveActiveMs = live,
            LiveOpenSince = sink.LiveOpenSince,
        };
    }

    private static long ToMs(double seconds) => (long)Math.Round(seconds * 1000.0);
}
