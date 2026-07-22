// IconPackTests.cs — failure-matrix coverage for the shared icon-pack
// contract (spec #9): pack-value parsing, palette order, and the
// directory → catalogue builder the bridge exposes to the picker.
//
// Release blockers pinned here: legacy bare values keep resolving as
// blue, explicit colors parse, underscore names survive, and malformed
// or path-like values are rejected before any filesystem lookup.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using RST.Core.Ribbon;
using Xunit;

namespace RST.Tests.Ribbon;

public sealed class IconPackValueTests
{
    [Fact]
    public void Canonical_palette_is_fixed_and_ordered()
    {
        IconPack.CanonicalColors.Should().Equal(
            "light_grey", "dark_grey", "blue", "purple", "green", "orange", "red");
    }

    [Theory]
    [InlineData("pack:move", "move")]
    [InlineData("PACK:move", "move")]                 // scheme is case-insensitive
    [InlineData("Pack:Move", "Move")]                 // name case is preserved
    [InlineData("  pack:move  ", "move")]             // outer whitespace trimmed
    [InlineData("pack:  move", "move")]               // whitespace right of prefix (legacy parity)
    [InlineData("pack:link_external", "link_external")] // underscore name stays bare
    [InlineData("pack:move_chartreuse", "move_chartreuse")] // unknown suffix is not a color
    public void Bare_values_parse_as_blue_alias(string input, string expectedName)
    {
        IconPack.TryParseValue(input, out var value).Should().BeTrue();
        value!.Name.Should().Be(expectedName);
        value.Color.Should().BeNull();
        value.IsExplicit.Should().BeFalse();
        value.RelativePath.Should().Be($"icons/pack/32_{expectedName}.png");
    }

    [Theory]
    [InlineData("light_grey")]
    [InlineData("dark_grey")]
    [InlineData("blue")]
    [InlineData("purple")]
    [InlineData("green")]
    [InlineData("orange")]
    [InlineData("red")]
    public void Every_canonical_color_parses(string color)
    {
        IconPack.TryParseValue($"pack:move_{color}", out var value).Should().BeTrue();
        value!.Name.Should().Be("move");
        value.Color.Should().Be(color);
        value.IsExplicit.Should().BeTrue();
        value.RelativePath.Should().Be($"icons/pack/32_move_{color}.png");
    }

    [Theory]
    [InlineData("pack:move_GREEN", "green")]           // color case-insensitive, normalized
    [InlineData("pack:move_Light_Grey", "light_grey")]
    [InlineData("pack:link_external_purple", "purple")] // underscore name + explicit color
    [InlineData("PACK:Move_Blue", "blue")]
    public void Explicit_values_normalize_the_color(string input, string expectedColor)
    {
        IconPack.TryParseValue(input, out var value).Should().BeTrue();
        value!.Color.Should().Be(expectedColor);
    }

    [Fact]
    public void Explicit_value_keeps_the_full_underscore_name()
    {
        IconPack.TryParseValue("pack:link_external_purple", out var value).Should().BeTrue();
        value!.Name.Should().Be("link_external");
        value.RelativePath.Should().Be("icons/pack/32_link_external_purple.png");
    }

    [Fact]
    public void Normalized_keys_distinguish_bare_from_explicit()
    {
        IconPack.TryParseValue("pack:move", out var bare).Should().BeTrue();
        IconPack.TryParseValue("pack:move_blue", out var blue).Should().BeTrue();
        // Different files (alias vs explicit variant) → different cache
        // entries even under the resolver's OrdinalIgnoreCase dictionary.
        bare!.NormalizedKey.Should().NotBeEquivalentTo(blue!.NormalizedKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("pack:")]                    // empty name
    [InlineData("pack:   ")]
    [InlineData("pack:_blue")]               // empty name before a known suffix
    [InlineData("move")]                     // no scheme
    [InlineData("pack")]                     // scheme word without the colon
    [InlineData("packs:move")]               // near-miss scheme
    [InlineData("http://x/move.png")]        // non-pack scheme
    [InlineData("profile/foo.png")]          // per-profile asset path — not pack
    [InlineData("pack:a/b")]                 // separator
    [InlineData("pack:a\\b")]                // separator
    [InlineData("pack:../escape")]           // traversal
    [InlineData("pack:..\\escape")]          // traversal
    [InlineData("pack:a..b")]                // traversal segment
    [InlineData("pack:/abs")]                // rooted
    [InlineData("pack:\\abs")]               // rooted
    [InlineData("pack:C:\\icons\\x.png")]    // rooted (Windows drive)
    [InlineData("pack:C:x")]                 // drive-relative
    public void Malformed_or_path_like_values_are_rejected(string? input)
    {
        IconPack.TryParseValue(input, out var value).Should().BeFalse();
        value.Should().BeNull();
    }
}

public sealed class IconPackCatalogueTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private string NewPackDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rst-iconpack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static void WriteFile(string dir, string name) =>
        File.WriteAllBytes(Path.Combine(dir, name), new byte[] { 0x89, 0x50 }); // content irrelevant

    private static void WriteDesign(string dir, string name, params string[] colors)
    {
        foreach (var color in colors)
            WriteFile(dir, $"32_{name}_{color}.png");
    }

