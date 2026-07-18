// ActivityAggregatorTests.cs — Activity series over synthetic outboxes:
// per-day open-hours (multi-session union, midnight spans, crashed
// sessions, toggle gaps, Save-As siblings and re-identification),
// open/sync duration series, current-file match priority with
// identity-capture-gap fallback, and range windowing (Telemetry v1
// spec, Build Plan step 3).

using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using RST.Core.Telemetry;
using Xunit;
using static RST.Tests.Telemetry.OutboxTestData;

namespace RST.Tests.Telemetry;

public sealed class ActivityAggregatorTests : IDisposable
{
    /// <summary>The "current instant" every test aggregates at.</summary>
    private static readonly DateTimeOffset Now = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root;

    public ActivityAggregatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "RST-ActivityTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // ---- builders -------------------------------------------------------

    private static DateTimeOffset D(int day, int hour, int minute = 0) =>
        new(2026, 7, day, hour, minute, 0, TimeSpan.Zero);

    private static DocumentIdentity ByCreation(string creationGuid, string? localPath = null) => new()
    {
        CreationGuid = creationGuid,
        LocalPath = localPath ?? "C:\\models\\" + creationGuid + ".rvt",
        Title = creationGuid + ".rvt",
    };

    private static TelemetryEvent Opening(string s, long seq, DateTimeOffset ts, string localPath)
    {
        var e = Event(s, seq, TelemetryEventTypes.DocOpening, ts);
        e.SetField(TelemetryFields.LocalPath, localPath);
        return e;
    }

    private static TelemetryEvent Opened(string s, long seq, DateTimeOffset ts, DocumentIdentity id)
    {
        var e = Event(s, seq, TelemetryEventTypes.DocOpened, ts);
        id.WriteTo(e);
        return e;
    }

    private static TelemetryEvent Closing(string s, long seq, DateTimeOffset ts, DocumentIdentity id, string closingId)
    {
        var e = Event(s, seq, TelemetryEventTypes.DocClosing, ts);
        id.WriteKeysTo(e);
        e.SetField(TelemetryFields.ClosingId, closingId);
        return e;
    }

    /// <summary>A real doc_closed: closing_id + status only — no identity
    /// keys, exactly as DocumentClosed leaves them.</summary>
    private static TelemetryEvent Closed(string s, long seq, DateTimeOffset ts, string closingId, string? status = null)
    {
        var e = Event(s, seq, TelemetryEventTypes.DocClosed, ts);
        e.SetField(TelemetryFields.ClosingId, closingId);
        if (status is not null) e.SetField(TelemetryFields.Status, status);
        return e;
    }

    private static TelemetryEvent SyntheticClosed(string s, long seq, DateTimeOffset ts, DocumentIdentity id)
    {
        var e = Event(s, seq, TelemetryEventTypes.DocClosed, ts);
        e.Source = TelemetrySources.Recovery;
        e.Synthetic = true;
        id.WriteKeysTo(e);
        return e;
    }

    private static TelemetryEvent SyntheticEnd(string s, long seq, DateTimeOffset ts)
    {
        var e = Event(s, seq, TelemetryEventTypes.SessionEnd, ts);
        e.Source = TelemetrySources.Recovery;
        e.Synthetic = true;
        return e;
    }

    private static TelemetryEvent SavedAs(
        string s, long seq, DateTimeOffset ts, DocumentIdentity id, string previousLocalPath)
    {
        var e = Event(s, seq, TelemetryEventTypes.DocSavedAs, ts);
        id.WriteTo(e);
        e.SetField(TelemetryFields.PreviousLocalPath, previousLocalPath);
        return e;
    }

    private static TelemetryEvent Sync(string s, long seq, DateTimeOffset ts, DocumentIdentity id, string eventType)
    {
        var e = Event(s, seq, eventType, ts);
        id.WriteKeysTo(e);
        return e;
    }

    private ActivitySeries Aggregate(
        DocumentIdentity? currentFile, int rangeDays = 7,
        TimeZoneInfo? zone = null, string? liveSessionGuid = null) =>
        ActivityAggregator.Aggregate(_root, currentFile, rangeDays, Now, zone, liveSessionGuid);

    private static double HoursOn(ActivitySeries series, int day) =>
        series.PerDayOpenHours.Single(p => p.Date == new DateOnly(2026, 7, day)).Hours;

    // ---- per-day open hours ---------------------------------------------

