// TelemetryJsonTests.cs — wire-shape guarantees for the outbox line
// format: snake_case envelope, fixed UTC ISO-8601 ts, flat payload,
// synthetic omitted unless set, tolerant line parsing, and the
// per-session seq contract (Telemetry v1 spec, Build Plan step 1).

using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using RST.Core.Telemetry;
using Xunit;

namespace RST.Tests.Telemetry;

public sealed class TelemetryJsonTests
{
    [Fact]
    public void Envelope_serializes_flat_snake_case_with_fixed_ts_shape()
    {
        var e = new TelemetryEvent
        {
            EventId = "11111111-1111-1111-1111-111111111111",
            SessionGuid = "22222222-2222-2222-2222-222222222222",
            Seq = 42,
            Ts = new DateTimeOffset(2026, 7, 18, 14, 3, 7, 123, TimeSpan.Zero),
            EventType = TelemetryEventTypes.DocOpened,
        };
        e.SetField(TelemetryFields.CreationGuid, "abc");
        e.SetField(TelemetryFields.CentralGuid, null);

        var line = TelemetryJson.SerializeLine(e);
        line.Should().NotContain("\n", "one event is one line");

        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        root.GetProperty("event_id").GetString().Should().Be("11111111-1111-1111-1111-111111111111");
        root.GetProperty("session_guid").GetString().Should().Be("22222222-2222-2222-2222-222222222222");
        root.GetProperty("seq").GetInt64().Should().Be(42);
        root.GetProperty("ts").GetString().Should().Be("2026-07-18T14:03:07.123Z");
        root.GetProperty("event_type").GetString().Should().Be("doc_opened");
        root.GetProperty("schema_version").GetInt32().Should().Be(1);
        root.GetProperty("source").GetString().Should().Be("addin");

        // Payload rides flat at the top level; nulls are written (an
        // identity gap is data); synthetic is absent unless set.
        root.GetProperty("creation_guid").GetString().Should().Be("abc");
        root.GetProperty("central_guid").ValueKind.Should().Be(JsonValueKind.Null);
        root.TryGetProperty("synthetic", out _).Should().BeFalse();
    }

    [Fact]
    public void Synthetic_true_serializes_when_set()
    {
        var e = new TelemetryEvent
        {
            EventType = TelemetryEventTypes.SessionEnd,
            Source = TelemetrySources.Recovery,
            Synthetic = true,
        };
        using var doc = JsonDocument.Parse(TelemetryJson.SerializeLine(e));
        doc.RootElement.GetProperty("synthetic").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("source").GetString().Should().Be("recovery");
    }

