// CleanupDefaultsTests.cs — the preset allowlist that locks Health cleanup
// to RST's own paths. A profile is importable/hand-editable, so the cleaner
// must refuse any path that isn't one of these presets.

using System.Linq;
using FluentAssertions;
using RST.Core.Profiles;
using Xunit;

namespace RST.Tests.Profiles;

public sealed class CleanupDefaultsTests
{
    [Fact]
    public void Every_OOB_target_path_is_a_preset()
    {
        foreach (var t in CleanupDefaults.Build())
            CleanupDefaults.IsPresetPath(t.Path).Should().BeTrue($"'{t.Path}' is an OOB target");
    }

    [Theory]
    [InlineData(@"%LocalAppData%\Temp")]
    [InlineData(@"%AppData%\RST\logs")]
    [InlineData(@"  %localappdata%\temp  ")] // case-insensitive + trimmed
    public void Preset_paths_are_allowed(string path)
    {
        CleanupDefaults.IsPresetPath(path).Should().BeTrue();
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"%UserProfile%\Documents")]
    [InlineData(@"\\server\share")]
    [InlineData(@"%LocalAppData%\Temp\subdir")] // a sub-path is NOT the preset
    [InlineData(@"%LocalAppData%")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void Non_preset_paths_are_refused(string? path)
    {
        CleanupDefaults.IsPresetPath(path).Should().BeFalse();
    }
}
