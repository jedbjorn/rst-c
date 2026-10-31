// ActivityAggregator.cs (test harness) — the pre-spec-#24 Activity
// aggregation, kept in RST.Tests only: the Health window now reads the
// per-model rollups (ModelActivityReader), but these replays of whole
// outboxes are the regression net for SessionReplay's matching ladder and
// join rules (SC-032, SC-038 to SC-043), which the rollup fold shares.
// Moved out of RST.Core unchanged (F19 #24 WU4).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RST.Core.Telemetry;

namespace RST.Tests.Telemetry;

/// <summary>Hours the current file was open on one calendar day. The
/// per-day series carries one point for every day in range, zeros
/// included.</summary>
public sealed record DayOpenHoursPoint(DateOnly Date, double Hours);

public sealed class ActivitySeries
{
    public static readonly ActivitySeries Empty = new();

    /// <summary>See <see cref="ActivityMatchKinds"/>; null when no key
    /// was usable and the series are necessarily empty.</summary>
    public string? MatchedKeyKind { get; init; }

    /// <summary>When the live session (the caller's
    /// <c>liveSessionGuid</c>) currently has the file open: the ts its
    /// earliest still-open interval started — the "open since" the
    /// current-session block shows. Null when no live session was named,
    /// the file isn't open in it, or collection was toggled off.</summary>
    public DateTimeOffset? LiveOpenSinceUtc { get; init; }

    /// <summary>One point per calendar day in range, oldest first,
    /// zero-hour days included.</summary>
    public IReadOnlyList<DayOpenHoursPoint> PerDayOpenHours { get; init; } =
        Array.Empty<DayOpenHoursPoint>();

    /// <summary>Load durations, one per open event in range, oldest first.</summary>
    public IReadOnlyList<DurationPoint> OpenEvents { get; init; } =
        Array.Empty<DurationPoint>();

    /// <summary>Sync durations, one per sync in range, oldest first.</summary>
    public IReadOnlyList<DurationPoint> SyncEvents { get; init; } =
        Array.Empty<DurationPoint>();
}

internal static class ActivityAggregator
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
        var matcher = currentFile is null ? null : SessionReplay.BuildMatcher(currentFile);
        if (matcher is null) return ActivitySeries.Empty;

        var zone = dayZone ?? TimeZoneInfo.Utc;
        if (rangeDays < 1) rangeDays = 1;
        var lastDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, zone).Date);
        var firstDay = lastDay.AddDays(-(rangeDays - 1));

        var sink = new ReplaySink();

        foreach (var path in ListOutboxFiles(outboxDir, log))
        {
            try
            {
                var events = OutboxFiles.ReadEvents(path);
                if (events.Count == 0) continue;
                SessionReplay.Replay(events, matcher.Value.Matches, nowUtc, liveSessionGuid, sink);
            }
            catch (Exception ex)
            {
                SafeLog(log, "telemetry aggregate skipped " + Path.GetFileName(path) + ": " + ex.Message);
            }
        }

        var days = BucketByDay(SessionReplay.MergeIntervals(sink.Intervals), zone, firstDay, lastDay);
        var perDay = new List<DayOpenHoursPoint>(rangeDays);
        for (var d = firstDay; d <= lastDay; d = d.AddDays(1))
            perDay.Add(new DayOpenHoursPoint(d, days.GetValueOrDefault(d)));

        return new ActivitySeries
        {
            MatchedKeyKind = matcher.Value.Kind,
            LiveOpenSinceUtc = sink.LiveOpenSince,
            PerDayOpenHours = perDay,
            OpenEvents = FilterToRange(sink.OpenPoints, zone, firstDay, lastDay),
            SyncEvents = FilterToRange(sink.SyncPoints, zone, firstDay, lastDay),
        };
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
                var dayEndUtc = SessionReplay.NextLocalMidnightUtc(local, zone);
                var segEnd = end < dayEndUtc ? end : dayEndUtc;
                if (segEnd <= cur) break; // safety: never loop in place
                if (day >= firstDay && day <= lastDay)
                    days[day] = days.GetValueOrDefault(day) + (segEnd - cur).TotalHours;
                cur = segEnd;
            }
        }
        return days;
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
