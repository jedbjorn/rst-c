// ActivityAggregator.cs — parses outbox files and computes the Activity
// tab's series for the current file (Telemetry v1 spec, Build Plan
// step 3). Pure, unit-testable, no Revit references.
//
// Current-file matching uses the same priority order the server would —
// cloud GUID pair → central GUID → central path → creation GUID
// (WorksharingCentralGUID is Revit Server-only, so file-share centrals
// match on the normalized user-visible central path; SC-032) — decided
// per event at the highest level the event carries: a present key that differs
// rejects, an absent key falls through, so an identity-capture gap on
// one event can't orphan its close or sync endpoint. The cloud level
// confirms only a complete pair — a matching model guid without its
// project guid falls through like an absent key. Presentation-time
// filtering only; raw events stay raw.
//
// Open-hours replay mirrors RecoveryScanner.TrackOpenDocs: doc_closed
// carries only closing_id + status, so identity joins through the
// preceding doc_closing; recovery-written synthetic closes carry join
// keys directly. Duration errors are bounded by the session and never
// invented from a wrong pairing:
//   - an unclosed non-live session is capped at its last observed event
//     (recovery's ≤2-minute truncation, applied at read time);
//   - a collection_disabled marker caps open intervals at the toggle —
//     time in the gap is unknown and never invented;
//   - negative spans (clock changes mid-session) are dropped, not
//     clamped into being;
//   - a close whose keys more than one live doc could own (an
//     identity-capture gap keying it at a level lineage siblings
//     share — SC-038 — including a key a gapped-open sibling sits
//     under exactly, SC-039) retires nothing: the doc stays open to
//     session_end/recovery's cap, a bounded overshoot accepted over
//     truncating the wrong doc.
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
        DateTimeOffset? liveOpenSince = null;

        foreach (var path in ListOutboxFiles(outboxDir, log))
        {
            try
            {
                var events = OutboxFiles.ReadEvents(path);
                if (events.Count == 0) continue;
                ReplaySession(events, matcher.Value.Matches, nowUtc, liveSessionGuid,
                    intervals, openPoints, syncPoints, ref liveOpenSince);
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
            LiveOpenSinceUtc = liveOpenSince,
            PerDayOpenHours = perDay,
            OpenEvents = FilterToRange(openPoints, zone, firstDay, lastDay),
            SyncEvents = FilterToRange(syncPoints, zone, firstDay, lastDay),
        };
    }

    /// <summary>
    /// The Activity footer's status line: file count, total bytes on
    /// disk, and the oldest parseable event's ts across the outbox.
    /// Never throws; unreadable files still count toward size when their
    /// length is readable, and are skipped for the oldest-event scan.
    /// </summary>
    public static OutboxStatus ReadStatus(string outboxDir, Action<string>? log = null)
    {
        var files = ListOutboxFiles(outboxDir, log);
        if (files.Length == 0) return OutboxStatus.Empty;

        var count = 0;
        long totalBytes = 0;
        DateTimeOffset? oldest = null;
        foreach (var path in files)
        {
            count++;
            try { totalBytes += new FileInfo(path).Length; }
            catch (Exception ex) { SafeLog(log, "telemetry status size failed for " + Path.GetFileName(path) + ": " + ex.Message); }
            try
            {
                if (ReadFirstEventTs(path) is { } ts && (oldest is null || ts < oldest))
                    oldest = ts;
            }
            catch (Exception ex)
            {
                SafeLog(log, "telemetry status read failed for " + Path.GetFileName(path) + ": " + ex.Message);
            }
        }
        return new OutboxStatus(count, totalBytes, oldest);
    }

    /// <summary>Ts of the first parseable line — the whole file is
    /// scanned only when every earlier line is damaged.</summary>
    private static DateTimeOffset? ReadFirstEventTs(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (TelemetryJson.TryParseLine(line) is { } e) return e.Ts;
        }
        return null;
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
    /// (cloud pair → central guid → central path → creation) it actually carries: a present
    /// key that differs rejects, an absent key falls through to the next
    /// level — an identity-capture gap on one event must not orphan its
    /// close or sync endpoint. The cloud level confirms only when both
    /// halves of the pair are present and match; an incomplete pair
    /// falls through. Null when the file carries no usable key —
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
                if (eModel is not null && !KeyEquals(eModel, model))
                    return false; // another model
                if (eProject is not null && !KeyEquals(eProject, project))
                    return false; // another project — cannot be this model
                if (eModel is not null && eProject is not null)
                    return true;
                // Cloud identity is the project+model pair: an incomplete
                // pair can't confirm — lower keys decide.
                return null;
            });
        }

        if (file.CentralGuid is { } central)
        {
            kind ??= ActivityMatchKinds.Central;
            levels.Add(e => e.GetString(TelemetryFields.CentralGuid) is { } v ? KeyEquals(v, central) : null);
        }

        if (file.CentralPath is { Length: > 0 } centralPath)
        {
            // File-share centrals carry no WorksharingCentralGUID (Revit
            // Server-only) — the user-visible central path is their key,
            // compared case-insensitive ordinal. Keys-only events carry
            // it too (SC-032, decision #3), so close/sync endpoints are
            // decided here, not at creation_guid — a same-lineage sibling
            // central can never claim them.
            kind ??= ActivityMatchKinds.CentralPath;
            levels.Add(e => e.GetString(TelemetryFields.CentralPath) is { Length: > 0 } v
                ? KeyEquals(v, centralPath) : null);
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
        List<DurationPoint> syncPoints,
        ref DateTimeOffset? liveOpenSince)
    {
        events.Sort((a, b) => a.Seq.CompareTo(b.Seq));

        // Open doc_openings awaiting their doc_opened, in seq order.
        var pendingOpenings = new List<(DateTimeOffset Ts, string? LocalPath)>();
        // Current file's open docs: per-session join key → opened ts —
        // the interval bookkeeping. Identity joins (previous_local_path,
        // creation-GUID lineage) run on allOpenDocs below.
        var openDocs = new Dictionary<string, DateTimeOffset>();
        // Every open doc in the session, matched or not: join key → the
        // identity it is open under. The Save-As joins and the close
        // resolution run against this view (SC-037): uniqueness judged
        // only among the current file's docs would miss an open keyed
        // sibling (same creation GUID, its own central), falsely call
        // the match unique, and retire the current file at the
        // sibling's save or close. Mirrors RecoveryScanner.TrackOpenDocs'
        // full visibility.
        var allOpenDocs = new Dictionary<string, DocumentIdentity>();
        // closing_id → the doc_closing's identity, for every doc:
        // doc_closed carries no keys of its own, so this is what the
        // close resolution judges against.
        var closingIds = new Dictionary<string, DocumentIdentity>();
        // Current file's in-flight syncs: join key → sync_start ts.
        var pendingSyncs = new Dictionary<string, DateTimeOffset>();
        var sessionEnded = false;

        void CloseAll(DateTimeOffset endTs)
        {
            foreach (var openedTs in openDocs.Values)
                AddInterval(intervals, openedTs, endTs, nowUtc);
            openDocs.Clear();
            allOpenDocs.Clear();
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

                    var openedId = DocumentIdentity.ReadFrom(e);
                    var key = openedId.JoinKey ?? e.EventId;
                    if (!allOpenDocs.ContainsKey(key)) allOpenDocs[key] = openedId;

                    if (!matches(e)) break;
                    if (opening is { } o && e.Ts >= o.Ts)
                        openPoints.Add(new DurationPoint(e.Ts, (e.Ts - o.Ts).TotalSeconds));
                    if (!openDocs.ContainsKey(key)) openDocs[key] = e.Ts;
                    break;
                }

                case TelemetryEventTypes.DocSavedAs:
                {
                    // Save As re-identifies an open model in place. The
                    // entry opened under the previous identity (joined by
                    // previous_local_path, falling back to creation-GUID
                    // lineage when the path join is absent) ends here when
                    // the join key changed — post-save time belongs to the
                    // new identity, pre-save time to the old; neither ever
                    // inherits the other's. Same key = same bookkeeping
                    // doc: the interval continues, only the path moves.
                    // Both joins run against allOpenDocs (SC-037): the
                    // doc that re-identified may be a sibling the current
                    // file's view can't see, and only full visibility
                    // makes the uniqueness verdict sound.
                    var id = DocumentIdentity.ReadFrom(e);
                    var newKey = id.JoinKey;
                    var prevPath = e.GetString(TelemetryFields.PreviousLocalPath);
                    string? oldKey = null;
                    if (prevPath is not null)
                        foreach (var kv in allOpenDocs)
                            if (kv.Value.LocalPath is not null &&
                                string.Equals(kv.Value.LocalPath, prevPath, StringComparison.OrdinalIgnoreCase))
                            { oldKey = kv.Key; break; }
                    oldKey ??= LineageFallbackKey(allOpenDocs, id.CreationGuid, prevPath);

                    if (oldKey is not null)
                    {
                        if (newKey is not null && KeyEquals(oldKey, newKey))
                        {
                            // Same key = same bookkeeping doc — only the
                            // identity fields move; a matched interval
                            // keeps its opened ts untouched. A doc that
                            // opened unmatched can match from here (an
                            // identity gap healed in place) — its matched
                            // interval starts at the save.
                            allOpenDocs[oldKey] = id;
                            if (matches(e) && !openDocs.ContainsKey(oldKey))
                                openDocs[oldKey] = e.Ts;
                            break;
                        }
                        allOpenDocs.Remove(oldKey);
                        if (openDocs.TryGetValue(oldKey, out var openedTs))
                        {
                            AddInterval(intervals, openedTs, e.Ts, nowUtc);
                            openDocs.Remove(oldKey);
                        }
                    }

                    var key = newKey ?? e.EventId;
                    if (!allOpenDocs.ContainsKey(key)) allOpenDocs[key] = id;
                    if (!matches(e)) break;
                    if (!openDocs.ContainsKey(key)) openDocs[key] = e.Ts;
                    break;
                }

                case TelemetryEventTypes.DocClosing:
                {
                    var closingId = e.GetString(TelemetryFields.ClosingId);
                    if (closingId is not null) closingIds[closingId] = DocumentIdentity.ReadFrom(e);
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
                    var closeId = closingId is not null && closingIds.TryGetValue(closingId, out var ci)
                        ? ci
                        : DocumentIdentity.ReadFrom(e);
                    if (closingId is not null) closingIds.Remove(closingId);

                    // The close retires a doc only when exactly one live
                    // candidate is consistent with its identity, judged
                    // over every open doc, matched or not (SC-038). An
                    // exact key hit is no exemption (SC-039): a sibling
                    // opened through a capture gap sits under the shared
                    // lower-level key itself, so the hit proves the key
                    // is live, not which lineage doc the close meant —
                    // a live higher-key sibling makes it ambiguous all
                    // the same. Ambiguity declines: nothing retires, the
                    // doc stays open, and session_end/recovery caps it —
                    // undercounting a close is recoverable; retiring the
                    // wrong doc is not. A non-ambiguous exact hit still
                    // wins outright — it also covers docs keyed below
                    // the identity levels (local path / title), which
                    // the pairwise judgement can't see. Both views
                    // resolve through the same key, so a declined close
                    // can't strand one of them.
                    var (docKey, ambiguousClose) = ResolveOpenDoc(allOpenDocs, closeId);
                    var key = ambiguousClose
                        ? null
                        : closeId.JoinKey is { } exact && allOpenDocs.ContainsKey(exact)
                            ? exact
                            : docKey;
                    if (key is null) break;
                    allOpenDocs.Remove(key);
                    if (openDocs.TryGetValue(key, out var openedTs))
                    {
                        openDocs.Remove(key);
                        AddInterval(intervals, openedTs, e.Ts, nowUtc);
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
                    var id = DocumentIdentity.ReadFrom(e);
                    var key = id.JoinKey;
                    // An identity-capture gap can key a matched sync_end
                    // under a lower level than its sync_start. Durations
                    // are per-pair (no union to hide a wrong pairing), so
                    // an end whose identity is ambiguous among live docs
                    // drops outright — even when its key hits a pending
                    // sync exactly (SC-039), a gapped-open sibling puts a
                    // live doc under that very key, so the hit says
                    // nothing about whose end this is. A keyed miss falls
                    // back only when a single pending sync makes the
                    // pairing unambiguous AND the end's identity resolves
                    // to a unique live doc (SC-038); otherwise drop —
                    // undercount, never invent.
                    var (endDocKey, ambiguousEnd) = ResolveOpenDoc(allOpenDocs, id);
                    if (ambiguousEnd) break;
                    if (key is not null && !pendingSyncs.ContainsKey(key) && pendingSyncs.Count == 1
                        && endDocKey is not null)
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
                    closingIds.Clear();
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
            if (isLive)
                foreach (var openedTs in openDocs.Values)
                    if (liveOpenSince is null || openedTs < liveOpenSince) liveOpenSince = openedTs;
            CloseAll(isLive ? nowUtc : events[^1].Ts);
        }
    }

    /// <summary>
    /// Save-As retirement fallback when the previous_local_path ↔
    /// LocalPath join is absent (an allowed LocalPath read gap on the
    /// open, or a missing previous_local_path): Save As keeps the
    /// creation GUID, so an open entry sharing it is the doc that just
    /// re-identified. Conservative — a stored path that contradicts
    /// previous_local_path rejects the candidate, and anything but
    /// exactly one candidate returns null; never guess which doc moved.
    /// Judged over every open doc, matched or not (SC-037) — a keyed
    /// sibling of the lineage is a candidate even though the current
    /// file's view can't see it. Mirrors RecoveryScanner's fallback.
    /// </summary>
    private static string? LineageFallbackKey(
        Dictionary<string, DocumentIdentity> allOpenDocs,
        string? creationGuid, string? prevPath)
    {
        if (creationGuid is null) return null;
        string? match = null;
        foreach (var kv in allOpenDocs)
        {
            if (!KeyEquals(kv.Value.CreationGuid, creationGuid)) continue;
            if (kv.Value.LocalPath is not null && prevPath is not null &&
                !string.Equals(kv.Value.LocalPath, prevPath, StringComparison.OrdinalIgnoreCase))
                continue; // a present path that differs is another doc in the lineage
            if (match is not null) return null; // ambiguous
            match = kv.Key;
        }
        return match;
    }

    /// <summary>
    /// Resolve which live doc an event's identity belongs to. Every open
    /// doc, matched or not, is judged pairwise at the highest identity
    /// level the two sides share; a doc confirming at a higher priority
    /// level outranks lower-level candidates — the event names its doc
    /// at the best level it carries. Exactly one candidate at the
    /// winning level resolves to its key; zero candidates resolve to
    /// null quietly; several tied (live lineage siblings sharing the
    /// gapped key) return Ambiguous — the general decline rule (SC-038):
    /// never guess which doc an ambiguous key means. Ambiguity binds the
    /// caller even when the event's own join key names an open doc
    /// exactly (SC-039): a sibling opened through a capture gap sits
    /// under the shared lower-level key itself, so the exact hit proves
    /// the key is live, not which lineage doc the event meant.
    /// </summary>
    private static (string? Key, bool Ambiguous) ResolveOpenDoc(
        Dictionary<string, DocumentIdentity> allOpenDocs, DocumentIdentity id)
    {
        string? match = null;
        var matchLevel = int.MaxValue;
        var ambiguous = false;
        foreach (var kv in allOpenDocs)
        {
            if (ConfirmLevel(id, kv.Value) is not { } level) continue;
            if (level < matchLevel) { match = kv.Key; matchLevel = level; ambiguous = false; }
            else if (level == matchLevel) ambiguous = true;
        }
        return ambiguous ? (null, true) : (match, false);
    }

    /// <summary>
    /// The priority level (0 = cloud pair … 3 = creation GUID) at which
    /// two identities confirm as the same document, or null when they
    /// reject or share no decidable level — BuildMatcher's per-level
    /// semantics applied pairwise: a present key that differs rejects,
    /// an absent key falls through, and the cloud level confirms only a
    /// complete pair while a differing present half rejects outright.
    /// </summary>
    private static int? ConfirmLevel(DocumentIdentity a, DocumentIdentity b)
    {
        if (a.CloudModelGuid is not null && b.CloudModelGuid is not null &&
            !KeyEquals(a.CloudModelGuid, b.CloudModelGuid)) return null;
        if (a.CloudProjectGuid is not null && b.CloudProjectGuid is not null &&
            !KeyEquals(a.CloudProjectGuid, b.CloudProjectGuid)) return null;
        if (a.CloudModelGuid is not null && b.CloudModelGuid is not null &&
            a.CloudProjectGuid is not null && b.CloudProjectGuid is not null)
            return 0; // complete pair on both sides, halves equal
        if (a.CentralGuid is not null && b.CentralGuid is not null)
            return KeyEquals(a.CentralGuid, b.CentralGuid) ? 1 : null;
        if (a.CentralPath is { Length: > 0 } aPath && b.CentralPath is { Length: > 0 } bPath)
            return KeyEquals(aPath, bPath) ? 2 : null;
        if (a.CreationGuid is not null && b.CreationGuid is not null)
            return KeyEquals(a.CreationGuid, b.CreationGuid) ? 3 : null;
        return null;
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
