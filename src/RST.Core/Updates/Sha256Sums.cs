// rst-c's own (not vendored): a release's SHA256SUMS.txt (spec #20 §5, §8; F17 WU3), the format `sha256sum`
// writes and the sister repos publish:
//
//   06f403468ae484526dc7e8ddc9251bf2b34715ddcb14d668049b63c74d1eb18b  GanttIt-1.1.0.msi
//
// Exactly the form build/sha256sums.sh writes (`sha256sum` text mode, as the sister repos publish too): 64
// LOWER-CASE hex digits, TWO spaces, a bare file name with no path separator and no control character. Strict and
// fail closed, because WU4 installs only what this file vouches for: any line that is not that shape (upper-case
// hex and sha256sum's ` *` binary marker included), a name with a path in it, or a name listed twice makes the
// whole file unreadable. Blank lines and CRLF endings are allowed. MaxLength counts characters: WU4 caps the
// download in bytes before decoding it.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RST.Core.Updates;

public sealed class Sha256Sums
{
    /// <summary>A file larger than this is refused unread (a real one is a few hundred bytes).</summary>
    public const int MaxLength = 64 * 1024;

    private static readonly Regex LinePattern = new(@"^([0-9a-f]{64})  ([^\s/\\\p{Cc}][^/\\\p{Cc}]*)\z",
        RegexOptions.CultureInvariant);

    private readonly Dictionary<string, string> _byName;

    private Sha256Sums(Dictionary<string, string> byName) => _byName = byName;

    /// <summary>The file names listed, in no particular order.</summary>
    public IReadOnlyCollection<string> Names => _byName.Keys;

    public int Count => _byName.Count;

    /// <summary>
    /// Parses <paramref name="text"/>. Null, with <paramref name="error"/> saying why, when the file is empty,
    /// too large, or has any malformed line or repeated name.
    /// </summary>
    public static Sha256Sums? TryParse(string? text, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(text))
        {
            error = "empty";
            return null;
        }
        if (text.Length > MaxLength)
        {
            error = $"larger than {MaxLength} characters";
            return null;
        }

        var byName = new Dictionary<string, string>(StringComparer.Ordinal);
        var lineNumber = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNumber++;
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;
            if (line.Length == 0) continue;
            var m = LinePattern.Match(line);
            if (!m.Success)
            {
                error = string.Create(CultureInfo.InvariantCulture, $"line {lineNumber} is not '<sha256>  <file name>'");
                return null;
            }
            var name = m.Groups[2].Value;
            if (name.TrimEnd() != name)
            {
                error = string.Create(CultureInfo.InvariantCulture, $"line {lineNumber}: the file name ends in a space");
                return null;
            }
            // Names compare exactly; two names that differ only by case would both match one Windows file.
            foreach (var existing in byName.Keys)
            {
                if (string.Equals(existing, name, StringComparison.OrdinalIgnoreCase))
                {
                    error = string.Create(CultureInfo.InvariantCulture, $"line {lineNumber}: {name} is listed twice");
                    return null;
                }
            }
            byName[name] = m.Groups[1].Value.ToLowerInvariant();
        }

        if (byName.Count == 0)
        {
            error = "no entries";
            return null;
        }
        return new Sha256Sums(byName);
    }

    /// <summary>The lowercase hex SHA256 listed for <paramref name="fileName"/> (exact name), or null when it is not listed.</summary>
    public string? HashOf(string fileName) =>
        fileName is not null && _byName.TryGetValue(fileName, out var hash) ? hash : null;
}
