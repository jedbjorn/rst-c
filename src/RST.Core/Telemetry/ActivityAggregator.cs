// ActivityAggregator.cs — parses outbox files and computes the Activity
// tab's series for the current file (Telemetry v1 spec, Build Plan
// step 3). Pure, unit-testable, no Revit references.
//
// Current-file matching uses the same priority order the server would —
// cloud GUID pair → central GUID → creation GUID — decided per event at
// the highest level the event carries: a present key that differs
// rejects, an absent key falls through, so an identity-capture gap on
// one event can't orphan its close or sync endpoint. Presentation-time
// filtering only; raw events stay raw.
//
// Open-hours replay mirrors RecoveryScanner.TrackOpenDocs: doc_closed
// carries only closing_id + status, so identity joins through the
// preceding doc_closing; recovery-written synthetic closes carry join
// keys directly. Durations are bounded undercounts, never overcounts:
//   - an unclosed non-live session is capped at its last observed event
//     (recovery's ≤2-minute truncation, applied at read time);
//   - a collection_disabled marker caps open intervals at the toggle —
//     time in the gap is unknown and never invented;
//   - negative spans (clock changes mid-session) are dropped, not
//     clamped into being.
// Overlapping intervals across concurrent sessions are unioned before
// bucketing, so a file open in two instances never exceeds wall-clock.

using System.IO;

namespace RST.Core.Telemetry;

