// ActivitySeries.cs — small shared telemetry read types: a timed event
// point, the identity ladder's level names and the outbox's status line.
// From spec #24 the Activity tab reads the per-model rollups
// (ModelActivityReader); the old outbox-replay aggregator lives on only as
// the test harness of SessionReplay's join rules (RST.Tests).
//
// All timestamps stay UTC; the UI converts for display.

namespace RST.Core.Telemetry;

/// <summary>One timed event — a load (doc_opening → doc_opened) or a
/// sync (sync_start → sync_end) — plotted at <paramref name="Ts"/> with
/// its duration in seconds.</summary>
public sealed record DurationPoint(DateTimeOffset Ts, double Seconds);

/// <summary>
/// The current file's best usable identity key — the same priority order
/// the server would use (cloud GUID pair → central GUID → creation
/// GUID). Individual events may match at a lower level when a higher key
/// is absent from the event. Null on ActivitySeries.MatchedKeyKind = no
/// usable key (or no current file).
/// </summary>
public static class ActivityMatchKinds
{
    public const string Cloud = "cloud";
    public const string Central = "central";
    public const string CentralPath = "central_path";
    public const string Creation = "creation";
}

/// <summary>Footer status line for the Activity tab: what the outbox
/// holds right now — file count and bytes on disk (JSONL). The footer's
/// "since" is the current model's oldest rollup day (spec #24), not the
/// outbox's.</summary>
public sealed record OutboxStatus(int FileCount, long TotalSizeBytes)
{
    public static readonly OutboxStatus Empty = new(0, 0);
}
