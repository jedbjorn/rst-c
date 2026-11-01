// ModelStatsGateTests.cs — when model_stats is captured (spec #24
// Capture Additions, AC 9): open/save/sync requests coalesce into one
// capture per document at the next Idling, at most once per model per
// 5 minutes; a request inside the window queues no Idling work. A
// document that closes before Idling still gets its capture, in either
// Revit event order (review WU2-01), and the window runs on a monotonic
// clock, so wall-clock changes neither stretch nor shorten it (review
// WU2-02). rc.2: an open measures the file size only, a save or sync the
// warnings too (decision #71, VM F1); requests name the exact document,
// so two open copies of one model (same tracking key) are never mixed up
// (VM F2). Also pins the wire names the fold (WU1) and a fork's shipper read.

using System;
using System.Linq;
using FluentAssertions;
using RST.Core.Telemetry;
using Xunit;

namespace RST.Tests.Telemetry;

public sealed class ModelStatsGateTests
{
    // The gate's only time input: monotonic elapsed time.
    private TimeSpan _mono = TimeSpan.FromHours(3);

    private ModelStatsGate NewGate() => new(monotonicNow: () => _mono);

    // One document per model unless a test says otherwise: doc ids are
    // the collector's per-open-document ids, keys the tracking keys.
    private const string A = "doc-a", B = "doc-b", C = "doc-c";
    private static string KeyOf(string doc) => "model-of-" + doc;

    private static ModelStatsDecision Save(ModelStatsGate gate, string doc) =>
        gate.Request(doc, KeyOf(doc), ModelStatsTrigger.Save);

    private static string[] Docs(ModelStatsGate gate) => gate.TakePending().Select(r => r.DocumentId).ToArray();

    /// <summary>What the collector's capture does for one key.</summary>
    private static bool Emit(ModelStatsGate gate, string key) =>
        gate.CanEmit(key) && gate.TryAcquire(key);

    /// <summary>An open captured at Idling, leaving doc-a's model inside its window.</summary>
    private static ModelStatsGate Opened(ModelStatsGate gate, string doc = A)
    {
        gate.Request(doc, KeyOf(doc), ModelStatsTrigger.Open).Should().Be(ModelStatsDecision.Defer);
        gate.TakePending();
        Emit(gate, KeyOf(doc)).Should().BeTrue();
        return gate;
    }

    [Fact]
    public void Default_window_is_five_minutes()
    {
        ModelStatsGate.DefaultInterval.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void A_fresh_model_is_deferred_and_pending_until_taken()
    {
        var gate = NewGate();
        gate.HasPending.Should().BeFalse();

        Save(gate, A).Should().Be(ModelStatsDecision.Defer);
        gate.HasPending.Should().BeTrue();

        gate.TakePending().Should().Equal(new ModelStatsRequest(A, KeyOf(A), MeasureWarnings: true));
        gate.HasPending.Should().BeFalse("taking empties the set");
        gate.TakePending().Should().BeEmpty();
    }

    [Fact]
    public void Open_then_save_before_idling_is_one_capture()
    {
        var gate = NewGate();
        gate.Request(A, KeyOf(A), ModelStatsTrigger.Open).Should().Be(ModelStatsDecision.Defer, "the open");
        Save(gate, A).Should().Be(ModelStatsDecision.Skip, "the save coalesces into the pending capture");

        gate.TakePending().Should().ContainSingle().Which.DocumentId.Should().Be(A);
    }

    [Fact]
    public void Each_model_is_pending_independently()
    {
        var gate = NewGate();
        Save(gate, A);
        Save(gate, B);

        Docs(gate).Should().Equal(A, B);
    }

    [Fact]
    public void A_request_inside_the_window_is_dropped_on_arrival()
    {
        var gate = Opened(NewGate());

        _mono += TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59);
        Save(gate, A).Should().Be(ModelStatsDecision.Skip, "a save 4:59 after the last record cannot emit");
        gate.HasPending.Should().BeFalse("no Idling work is queued for it");
    }

    [Fact]
    public void A_request_after_the_window_captures_again()
    {
        var gate = Opened(NewGate());

        _mono += TimeSpan.FromMinutes(5);
        Save(gate, A).Should().Be(ModelStatsDecision.Defer);
        gate.TakePending();
        Emit(gate, KeyOf(A)).Should().BeTrue();
    }

    [Fact]
    public void The_window_is_per_model()
    {
        var gate = Opened(NewGate());

        _mono += TimeSpan.FromMinutes(1);
        Save(gate, B).Should().Be(ModelStatsDecision.Defer, "another model's window does not apply");
    }

    [Fact]
    public void A_capture_that_never_emitted_does_not_open_a_window()
    {
        // Collection was switched off before Idling: nothing was acquired,
        // so the next save captures.
        var gate = NewGate();
        Save(gate, A);
        gate.TakePending();

        _mono += TimeSpan.FromSeconds(30);
        Save(gate, A).Should().Be(ModelStatsDecision.Defer);
    }

