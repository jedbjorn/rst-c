// CleanupPathResolver.cs — token expansion for CleanupTarget.Path.
//
// Tokens are case-insensitive. Substitution order:
//   1. %LocalAppData%, %AppData%, %UserProfile% — replaced inline against
//      the current Windows user's special folders.
//   2. {revit-version} — fan-out wildcard. The substring up to and
//      including the segment containing this token is treated as a
//      "scan parent". Every direct subdir of the scan parent whose name
//      matches ^Autodesk Revit \d{4}$ contributes one resolved path
//      (with the matched dir name substituted in for the token).
//      A path with N {revit-version} occurrences is fanned across the
//      first occurrence only — multi-token paths aren't currently used
//      and the simpler resolver covers every OOB and likely admin entry.
//
// A path with no fan-out token returns one resolved entry. A path with
// {revit-version} and zero matching subdirs returns an empty list (the
// caller treats that as "no work to do" — counts roll up as 0/0).
//
// ResolveEach also says which Revit year each fanned-out path belongs to
// (from its "Autodesk Revit YYYY" folder), so the tray's Clean Junk Files
// can list and clean a year-specific target once per year found (spec #25,
// decision #56). Resolve is ResolveEach's paths, in the same order.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RST.Core.Health;
using RST.Core.Logging;

namespace RST.UI.Health;

/// <summary>The user folders the path tokens stand for. <see cref="Current"/> is this Windows user's; tests pass their own.</summary>
public sealed record CleanupFolders(string LocalAppData, string AppData, string UserProfile)
{
    public static CleanupFolders Current => new(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
}

/// <summary>One concrete path a target resolved to, and the Revit year of its folder (null: not year-specific).</summary>
public sealed record ResolvedCleanupPath(string Path, int? RevitYear);

internal static class CleanupPathResolver
{
    private const string TokenLocalAppData = "%LocalAppData%";
    private const string TokenAppData      = "%AppData%";
    private const string TokenUserProfile  = "%UserProfile%";
    private const string TokenRevitVersion = CleanupYears.Token;

    /// <summary>
    /// Expand <paramref name="rawPath"/> into 0..N concrete filesystem
    /// paths. Returns an empty list when:
    ///   - the path is empty/whitespace,
    ///   - a {revit-version} token finds zero matching subdirs.
    /// </summary>
    public static IReadOnlyList<string> Resolve(string? rawPath, CleanupFolders? folders = null)
        => ResolveEach(rawPath, folders).Select(p => p.Path).ToArray();

    /// <summary>
    /// <see cref="Resolve"/>, with the Revit year of each fanned-out path (oldest first). A path with no
    /// {revit-version} token resolves to one entry with no year.
    /// </summary>
    public static IReadOnlyList<ResolvedCleanupPath> ResolveEach(string? rawPath, CleanupFolders? folders = null)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return Array.Empty<ResolvedCleanupPath>();

        var withFolders = SubstituteFolderTokens(rawPath!, folders ?? CleanupFolders.Current);
        if (!ContainsRevitVersionToken(withFolders))
        {
            return new[] { new ResolvedCleanupPath(withFolders, null) };
        }
        return ExpandRevitVersion(withFolders);
    }

    /// <summary>
    /// Case-insensitive replacement of the user-folder tokens. Done by
    /// repeated IndexOf so we don't depend on platform-specific
    /// regex behaviour with backslashes inside the path.
    /// </summary>
    private static string SubstituteFolderTokens(string path, CleanupFolders folders)
    {
        path = ReplaceCI(path, TokenLocalAppData, folders.LocalAppData);
        path = ReplaceCI(path, TokenAppData, folders.AppData);
        path = ReplaceCI(path, TokenUserProfile, folders.UserProfile);
        return path;
    }

    private static bool ContainsRevitVersionToken(string path)
        => path.IndexOf(TokenRevitVersion, StringComparison.OrdinalIgnoreCase) >= 0;

    private static IReadOnlyList<ResolvedCleanupPath> ExpandRevitVersion(string path)
    {
        var idx = path.IndexOf(TokenRevitVersion, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return new[] { new ResolvedCleanupPath(path, null) };

        // The directory immediately above the token is the scan parent.
        // Locate the path separator that ends the previous segment.
        var parentEnd = path.LastIndexOfAny(new[] { '\\', '/' }, idx - 1);
        if (parentEnd <= 0)
        {
            Log.Debug("CleanupPathResolver: malformed {revit-version} usage in {Path}", path);
            return Array.Empty<ResolvedCleanupPath>();
        }
        var scanParent = path.Substring(0, parentEnd);
        var afterToken = path.Substring(idx + TokenRevitVersion.Length);

        if (!Directory.Exists(scanParent))
        {
            Log.Debug("CleanupPathResolver: scan parent missing {Parent}", scanParent);
            return Array.Empty<ResolvedCleanupPath>();
        }

        try
        {
            var matches = Directory.GetDirectories(scanParent)
                .Select(Path.GetFileName)
                .Select(n => (Name: n, Year: CleanupYears.YearOfFolder(n)))
                .Where(m => m.Year is not null)
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .Select(m => new ResolvedCleanupPath(scanParent + Path.DirectorySeparatorChar + m.Name + afterToken, m.Year))
                .ToArray();
            return matches;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "CleanupPathResolver: scan failed for {Parent}", scanParent);
            return Array.Empty<ResolvedCleanupPath>();
        }
    }

    private static string ReplaceCI(string haystack, string token, string replacement)
    {
        if (string.IsNullOrEmpty(replacement)) replacement = "";
        var sb = new System.Text.StringBuilder(haystack.Length);
        int i = 0;
        while (i < haystack.Length)
        {
            var match = haystack.IndexOf(token, i, StringComparison.OrdinalIgnoreCase);
            if (match < 0) { sb.Append(haystack, i, haystack.Length - i); break; }
            sb.Append(haystack, i, match - i);
            sb.Append(replacement);
            i = match + token.Length;
        }
        return sb.ToString();
    }
}