    [Fact]
    public void Open_and_close_produce_day_hours_with_zero_days_padded()
    {
        var doc = ByCreation("doc-a");
        var s = Guid.NewGuid().ToString();
        WriteSessionFile(_root, s,
            Event(s, 1, TelemetryEventTypes.SessionStart, D(18, 8)),
            Opened(s, 2, D(18, 9), doc),
            Closing(s, 3, D(18, 11, 30), doc, "c1"),
            Closed(s, 4, D(18, 11, 30), "c1"),
            Event(s, 5, TelemetryEventTypes.SessionEnd, D(18, 11, 45)));

        var series = Aggregate(doc);

        series.MatchedKeyKind.Should().Be(ActivityMatchKinds.Creation);
        series.PerDayOpenHours.Should().HaveCount(7, "one point per day in range, zeros included");
        series.PerDayOpenHours.Select(p => p.Date).Should().BeInAscendingOrder();
        series.PerDayOpenHours[0].Date.Should().Be(new DateOnly(2026, 7, 12));
        HoursOn(series, 18).Should().BeApproximately(2.5, 1e-9);
        series.PerDayOpenHours.Where(p => p.Date != new DateOnly(2026, 7, 18))
            .Should().OnlyContain(p => p.Hours == 0);
    }

    [Fact]
    public void Real_doc_closed_joins_through_closing_id_not_keys()
    {
        // The doc_closed carries no identity; only the closing_id join can
        // end the interval. A wrong join would run the interval to
        // session_end (3h) instead of the close (1h).
        var doc = ByCreation("doc-a");
        var s = Guid.NewGuid().ToString();
        WriteSessionFile(_root, s,
            Opened(s, 1, D(18, 8), doc),
            Closing(s, 2, D(18, 9), doc, "c9"),
            Closed(s, 3, D(18, 9), "c9"),
            Event(s, 4, TelemetryEventTypes.SessionEnd, D(18, 11)));

        HoursOn(Aggregate(doc), 18).Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void Cancelled_close_keeps_the_doc_open_until_session_end()
    {
        var doc = ByCreation("doc-a");
        var s = Guid.NewGuid().ToString();
        WriteSessionFile(_root, s,
            Opened(s, 1, D(18, 8), doc),
            Closing(s, 2, D(18, 9), doc, "c1"),
            Closed(s, 3, D(18, 9), "c1", status: "Cancelled"),
            Event(s, 4, TelemetryEventTypes.SessionEnd, D(18, 10)));

        HoursOn(Aggregate(doc), 18).Should().BeApproximately(2.0, 1e-9,
            "a cancelled close never happened — the doc stays open to session_end");
    }

    [Fact]
    public void Interval_spanning_local_midnight_splits_across_days()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("T+2", TimeSpan.FromHours(2), "T+2", "T+2");
        var doc = ByCreation("doc-a");
        var s = Guid.NewGuid().ToString();
        // 21:00Z→01:00Z = 23:00→03:00 local: 1h on the 17th, 3h on the 18th.
        WriteSessionFile(_root, s,
            Opened(s, 1, D(17, 21), doc),
            Closing(s, 2, D(18, 1), doc, "c1"),
            Closed(s, 3, D(18, 1), "c1"),
            Event(s, 4, TelemetryEventTypes.SessionEnd, D(18, 1, 5)));

        var series = Aggregate(doc, zone: zone);

        HoursOn(series, 17).Should().BeApproximately(1.0, 1e-9);
        HoursOn(series, 18).Should().BeApproximately(3.0, 1e-9);
    }

    [Fact]
    public void Overlapping_sessions_union_to_wall_clock_hours()
    {
        var doc = ByCreation("doc-a");
        var s1 = Guid.NewGuid().ToString();
        var s2 = Guid.NewGuid().ToString();
        WriteSessionFile(_root, s1,
            Opened(s1, 1, D(18, 9), doc),
            Closing(s1, 2, D(18, 11), doc, "c1"),
            Closed(s1, 3, D(18, 11), "c1"),
            Event(s1, 4, TelemetryEventTypes.SessionEnd, D(18, 11)));
        WriteSessionFile(_root, s2,
            Opened(s2, 1, D(18, 10), doc),
            Closing(s2, 2, D(18, 12), doc, "c1"),
            Closed(s2, 3, D(18, 12), "c1"),
            Event(s2, 4, TelemetryEventTypes.SessionEnd, D(18, 12)));

        HoursOn(Aggregate(doc), 18).Should().BeApproximately(3.0, 1e-9,
            "09–11 and 10–12 in two concurrent instances is 3h of wall clock, never 4");
    }

