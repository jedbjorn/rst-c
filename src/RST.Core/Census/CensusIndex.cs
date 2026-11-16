// rst-c's own (not vendored): the Ribbon Tool Census index (Feature #22, spec #34, "Index files" and "Loader").
//
// The census (src/RST.Census, tools/census) starts every stock ribbon tool on the test VM, per Revit year and
// context, and publishes the full table in census/<year>/full.tsv. Only its exceptions ship with RST:
// census/<year>/exceptions.tsv, embedded into this assembly from the same repo files (decision #97: no second
// copy; RST.Core.csproj). Each row says that a tool does not run in a context (`not-runnable`: Revit calls it
// clickable but nothing starts) or that a ribbon place Revit models is not drawn (`not-drawn`).
//
// The index never adds and never hides a showing tool (decision #107): the caller lists what its live ribbon walk
// shows, then asks Mark() per place. A year with no file, a context with no rows, or an id the census did not
// see all give None: the live checks alone then decide. Malformed rows are skipped and counted, never thrown,
// because a bad shipped row must not break the window that reads it.
//
// Pure Core: no Revit types. The caller builds the context key from the active document and view (see
// CensusContext) and the place from its scan.

using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;

namespace RST.Core.Census;

/// <summary>What the census index says about one tool at one place in one context.</summary>
public enum CensusMarkKind
{
    /// <summary>No row: the live checks decide.</summary>
    None,
    /// <summary>Revit lets it be clicked here, but nothing starts (or Revit refuses it with no route).</summary>
    NotRunnable,
    /// <summary>Revit models the item at this place but does not draw it there.</summary>
    NotDrawn,
}

/// <summary>A mark with the census's short reason (often the journal line), empty for <see cref="CensusMarkKind.None"/>.</summary>
public readonly record struct CensusMark(CensusMarkKind Kind, string Reason)
{
    public static CensusMark None => new(CensusMarkKind.None, "");
}

/// <summary>
/// A ribbon place as the census records it: the tab's id, the panel's title and the container path inside the panel
/// (empty for an item straight on the panel). <see cref="Path"/> null means "any path": runnability is a property
/// of the id in a context, while not-drawn is a property of the exact place.
/// </summary>
public readonly record struct CensusPlace(string? TabId, string? Panel, string? Path = null);

/// <summary>One parsed row of an exceptions.tsv.</summary>
public sealed record CensusException(string Context, string Id, string TabId, string Panel, string Path, CensusMarkKind Mark, string Reason);

/// <summary>The shipped census exceptions of one Revit year.</summary>
public sealed class CensusIndex
{
    /// <summary>The exceptions.tsv header, in order (spec #34, "Exceptions columns").</summary>
    public static readonly IReadOnlyList<string> Columns = new[] { "context", "id", "tab", "panel", "path", "mark", "reason" };

    /// <summary>Embedded resource names are <c>RST.Core.Census.&lt;year&gt;.exceptions.tsv</c> (RST.Core.csproj).</summary>
    public const string ResourcePrefix = "RST.Core.Census.";
    public const string ResourceSuffix = ".exceptions.tsv";

    private static readonly Regex ResourceName = new(@"^RST\.Core\.Census\.(\d{4})\.exceptions\.tsv$", RegexOptions.CultureInvariant);
    private static readonly ConcurrentDictionary<int, CensusIndex> Cache = new();

    private readonly Dictionary<(string Context, string Id), List<CensusException>> _rows;

    private CensusIndex(int year, List<CensusException> rows, int malformed)
    {
        Year = year;
        Rows = rows;
        MalformedRows = malformed;
        _rows = new Dictionary<(string, string), List<CensusException>>();
        foreach (var r in rows)
        {
            if (!_rows.TryGetValue((r.Context, r.Id), out var list)) _rows[(r.Context, r.Id)] = list = new List<CensusException>();
            list.Add(r);
        }
    }

    public int Year { get; }

    /// <summary>Every well-formed row, in file order.</summary>
    public IReadOnlyList<CensusException> Rows { get; }

    /// <summary>Rows that were skipped: wrong column count, an empty key field, or an unknown mark.</summary>
    public int MalformedRows { get; }

    /// <summary>True when the year has no rows (no file shipped, or an empty one).</summary>
    public bool IsEmpty => Rows.Count == 0;

    /// <summary>An empty index: every <see cref="Mark"/> gives None.</summary>
    public static CensusIndex Empty(int year) => new(year, new List<CensusException>(), 0);

