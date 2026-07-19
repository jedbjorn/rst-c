// DocumentIdentityTests.cs — the two write shapes on the wire. The
// keys-only set is the amended capture set (SC-032, decision #3):
// creation_guid + cloud pair + central_guid + central_path — every join
// level, so close/sync endpoints stay matchable for file-share centrals.
// Descriptive fields remain full-block-only.

using FluentAssertions;
using RST.Core.Telemetry;
using Xunit;
using static RST.Tests.Telemetry.OutboxTestData;

namespace RST.Tests.Telemetry;

public sealed class DocumentIdentityTests
{
    private static DocumentIdentity FullIdentity() => new()
    {
        CreationGuid = "doc-a",
        VersionGuid = "v-1",
        SaveCount = 3,
        CloudProjectGuid = "proj-1",
        CloudModelGuid = "model-1",
        CentralGuid = "central-1",
        CentralPath = "\\\\server\\projects\\Tower_Central.rvt",
        LocalPath = "C:\\models\\tower.rvt",
        Title = "tower.rvt",
        IsWorkshared = true,
        IsCloud = false,
    };

    [Fact]
    public void Keys_only_shape_carries_every_join_key_including_central_path()
    {
        var e = Event("s", 1, TelemetryEventTypes.DocClosing, System.DateTimeOffset.UnixEpoch);
        FullIdentity().WriteKeysTo(e);

        e.GetString(TelemetryFields.CreationGuid).Should().Be("doc-a");
        e.GetString(TelemetryFields.CloudProjectGuid).Should().Be("proj-1");
        e.GetString(TelemetryFields.CloudModelGuid).Should().Be("model-1");
        e.GetString(TelemetryFields.CentralGuid).Should().Be("central-1");
        e.GetString(TelemetryFields.CentralPath).Should().Be(
            "\\\\server\\projects\\Tower_Central.rvt",
            "file-share centrals have no WorksharingCentralGUID — without the path, keys-only events are unmatchable");

        e.GetString(TelemetryFields.LocalPath).Should().BeNull("descriptive fields are full-block-only");
        e.GetString(TelemetryFields.Title).Should().BeNull("descriptive fields are full-block-only");
    }

    // SC-032 round 4: every central-path capture site (CaptureKeys,
    // HealthCommand, the sync_start args.Location fallback) gates on this
    // predicate, so it IS the capture-seam behavior: unknown cloud-ness
    // (IsModelInCloud read failed → null) must suppress the path exactly
    // like known cloud — GetWorksharingCentralModelPath returns a real
    // path for cloud models, and a wrongly-stamped central_path is worse
    // than a null. Known non-cloud must still capture.
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(null, false)]
    public void Central_path_capture_requires_known_non_cloud(bool? isCloud, bool allowed)
    {
        DocumentIdentity.AllowsCentralPath(isCloud).Should().Be(
            allowed,
            "unknown cloud-ness is UNKNOWN, not non-cloud — only a successful IsModelInCloud=false read permits central_path");
    }

    // SC-035: the bookkeeping key must honor the amended match priority.
    // File-share centrals have no WorksharingCentralGUID, and a Save As
    // between centrals keeps the creation GUID — so a present central
    // path outranks creation lineage, case-insensitively.
    [Fact]
    public void Join_key_ranks_normalized_central_path_ahead_of_creation_guid()
    {
        var a = new DocumentIdentity
        {
            CreationGuid = "doc-a",
            CentralPath = "\\\\server\\projects\\Tower_Central.rvt",
            LocalPath = "C:\\models\\tower.rvt",
        };
        var b = new DocumentIdentity { CentralPath = "\\\\SERVER\\Projects\\tower_central.RVT" };

        a.JoinKey.Should().NotBe("doc-a", "a present central path outranks creation lineage");
        a.JoinKey.Should().Be(b.JoinKey, "casing drift must not split one document into two keys");
    }

    [Fact]
    public void Join_key_priority_holds_around_the_central_path_level()
    {
        new DocumentIdentity { CreationGuid = "doc-a", CentralPath = "" }
            .JoinKey.Should().Be("doc-a", "an empty central path is no key");
        new DocumentIdentity { CentralGuid = "central-1", CentralPath = "\\\\server\\a.rvt" }
            .JoinKey.Should().Be("central-1", "central GUID still outranks the path level");
    }

    [Fact]
    public void Full_block_shape_carries_keys_and_descriptive_fields()
    {
        var e = Event("s", 1, TelemetryEventTypes.DocOpened, System.DateTimeOffset.UnixEpoch);
        FullIdentity().WriteTo(e);

        e.GetString(TelemetryFields.CentralPath).Should().Be("\\\\server\\projects\\Tower_Central.rvt");
        e.GetString(TelemetryFields.VersionGuid).Should().Be("v-1");
        e.GetInt64(TelemetryFields.SaveCount).Should().Be(3);
        e.GetString(TelemetryFields.LocalPath).Should().Be("C:\\models\\tower.rvt");
        e.GetString(TelemetryFields.Title).Should().Be("tower.rvt");
    }
}