    [Fact]
    public void Crashed_session_with_synthetic_closes_counts_to_recovery_ts()
    {
        var doc = ByCreation("doc-a");
        var s = Guid.NewGuid().ToString();
        var lastHeartbeat = D(18, 10);
        WriteSessionFile(_root, s,
            Event(s, 1, TelemetryEventTypes.SessionStart, D(18, 8)),
            Opened(s, 2, D(18, 9), doc),
            Event(s, 3, TelemetryEventTypes.Heartbeat, lastHeartbeat),
            SyntheticClosed(s, 4, lastHeartbeat, doc),
            SyntheticEnd(s, 5, lastHeartbeat));

        HoursOn(Aggregate(doc), 18).Should().BeApproximately(1.0, 1e-9,
            "recovery truncated the session to its last heartbeat");
    }

    [Fact]
    public void Unclosed_non_live_session_caps_at_last_observed_event()
    {
        // A crashed session the recovery scanner hasn't visited yet.
        var doc = ByCreation("doc-a");
        var s = Guid.NewGuid().ToString();
        WriteSessionFile(_root, s,
            Opened(s, 1, D(18, 9), doc),
            Event(s, 2, TelemetryEventTypes.Heartbeat, D(18, 10)));

        HoursOn(Aggregate(doc, liveSessionGuid: Guid.NewGuid().ToString()), 18)
            .Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void Live_session_extends_to_now()
    {
        var doc = ByCreation("doc-a");
        var s = Guid.NewGuid().ToString();
        WriteSessionFile(_root, s,
            Opened(s, 1, D(18, 9), doc),
            Event(s, 2, TelemetryEventTypes.Heartbeat, D(18, 10)));

        HoursOn(Aggregate(doc, liveSessionGuid: s), 18).Should().BeApproximately(3.0, 1e-9,
            "the live session's open doc is still open at Now (12:00)");
    }

    [Fact]
    public void Toggle_gap_caps_open_time_at_disable()
    {
        var doc = ByCreation("doc-a");
        var s = Guid.NewGuid().ToString();
        WriteSessionFile(_root, s,
            Opened(s, 1, D(18, 9), doc),
            Event(s, 2, TelemetryEventTypes.CollectionDisabled, D(18, 10)),
            Event(s, 3, TelemetryEventTypes.CollectionEnabled, D(18, 11)),
            Closing(s, 4, D(18, 11, 30), doc, "c1"),
            Closed(s, 5, D(18, 11, 30), "c1"),
            Event(s, 6, TelemetryEventTypes.SessionEnd, D(18, 11, 30)));

        HoursOn(Aggregate(doc), 18).Should().BeApproximately(1.0, 1e-9,
            "the gap is unobserved — open time caps at the toggle and never resumes on its own");
    }

    [Fact]
    public void Save_as_siblings_merge_by_creation_guid()
    {
        var original = ByCreation("doc-a", "C:\\models\\a.rvt");
        var sibling = ByCreation("doc-a", "C:\\models\\a-copy.rvt");
        var s1 = Guid.NewGuid().ToString();
        var s2 = Guid.NewGuid().ToString();
        WriteSessionFile(_root, s1,
            Opened(s1, 1, D(18, 9), original),
            Closing(s1, 2, D(18, 10), original, "c1"),
            Closed(s1, 3, D(18, 10), "c1"),
            Event(s1, 4, TelemetryEventTypes.SessionEnd, D(18, 10)));
        WriteSessionFile(_root, s2,
            Opened(s2, 1, D(17, 9), sibling),
            Closing(s2, 2, D(17, 11), sibling, "c1"),
            Closed(s2, 3, D(17, 11), "c1"),
            Event(s2, 4, TelemetryEventTypes.SessionEnd, D(17, 11)));

        var series = Aggregate(original);

        HoursOn(series, 18).Should().BeApproximately(1.0, 1e-9);
        HoursOn(series, 17).Should().BeApproximately(2.0, 1e-9,
            "non-workshared siblings share creation_guid — merged history is accepted v1 semantics");
    }

    // ---- current-file matching ------------------------------------------

    [Fact]
    public void Cloud_pair_outranks_creation_guid()
    {
        var current = new DocumentIdentity
        {
            CreationGuid = "doc-a",
            CloudProjectGuid = "proj-1",
            CloudModelGuid = "model-1",
        };
        // Same creation lineage, different cloud model — a detached copy
        // published as its own cloud model must not pollute the series.
        var other = new DocumentIdentity
        {
            CreationGuid = "doc-a",
            CloudProjectGuid = "proj-1",
            CloudModelGuid = "model-2",
        };
        var s = Guid.NewGuid().ToString();
        WriteSessionFile(_root, s,
            Opened(s, 1, D(18, 9), other),
            Closing(s, 2, D(18, 11), other, "c1"),
            Closed(s, 3, D(18, 11), "c1"),
            Event(s, 4, TelemetryEventTypes.SessionEnd, D(18, 11)));

        var series = Aggregate(current);

        series.MatchedKeyKind.Should().Be(ActivityMatchKinds.Cloud);
        HoursOn(series, 18).Should().Be(0);
    }

    [Fact]
    public void Matching_model_guid_without_project_guid_cannot_confirm_cloud_match()
    {
        // Cloud identity is the project+model pair: a matching model guid
        // with no project guid is an incomplete pair and must fall
        // through — where the conflicting creation guid rejects. Deciding
        // at the cloud level would count the foreign doc's whole session.
        var current = new DocumentIdentity
        {
            CreationGuid = "doc-a",
            CloudProjectGuid = "proj-1",
            CloudModelGuid = "model-1",
        };
        var other = new DocumentIdentity
        {
            CreationGuid = "doc-b",
            CloudModelGuid = "model-1",
        };
        var s = Guid.NewGuid().ToString();
        WriteSessionFile(_root, s,
            Opened(s, 1, D(18, 9), other),
            Closing(s, 2, D(18, 10), other, "c1"),
            Closed(s, 3, D(18, 10), "c1"),
            Event(s, 4, TelemetryEventTypes.SessionEnd, D(18, 10)));

        var series = Aggregate(current);

        series.MatchedKeyKind.Should().Be(ActivityMatchKinds.Cloud);
        HoursOn(series, 18).Should().Be(0);
    }

    [Fact]
    public void Central_guid_matches_when_no_cloud_keys()
    {
        var current = new DocumentIdentity { CreationGuid = "doc-a", CentralGuid = "central-1" };
        var doc = new DocumentIdentity { CreationGuid = "doc-b", CentralGuid = "central-1" };
        var s = Guid.NewGuid().ToString();
        WriteSessionFile(_root, s,
            Opened(s, 1, D(18, 9), doc),
            Closing(s, 2, D(18, 10), doc, "c1"),
            Closed(s, 3, D(18, 10), "c1"),
            Event(s, 4, TelemetryEventTypes.SessionEnd, D(18, 10)));

        var series = Aggregate(current);

        series.MatchedKeyKind.Should().Be(ActivityMatchKinds.Central);
        HoursOn(series, 18).Should().BeApproximately(1.0, 1e-9,
            "central GUID is the join key — local copies of one central share it");
    }

    [Fact]
    public void Central_path_matches_file_share_central_and_outranks_creation_guid()
    {
        // File-share central: WorksharingCentralGUID is Revit Server-only,
        // so the user-visible central path is the only central key
        // (SC-032). It joins local copies case-insensitively and outranks
        // a conflicting creation guid — lineage never overrides a present
        // central identity.
        var current = new DocumentIdentity
        {
            CreationGuid = "doc-a",
            CentralPath = "\\\\server\\projects\\Tower_Central.rvt",
        };
        var localCopy = new DocumentIdentity
        {
            CreationGuid = "doc-b",
            CentralPath = "\\\\SERVER\\Projects\\tower_central.RVT",
        };
        var s = Guid.NewGuid().ToString();
        WriteSessionFile(_root, s,
            Opened(s, 1, D(18, 9), localCopy),
            Event(s, 2, TelemetryEventTypes.SessionEnd, D(18, 10)));

        var series = Aggregate(current);

        series.MatchedKeyKind.Should().Be(ActivityMatchKinds.CentralPath);
        HoursOn(series, 18).Should().BeApproximately(1.0, 1e-9,
            "the normalized central path joins file-share local copies despite the differing creation guid");
    }

    [Fact]
    public void Close_with_identity_capture_gap_ends_the_interval_via_a_lower_key()
    {
        // The doc opened with full cloud identity, but the doc_closing
        // lost its cloud keys to a capture gap and carries only the
        // creation guid. The close must still end the interval — locking
        // matches to the file's highest key would run it to session_end.
        var cloudDoc = new DocumentIdentity
        {
            CreationGuid = "doc-a",
            CloudProjectGuid = "proj-1",
            CloudModelGuid = "model-1",
        };
        var gapId = new DocumentIdentity { CreationGuid = "doc-a" };
        var s = Guid.NewGuid().ToString();
        WriteSessionFile(_root, s,
            Opened(s, 1, D(18, 9), cloudDoc),
            Closing(s, 2, D(18, 10), gapId, "c1"),
            Closed(s, 3, D(18, 10), "c1"),
            Event(s, 4, TelemetryEventTypes.SessionEnd, D(18, 11, 30)));

        var series = Aggregate(cloudDoc);

        series.MatchedKeyKind.Should().Be(ActivityMatchKinds.Cloud);
        HoursOn(series, 18).Should().BeApproximately(1.0, 1e-9,
            "the creation-guid fallback attributes the close; 2.5 means it ran to session_end");
    }

    [Fact]
    public void Conflicting_central_guid_rejects_despite_matching_creation_guid()
    {
        // Fallback is for absent keys only: a present-but-different
        // central guid is another central model, whatever the lineage.
        var current = new DocumentIdentity { CreationGuid = "doc-a", CentralGuid = "central-b" };
        var other = new DocumentIdentity { CreationGuid = "doc-a", CentralGuid = "central-a" };
        var s = Guid.NewGuid().ToString();
        WriteSessionFile(_root, s,
            Opened(s, 1, D(18, 9), other),
            Closing(s, 2, D(18, 10), other, "c1"),
            Closed(s, 3, D(18, 10), "c1"),
            Event(s, 4, TelemetryEventTypes.SessionEnd, D(18, 10)));

        HoursOn(Aggregate(current), 18).Should().Be(0);
    }

    [Fact]
    public void Sync_end_with_identity_capture_gap_still_pairs()
    {
        var cloudDoc = new DocumentIdentity
        {
            CreationGuid = "doc-a",
            CloudProjectGuid = "proj-1",
            CloudModelGuid = "model-1",
        };
        var gapId = new DocumentIdentity { CreationGuid = "doc-a" };
        var s = Guid.NewGuid().ToString();
        WriteSessionFile(_root, s,
            Opened(s, 1, D(18, 9), cloudDoc),
            Sync(s, 2, D(18, 9, 30), cloudDoc, TelemetryEventTypes.SyncStart),
            Sync(s, 3, D(18, 9, 33), gapId, TelemetryEventTypes.SyncEnd),
            Event(s, 4, TelemetryEventTypes.SessionEnd, D(18, 10)));

        var point = Aggregate(cloudDoc).SyncEvents.Should().ContainSingle(
            "the sole pending sync pairs unambiguously despite the key gap").Subject;
        point.Ts.Should().Be(D(18, 9, 30));
        point.Seconds.Should().BeApproximately(180, 1e-9);
    }

    [Fact]
    public void No_usable_key_and_no_current_file_yield_empty_series()
    {
        var s = Guid.NewGuid().ToString();
        WriteSessionFile(_root, s,
            Opened(s, 1, D(18, 9), ByCreation("doc-a")),
            Event(s, 2, TelemetryEventTypes.SessionEnd, D(18, 10)));

        var pathOnly = new DocumentIdentity { LocalPath = "C:\\models\\a.rvt", Title = "a.rvt" };
        foreach (var series in new[] { Aggregate(pathOnly), Aggregate(null) })
        {
            series.MatchedKeyKind.Should().BeNull();
            series.PerDayOpenHours.Should().BeEmpty();
            series.OpenEvents.Should().BeEmpty();
            series.SyncEvents.Should().BeEmpty();
        }
    }

    // ---- open / sync duration series ------------------------------------

    [Fact]
    public void Load_durations_pair_openings_by_local_path_across_interleaved_opens()
    {
        var docA = ByCreation("doc-a", "C:\\models\\a.rvt");
        var docB = ByCreation("doc-b", "C:\\models\\b.rvt");
        var s = Guid.NewGuid().ToString();
        WriteSessionFile(_root, s,
            Opening(s, 1, D(18, 9, 0), "C:\\models\\a.rvt"),
            Opening(s, 2, D(18, 9, 1), "C:\\models\\b.rvt"),
            Opened(s, 3, D(18, 9, 2), docB),
            Opened(s, 4, D(18, 9, 5), docA),
            Event(s, 5, TelemetryEventTypes.SessionEnd, D(18, 10)));

        var series = Aggregate(docA);

        var point = series.OpenEvents.Should().ContainSingle("only doc-a is the current file").Subject;
        point.Ts.Should().Be(D(18, 9, 5), "plotted at the open's completion");
        point.Seconds.Should().BeApproximately(300, 1e-9,
            "doc-a's opened pairs with doc-a's opening by path, not with the nearer doc-b opening");
    }

    [Fact]
    public void Sync_durations_pair_start_to_end_and_drop_unfinished_syncs()
    {
        var doc = ByCreation("doc-a");
        var s = Guid.NewGuid().ToString();
        WriteSessionFile(_root, s,
            Opened(s, 1, D(18, 9), doc),
            Sync(s, 2, D(18, 9, 30), doc, TelemetryEventTypes.SyncStart),
            Sync(s, 3, D(18, 9, 33), doc, TelemetryEventTypes.SyncEnd),
            Sync(s, 4, D(18, 10), doc, TelemetryEventTypes.SyncStart),
            Event(s, 5, TelemetryEventTypes.SessionEnd, D(18, 10, 30)));

        var series = Aggregate(doc);

        var point = series.SyncEvents.Should().ContainSingle("the crashed-mid-sync start has no duration").Subject;
        point.Ts.Should().Be(D(18, 9, 30), "plotted at the sync's start");
        point.Seconds.Should().BeApproximately(180, 1e-9);
    }

    [Fact]
    public void Stale_opening_with_conflicting_path_never_invents_a_load_duration()
    {
        // A failed open of a.rvt left its doc_opening unconsumed; the
        // current file then opened without its own doc_opening (lost to
        // a gap). Recency fallback across two nonblank differing paths
        // would invent a 5-minute load from the unrelated opening.
        var docB = ByCreation("doc-b", "C:\\models\\b.rvt");
        var s = Guid.NewGuid().ToString();
        WriteSessionFile(_root, s,
            Opening(s, 1, D(18, 9), "C:\\models\\a.rvt"),
            Opened(s, 2, D(18, 9, 5), docB),
            Closing(s, 3, D(18, 10, 5), docB, "c1"),
            Closed(s, 4, D(18, 10, 5), "c1"),
            Event(s, 5, TelemetryEventTypes.SessionEnd, D(18, 10, 5)));

        var series = Aggregate(docB);

        series.OpenEvents.Should().BeEmpty("no doc_opening belongs to this open");
        HoursOn(series, 18).Should().BeApproximately(1.0, 1e-9,
            "the open interval itself still counts");
    }

    // ---- save-as re-identification --------------------------------------

    [Fact]
    public void Save_as_that_changes_identity_starts_the_new_key_at_save_ts()
    {
        // Save As turned a local of central-a into a new central-b. For
        // the new identity, history starts at the save — pre-save time
        // belongs to central-a and is never inherited.
        var before = new DocumentIdentity
        {
            CreationGuid = "doc-a",
            CentralGuid = "central-a",
            LocalPath = "C:\\models\\a.rvt",
            Title = "a.rvt",
        };
        var after = new DocumentIdentity
        {
            CreationGuid = "doc-a",
            CentralGuid = "central-b",
            LocalPath = "C:\\models\\b.rvt",
            Title = "b.rvt",
        };
        var s = Guid.NewGuid().ToString();
        WriteSessionFile(_root, s,
            Opened(s, 1, D(18, 9), before),
            SavedAs(s, 2, D(18, 10), after, "C:\\models\\a.rvt"),
            Closing(s, 3, D(18, 11), after, "c1"),
            Closed(s, 4, D(18, 11), "c1"),
            Event(s, 5, TelemetryEventTypes.SessionEnd, D(18, 11, 30)));

        var series = Aggregate(after);

        series.MatchedKeyKind.Should().Be(ActivityMatchKinds.Central);
        HoursOn(series, 18).Should().BeApproximately(1.0, 1e-9,
            "the new identity was open 10:00–11:00; 0 means doc_saved_as was ignored, " +
            "2 means pre-save time leaked to the new key");
    }

    [Fact]
    public void Save_as_that_changes_identity_ends_the_old_key_at_save_ts()
    {
        // The mirror view: once the open model re-identified to
        // central-b, central-a is no longer open — its interval must end
        // at the save, not run to session_end.
        var before = new DocumentIdentity
        {
            CreationGuid = "doc-a",
            CentralGuid = "central-a",
            LocalPath = "C:\\models\\a.rvt",
            Title = "a.rvt",
        };
        var after = new DocumentIdentity
        {
            CreationGuid = "doc-a",
            CentralGuid = "central-b",
            LocalPath = "C:\\models\\b.rvt",
            Title = "b.rvt",
        };
        var s = Guid.NewGuid().ToString();
        WriteSessionFile(_root, s,
            Opened(s, 1, D(18, 9), before),
            SavedAs(s, 2, D(18, 10), after, "C:\\models\\a.rvt"),
            Event(s, 3, TelemetryEventTypes.SessionEnd, D(18, 11, 30)));

        HoursOn(Aggregate(before), 18).Should().BeApproximately(1.0, 1e-9,
            "central-a was open 09:00–10:00; 2.5 means it ran to session_end past the save-as");
    }

    [Fact]
    public void Save_as_keeping_the_join_key_continues_the_interval()
    {
        // A plain save-as sibling keeps the creation guid — same
        // bookkeeping doc, the interval continues across the save.
        var original = ByCreation("doc-a", "C:\\models\\a.rvt");
        var renamed = ByCreation("doc-a", "C:\\models\\a-copy.rvt");
        var s = Guid.NewGuid().ToString();
        WriteSessionFile(_root, s,
            Opened(s, 1, D(18, 9), original),
            SavedAs(s, 2, D(18, 10), renamed, "C:\\models\\a.rvt"),
            Closing(s, 3, D(18, 11), renamed, "c1"),
            Closed(s, 4, D(18, 11), "c1"),
            Event(s, 5, TelemetryEventTypes.SessionEnd, D(18, 11)));

        HoursOn(Aggregate(original), 18).Should().BeApproximately(2.0, 1e-9);
    }

    // ---- range windowing & tolerance ------------------------------------

    [Fact]
    public void Range_window_excludes_out_of_range_points_and_clips_old_intervals()
    {
        var doc = ByCreation("doc-a");
        var s = Guid.NewGuid().ToString();
        // 7-day range at Now covers the 12th–18th. This session spans the
        // 10th 09:00 → 12th 12:00; only the 12th's 12 hours are in range.
        WriteSessionFile(_root, s,
            Opening(s, 1, D(10, 8, 59), "C:\\models\\doc-a.rvt"),
            Opened(s, 2, D(10, 9), doc),
            Sync(s, 3, D(10, 10), doc, TelemetryEventTypes.SyncStart),
            Sync(s, 4, D(10, 10, 5), doc, TelemetryEventTypes.SyncEnd),
            Closing(s, 5, D(12, 12), doc, "c1"),
            Closed(s, 6, D(12, 12), "c1"),
            Event(s, 7, TelemetryEventTypes.SessionEnd, D(12, 12)));

        var series = Aggregate(doc);

        series.PerDayOpenHours.Should().HaveCount(7);
        series.PerDayOpenHours[0].Date.Should().Be(new DateOnly(2026, 7, 12));
        HoursOn(series, 12).Should().BeApproximately(12.0, 1e-9,
            "the open interval clips to the range window's first day");
        series.OpenEvents.Should().BeEmpty("the open completed before the range window");
        series.SyncEvents.Should().BeEmpty("the sync happened before the range window");
    }

    [Fact]
    public void Partial_trailing_line_and_unreadable_noise_are_tolerated()
    {
        var doc = ByCreation("doc-a");
        var s = Guid.NewGuid().ToString();
        var path = WriteSessionFile(_root, s,
            Opened(s, 1, D(18, 9), doc),
            Closing(s, 2, D(18, 10), doc, "c1"),
            Closed(s, 3, D(18, 10), "c1"),
            Event(s, 4, TelemetryEventTypes.SessionEnd, D(18, 10)));
        File.AppendAllText(path, "{\"event_id\":\"truncated-by-a-cra");

        HoursOn(Aggregate(doc), 18).Should().BeApproximately(1.0, 1e-9,
            "the partial trailing line a crash leaves is skipped, never thrown");
    }

    [Fact]
    public void Missing_outbox_dir_is_an_empty_outbox()
    {
        var series = ActivityAggregator.Aggregate(
            Path.Combine(_root, "does-not-exist"), ByCreation("doc-a"), 7, Now);

        series.MatchedKeyKind.Should().Be(ActivityMatchKinds.Creation);
        series.PerDayOpenHours.Should().HaveCount(7);
        series.PerDayOpenHours.Should().OnlyContain(p => p.Hours == 0);
    }

    // ---- live open-since (Activity tab current-session sub-line) --------

    [Fact]
    public void Live_session_reports_open_since_and_extends_to_now()
    {
        var doc = ByCreation("doc-a");
        var s = Guid.NewGuid().ToString();
        WriteSessionFile(_root, s,
            Event(s, 1, TelemetryEventTypes.SessionStart, D(18, 8)),
            Opened(s, 2, D(18, 9), doc));

        var series = Aggregate(doc, liveSessionGuid: s);

        series.LiveOpenSinceUtc.Should().Be(D(18, 9));
        HoursOn(series, 18).Should().BeApproximately(3.0, 1e-9,
            "the live session's open interval extends to now (12:00)");
    }

    [Fact]
    public void Unclosed_file_without_live_guid_reports_no_open_since()
    {
        var doc = ByCreation("doc-a");
        var s = Guid.NewGuid().ToString();
        WriteSessionFile(_root, s,
            Opened(s, 1, D(18, 9), doc),
            Event(s, 2, TelemetryEventTypes.Heartbeat, D(18, 10)));

        Aggregate(doc).LiveOpenSinceUtc.Should().BeNull(
            "an unclosed file with no live session named is a crashed session, not a live open");
    }

    [Fact]
    public void Doc_closed_in_live_session_reports_no_open_since()
    {
        var doc = ByCreation("doc-a");
        var s = Guid.NewGuid().ToString();
        WriteSessionFile(_root, s,
            Opened(s, 1, D(18, 9), doc),
            Closing(s, 2, D(18, 10), doc, "c1"),
            Closed(s, 3, D(18, 10), "c1"));

        var series = Aggregate(doc, liveSessionGuid: s);

        series.LiveOpenSinceUtc.Should().BeNull("the file is no longer open in the live session");
        HoursOn(series, 18).Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void Toggle_off_in_live_session_clears_open_since()
    {
        var doc = ByCreation("doc-a");
        var s = Guid.NewGuid().ToString();
        WriteSessionFile(_root, s,
            Opened(s, 1, D(18, 9), doc),
            Event(s, 2, TelemetryEventTypes.CollectionDisabled, D(18, 10)));

        Aggregate(doc, liveSessionGuid: s).LiveOpenSinceUtc.Should().BeNull(
            "open time caps at the toggle; the gap is unobserved");
    }

    // ---- outbox status (Activity tab footer) ----------------------------

    [Fact]
    public void ReadStatus_empty_or_missing_dir_is_empty()
    {
        ActivityAggregator.ReadStatus(Path.Combine(_root, "missing")).Should().Be(OutboxStatus.Empty);
        ActivityAggregator.ReadStatus(_root).Should().Be(OutboxStatus.Empty);
    }

    [Fact]
    public void ReadStatus_counts_files_bytes_and_oldest_event()
    {
        var s1 = Guid.NewGuid().ToString();
        var s2 = Guid.NewGuid().ToString();
        var p1 = WriteSessionFile(_root, s1,
            Event(s1, 1, TelemetryEventTypes.SessionStart, D(15, 8)),
            Event(s1, 2, TelemetryEventTypes.SessionEnd, D(15, 9)));
        var p2 = WriteSessionFile(_root, s2,
            Event(s2, 1, TelemetryEventTypes.SessionStart, D(12, 7)),
            Event(s2, 2, TelemetryEventTypes.SessionEnd, D(12, 8)));

        var status = ActivityAggregator.ReadStatus(_root);

        status.FileCount.Should().Be(2);
        status.TotalSizeBytes.Should().Be(new FileInfo(p1).Length + new FileInfo(p2).Length);
        status.OldestEventTs.Should().Be(D(12, 7));
    }

    [Fact]
    public void ReadStatus_skips_corrupt_leading_lines_for_oldest()
    {
        var s = Guid.NewGuid().ToString();
        var path = Path.Combine(_root, OutboxFiles.SessionFileName(InstallId, s));
        File.WriteAllText(path,
            "{not json\n" +
            TelemetryJson.SerializeLine(Event(s, 2, TelemetryEventTypes.SessionStart, D(14, 6))) + "\n");

        ActivityAggregator.ReadStatus(_root).OldestEventTs.Should().Be(D(14, 6));
    }
}
