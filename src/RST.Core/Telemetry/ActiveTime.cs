// rst-c's own (not vendored): the active-time rule (decision #46; spec #24,
// Active Time). Pure, no Revit references.
//
// For each model, within each interval it is open in a session:
//   1. every doc_changed_pulse at t marks [t, t + 1 min) active;
//   2. two consecutive pulses t1 < t2 of that interval with t2 − t1 ≤ 10 min
//      also mark [t1, t2) active;
//   3. spans are clipped to the open interval (doc_opened to its close,
//      session_end or collection_disabled, whichever comes first) — a gap
//      never bridges a close or a collection gap, because pulses are only
//      paired inside one interval;
//   4. active time is the length of the union of the spans, split at local
//      midnight into calendar days.
// The open intervals come from SessionReplay, so every join rule the Health
// window already trusts (SC-032, SC-038..SC-043) decides where a model is
// open; this file adds only the pulse arithmetic.

namespace RST.Core.Telemetry;

public static class ActiveTime
{
    /// <summary>Each pulse marks this much time after it as active.</summary>
    public static readonly TimeSpan PulseSpan = TimeSpan.FromMinutes(1);

    /// <summary>Gaps up to this long between two pulses of one open interval count as active.</summary>
    public static readonly TimeSpan MaxGap = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The model's active spans: sorted, non-overlapping, each inside one of
    /// <paramref name="openIntervals"/>. Intervals may overlap (two copies of one
    /// central open at once); the result is their union. Pulses outside every
    /// interval count for nothing.
    /// </summary>
    public static List<(DateTimeOffset Start, DateTimeOffset End)> Spans(
        IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> openIntervals,
        IEnumerable<DateTimeOffset> pulses)
    {
        var sorted = pulses.ToArray();
        Array.Sort(sorted);
        var spans = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var (start, end) in openIntervals)
        {
            if (end <= start) continue;
            var i = LowerBound(sorted, start);
            DateTimeOffset runStart = default, runEnd = default, prev = default;
            var inRun = false;
            for (; i < sorted.Length && sorted[i] < end; i++)
            {
                var p = sorted[i];
                if (!inRun)
                {
                    runStart = p;
                    runEnd = p + PulseSpan;
                    inRun = true;
                }
                else if (p - prev <= MaxGap)
                {
                    var pEnd = p + PulseSpan;
                    if (pEnd > runEnd) runEnd = pEnd;
                }
                else
                {
                    spans.Add((runStart, runEnd < end ? runEnd : end));
                    runStart = p;
                    runEnd = p + PulseSpan;
                }
                prev = p;
            }
            if (inRun) spans.Add((runStart, runEnd < end ? runEnd : end));
        }
        return SessionReplay.MergeIntervals(spans);
    }

    /// <summary>Total length of already-merged spans, in whole milliseconds.</summary>
    public static long TotalMilliseconds(IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> spans)
    {
        long total = 0;
        foreach (var (s, e) in spans)
            if (e > s) total += ToMs(e - s);
        return total;
    }

    /// <summary>
    /// Split spans at local midnight in <paramref name="zone"/> and sum each calendar
    /// day, in whole milliseconds. The spans should already be unioned.
    /// </summary>
    public static SortedDictionary<DateOnly, long> MillisecondsByDay(
        IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> spans, TimeZoneInfo zone)
    {
        var days = new SortedDictionary<DateOnly, long>();
        foreach (var (start, end) in spans)
        {
            var cur = start;
            while (cur < end)
            {
                var local = TimeZoneInfo.ConvertTime(cur, zone);
                var day = DateOnly.FromDateTime(local.Date);
                var dayEndUtc = SessionReplay.NextLocalMidnightUtc(local, zone);
                var segEnd = end < dayEndUtc ? end : dayEndUtc;
                if (segEnd <= cur) break; // safety: never loop in place
                days[day] = days.GetValueOrDefault(day) + ToMs(segEnd - cur);
                cur = segEnd;
            }
        }
        return days;
    }

    /// <summary>The calendar day of <paramref name="ts"/> in <paramref name="zone"/>.</summary>
    public static DateOnly LocalDay(DateTimeOffset ts, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(ts, zone).Date);

    internal static long ToMs(TimeSpan span) => span.Ticks / TimeSpan.TicksPerMillisecond;

    private static int LowerBound(DateTimeOffset[] sorted, DateTimeOffset value)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (sorted[mid] < value) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}
