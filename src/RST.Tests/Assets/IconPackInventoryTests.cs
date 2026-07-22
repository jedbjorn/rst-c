// IconPackInventoryTests.cs — build-time invariant over the vendored icon pack.
//
// Spec #9 (Colored Icon Pack Picker) asset contract: 52 logical icon
// designs, each with exactly seven canonical color variants
// (32_<name>_<color>.png) plus a blue compatibility alias (32_<name>.png)
// byte-equivalent to its blue variant. The bridge enumerates this
// directory and the resolver loads from it — a missing variant or alias
// is a release blocker, so the inventory itself is asserted here.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using RST.Core.Ribbon;
using Xunit;

namespace RST.Tests.Assets;

public class IconPackInventoryTests
{
    // Canonical palette (fleet decision #4 / spec #9 palette contract),
    // shared with the resolver and bridge via the Core icon-pack contract.
    private static readonly IReadOnlyList<string> CanonicalColors = IconPack.CanonicalColors;

    private const int ExpectedDesignCount = 52;

    // The four designs spec #9 adds to the original 48.
    private static readonly string[] NewDesigns =
        { "box_cube", "box_slot", "edit_line", "house_chimney" };

    private static string PackDir()
    {
        // Walk up from the test assembly to the repo root (anchor: the sln),
        // so the invariant runs against the vendored source directory.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RST-C.sln")))
            dir = dir.Parent;
        dir.Should().NotBeNull($"repo root (RST-C.sln) must be discoverable from {AppContext.BaseDirectory}");
        return Path.Combine(dir!.FullName, "src", "RST.Engine", "Assets", "icons", "pack");
    }

    // Split a pack filename into (name, color). A right-hand suffix equal to
    // a known color key is the color; anything else is a blue compatibility
    // alias (color = null). Names may themselves contain underscores.
    private static (string Name, string? Color) Parse(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        stem.Should().StartWith("32_", $"{fileName} must carry the 32_ prefix");
        var rest = stem.Substring(3);
        rest.Should().NotBeNullOrEmpty($"{fileName} has an empty icon name");
        foreach (var color in CanonicalColors)
        {
            if (rest.EndsWith("_" + color, StringComparison.Ordinal))
                return (rest.Substring(0, rest.Length - color.Length - 1), color);
        }
        return (rest, null);
    }

    [Fact]
    public void Pack_directory_contains_only_contract_files()
    {
        var dir = PackDir();
        Directory.Exists(dir).Should().BeTrue($"icon pack must be vendored at {dir}");

        var files = Directory.GetFiles(dir).Select(Path.GetFileName).ToArray();
        files.Should().OnlyContain(f => f!.EndsWith(".png", StringComparison.OrdinalIgnoreCase),
            "the pack directory holds PNG assets only");

        var parsed = files.Select(f => Parse(f!)).ToArray();
        var aliases = parsed.Where(p => p.Color is null).Select(p => p.Name).ToArray();
        var variants = parsed.Where(p => p.Color is not null).ToArray();

        aliases.Should().HaveCount(ExpectedDesignCount, "every design keeps a blue compatibility alias");
        aliases.Should().OnlyHaveUniqueItems("no duplicate logical names");
        variants.Should().HaveCount(ExpectedDesignCount * CanonicalColors.Count,
            "every design ships exactly the seven canonical variants");
        variants.Select(p => p.Name).Should().OnlyContain(n => aliases.Contains(n),
            "no variant may exist without its alias");
    }

    [Fact]
    public void Every_design_has_the_seven_canonical_variants()
    {
        var files = Directory.GetFiles(PackDir()).Select(Path.GetFileName);
        var byName = new Dictionary<string, List<string?>>();
        foreach (var (name, color) in files.Select(f => Parse(f!)))
        {
            if (!byName.TryGetValue(name, out var list)) byName[name] = list = new List<string?>();
            list.Add(color);
        }

        byName.Should().HaveCount(ExpectedDesignCount);
        foreach (var (name, colors) in byName)
        {
            colors.Where(c => c is not null).Should().BeEquivalentTo(CanonicalColors,
                $"{name} must ship exactly the canonical seven");
            colors.Should().Contain(c => c == null, $"{name} must keep its blue alias");
        }
    }

    [Fact]
    public void The_four_new_designs_are_vendored()
    {
        var aliases = Directory.GetFiles(PackDir(), "32_*.png")
            .Select(Path.GetFileName)
            .Select(f => Parse(f!))
            .Where(p => p.Color is null)
            .Select(p => p.Name);
        aliases.Should().Contain(NewDesigns, "spec #9 extends the original 48 with these four");
    }

    [Fact]
    public void Every_alias_is_byte_equal_to_its_blue_variant()
    {
        var dir = PackDir();
        var aliases = Directory.GetFiles(dir, "32_*.png")
            .Select(Path.GetFileName)
            .Select(f => Parse(f!))
            .Where(p => p.Color is null)
            .Select(p => p.Name);

        foreach (var name in aliases)
        {
            var alias = File.ReadAllBytes(Path.Combine(dir, $"32_{name}.png"));
            var blue = File.ReadAllBytes(Path.Combine(dir, $"32_{name}_blue.png"));
            alias.Should().Equal(blue, $"32_{name}.png must be byte-equivalent to its blue variant");
        }
    }
}
