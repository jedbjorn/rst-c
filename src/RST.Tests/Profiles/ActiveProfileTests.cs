// ActiveProfileTests.cs — read/write/blank shapes for the active-profile pointer.

using System.IO;
using FluentAssertions;
using RST.Core.Profiles;
using Xunit;

namespace RST.Tests.Profiles;

public sealed class ActiveProfileTests
{
    [Fact]
    public void Read_missing_file_returns_blank()
    {
        using var t = new TempFile();
        var ap = ActiveProfile.Read(t.Path);
        ap.IsBlank.Should().BeTrue();
        ap.Blank.Should().BeTrue();
        ap.ProfileName.Should().Be("BlankRST");
    }

    [Fact]
    public void Read_corrupt_file_returns_blank()
    {
        using var t = new TempFile();
        File.WriteAllText(t.Path, "{ not json");
        var ap = ActiveProfile.Read(t.Path);
        ap.IsBlank.Should().BeTrue();
    }

    [Fact]
    public void WriteBlank_round_trips_to_a_blank_pointer()
    {
        using var t = new TempFile();
        ActiveProfile.WriteBlank(t.Path);

        var ap = ActiveProfile.Read(t.Path);
        ap.IsBlank.Should().BeTrue();
        ap.Blank.Should().BeTrue();
    }

    [Fact]
    public void FromProfile_writes_a_real_pointer_and_round_trips()
    {
        using var t = new TempFile();
        var profile = new Profile
        {
            SchemaVersion = ProfileSerializer.CurrentSchemaVersion,
            Id = "id-99",
            ProfileName = "Studio",
            Tab = "Studio",
            ExportDate = "2026-05-04",
        };

        ActiveProfile
            .FromProfile(profile, "Studio_2026-05-04.json", new[] { "Architecture" }, disableNonRequired: true)
            .Write(t.Path);

        var ap = ActiveProfile.Read(t.Path);
        ap.IsBlank.Should().BeFalse();
        ap.ProfileId.Should().Be("id-99");
        ap.ProfileFile.Should().Be("Studio_2026-05-04.json");
        ap.Tab.Should().Be("Studio");
        ap.HiddenTabs.Should().ContainSingle().Which.Should().Be("Architecture");
        ap.DisableNonRequired.Should().BeTrue();
        ap.LoadedAt.Should().NotBeNullOrEmpty();
    }

    private sealed class TempFile : System.IDisposable
    {
        public string Path { get; }
        public TempFile()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rst-active-" + System.Guid.NewGuid().ToString("N") + ".json");
        }
        public void Dispose()
        {
            try { if (File.Exists(Path)) File.Delete(Path); } catch { }
        }
    }
}
