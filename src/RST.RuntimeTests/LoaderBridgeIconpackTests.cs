// LoaderBridgeIconpackTests.cs — production-path bridge contract tests
// for spec #9 (Colored Icon Pack Picker). These invoke the real
// LoaderBridge.ListIconpack — the JSON the Builder's picker actually
// receives — which is why they must run on a Windows runtime (RST.UI is
// WPF): the exact lowercase shape, canonical color order, incomplete-set
// omission, and the missing/unreadable-directory no-throw behavior are
// asserted here, not approximated against Core internals.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using FluentAssertions;
using RST.Core.Ribbon;
using RST.Core.Scanning;
using RST.UI.Loader;
using Xunit;

namespace RST.RuntimeTests;

public sealed class LoaderBridgeIconpackTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static LoaderBridge NewBridge() =>
        new("2025", Array.Empty<ScannedCommand>(), () => { });

    private string NewPackDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rst-bridge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static void WriteCompleteDesign(string dir, string name)
    {
        foreach (var color in IconPack.CanonicalColors)
            File.WriteAllBytes(Path.Combine(dir, $"32_{name}_{color}.png"), new byte[] { 0x89, 0x50 });
        File.WriteAllBytes(Path.Combine(dir, $"32_{name}.png"), new byte[] { 0x89, 0x50 });
    }

    private static JsonElement.ArrayEnumerator Entries(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateArray();

    [Fact]
    public void Production_path_returns_the_vendored_catalogue()
    {
        // The real ListIconpack(): pack dir next to the loaded RST.UI.dll,
        // populated by the same transitive content flow the addin uses.
        var json = NewBridge().ListIconpack();
        var entries = Entries(json).ToArray();

        entries.Should().HaveCount(52, "the vendored pack ships 52 complete logical designs");
        entries.Select(e => e.GetProperty("name").GetString())
            .Should().BeInAscendingOrder(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            entry.EnumerateObject().Select(p => p.Name).Should().Equal(
                new[] { "name", "colors" },
                "the bridge contract is exactly {{ name, colors[] }}, lowercase");
            entry.GetProperty("colors").EnumerateArray().Select(c => c.GetString())
                .Should().Equal(IconPack.CanonicalColors,
                    "colors follow canonical palette order, never filesystem order");
        }
        entries.Select(e => e.GetProperty("name").GetString())
            .Should().Contain(new[] { "move", "link_external", "box_cube", "box_slot", "edit_line", "house_chimney" });
    }

    [Fact]
    public void Exact_json_shape_is_lowercase_name_then_colors()
    {
        var dir = NewPackDir();
        WriteCompleteDesign(dir, "move");

        NewBridge().ListIconpackCore(dir).Should().Be(
            """[{"name":"move","colors":["light_grey","dark_grey","blue","purple","green","orange","red"]}]""");
    }

    [Fact]
    public void Incomplete_set_is_omitted_from_the_output()
    {
        var dir = NewPackDir();
        WriteCompleteDesign(dir, "move");
        foreach (var color in IconPack.CanonicalColors.Where(c => c != "red"))
            File.WriteAllBytes(Path.Combine(dir, $"32_bolt_{color}.png"), new byte[] { 0x89, 0x50 });

        var entries = Entries(NewBridge().ListIconpackCore(dir)).ToArray();
        entries.Select(e => e.GetProperty("name").GetString()).Should().Equal("move");
    }

    [Fact]
    public void Missing_pack_dir_returns_empty_array_without_throwing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "rst-bridge-" + Guid.NewGuid().ToString("N"));
        Action act = () => NewBridge().ListIconpackCore(missing);
        act.Should().NotThrow("the bridge's no-throw COM boundary must hold");
        NewBridge().ListIconpackCore(missing).Should().Be("[]");
    }

    [Fact]
    public void Unreadable_pack_dir_returns_empty_array_without_throwing()
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
            // Directory.Exists still answers (attribute read needs no list
            // right); enumeration throws, and the bridge must convert that
            // into "[]" — nothing crosses the COM boundary as an HRESULT.
            Action act = () => NewBridge().ListIconpackCore(dir);
            act.Should().NotThrow();
            NewBridge().ListIconpackCore(dir).Should().Be("[]");
        }
        finally
        {
            acl.RemoveAccessRuleSpecific(deny);
            info.SetAccessControl(acl);
        }
    }
}