    [Fact]
    public void Two_emissions_inside_one_window_admit_only_the_first()
    {
        // The lifecycle's admit callback is the authoritative commit.
        var gate = NewGate();
        gate.TryAcquire("model-a").Should().BeTrue();
        gate.CanEmit("model-a").Should().BeFalse();
        gate.TryAcquire("model-a").Should().BeFalse();
    }

    // ---- what is measured (decision #71, VM F1) ---------------------------

    [Fact]
    public void An_open_measures_the_file_size_only()
    {
        ModelStatsTrigger.Open.MeasuresWarnings().Should().BeFalse("GetWarnings froze Revit 2026 for 3.2 s after an open");
        ModelStatsTrigger.Save.MeasuresWarnings().Should().BeTrue();
        ModelStatsTrigger.Sync.MeasuresWarnings().Should().BeTrue();

        var gate = NewGate();
        gate.Request(A, KeyOf(A), ModelStatsTrigger.Open);
        gate.TakePending().Single().MeasureWarnings.Should().BeFalse();
    }

    [Fact]
    public void A_save_or_sync_measures_the_warnings()
    {
        var gate = NewGate();
        gate.Request(A, KeyOf(A), ModelStatsTrigger.Save);
        gate.Request(B, KeyOf(B), ModelStatsTrigger.Sync);
        gate.TakePending().Should().OnlyContain(r => r.MeasureWarnings);
    }

    [Fact]
    public void An_open_merged_with_a_save_measures_the_warnings_in_either_order()
    {
        var gate = NewGate();
        gate.Request(A, KeyOf(A), ModelStatsTrigger.Open);
        gate.Request(A, KeyOf(A), ModelStatsTrigger.Save);
        gate.Request(B, KeyOf(B), ModelStatsTrigger.Sync);
        gate.Request(B, KeyOf(B), ModelStatsTrigger.Open);

        gate.TakePending().Should().OnlyContain(r => r.MeasureWarnings, "the save asked for them; the open never unasks");
    }

    [Fact]
    public void A_pending_open_taken_by_a_close_still_skips_the_warnings()
    {
        var gate = NewGate();
        gate.Request(A, KeyOf(A), ModelStatsTrigger.Open);

        gate.Closing(A).Should().Be(new ModelStatsRequest(A, KeyOf(A), MeasureWarnings: false));
    }

    // ---- two open copies of one model (VM F2) -----------------------------

    // F19-Local-2026.rvt is open; its UNC copy, with the same creation
    // GUID and so the same tracking key, opens next.
    private const string Local = "101|C:\\Models\\F19-Local-2026.rvt";
    private const string Unc = "202|\\\\localhost\\rstshare\\F19-UNC-2026.rvt";
    private const string SharedKey = "guid:6f0e";

    [Fact]
    public void A_request_for_the_second_of_two_copies_captures_the_second()
    {
        var gate = NewGate();
        gate.Request(Unc, SharedKey, ModelStatsTrigger.Open).Should().Be(ModelStatsDecision.Defer);

        // Revit lists the local copy first, as on the VM.
        var open = new[] { (Id: Local, Name: "local"), (Id: Unc, Name: "unc") };
        var resolved = ModelStatsGate.Resolve(gate.TakePending(), open, d => d.Id);

        resolved.Should().ContainSingle();
        resolved[0].Document.Name.Should().Be("unc", "the document that asked is the one sampled");
        resolved[0].Request.ModelKey.Should().Be(SharedKey, "the model key stays for the window and the record");
    }

    [Fact]
    public void Two_copies_pending_at_once_are_two_requests_and_the_window_admits_one()
    {
        var gate = NewGate();
        gate.Request(Local, SharedKey, ModelStatsTrigger.Save).Should().Be(ModelStatsDecision.Defer);
        gate.Request(Unc, SharedKey, ModelStatsTrigger.Open).Should().Be(ModelStatsDecision.Defer,
            "a request names its document; it does not merge into the other copy's");

        var open = new[] { (Id: Unc, Name: "unc"), (Id: Local, Name: "local") };
        var resolved = ModelStatsGate.Resolve(gate.TakePending(), open, d => d.Id);
        resolved.Select(r => r.Document.Name).Should().Equal(new[] { "local", "unc" }, "request order, each its own document");
        resolved[0].Request.MeasureWarnings.Should().BeTrue();
        resolved[1].Request.MeasureWarnings.Should().BeFalse();

        Emit(gate, resolved[0].Request.ModelKey).Should().BeTrue();
        gate.CanEmit(resolved[1].Request.ModelKey).Should().BeFalse("still at most one record per model per 5 minutes");
    }

