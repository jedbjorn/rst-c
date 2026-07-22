// IconPack.cs — shared contract for the vendored colored icon pack.
//
// Spec #9 (Colored Icon Pack Picker): every logical design ships seven
// explicit color variants (Assets/icons/pack/32_<name>_<color>.png) plus
// a blue compatibility alias (32_<name>.png) that legacy bare
// "pack:<name>" profile values resolve to. This type owns the three
// pieces the Engine resolver, the UI bridge, and the tests must agree
// on:
//
//   1. Canonical palette — seven color keys in a fixed order. The same
//      order drives bridge output, the picker popdown, and the docs; it
//      never comes from filesystem enumeration order.
//   2. Pack-value parsing — profile iconFile values. Only a RIGHT-SIDE
//      suffix equal to a known color key splits off as a color, so
//      "pack:link_external" stays the bare name "link_external" while
//      "pack:link_external_purple" is (link_external, purple). Empty
//      names, path separators, traversal segments, rooted paths, and
//      non-pack schemes are rejected before any filesystem lookup.
//   3. Directory → catalogue — the picker-facing enumeration: one entry
//      per logical design with a COMPLETE seven-variant set (incomplete
//      designs are reported via IncompleteNames, never returned), bare
//      blue aliases excluded as icons, names sorted OrdinalIgnoreCase.
//
// Lives in Core (no WPF / WebView2 dependencies) because RST.Engine's
// IconAssets, RST.UI's LoaderBridge, and RST.Tests (which references
// only Core) all consume it. Logging stays with the callers — Core has
// no Serilog dependency.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;

namespace RST.Core.Ribbon;

public static class IconPack
{
    /// <summary>Profile-value scheme prefix ("pack:&lt;name&gt;[_&lt;color&gt;]").</summary>
    public const string ValuePrefix = "pack:";

    /// <summary>On-disk filename prefix ("32_&lt;name&gt;[_&lt;color&gt;].png").</summary>
    public const string FilePrefix = "32_";

    private static readonly string[] Palette =
        { "light_grey", "dark_grey", "blue", "purple", "green", "orange", "red" };

    /// <summary>
    /// Canonical color keys in their fixed palette order (fleet decision
    /// #4 / spec #9 palette contract). Keys are identifiers — never
    /// localized, never reordered.
    /// </summary>
    public static IReadOnlyList<string> CanonicalColors => Palette;

    // A pack value may not cross filesystem boundaries. ':' additionally
    // kills rooted Windows forms ("C:\...", "C:...") that '/' and '\'
    // alone would miss.
    private static readonly char[] RejectedNameChars = { '/', '\\', ':' };

