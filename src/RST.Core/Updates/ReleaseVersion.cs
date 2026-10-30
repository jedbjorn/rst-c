// rst-c's own (not vendored): a release's a.b.c version (spec #20 §4; F17 WU3). Two inputs, two rules:
//
//   - a release TAG offered by the feed must be exactly v<major>.<minor>.<patch> (the spec's ^v\d+\.\d+\.\d+$,
//     written with ASCII [0-9], since .NET's \d also matches other scripts' digits, and \z, since .NET's $ also
//     matches before a final newline). Anything else is refused, a pre-release such as v3.1.0-rc.1 included, so a
//     pre-release is never offered;
//   - an INSTALLED version is what Windows Installer reports (MsiGetProductInfoEx VersionString): a.b.c, or
//     a.b.c.d, whose fourth field Windows Installer itself ignores when it compares versions, so it is dropped.
//
// Comparison is numeric, field by field.

using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RST.Core.Updates;

public readonly record struct ReleaseVersion(int Major, int Minor, int Patch) : IComparable<ReleaseVersion>
{
    // At most nine digits a field, so a field always fits an int.
    private static readonly Regex TagPattern = new(@"^v([0-9]{1,9})\.([0-9]{1,9})\.([0-9]{1,9})\z",
        RegexOptions.CultureInvariant);

    private static readonly Regex InstalledPattern = new(@"^([0-9]{1,9})\.([0-9]{1,9})\.([0-9]{1,9})(\.[0-9]{1,9})?\z",
        RegexOptions.CultureInvariant);

    /// <summary>A full-release tag (<c>v3.1.0</c>), or null: a pre-release, a draft name or anything else.</summary>
    public static ReleaseVersion? TryParseTag(string? tag) => Match(TagPattern, tag);

    /// <summary>An installed product's version string (<c>3.1.0</c> or <c>3.1.0.0</c>), or null.</summary>
    public static ReleaseVersion? TryParseInstalled(string? version) => Match(InstalledPattern, version?.Trim());

    /// <summary>The tag that names this version: <c>v3.1.0</c>.</summary>
    public string Tag => "v" + ToString();

    public int CompareTo(ReleaseVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        return c != 0 ? c : Patch.CompareTo(other.Patch);
    }

    public static bool operator <(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) < 0;

    public static bool operator >(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) > 0;

    public static bool operator <=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) <= 0;

    public static bool operator >=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) >= 0;

    /// <summary><c>3.1.0</c>.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    private static ReleaseVersion? Match(Regex pattern, string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = pattern.Match(text);
        if (!m.Success) return null;
        return new ReleaseVersion(
            int.Parse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture));
    }
}
