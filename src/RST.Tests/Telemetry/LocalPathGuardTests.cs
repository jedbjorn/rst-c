// LocalPathGuardTests.cs — the SC-026 path guard: only a verified-local
// drive-letter path may reach BasicFileInfo; UNC shares, mapped network
// drives, cloud pseudo-paths, and anything unverifiable fail closed.
// The drive-type probe is injected — CI cannot mint network drives.

using System;
using System.IO;
using FluentAssertions;
using RST.Core.Telemetry;
using Xunit;

namespace RST.Tests.Telemetry;

public sealed class LocalPathGuardTests
{
    private static Func<char, DriveType?> Probe(DriveType? result) => _ => result;

    [Theory]
    [InlineData(@"\\server\share\model.rvt")]
    [InlineData(@"\\server\share")]
    [InlineData("//server/share/model.rvt")]
    [InlineData(@"/\server\share\model.rvt")]
    public void Unc_paths_are_rejected_without_probing(string path)
    {
        var probed = false;
        LocalPathGuard.IsLocalFile(path, _ => { probed = true; return DriveType.Fixed; })
            .Should().BeFalse();
        probed.Should().BeFalse("a UNC path must be rejected before any drive lookup");
    }

    [Theory]
    [InlineData("BIM 360://Project/model.rvt")]
    [InlineData("Autodesk Docs://Hub/Project/model.rvt")]
    public void Cloud_pseudo_paths_are_rejected(string path) =>
        LocalPathGuard.IsLocalFile(path, Probe(DriveType.Fixed)).Should().BeFalse();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Null_and_blank_are_rejected(string? path) =>
        LocalPathGuard.IsLocalFile(path, Probe(DriveType.Fixed)).Should().BeFalse();

    [Theory]
    [InlineData(@"models\a.rvt")]      // relative
    [InlineData(@"C:file.rvt")]        // drive-relative, not rooted
    [InlineData("C:")]                 // too short
    [InlineData("/tmp/model.rvt")]     // rooted but no drive letter — unverifiable
    [InlineData(@"1:\model.rvt")]      // not a letter
    public void Non_drive_letter_paths_are_rejected(string path) =>
        LocalPathGuard.IsLocalFile(path, Probe(DriveType.Fixed)).Should().BeFalse();

    [Theory]
    [InlineData(@"C:\Models\a.rvt")]
    [InlineData("c:/models/a.rvt")]
    [InlineData(@"Z:\a.rvt")]
    public void Local_drive_letter_paths_pass_when_drive_probes_local(string path) =>
        LocalPathGuard.IsLocalFile(path, Probe(DriveType.Fixed)).Should().BeTrue();

    [Theory]
    [InlineData(DriveType.Fixed, true)]
    [InlineData(DriveType.Removable, true)]
    [InlineData(DriveType.Ram, true)]
    [InlineData(DriveType.CDRom, true)]
    [InlineData(DriveType.Network, false)]     // mapped network drive
    [InlineData(DriveType.Unknown, false)]
    [InlineData(DriveType.NoRootDirectory, false)]
    [InlineData(null, false)]                  // probe failed — not provably local
    public void Drive_type_decides_verified_local(DriveType? type, bool expected) =>
        LocalPathGuard.IsLocalFile(@"H:\proj\model.rvt", Probe(type)).Should().Be(expected);

    [Fact]
    public void Probe_receives_the_drive_letter()
    {
        char? seen = null;
        LocalPathGuard.IsLocalFile(@"Q:\a.rvt", c => { seen = c; return DriveType.Fixed; });
        seen.Should().Be('Q');
    }
}