public static class ActivityAggregator
{
    /// <summary>
    /// Compute the Activity tab's series for <paramref name="currentFile"/>
    /// from every readable outbox file. Never throws; unreadable files are
    /// logged and skipped, a missing dir is an empty outbox.
    /// </summary>
    /// <param name="outboxDir">The outbox directory to scan.</param>
    /// <param name="currentFile">Identity keys of the active document;
    /// null (no model open) yields an empty series.</param>
    /// <param name="rangeDays">Range window in days (7/30/90/180); the
    /// series covers the last <paramref name="rangeDays"/> calendar days
    /// ending today.</param>
    /// <param name="nowUtc">The current instant; the live session's open
    /// interval extends to it.</param>
    /// <param name="dayZone">Zone that defines "calendar day" for the
    /// per-day buckets and range window — the user's local zone in
    /// production. Null = UTC.</param>
    /// <param name="liveSessionGuid">This session's guid: its unclosed
    /// file extends to now; any other unclosed file is capped at its last
    /// event (a crashed session recovery hasn't reached yet).</param>
    public static ActivitySeries Aggregate(
        string outboxDir,
        DocumentIdentity? currentFile,
        int rangeDays,
        DateTimeOffset nowUtc,
        TimeZoneInfo? dayZone = null,
        string? liveSessionGuid = null,
        Action<string>? log = null)
    {
        var matcher = currentFile is null ? null : BuildMatcher(currentFile);
        if (matcher is null) return ActivitySeries.Empty;

        var zone = dayZone ?? TimeZoneInfo.Utc;
        if (rangeDays < 1) rangeDays = 1;
        var lastDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, zone).Date);
        var firstDay = lastDay.AddDays(-(rangeDays - 1));

        var intervals = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        var openPoints = new List<DurationPoint>();
        var syncPoints = new List<DurationPoint>();

        foreach (var path in ListOutboxFiles(outboxDir, log))
        {
            try
            {
                var events = OutboxFiles.ReadEvents(path);
                if (events.Count == 0) continue;
                ReplaySession(events, matcher.Value.Matches, nowUtc, liveSessionGuid,
                    intervals, openPoints, syncPoints);
            }
            catch (Exception ex)
            {
                SafeLog(log, "telemetry aggregate skipped " + Path.GetFileName(path) + ": " + ex.Message);
            }
        }

        var days = BucketByDay(MergeIntervals(intervals), zone, firstDay, lastDay);
        var perDay = new List<DayOpenHoursPoint>(rangeDays);
        for (var d = firstDay; d <= lastDay; d = d.AddDays(1))
            perDay.Add(new DayOpenHoursPoint(d, days.GetValueOrDefault(d)));

        return new ActivitySeries
        {
            MatchedKeyKind = matcher.Value.Kind,
            PerDayOpenHours = perDay,
            OpenEvents = FilterToRange(openPoints, zone, firstDay, lastDay),
            SyncEvents = FilterToRange(syncPoints, zone, firstDay, lastDay),
        };
    }

    private static string[] ListOutboxFiles(string outboxDir, Action<string>? log)
    {
        try
        {
            if (!Directory.Exists(outboxDir)) return Array.Empty<string>();
            var files = Directory.GetFiles(outboxDir, "*" + OutboxFiles.Extension);
            Array.Sort(files, StringComparer.Ordinal);
            return files;
        }
        catch (Exception ex)
        {
            SafeLog(log, "telemetry aggregate scan failed: " + ex.Message);
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Return the current file's best key kind and a per-event match
    /// predicate. Each event is decided at the highest priority level
    /// (cloud pair → central → creation) it actually carries: a present
    /// key that differs rejects, an absent key falls through to the next
    /// level — an identity-capture gap on one event must not orphan its
    /// close or sync endpoint. Null when the file carries no usable key —
    /// nothing can match.
    /// </summary>
    private static (string Kind, Func<TelemetryEvent, bool> Matches)? BuildMatcher(DocumentIdentity file)
    {
        // Each level returns true/false when the event carries that key,
        // null when it is absent and the next level should decide.
        var levels = new List<Func<TelemetryEvent, bool?>>();
        string? kind = null;

        if (file.CloudProjectGuid is { } project && file.CloudModelGuid is { } model)
        {
            kind = ActivityMatchKinds.Cloud;
            levels.Add(e =>
            {
                var eModel = e.GetString(TelemetryFields.CloudModelGuid);
                var eProject = e.GetString(TelemetryFields.CloudProjectGuid);
                if (eModel is not null)
                    return KeyEquals(eModel, model) && (eProject is null || KeyEquals(eProject, project));
                if (eProject is not null && !KeyEquals(eProject, project))
                    return false; // another project — cannot be this model
                return null; // a project guid alone can't identify a model
            });
        }

        if (file.CentralGuid is { } central)
        {
            kind ??= ActivityMatchKinds.Central;
            levels.Add(e => e.GetString(TelemetryFields.CentralGuid) is { } v ? KeyEquals(v, central) : null);
        }

        if (file.CreationGuid is { } creation)
        {
            kind ??= ActivityMatchKinds.Creation;
            levels.Add(e => e.GetString(TelemetryFields.CreationGuid) is { } v ? KeyEquals(v, creation) : null);
        }

        if (kind is null) return null;
        return (kind, e =>
        {
            foreach (var level in levels)
                if (level(e) is { } decided) return decided;
            return false;
        });
    }

    private static bool KeyEquals(string? a, string b) =>
        a is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Replay one session file in seq order, appending the current file's
    /// open intervals, load-duration points, and sync-duration points.
    /// </summary>
    private static void ReplaySession(
        List<TelemetryEvent> events,
        Func<TelemetryEvent, bool> matches,
        DateTimeOffset nowUtc,
        string? liveSessionGuid,
        List<(DateTimeOffset Start, DateTimeOffset End)> intervals,
        List<DurationPoint> openPoints,
        List<DurationPoint> syncPoints)
    {
        events.Sort((a, b) => a.Seq.CompareTo(b.Seq));

        // Open doc_openings awaiting their doc_opened, in seq order.
        var pendingOpenings = new List<(DateTimeOffset Ts, string? LocalPath)>();
        // Current file's open docs: per-session join key → (opened ts,
        // local path). The path is what doc_saved_as re-identification
        // joins through (previous_local_path).
        var openDocs = new Dictionary<string, (DateTimeOffset Ts, string? LocalPath)>();
        // closing_id → join key, for docs that matched at doc_closing.
        var closingIdToKey = new Dictionary<string, string>();
        // Current file's in-flight syncs: join key → sync_start ts.
        var pendingSyncs = new Dictionary<string, DateTimeOffset>();
        var sessionEnded = false;

        void CloseAll(DateTimeOffset endTs)
        {
            foreach (var entry in openDocs.Values)
                AddInterval(intervals, entry.Ts, endTs, nowUtc);
            openDocs.Clear();
        }

        // Every openDocs entry is the current file (only matched events
        // create one), and overlapping intervals union before bucketing —
        // so when an identity-capture gap keys a matched close under a
        // different level than its open, ending the latest-started entry
        // yields the same union as ending the "right" one.
        string? LatestOpenKey()
        {
            string? latest = null;
            var best = DateTimeOffset.MinValue;
            foreach (var kv in openDocs)
                if (kv.Value.Ts >= best) { best = kv.Value.Ts; latest = kv.Key; }
            return latest;
        }

        foreach (var e in events)
        {
            switch (e.EventType)
            {
                case TelemetryEventTypes.DocOpening:
                    pendingOpenings.Add((e.Ts, e.GetString(TelemetryFields.LocalPath)));
                    break;

                case TelemetryEventTypes.DocOpened:
                {
                    // Pair with the latest unconsumed doc_opening — by
                    // local_path when one matches, else by recency among
                    // openings that don't carry a conflicting path — and
                    // consume the pairing, so interleaved opens can't
                    // cross-attribute. Two nonblank differing paths are
                    // different documents: pairing across them would let
                    // a failed or stale opening invent a load duration
                    // for an unrelated file.
                    var localPath = e.GetString(TelemetryFields.LocalPath);
                    var idx = pendingOpenings.FindLastIndex(p =>
                        p.LocalPath is not null && localPath is not null &&
                        string.Equals(p.LocalPath, localPath, StringComparison.OrdinalIgnoreCase));
                    if (idx < 0)
                        idx = pendingOpenings.FindLastIndex(p => p.LocalPath is null || localPath is null);
                    (DateTimeOffset Ts, string? LocalPath)? opening = idx >= 0 ? pendingOpenings[idx] : null;
                    if (idx >= 0) pendingOpenings.RemoveAt(idx);

                    if (!matches(e)) break;
                    if (opening is { } o && e.Ts >= o.Ts)
                        openPoints.Add(new DurationPoint(e.Ts, (e.Ts - o.Ts).TotalSeconds));

                    var key = DocumentIdentity.ReadFrom(e).JoinKey ?? e.EventId;
                    if (!openDocs.ContainsKey(key)) openDocs[key] = (e.Ts, localPath);
                    break;
                }

                case TelemetryEventTypes.DocSavedAs:
                {
                    // Save As re-identifies an open model in place. The
                    // entry opened under the previous identity (joined by
                    // previous_local_path) ends here when the join key
                    // changed — post-save time belongs to the new
                    // identity, pre-save time to the old; neither ever
                    // inherits the other's. Same key = same bookkeeping
                    // doc: the interval continues, only the path moves.
                    var id = DocumentIdentity.ReadFrom(e);
                    var newKey = id.JoinKey;
                    var prevPath = e.GetString(TelemetryFields.PreviousLocalPath);
                    string? oldKey = null;
                    if (prevPath is not null)
                        foreach (var kv in openDocs)
                            if (kv.Value.LocalPath is not null &&
                                string.Equals(kv.Value.LocalPath, prevPath, StringComparison.OrdinalIgnoreCase))
                            { oldKey = kv.Key; break; }

                    if (oldKey is not null)
                    {
                        if (newKey is not null && KeyEquals(oldKey, newKey))
                        {
                            openDocs[oldKey] = (openDocs[oldKey].Ts, id.LocalPath);
                            break;
                        }
                        AddInterval(intervals, openDocs[oldKey].Ts, e.Ts, nowUtc);
                        openDocs.Remove(oldKey);
                    }

                    if (!matches(e)) break;
                    var key = newKey ?? e.EventId;
                    if (!openDocs.ContainsKey(key)) openDocs[key] = (e.Ts, id.LocalPath);
                    break;
                }

                case TelemetryEventTypes.DocClosing:
                {
                    if (!matches(e)) break;
                    var closingId = e.GetString(TelemetryFields.ClosingId);
                    var key = DocumentIdentity.ReadFrom(e).JoinKey;
                    if (closingId is not null && key is not null) closingIdToKey[closingId] = key;
                    break;
                }

                case TelemetryEventTypes.DocClosed:
                {
                    var status = e.GetString(TelemetryFields.Status);
                    if (status is not null &&
                        (status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) ||
                         status.Equals("Failed", StringComparison.OrdinalIgnoreCase)))
                        break; // close didn't happen — doc is still open

                    // Real closes carry no identity — join through the
                    // doc_closing's closing_id; synthetic closes carry
                    // keys directly.
                    var closingId = e.GetString(TelemetryFields.ClosingId);
                    var key = closingId is not null && closingIdToKey.TryGetValue(closingId, out var k)
                        ? k
                        : matches(e) ? DocumentIdentity.ReadFrom(e).JoinKey : null;
                    if (closingId is not null) closingIdToKey.Remove(closingId);
                    // key non-null = a matched close. An identity-capture
                    // gap can key it under a lower level than its open —
                    // fall back to the latest matched entry (see
                    // LatestOpenKey for why any matched entry is sound).
                    if (key is not null && !openDocs.ContainsKey(key))
                        key = LatestOpenKey();
                    if (key is not null && openDocs.TryGetValue(key, out var entry))
                    {
                        openDocs.Remove(key);
                        AddInterval(intervals, entry.Ts, e.Ts, nowUtc);
                    }
                    break;
                }

                case TelemetryEventTypes.SyncStart:
                {
                    if (!matches(e)) break;
                    var key = DocumentIdentity.ReadFrom(e).JoinKey;
                    // A new start supersedes an unfinished one — a sync
                    // that never ended has no duration to plot.
                    if (key is not null) pendingSyncs[key] = e.Ts;
                    break;
                }

                case TelemetryEventTypes.SyncEnd:
                {
                    if (!matches(e)) break;
                    var key = DocumentIdentity.ReadFrom(e).JoinKey;
                    // An identity-capture gap can key a matched sync_end
                    // under a lower level than its sync_start. Durations
                    // are per-pair (no union to hide a wrong pairing), so
                    // fall back only when a single pending sync makes the
                    // pairing unambiguous; otherwise drop — undercount,
                    // never invent.
                    if (key is not null && !pendingSyncs.ContainsKey(key) && pendingSyncs.Count == 1)
                        key = pendingSyncs.Keys.First();
                    if (key is not null && pendingSyncs.TryGetValue(key, out var startTs))
                    {
                        pendingSyncs.Remove(key);
                        if (e.Ts >= startTs)
                            syncPoints.Add(new DurationPoint(startTs, (e.Ts - startTs).TotalSeconds));
                    }
                    break;
                }

                case TelemetryEventTypes.CollectionDisabled:
                    // The gap is unobserved: cap open time at the toggle
                    // and drop in-flight pairings — undercount, never
                    // invent. Nothing reopens at collection_enabled; a
                    // doc still open across the gap resumes counting only
                    // if a later event re-establishes it.
                    CloseAll(e.Ts);
                    pendingOpenings.Clear();
                    pendingSyncs.Clear();
                    closingIdToKey.Clear();
                    break;

                case TelemetryEventTypes.SessionEnd:
                    CloseAll(e.Ts);
                    sessionEnded = true;
                    break;
            }
        }

        if (!sessionEnded && openDocs.Count > 0)
        {
            // Unclosed file: the live session is genuinely still open —
            // extend to now. Anything else is a crashed session recovery
            // hasn't reached yet — cap at its last observed event, the
            // same truncation recovery will apply.
            var isLive = liveSessionGuid is not null &&
                string.Equals(events[^1].SessionGuid, liveSessionGuid, StringComparison.OrdinalIgnoreCase);
            CloseAll(isLive ? nowUtc : events[^1].Ts);
        }
    }

    private static void AddInterval(
        List<(DateTimeOffset Start, DateTimeOffset End)> intervals,
        DateTimeOffset start, DateTimeOffset end, DateTimeOffset nowUtc)
    {
        if (end > nowUtc) end = nowUtc; // clock skew — never count the future
        if (end > start) intervals.Add((start, end));
    }

    private static List<(DateTimeOffset Start, DateTimeOffset End)> MergeIntervals(
        List<(DateTimeOffset Start, DateTimeOffset End)> intervals)
    {
        var merged = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var iv in intervals.OrderBy(i => i.Start))
        {
            if (merged.Count > 0 && iv.Start <= merged[^1].End)
            {
                if (iv.End > merged[^1].End)
                    merged[^1] = (merged[^1].Start, iv.End);
            }
            else
            {
                merged.Add(iv);
            }
        }
        return merged;
    }

    /// <summary>Split merged intervals at local-midnight boundaries and
    /// sum hours per calendar day in [firstDay, lastDay].</summary>
    private static Dictionary<DateOnly, double> BucketByDay(
        List<(DateTimeOffset Start, DateTimeOffset End)> merged,
        TimeZoneInfo zone, DateOnly firstDay, DateOnly lastDay)
    {
        var days = new Dictionary<DateOnly, double>();
        foreach (var (start, end) in merged)
        {
            var cur = start;
            while (cur < end)
            {
                var local = TimeZoneInfo.ConvertTime(cur, zone);
                var day = DateOnly.FromDateTime(local.Date);
                var dayEndUtc = NextLocalMidnightUtc(local, zone);
                var segEnd = end < dayEndUtc ? end : dayEndUtc;
                if (segEnd <= cur) break; // safety: never loop in place
                if (day >= firstDay && day <= lastDay)
                    days[day] = days.GetValueOrDefault(day) + (segEnd - cur).TotalHours;
                cur = segEnd;
            }
        }
        return days;
    }

    private static DateTimeOffset NextLocalMidnightUtc(DateTimeOffset local, TimeZoneInfo zone)
    {
        var nextMidnight = DateTime.SpecifyKind(local.Date.AddDays(1), DateTimeKind.Unspecified);
        try
        {
            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(nextMidnight, zone), TimeSpan.Zero);
        }
        catch (ArgumentException)
        {
            // A DST transition landing exactly on midnight makes it
            // invalid/ambiguous — approximate with the current offset.
            return new DateTimeOffset(nextMidnight, local.Offset).ToUniversalTime();
        }
    }

    private static IReadOnlyList<DurationPoint> FilterToRange(
        List<DurationPoint> points, TimeZoneInfo zone, DateOnly firstDay, DateOnly lastDay)
    {
        return points
            .Where(p =>
            {
                var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(p.Ts, zone).Date);
                return day >= firstDay && day <= lastDay;
            })
            .OrderBy(p => p.Ts)
            .ToList();
    }

    private static void SafeLog(Action<string>? log, string message)
    {
        try { log?.Invoke(message); }
        catch { /* logging must never take aggregation down */ }
    }
}
