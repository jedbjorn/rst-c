// ActivityAggregator.cs — parses outbox files and computes the Activity
// tab's series for the current file (Telemetry v1 spec, Build Plan
// step 3). Pure, unit-testable, no Revit references.
//
// The matching ladder and the replay's join rules (SC-032, SC-038 to
// SC-043) live in SessionReplay, shared with the rollup fold (spec #24).
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

        var days = SessionReplay.BucketByDay(SessionReplay.MergeIntervals(sink.Intervals), zone, firstDay, lastDay);
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