    /// <summary>
    /// Parse a profile <c>iconFile</c> value into its logical name and
    /// optional explicit color. Accepts <c>pack:</c> case-insensitively
    /// and trims outer whitespace (plus whitespace right of the prefix,
    /// preserving the pre-color resolver's behavior). Returns false for
    /// null/empty values, non-<c>pack:</c> schemes, empty names, path
    /// separators, traversal segments, and rooted paths — callers treat
    /// those as unresolved and fall back to the default icon. An
    /// unrecognized right-hand suffix is NOT a color: the whole
    /// remainder stays the bare logical name (which then resolves, or
    /// not, via its blue-alias file).
    /// </summary>
    public static bool TryParseValue(string? iconFile, [NotNullWhen(true)] out IconPackValue? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(iconFile)) return false;
        var trimmed = iconFile.Trim();
        if (!trimmed.StartsWith(ValuePrefix, StringComparison.OrdinalIgnoreCase)) return false;
        var rest = trimmed.Substring(ValuePrefix.Length).Trim();
        if (rest.Length == 0) return false;
        if (rest.IndexOfAny(RejectedNameChars) >= 0) return false;
        if (rest.Contains("..", StringComparison.Ordinal)) return false;
        var (name, color) = SplitColorSuffix(rest);
        if (name.Length == 0) return false;
        value = new IconPackValue(name, color);
        return true;
    }

    /// <summary>
    /// Build the picker-facing catalogue from a pack directory: one entry
    /// per logical design that ships all seven canonical variants, names
    /// sorted <see cref="StringComparer.OrdinalIgnoreCase"/>, colors in
    /// canonical palette order. Bare blue aliases
    /// (<c>32_&lt;name&gt;.png</c>) are compatibility files, not icons.
    /// Designs with at least one explicit variant but an incomplete set
    /// surface in <see cref="IconPackCatalogue.IncompleteNames"/> (sorted,
    /// for one-shot caller logging) and are never returned as entries.
    /// Non-PNG files, files without the <c>32_</c> prefix, and empty
    /// names are ignored. A missing directory yields
    /// <see cref="IconPackCatalogue.DirectoryExists"/> = false with empty
    /// lists; an unreadable directory throws (callers own the no-throw
    /// boundary).
    /// </summary>
    public static IconPackCatalogue BuildCatalogue(string packDir)
    {
        if (!Directory.Exists(packDir))
            return new IconPackCatalogue(false, Array.Empty<IconPackEntry>(), Array.Empty<string>());

        // Group explicit variants by logical name (OrdinalIgnoreCase so
        // case-variant filenames collapse into one design). Sorting the
        // file list first keeps the display-name pick deterministic when
        // case variants collide on a case-sensitive filesystem.
        var colorsByName = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var displayName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.GetFiles(packDir).OrderBy(p => p, StringComparer.Ordinal))
        {
            if (!TryParseFileName(Path.GetFileName(path), out var name, out var color)) continue;
            if (color is null) continue; // bare blue alias — not a picker icon
            if (!colorsByName.TryGetValue(name, out var colors))
                colorsByName[name] = colors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!displayName.ContainsKey(name)) displayName[name] = name;
            colors.Add(color);
        }

        var entries = new List<IconPackEntry>();
        var incomplete = new List<string>();
        foreach (var (name, colors) in colorsByName)
        {
            // colors only ever holds canonical keys, so Count == 7 iff
            // the design ships the complete set.
            if (colors.Count == Palette.Length)
                entries.Add(new IconPackEntry(displayName[name], CanonicalColors));
            else
                incomplete.Add(displayName[name]);
        }
        entries = entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        incomplete.Sort(StringComparer.OrdinalIgnoreCase);
        return new IconPackCatalogue(true, entries, incomplete);
    }

    // 32_<name>[_<color>].png → (name, canonical color | null).
    private static bool TryParseFileName(string fileName, out string name, out string? color)
    {
        name = string.Empty;
        color = null;
        if (!fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return false;
        var stem = fileName.Substring(0, fileName.Length - ".png".Length);
        if (!stem.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase)) return false;
        var rest = stem.Substring(FilePrefix.Length);
        if (rest.Length == 0) return false;
        (name, color) = SplitColorSuffix(rest);
        return name.Length > 0;
    }

    // Split a right-hand known-color suffix off a name remainder. Only a
    // trailing "_<color>" segment equal to a canonical key (case-
    // insensitive) is a color — matched against the full key, since keys
    // themselves contain underscores ("light_grey"). Anything else keeps
    // the whole remainder as a bare name, so underscore-containing names
    // like "link_external" survive. An empty name before a known suffix
    // ("_blue") yields Name = "" and is rejected by the callers. No key
    // is a suffix of another key, so match order is irrelevant.
    private static (string Name, string? Color) SplitColorSuffix(string rest)
    {
        foreach (var known in Palette)
        {
            var suffix = "_" + known;
            if (rest.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return (rest.Substring(0, rest.Length - suffix.Length), known);
        }
        return (rest, null);
    }
}

/// <summary>
/// A parsed <c>pack:</c> profile value: logical <paramref name="Name"/>
/// plus the canonical color key for an explicit variant, or null for a
/// legacy bare value (blue compatibility alias).
/// </summary>
public sealed record IconPackValue(string Name, string? Color)
{
    /// <summary>True when the value names an explicit color variant.</summary>
    public bool IsExplicit => Color is not null;

    /// <summary>
    /// Pack-relative asset path (<c>icons/pack/32_&lt;name&gt;[_&lt;color&gt;].png</c>)
    /// for the runtime loader.
    /// </summary>
    public string RelativePath => Color is null
        ? $"icons/pack/{IconPack.FilePrefix}{Name}.png"
        : $"icons/pack/{IconPack.FilePrefix}{Name}_{Color}.png";

    /// <summary>
    /// Case-insensitive cache key for resolved images — distinct per
    /// (name, color) pair so a bare value and its explicit-blue form
    /// stay separate cache entries (they are different files).
    /// </summary>
    public string NormalizedKey => Color is null ? Name : Name + "|" + Color;
}

/// <summary>
/// One picker-facing logical design: its name and the available color
/// keys in canonical palette order.
/// </summary>
public sealed record IconPackEntry(string Name, IReadOnlyList<string> Colors);

/// <summary>
/// Result of <see cref="IconPack.BuildCatalogue"/>: whether the pack
/// directory exists, the complete-set entries, and the names of designs
/// omitted for shipping an incomplete variant set.
/// </summary>
public sealed record IconPackCatalogue(
    bool DirectoryExists,
    IReadOnlyList<IconPackEntry> Entries,
    IReadOnlyList<string> IncompleteNames);