    /// <summary>The years this assembly carries an exceptions list for.</summary>
    public static IReadOnlyList<int> EmbeddedYears =>
        typeof(CensusIndex).Assembly.GetManifestResourceNames()
            .Select(n => ResourceName.Match(n))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .OrderBy(y => y)
            .ToList();

    /// <summary>The embedded resource name for a year.</summary>
    public static string ResourceNameFor(int year) => ResourcePrefix + year.ToString(System.Globalization.CultureInfo.InvariantCulture) + ResourceSuffix;

    /// <summary>
    /// The index for a Revit year, read once from the embedded exceptions list. A year with no list gives an
    /// empty index. Never throws: an unreadable resource is an empty index.
    /// </summary>
    public static CensusIndex For(int year) => Cache.GetOrAdd(year, Load);

    private static CensusIndex Load(int year)
    {
        try
        {
            using var stream = typeof(CensusIndex).Assembly.GetManifestResourceStream(ResourceNameFor(year));
            if (stream is null) return Empty(year);
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return Parse(year, reader.ReadToEnd());
        }
        catch (Exception)
        {
            return Empty(year);
        }
    }

    /// <summary>
    /// Parses an exceptions.tsv. The first line is the header when it starts with "context"; blank lines and lines
    /// starting with '#' are ignored. A row is skipped (and counted) when it has fewer than 6 or more than 7 fields,
    /// an empty context, id or tab, or a mark other than not-runnable / not-drawn.
    /// </summary>
    public static CensusIndex Parse(int year, string? tsv)
    {
        var rows = new List<CensusException>();
        var malformed = 0;
        if (string.IsNullOrEmpty(tsv)) return new CensusIndex(year, rows, 0);
        var lines = tsv.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (i == 0 && line.StartsWith("context\t", StringComparison.Ordinal)) continue;
            var f = line.Split('\t');
            if (f.Length < 6 || f.Length > 7) { malformed++; continue; }
            var mark = ParseMark(f[5]);
            if (mark == CensusMarkKind.None || f[0].Trim().Length == 0 || f[1].Trim().Length == 0 || f[2].Trim().Length == 0)
            {
                malformed++;
                continue;
            }
            rows.Add(new CensusException(f[0].Trim(), f[1].Trim(), f[2].Trim(), f[3].Trim(), f[4].Trim(), mark, f.Length > 6 ? f[6].Trim() : ""));
        }
        return new CensusIndex(year, rows, malformed);
    }

    /// <summary>"not-runnable" or "not-drawn" (ordinal, case-insensitive); anything else is None.</summary>
    public static CensusMarkKind ParseMark(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "not-runnable" => CensusMarkKind.NotRunnable,
        "not-drawn" => CensusMarkKind.NotDrawn,
        _ => CensusMarkKind.None,
    };

    /// <summary>
    /// What the index says about <paramref name="id"/> at <paramref name="place"/> in <paramref name="context"/>.
    /// <list type="bullet">
    /// <item>NotDrawn when a not-drawn row has the same tab and panel and, if the caller gives one, the same path.</item>
    /// <item>Else NotRunnable when any not-runnable row has the id in this context: whether a tool starts does not depend
    /// on which of its places is clicked.</item>
    /// <item>Else None (also for an unknown context, an unknown id, or empty arguments).</item>
    /// </list>
    /// </summary>
    public CensusMark Mark(string? context, string? id, CensusPlace place)
    {
        if (string.IsNullOrEmpty(context) || string.IsNullOrEmpty(id)) return CensusMark.None;
        if (!_rows.TryGetValue((context, id), out var rows)) return CensusMark.None;
        foreach (var r in rows)
        {
            if (r.Mark != CensusMarkKind.NotDrawn) continue;
            if (!string.Equals(r.TabId, place.TabId, StringComparison.Ordinal)) continue;
            if (!string.Equals(r.Panel, place.Panel ?? "", StringComparison.Ordinal)) continue;
            if (place.Path is not null && !string.Equals(r.Path, place.Path, StringComparison.Ordinal)) continue;
            return new CensusMark(CensusMarkKind.NotDrawn, r.Reason);
        }
        foreach (var r in rows)
            if (r.Mark == CensusMarkKind.NotRunnable) return new CensusMark(CensusMarkKind.NotRunnable, r.Reason);
        return CensusMark.None;
    }

    /// <summary>The distinct context keys that have rows.</summary>
    public IReadOnlyCollection<string> Contexts => Rows.Select(r => r.Context).Distinct(StringComparer.Ordinal).ToList();
}
