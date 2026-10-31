// rst-c's own (not vendored): the tray's Clean Junk Files lists each year-specific target once per Revit year
// found (spec #25 Device; decision #56): "Journals — 2026", "Journals — 2027". These are the pure rules behind
// that listing: which targets are year-specific, the year in an "Autodesk Revit YYYY" folder name and the path
// hint a per-year row shows. A row's identity is never encoded in a string a profile could also produce: the
// tray's DeviceHealthService keeps each row as a structured (target, year) pair (PR #176 review F1).

using System.Globalization;
using System.Text.RegularExpressions;

namespace RST.Core.Health;

public static class CleanupYears
{
    /// <summary>The fan-out token of a <c>CleanupTarget.Path</c> (one path per <c>Autodesk Revit YYYY</c> folder).</summary>
    public const string Token = "{revit-version}";

    private static readonly Regex FolderRe = new(@"^Autodesk Revit ([0-9]{4})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The path fans out across Revit years (it holds <see cref="Token"/>).</summary>
    public static bool IsYearSpecific(string? path) =>
        !string.IsNullOrEmpty(path) && path.Contains(Token, StringComparison.OrdinalIgnoreCase);

    /// <summary>The year of an <c>Autodesk Revit YYYY</c> folder name; null for any other name.</summary>
    public static int? YearOfFolder(string? folderName)
    {
        if (string.IsNullOrEmpty(folderName)) return null;
        var m = FolderRe.Match(folderName);
        return m.Success ? int.Parse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>"Autodesk Revit 2026".</summary>
    public static string FolderName(int year) => "Autodesk Revit " + year.ToString("0000", CultureInfo.InvariantCulture);

    /// <summary>The path hint of a per-year row: the target's path with the token replaced by that year's folder.</summary>
    public static string DisplayPath(string? rawPath, int year)
    {
        if (string.IsNullOrEmpty(rawPath)) return "";
        var at = rawPath.IndexOf(Token, StringComparison.OrdinalIgnoreCase);
        return at < 0 ? rawPath : string.Concat(rawPath.AsSpan(0, at), FolderName(year), rawPath.AsSpan(at + Token.Length));
    }
}