    private static void WriteCompleteDesign(string dir, string name, bool withAlias = true)
    {
        WriteDesign(dir, name, IconPack.CanonicalColors.ToArray());
        if (withAlias) WriteFile(dir, $"32_{name}.png");
    }

    [Fact]
    public void Missing_directory_reports_not_exists_and_no_entries()
    {
        var missing = Path.Combine(Path.GetTempPath(), "rst-iconpack-" + Guid.NewGuid().ToString("N"));
        var catalogue = IconPack.BuildCatalogue(missing);
        catalogue.DirectoryExists.Should().BeFalse();
        catalogue.Entries.Should().BeEmpty();
        catalogue.IncompleteNames.Should().BeEmpty();
    }

    [Fact]
    public void Complete_design_yields_one_entry_in_canonical_color_order()
    {
        var dir = NewPackDir();
        // Write in reverse palette order — catalogue order must come from
        // the palette, never from filesystem enumeration.
        WriteDesign(dir, "move", IconPack.CanonicalColors.Reverse().ToArray());
        WriteFile(dir, "32_move.png");

        var catalogue = IconPack.BuildCatalogue(dir);
        catalogue.DirectoryExists.Should().BeTrue();
        var entry = catalogue.Entries.Should().ContainSingle().Subject;
        entry.Name.Should().Be("move");
        entry.Colors.Should().Equal(IconPack.CanonicalColors);
    }

    [Fact]
    public void Entries_sort_by_name_ordinal_ignore_case()
    {
        var dir = NewPackDir();
        WriteCompleteDesign(dir, "cherry");
        WriteCompleteDesign(dir, "Apple");
        WriteCompleteDesign(dir, "banana");

        IconPack.BuildCatalogue(dir).Entries.Select(e => e.Name)
            .Should().Equal("Apple", "banana", "cherry");
    }

    [Fact]
    public void Underscore_named_design_keeps_its_full_name()
    {
        var dir = NewPackDir();
        WriteCompleteDesign(dir, "link_external");

        var entry = IconPack.BuildCatalogue(dir).Entries.Should().ContainSingle().Subject;
        entry.Name.Should().Be("link_external");
    }

    [Fact]
    public void Bare_alias_alone_is_not_a_picker_icon()
    {
        var dir = NewPackDir();
        WriteFile(dir, "32_move.png"); // legacy-style pack: alias, no variants

        var catalogue = IconPack.BuildCatalogue(dir);
        catalogue.Entries.Should().BeEmpty();
        catalogue.IncompleteNames.Should().BeEmpty("an alias without variants is not an incomplete design");
    }

    [Fact]
    public void Incomplete_set_is_omitted_and_reported_once()
    {
        var dir = NewPackDir();
        WriteCompleteDesign(dir, "move");
        // Six of seven — missing "red".
        WriteDesign(dir, "bolt", IconPack.CanonicalColors.Where(c => c != "red").ToArray());
        WriteFile(dir, "32_bolt.png");

        var catalogue = IconPack.BuildCatalogue(dir);
        catalogue.Entries.Select(e => e.Name).Should().Equal("move");
        catalogue.IncompleteNames.Should().Equal("bolt");
    }

    [Fact]
    public void Complete_set_without_alias_is_still_a_picker_icon()
    {
        // Alias presence is the build-time inventory invariant's job; the
        // catalogue contract is complete seven-variant sets.
        var dir = NewPackDir();
        WriteCompleteDesign(dir, "move", withAlias: false);

        IconPack.BuildCatalogue(dir).Entries.Select(e => e.Name).Should().Equal("move");
    }

    [Fact]
    public void Non_contract_files_are_ignored()
    {
        var dir = NewPackDir();
        WriteCompleteDesign(dir, "move");
        WriteFile(dir, "move_blue.png");   // no 32_ prefix
        WriteFile(dir, "32_move.txt");     // non-PNG
        WriteFile(dir, "32_.png");         // empty name
        WriteFile(dir, "32__blue.png");    // empty name before known suffix
        WriteFile(dir, "notes.txt");

        var catalogue = IconPack.BuildCatalogue(dir);
        catalogue.Entries.Select(e => e.Name).Should().Equal("move");
        catalogue.IncompleteNames.Should().BeEmpty();
    }

    [Fact]
    public void Unknown_color_suffix_does_not_become_an_entry()
    {
        var dir = NewPackDir();
        WriteCompleteDesign(dir, "move");
        WriteFile(dir, "32_move_fuchsia.png"); // unknown suffix → bare alias for "move_fuchsia"

        var catalogue = IconPack.BuildCatalogue(dir);
        catalogue.Entries.Select(e => e.Name).Should().Equal("move");
        catalogue.IncompleteNames.Should().BeEmpty();
    }

    [Fact]
    public void Case_variant_files_collapse_into_one_design()
    {
        var dir = NewPackDir();
        WriteFile(dir, "32_mix_blue.png");
        WriteFile(dir, "32_MIX_green.png");
        WriteDesign(dir, "mix", "light_grey", "dark_grey", "purple", "orange", "red");

        var catalogue = IconPack.BuildCatalogue(dir);
        var entry = catalogue.Entries.Should().ContainSingle().Subject;
        entry.Name.Should().BeEquivalentTo("mix");
        entry.Colors.Should().Equal(IconPack.CanonicalColors);
    }
}
