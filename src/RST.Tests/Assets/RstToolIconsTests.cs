// rst-c's own (not vendored): the RST panel's own button icons ship in both sizes. Each button
// (Builder, Loader, RSTify off and on, Health, Search-It) has a 32 px LargeImage and a hand-sized
// 16 px Image under src/RST.Engine/Assets/icons; a missing or mis-sized file would only show at
// run time in Revit, as the generic default icon.

using System.Buffers.Binary;
using FluentAssertions;
using Xunit;

namespace RST.Tests.Assets;

public class RstToolIconsTests
{
    [Theory]
    [InlineData("icon_creator")]
    [InlineData("icon_loader")]
    [InlineData("icon_minify")]
    [InlineData("icon_minify_on")]
    [InlineData("icon_health")]
    [InlineData("icon_search")]
    public void Every_RST_panel_icon_ships_at_32_and_16_px(string name)
    {
        Size(name + ".png").Should().Be((32, 32));
        Size(name + "_16.png").Should().Be((16, 16));
    }

    [Fact]
    public void IconAssets_loads_the_search_icons_by_these_names()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "RST.Engine", "Ribbon", "IconAssets.cs"));
        source.Should().Contain("\"icons/icon_search.png\"").And.Contain("\"icons/icon_search_16.png\"");
    }

    private static (int Width, int Height) Size(string file)
    {
        var path = Path.Combine(RepoRoot(), "src", "RST.Engine", "Assets", "icons", file);
        File.Exists(path).Should().BeTrue($"{file} must ship");
        var bytes = File.ReadAllBytes(path);
        bytes.Take(4).Should().Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, $"{file} must be a PNG");
        return (BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)), BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RST-C.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("RST-C.sln not found above " + AppContext.BaseDirectory);
    }
}
