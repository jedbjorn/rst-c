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
