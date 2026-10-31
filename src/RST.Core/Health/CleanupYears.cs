// rst-c's own (not vendored): the tray's Clean Junk Files lists each year-specific target once per Revit year
// found (spec #25 Device; decision #56): "Journals — 2026", "Journals — 2027". These are the pure rules behind
// that listing: which targets are year-specific, the year in an "Autodesk Revit YYYY" folder name, the id a
// per-year row carries ("<target id>@<year>") and the path hint it shows. An id names a target and a year
// only; the cleaner resolves the paths again from the profile's own target, so a row can never add a path.

using System.Globalization;
using System.Text.RegularExpressions;

namespace RST.Core.Health;

public static class CleanupYears
{
    /// <summary>The fan-out token of a <c>CleanupTarget.Path</c> (one path per <c>Autodesk Revit YYYY</c> folder).</summary>
    public const string Token = "{revit-version}";

    private const char Separator = '@';

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

    /// <summary>The id of a per-year row: the target's id (or name), "@", the year.</summary>
    public static string PickId(string targetKey, int year) =>
        targetKey + Separator + year.ToString("0000", CultureInfo.InvariantCulture);

    /// <summary>
    /// Splits a per-year row's id. False, with the whole id as the key, when it does not end in "@" and four
    /// digits with something before them: a plain target id.
    /// </summary>
    public static bool TryParsePickId(string? id, out string targetKey, out int year)
    {
        targetKey = id ?? "";
        year = 0;
        if (string.IsNullOrEmpty(id) || id.Length < 6) return false;
        var at = id.Length - 5;
        if (id[at] != Separator) return false;
        for (var i = at + 1; i < id.Length; i++)
            if (id[i] is < '0' or > '9') return false;
        targetKey = id[..at];
        year = int.Parse(id.AsSpan(at + 1), NumberStyles.None, CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>The path hint of a per-year row: the target's path with the token replaced by that year's folder.</summary>
    public static string DisplayPath(string? rawPath, int year)
    {
        if (string.IsNullOrEmpty(rawPath)) return "";
        var at = rawPath.IndexOf(Token, StringComparison.OrdinalIgnoreCase);
        return at < 0 ? rawPath : string.Concat(rawPath.AsSpan(0, at), FolderName(year), rawPath.AsSpan(at + Token.Length));
    }
}
