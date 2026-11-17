// rst-c's own (not vendored): where each tool runs, per census context (Feature #21, spec #35, "Runs-in resource").
//
// The census's full table (census/<year>/full.tsv) records, per context, every ribbon place of every command id and
// what happened when it was started. Only its exceptions ship as CensusIndex. Search-It also needs to know where a
// tool that can't run here does run, to badge it "Must Run In <views>" (decisions #119, #120), so each RST build
// derives one more resource from the same committed file (RST.Core.csproj, RstCensusRunsIn): the distinct
// (context, id) pairs whose result is `executes`, gzipped and embedded as RST.Core.Census.<year>.runs-in.tsv.gz.
// No copy is committed (decision #97); the census files are not changed.
//
// Format: a "context<TAB>id" header, then one pair per line, sorted ordinally by context and then id, "\n" line
// ends. A year with no resource, or one that can't be read, is an empty list: Search-It then removes and badges
// nothing on its account (spec #35, "No key, or no index for the year"). Never throws.

using System.Collections.Concurrent;

namespace RST.Core.Census;

/// <summary>The (context, id) pairs of one Revit year where the census saw the tool start.</summary>
public sealed class CensusRunsIn
{
    /// <summary>The resource's header line.</summary>
    public const string Header = "context\tid";

    private static readonly ConcurrentDictionary<int, CensusRunsIn> Cache = new();
    private static readonly IReadOnlyList<string> None = Array.Empty<string>();

    private readonly Dictionary<string, List<string>> _contextsById;

    private CensusRunsIn(int year, Dictionary<string, List<string>> contextsById, int pairs)
    {
        Year = year;
        _contextsById = contextsById;
        PairCount = pairs;
    }

    public int Year { get; }

    /// <summary>How many distinct pairs the list holds.</summary>
    public int PairCount { get; }

    /// <summary>True when the year has no pairs (no resource shipped, or an empty one).</summary>
    public bool IsEmpty => PairCount == 0;

    /// <summary>An empty list: <see cref="Runs"/> is false everywhere.</summary>
    public static CensusRunsIn Empty(int year) => new(year, new Dictionary<string, List<string>>(StringComparer.Ordinal), 0);

    /// <summary>The embedded resource name for a year (gzipped; the name ends in ".gz").</summary>
    public static string ResourceNameFor(int year) =>
        CensusIndex.ResourcePrefix + year.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".runs-in.tsv.gz";

    /// <summary>The year's runs-in list as embedded, decompressed; null if none.</summary>
    public static Stream? OpenResource(int year)
    {
        var gz = typeof(CensusRunsIn).Assembly.GetManifestResourceStream(ResourceNameFor(year));
        return gz is null ? null : new System.IO.Compression.GZipStream(gz, System.IO.Compression.CompressionMode.Decompress);
    }

    /// <summary>The list for a Revit year, read once from the embedded resource. Never throws: an unreadable one is empty.</summary>
    public static CensusRunsIn For(int year) => Cache.GetOrAdd(year, Load);

    private static CensusRunsIn Load(int year)
    {
        try
        {
            using var stream = OpenResource(year);
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
    /// Parses the resource's text. The header, blank lines and lines without exactly two non-empty fields are
    /// skipped. Repeated pairs count once.
    /// </summary>
    public static CensusRunsIn Parse(int year, string? tsv)
    {
        var byId = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var pairs = 0;
        if (string.IsNullOrEmpty(tsv)) return new CensusRunsIn(year, byId, 0);
        foreach (var raw in tsv.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line == Header) continue;
            var tab = line.IndexOf('\t');
            if (tab <= 0 || tab == line.Length - 1 || line.IndexOf('\t', tab + 1) >= 0) continue;
            var context = line[..tab];
            var id = line[(tab + 1)..];
            if (!byId.TryGetValue(id, out var contexts)) byId[id] = contexts = new List<string>();
            if (contexts.Contains(context)) continue;
            contexts.Add(context);
            pairs++;
        }
        return new CensusRunsIn(year, byId, pairs);
    }

    /// <summary>True when the census saw <paramref name="id"/> start in <paramref name="context"/>.</summary>
    public bool Runs(string? context, string? id) =>
        !string.IsNullOrEmpty(context) && !string.IsNullOrEmpty(id)
        && _contextsById.TryGetValue(id!, out var contexts) && contexts.Contains(context!);

    /// <summary>Every context where the census saw <paramref name="id"/> start; empty for an id it never saw start.</summary>
    public IReadOnlyList<string> ContextsOf(string? id) =>
        !string.IsNullOrEmpty(id) && _contextsById.TryGetValue(id!, out var contexts) ? contexts : None;
}
