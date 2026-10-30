// rst-c's own (not vendored): the icon picker's catalogue on the production path (spec #9, and
// spec #16 WU5). These replace LoaderBridgeIconpackTests when LoaderBridge was retired: the same
// cases against IconPackSource, which the WPF Builder reads (list_iconpack without the JSON): the
// shipped pack next to RST.UI.dll, canonical color order, incomplete-set omission, and the
// missing or unreadable folder giving an empty list without throwing.

using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using FluentAssertions;
using RST.Core.Ribbon;
using RST.UI.Builder;
using RST.UI.Loader;
using Xunit;

namespace RST.RuntimeTests.Builder;

public sealed class IconPackSourceTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private string NewPackDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rst-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static void WriteCompleteDesign(string dir, string name)
    {
        foreach (var color in IconPack.CanonicalColors)
            File.WriteAllBytes(Path.Combine(dir, $"32_{name}_{color}.png"), [0x89, 0x50]);
        File.WriteAllBytes(Path.Combine(dir, $"32_{name}.png"), [0x89, 0x50]);
    }

    [Fact]
    public void The_production_path_returns_the_shipped_catalogue()
    {
        var entries = IconPackSource.Read(LoaderAssets.IconPackDirectory);
        entries.Should().HaveCount(55, "the vendored pack ships 55 complete designs");
        entries.Select(e => e.Name).Should().BeInAscendingOrder(StringComparer.OrdinalIgnoreCase);
        entries.Should().OnlyContain(e => e.Colors.SequenceEqual(IconPack.CanonicalColors), "colors follow the palette order, never the file order");
        entries.Select(e => e.Name).Should().Contain(["move", "link_external", "box_cube", "box_slot", "edit_line", "house_chimney"]);
    }

    [Fact]
    public void A_complete_design_is_listed_with_its_seven_colors()
    {
        var dir = NewPackDir();
        WriteCompleteDesign(dir, "move");
        var only = IconPackSource.Read(dir).Single();
        only.Name.Should().Be("move");
        only.Colors.Should().Equal("light_grey", "dark_grey", "blue", "purple", "green", "orange", "red");
    }

    [Fact]
    public void An_incomplete_set_is_omitted()
    {
        var dir = NewPackDir();
        WriteCompleteDesign(dir, "move");
        foreach (var color in IconPack.CanonicalColors.Where(c => c != "red"))
            File.WriteAllBytes(Path.Combine(dir, $"32_bolt_{color}.png"), [0x89, 0x50]);
        IconPackSource.Read(dir).Select(e => e.Name).Should().Equal("move");
    }

    [Fact]
    public void A_missing_pack_folder_gives_an_empty_list_without_throwing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "rst-pack-" + Guid.NewGuid().ToString("N"));
        IconPackSource.Read(missing).Should().BeEmpty();
    }

    [Fact]
    public void An_unreadable_pack_folder_gives_an_empty_list_without_throwing()
    {
        var dir = NewPackDir();
        var info = new DirectoryInfo(dir);
        var acl = info.GetAccessControl();
        var deny = new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.Read | FileSystemRights.ListDirectory,
            AccessControlType.Deny);
        acl.AddAccessRule(deny);
        info.SetAccessControl(acl);
        try
        {
            Action act = () => IconPackSource.Read(dir);
            act.Should().NotThrow();
            IconPackSource.Read(dir).Should().BeEmpty();
        }
        finally
        {
            acl.RemoveAccessRuleSpecific(deny);
            info.SetAccessControl(acl);
        }
    }
}
