using RST.Core.Telemetry;
using Xunit;

namespace RST.Tests.Telemetry;

public sealed class DocumentCapturePolicyTests
{
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(null, false, false)]
    [InlineData(false, null, false)]
    public void Only_known_nonlinked_projects_enter_document_capture(bool? linked, bool? family, bool expected) =>
        Assert.Equal(expected, DocumentCapturePolicy.IsProject(linked, family));

    [Theory]
    [InlineData(@"C:\models\Door.rfa")]
    [InlineData(@"C:\models\DOOR.RFA")]
    [InlineData(@"\\server\families\Door.rfa")]
    [InlineData(@"C:\templates\Metric Generic Model.RFT")]
    public void Family_opening_is_rejected_before_a_document_exists(string path) =>
        Assert.False(DocumentCapturePolicy.AllowsOpening(path));

    [Theory]
    [InlineData(@"C:\models\Project.rvt")]
    [InlineData(@"C:\templates\Project.RTE")]
    [InlineData(@"\\server\models\Central.rvt")]
    [InlineData(@"BIM 360://Project/Model.rvt")]
    [InlineData(@"Autodesk Docs://Project/Model")]
    [InlineData(@"C:\families.rfa\Project.rvt")]
    [InlineData("")]
    [InlineData(null)]
    public void Project_and_unknown_paths_keep_existing_opening_behavior(string? path) =>
        Assert.True(DocumentCapturePolicy.AllowsOpening(path));
}