    [Fact]
    public void Round_trip_preserves_envelope_and_payload()
    {
        var e = new TelemetryEvent
        {
            EventId = Guid.NewGuid().ToString(),
            SessionGuid = Guid.NewGuid().ToString(),
            Seq = 7,
            Ts = new DateTimeOffset(2026, 1, 2, 3, 4, 5, 678, TimeSpan.Zero),
            EventType = TelemetryEventTypes.Heartbeat,
        };
        e.SetField(TelemetryFields.OpenDocCount, 3);
        e.SetField(TelemetryFields.IsWorkshared, true);
        e.SetField(TelemetryFields.LocalPath, null);

        var back = TelemetryJson.TryParseLine(TelemetryJson.SerializeLine(e));

        back.Should().NotBeNull();
        back!.EventId.Should().Be(e.EventId);
        back.Seq.Should().Be(7);
        back.Ts.Should().Be(e.Ts);
        back.EventType.Should().Be("heartbeat");
        back.GetInt64(TelemetryFields.OpenDocCount).Should().Be(3);
        back.GetBool(TelemetryFields.IsWorkshared).Should().BeTrue();
        back.GetString(TelemetryFields.LocalPath).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ \"event_id\": \"trunca")]     // partial trailing line
    [InlineData("not json at all")]
    [InlineData("{\"no_event_type\": true}")]    // parseable but not an event
    public void TryParseLine_returns_null_for_unusable_input(string? line)
    {
        TelemetryJson.TryParseLine(line).Should().BeNull();
    }

    [Theory]
    [InlineData("{\"event_type\":\"session_end\"}")]                       // bare event_type, no envelope
    [InlineData("{\"session_guid\":\"s\",\"seq\":1,\"ts\":\"2026-07-18T00:00:00.000Z\",\"event_type\":\"session_end\"}")]  // no event_id
    [InlineData("{\"event_id\":\"e\",\"seq\":1,\"ts\":\"2026-07-18T00:00:00.000Z\",\"event_type\":\"session_end\"}")]      // no session_guid
    [InlineData("{\"event_id\":\"e\",\"session_guid\":\"s\",\"seq\":0,\"ts\":\"2026-07-18T00:00:00.000Z\",\"event_type\":\"session_end\"}")]  // seq below 1
    [InlineData("{\"event_id\":\"e\",\"session_guid\":\"s\",\"seq\":1,\"event_type\":\"session_end\"}")]                   // no ts
    public void TryParseLine_rejects_incomplete_envelopes(string line)
    {
        TelemetryJson.TryParseLine(line).Should().BeNull(
            "recovery and prune decide closed-ness from parsed events — a forged or damaged " +
            "session_end without the mandatory envelope must not count");
    }

    [Fact]
    public void Identity_block_round_trips_through_payload()
    {
        var identity = new DocumentIdentity
        {
            CreationGuid = "cg",
            VersionGuid = "vg",
            SaveCount = 12,
            CloudProjectGuid = null,
            CloudModelGuid = null,
            CentralGuid = "central",
            CentralPath = @"\\server\central.rvt",
            LocalPath = @"C:\local\file.rvt",
            Title = "file.rvt",
            IsWorkshared = true,
            IsCloud = false,
            IsFamilyDoc = false,
            IsDetached = false,
        };
        var e = new TelemetryEvent
        {
            EventId = Guid.NewGuid().ToString(),
            SessionGuid = Guid.NewGuid().ToString(),
            Seq = 1,
            Ts = new DateTimeOffset(2026, 7, 18, 10, 0, 0, TimeSpan.Zero),
            EventType = TelemetryEventTypes.DocOpened,
        };
        identity.WriteTo(e);

        var back = DocumentIdentity.ReadFrom(
            TelemetryJson.TryParseLine(TelemetryJson.SerializeLine(e))!);

        back.Should().BeEquivalentTo(identity);
        back.JoinKey.Should().Be("central", "cloud > central > creation is the local join priority");
    }

    [Fact]
    public void Session_mints_monotonic_seq_and_addin_source()
    {
        var t0 = new DateTimeOffset(2026, 7, 18, 10, 0, 0, TimeSpan.Zero);
        var session = new TelemetrySession(clock: () => t0);

        var a = session.Create(TelemetryEventTypes.SessionStart);
        var b = session.Create(TelemetryEventTypes.Heartbeat);

        a.Seq.Should().Be(1);
        b.Seq.Should().Be(2);
        a.SessionGuid.Should().Be(session.SessionGuid).And.NotBeNullOrEmpty();
        a.EventId.Should().NotBe(b.EventId);
        a.Ts.Should().Be(t0);
        a.Source.Should().Be(TelemetrySources.Addin);
        a.SchemaVersion.Should().Be(1);
    }

    [Fact]
    public async Task Session_seq_is_unique_under_concurrent_minting()
    {
        // The collector mints from the UI thread and the heartbeat timer
        // thread concurrently — seq must never duplicate.
        var session = new TelemetrySession();
        var events = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => Task.Run(
                () => Enumerable.Range(0, 250)
                    .Select(_ => session.Create(TelemetryEventTypes.DocChangedPulse).Seq)
                    .ToArray())));

        var all = events.SelectMany(x => x).ToArray();
        all.Should().OnlyHaveUniqueItems();
        all.Max().Should().Be(2000);
    }
}
