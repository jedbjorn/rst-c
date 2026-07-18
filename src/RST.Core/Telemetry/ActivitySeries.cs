// ActivitySeries.cs — the Activity tab's precomputed series, as returned
// by ActivityAggregator. The bridge (RST.UI) serializes these to small
// JSON payloads; the HTML never parses JSONL.
//
// All timestamps stay UTC on the wire — the dashboard converts for
// display. Per-day buckets are calendar days in the zone the caller
// passed to the aggregator (the user's local zone in production).

namespace RST.Core.Telemetry;

/// <summary>Hours the current file was open on one calendar day. The
/// per-day series carries one point for every day in range, zeros
/// included.</summary>
public sealed record DayOpenHoursPoint(DateOnly Date, double Hours);

/// <summary>One timed event — a load (doc_opening → doc_opened) or a
/// sync (sync_start → sync_end) — plotted at <paramref name="Ts"/> with
/// its duration in seconds.</summary>
public sealed record DurationPoint(DateTimeOffset Ts, double Seconds);

/// <summary>
/// Which identity key matched events to the current file — the same
/// priority order the server would use (cloud GUID pair → central GUID →
/// creation GUID). Null on ActivitySeries.MatchedKeyKind = no usable key
/// (or no current file).
/// </summary>
public static class ActivityMatchKinds
{
    public const string Cloud = "cloud";
    public const string Central = "central";
    public const string Creation = "creation";
}

public sealed class ActivitySeries
{
    public static readonly ActivitySeries Empty = new();

    /// <summary>See <see cref="ActivityMatchKinds"/>; null when no key
    /// was usable and the series are necessarily empty.</summary>
    public string? MatchedKeyKind { get; init; }

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
