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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Serilog;

namespace RST.UI.Health;

internal static class CleanupPathResolver
{
    private const string TokenLocalAppData = "%LocalAppData%";
    private const string TokenAppData      = "%AppData%";
    private const string TokenUserProfile  = "%UserProfile%";
    private const string TokenRevitVersion = "{revit-version}";

    private static readonly Regex RevitVersionDirRe = new(
        @"^Autodesk Revit \d{4}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Expand <paramref name="rawPath"/> into 0..N concrete filesystem
    /// paths. Returns an empty list when:
    ///   - the path is empty/whitespace,
    ///   - a {revit-version} token finds zero matching subdirs.
    /// </summary>
    public static IReadOnlyList<string> Resolve(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return Array.Empty<string>();

        var withFolders = SubstituteFolderTokens(rawPath!);
        if (!ContainsRevitVersionToken(withFolders))
        {
            return new[] { withFolders };
        }
        return ExpandRevitVersion(withFolders);
    }

    /// <summary>
    /// Case-insensitive replacement of the user-folder tokens. Done by
    /// repeated IndexOf so we don't depend on platform-specific
    /// regex behaviour with backslashes inside the path.
    /// </summary>
    private static string SubstituteFolderTokens(string path)
    {
        path = ReplaceCI(path, TokenLocalAppData,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        path = ReplaceCI(path, TokenAppData,
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        path = ReplaceCI(path, TokenUserProfile,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        return path;
    }

    private static bool ContainsRevitVersionToken(string path)
        => path.IndexOf(TokenRevitVersion, StringComparison.OrdinalIgnoreCase) >= 0;

    private static IReadOnlyList<string> ExpandRevitVersion(string path)
    {
        var idx = path.IndexOf(TokenRevitVersion, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return new[] { path };

        // The directory immediately above the token is the scan parent.
        // Locate the path separator that ends the previous segment.
        var parentEnd = path.LastIndexOfAny(new[] { '\\', '/' }, idx - 1);
        if (parentEnd <= 0)
        {
            Log.Debug("CleanupPathResolver: malformed {revit-version} usage in {Path}", path);
            return Array.Empty<string>();
        }
        var scanParent = path.Substring(0, parentEnd);
        var afterToken = path.Substring(idx + TokenRevitVersion.Length);

        if (!Directory.Exists(scanParent))
        {
            Log.Debug("CleanupPathResolver: scan parent missing {Parent}", scanParent);
            return Array.Empty<string>();
        }

        try
        {
            var matches = Directory.GetDirectories(scanParent)
                .Select(Path.GetFileName)
                .Where(n => n is not null && RevitVersionDirRe.IsMatch(n!))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Select(name => scanParent + Path.DirectorySeparatorChar + name + afterToken)
                .ToArray();
            return matches;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "CleanupPathResolver: scan failed for {Parent}", scanParent);
            return Array.Empty<string>();
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