    [Fact]
    public void A_request_whose_document_has_closed_resolves_to_nothing_even_if_a_copy_is_open()
    {
        var gate = NewGate();
        gate.Request(Unc, SharedKey, ModelStatsTrigger.Open);

        var open = new[] { (Id: Local, Name: "local") };
        ModelStatsGate.Resolve(gate.TakePending(), open, d => d.Id).Should().BeEmpty(
            "never the other copy of the same model");
    }

    [Fact]
    public void A_document_whose_id_cannot_be_read_matches_nothing()
    {
        var gate = NewGate();
        gate.Request(Unc, SharedKey, ModelStatsTrigger.Open);

        var open = new[] { (Id: (string?)null, Name: "linked"), (Id: (string?)Unc, Name: "unc") };
        ModelStatsGate.Resolve(gate.TakePending(), open, d => d.Id)
            .Select(r => r.Document.Name).Should().Equal("unc");
    }

    [Fact]
    public void Closing_one_copy_leaves_the_other_copys_request_pending()
    {
        var gate = NewGate();
        gate.Request(Unc, SharedKey, ModelStatsTrigger.Open);

        gate.Closing(Local).Should().BeNull("the closing copy had nothing pending");
        gate.HasPending.Should().BeTrue();
        gate.TakePending().Should().Equal(new ModelStatsRequest(Unc, SharedKey, MeasureWarnings: false));
    }

    [Fact]
    public void A_copy_that_is_closing_does_not_make_the_other_copy_capture_at_once()
    {
        var gate = NewGate();
        gate.Closing(Local);

        gate.Request(Unc, SharedKey, ModelStatsTrigger.Save).Should().Be(ModelStatsDecision.Defer,
            "the UNC copy is not closing; it defers to Idling as usual");
        gate.Request(Local, SharedKey, ModelStatsTrigger.Save).Should().Be(ModelStatsDecision.CaptureNow,
            "the closing copy's own Close → Save captures at once");
        gate.TakePending().Select(r => r.DocumentId).Should().Equal(Unc);
    }

    [Fact]
    public void Close_then_save_finds_the_closing_copy_with_the_other_copy_pending()
    {
        // Close → Save on the local copy while the UNC copy waits for Idling.
        var gate = NewGate();
        gate.Request(Unc, SharedKey, ModelStatsTrigger.Open);
        gate.Request(Local, SharedKey, ModelStatsTrigger.Save).Should().Be(ModelStatsDecision.Defer);

        gate.Closing(Local).Should().Be(new ModelStatsRequest(Local, SharedKey, MeasureWarnings: true));
        gate.TakePending().Select(r => r.DocumentId).Should().Equal(Unc);
    }

    // ---- closing before Idling (review WU2-01) ---------------------------

    [Fact]
    public void Save_then_close_before_idling_captures_at_the_close()
    {
        // Revit order A: the close prompt's save fires before DocumentClosing.
        var gate = Opened(NewGate());
        _mono += TimeSpan.FromMinutes(6);

        Save(gate, A).Should().Be(ModelStatsDecision.Defer, "the save, deferred as usual");
        var request = gate.Closing(A);
        request.Should().NotBeNull("the pending capture runs while the Document is still valid");
        request!.MeasureWarnings.Should().BeTrue("Close → Save measures the warnings");
        Emit(gate, KeyOf(A)).Should().BeTrue();

        gate.HasPending.Should().BeFalse("the close took the request; Idling has nothing left to drop");
        gate.TakePending().Should().BeEmpty();
    }

    [Fact]
    public void Save_after_closing_has_started_captures_at_once()
    {
        // Revit order B: DocumentClosing fires, then the save (or a
        // Synchronize and Close sync) runs before DocumentClosed.
        var gate = Opened(NewGate());
        _mono += TimeSpan.FromMinutes(6);

        gate.Closing(A).Should().BeNull("nothing was pending at the close");
        Save(gate, A).Should().Be(ModelStatsDecision.CaptureNow, "the document will be gone before Idling");
        gate.Request(A, KeyOf(A), ModelStatsTrigger.Sync).Should().Be(ModelStatsDecision.CaptureNow,
            "Synchronize and Close likewise");
        gate.HasPending.Should().BeFalse("a close-time capture is never deferred");
        Emit(gate, KeyOf(A)).Should().BeTrue();
    }

    [Fact]
    public void Close_with_nothing_pending_captures_nothing()
    {
        var gate = NewGate();
        gate.Closing(A).Should().BeNull();
        gate.HasPending.Should().BeFalse();
    }

    [Fact]
    public void Save_on_close_inside_the_window_is_still_limited_to_one_per_five_minutes()
    {
        var gate = Opened(NewGate());
        _mono += TimeSpan.FromMinutes(2);

        Save(gate, A).Should().Be(ModelStatsDecision.Skip);
        gate.Closing(A).Should().BeNull();
        Save(gate, A).Should().Be(ModelStatsDecision.Skip, "closing does not lift the window");
    }

    [Fact]
    public void A_cancelled_close_defers_again()
    {
        var gate = NewGate();
        gate.Closing(A);
        gate.ClosingEnded(A); // DocumentClosed, status Cancelled

        Save(gate, A).Should().Be(ModelStatsDecision.Defer, "the document is open again");
    }

    [Fact]
    public void Closing_one_model_leaves_others_deferred()
    {
        var gate = NewGate();
        Save(gate, A);
        Save(gate, B);

        gate.Closing(A).Should().NotBeNull();
        Docs(gate).Should().Equal(B);
        Save(gate, C).Should().Be(ModelStatsDecision.Defer);
    }

    // ---- monotonic window (review WU2-02) --------------------------------

    [Fact]
    public void The_constructor_takes_no_wall_clock()
    {
        // The only time source is monotonic: a wall-clock change cannot
        // reach the window at all. Pin the signature so a DateTimeOffset
        // clock is not reintroduced.
        var ctor = typeof(ModelStatsGate).GetConstructors();
        ctor.Should().ContainSingle();
        ctor[0].GetParameters().Should().NotContain(p =>
            p.ParameterType == typeof(Func<DateTimeOffset>) || p.ParameterType == typeof(Func<DateTime>));
    }

    [Fact]
    public void A_wall_clock_set_back_an_hour_does_not_stretch_the_window()
    {
        // Emit at 10:00 wall time, then the clock is corrected to 09:00.
        // Five real minutes later (monotonic +5 min, wall 09:05) the model
        // captures again — not at 10:05 wall time, 65 minutes later.
        var gate = Opened(NewGate());

        _mono += TimeSpan.FromMinutes(5);
        Save(gate, A).Should().Be(ModelStatsDecision.Defer);
    }

    [Fact]
    public void A_wall_clock_set_forward_an_hour_does_not_shorten_the_window()
    {
        // Emit, then the clock jumps an hour ahead; 30 real seconds later
        // the window is still closed.
        var gate = Opened(NewGate());

        _mono += TimeSpan.FromSeconds(30);
        Save(gate, A).Should().Be(ModelStatsDecision.Skip);
    }

    [Fact]
    public void The_default_source_advances_with_real_time()
    {
        var gate = new ModelStatsGate(interval: TimeSpan.FromMilliseconds(250));
        gate.TryAcquire("model-a").Should().BeTrue();
        gate.CanEmit("model-a").Should().BeFalse();

        System.Threading.Thread.Sleep(400);
        gate.CanEmit("model-a").Should().BeTrue();
    }

    // ---- wire shape ------------------------------------------------------

    [Fact]
    public void Wire_names_are_the_spec_names()
    {
        TelemetryEventTypes.ModelStats.Should().Be("model_stats");
        TelemetryFields.WarningsCount.Should().Be("warnings_count");
        TelemetryFields.FileSizeBytes.Should().Be("file_size_bytes");
        TelemetryFields.RevitSubversion.Should().Be("revit_subversion");
    }

    [Fact]
    public void A_model_stats_record_round_trips_with_a_null_file_size()
    {
        var e = new TelemetrySession().Create(TelemetryEventTypes.ModelStats);
        e.SetField(TelemetryFields.CreationGuid, "c-1");
        e.SetField(TelemetryFields.WarningsCount, 42);
        e.SetField(TelemetryFields.FileSizeBytes, null);

        var back = TelemetryJson.TryParseLine(TelemetryJson.SerializeLine(e))!;

        back.EventType.Should().Be("model_stats");
        back.GetInt64(TelemetryFields.WarningsCount).Should().Be(42);
        back.GetInt64(TelemetryFields.FileSizeBytes).Should().BeNull();
        back.Payload.Should().ContainKey(TelemetryFields.FileSizeBytes,
            "null is written, so readers can tell 'not readable' from an older record");
    }

    [Fact]
    public void An_open_only_record_writes_warnings_count_null_not_absent()
    {
        // Decision #71: null = not measured; the field is present so a
        // reader can tell it from a pre-v3.2 record without model_stats.
        var e = new TelemetrySession().Create(TelemetryEventTypes.ModelStats);
        e.SetField(TelemetryFields.WarningsCount, null);
        e.SetField(TelemetryFields.FileSizeBytes, 97_882_112L);

        var back = TelemetryJson.TryParseLine(TelemetryJson.SerializeLine(e))!;

        back.Payload.Should().ContainKey(TelemetryFields.WarningsCount);
        back.GetInt64(TelemetryFields.WarningsCount).Should().BeNull();
        back.GetInt64(TelemetryFields.FileSizeBytes).Should().Be(97_882_112L);
    }
}
