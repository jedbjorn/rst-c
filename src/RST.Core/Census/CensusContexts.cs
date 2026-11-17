// rst-c's own (not vendored): the census context keys of a Revit year, and the key for the open file and view
// (Feature #21, spec #35, "Context Key").
//
// census/<year>/contexts.tsv lists every context the census ran, in order. Each RST build embeds it gzipped from the
// committed file (RST.Core.csproj; RST.Core.Census.<year>.contexts.tsv.gz). Search-It builds the key of the file and
// view the user has open from plain values the Engine reads (family or project, the family's BuiltInCategory and
// FamilyPlacementType, the active view's ViewType) and resolves it against this list:
//
//   Project (.rvt, or a .rte opened directly)   project/<ViewType>
//   Family (.rfa, or new from an .rft)          family/<BuiltInCategory>/<FamilyPlacementType>/<ViewType>
//
// Project views the census did not run use the nearest one it did (ProjectFallback). A family view the census did not
// run uses the family's first context, in file order, with the same category and placement. Anything else has no
// key: the census then removes, badges and labels nothing (spec #35). Never throws.

using System.Collections.Concurrent;

namespace RST.Core.Census;

/// <summary>One row of contexts.tsv: the key and the plain values it is made of.</summary>
public sealed record CensusContextRow(string Context, string Kind, string Category, string Placement, string ViewType)
{
    public bool IsFamily => Kind == CensusContext.FamilyKind;
}

/// <summary>What the Engine reads from the active document and view, as plain values.</summary>
/// <param name="IsFamily">The active document is a family (a project template is a project).</param>
/// <param name="Category">The family's BuiltInCategory name ("OST_GenericModel"); unused for a project.</param>
/// <param name="Placement">The family's FamilyPlacementType name ("OneLevelBased"); unused for a project.</param>
/// <param name="ViewType">The active view's ViewType name ("FloorPlan").</param>
public readonly record struct CensusDocument(bool IsFamily, string? Category, string? Placement, string? ViewType);

/// <summary>The census context the open file and view resolve to.</summary>
/// <param name="Key">The context key, as in the census tables.</param>
/// <param name="Asked">The key made from the open file and view; differs from <see cref="Key"/> when a fallback was used.</param>
public sealed record CensusContextKey(string Key, string Asked)
{
    /// <summary>The key came from a fallback (the project fallback map, or a family's first context).</summary>
    public bool IsFallback => !string.Equals(Key, Asked, StringComparison.Ordinal);

    public bool IsFamily => Key.StartsWith(CensusContext.FamilyKind + "/", StringComparison.Ordinal);

    /// <summary>The key's view part ("FloorPlan").</summary>
    public string ViewType => CensusContext.ViewPart(Key);

    /// <summary>"project/FloorPlan", or "project/FloorPlan (from AreaPlan)" after a fallback.</summary>
    public override string ToString() => IsFallback ? $"{Key} (from {CensusContext.ViewPart(Asked)})" : Key;
}

/// <summary>The context keys of one Revit year's census, in contexts.tsv order.</summary>
public sealed class CensusContexts
{
    /// <summary>contexts.tsv's header, in order.</summary>
    public static readonly IReadOnlyList<string> Columns = new[] { "context", "kind", "category", "placement", "view_type", "view", "templates" };

    /// <summary>
    /// Project views the census did not run, and the view type whose census rows they use (spec #35, "Fallback").
    /// Any other view type the census did not run has no key.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ProjectFallback = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["AreaPlan"] = "FloorPlan",
        ["EngineeringPlan"] = "FloorPlan",
        ["Detail"] = "Section",
        ["Walkthrough"] = "ThreeD",
        ["PanelSchedule"] = "Schedule",
        ["ColumnSchedule"] = "Schedule",
    };

    private static readonly ConcurrentDictionary<int, CensusContexts> Cache = new();

    private readonly HashSet<string> _keys;

    private CensusContexts(int year, List<CensusContextRow> rows)
    {
        Year = year;
        Rows = rows;
        _keys = new HashSet<string>(rows.Select(r => r.Context), StringComparer.Ordinal);
    }

    public int Year { get; }

    /// <summary>Every context, in file order.</summary>
    public IReadOnlyList<CensusContextRow> Rows { get; }

    public bool IsEmpty => Rows.Count == 0;

    public static CensusContexts Empty(int year) => new(year, new List<CensusContextRow>());

    public bool Contains(string? key) => key is not null && _keys.Contains(key);

    /// <summary>The embedded resource name for a year (gzipped; the name ends in ".gz").</summary>
    public static string ResourceNameFor(int year) =>
        CensusIndex.ResourcePrefix + year.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".contexts.tsv.gz";

    /// <summary>The year's contexts.tsv as embedded, decompressed; null if none.</summary>
    public static Stream? OpenResource(int year)
    {
        var gz = typeof(CensusContexts).Assembly.GetManifestResourceStream(ResourceNameFor(year));
        return gz is null ? null : new System.IO.Compression.GZipStream(gz, System.IO.Compression.CompressionMode.Decompress);
    }

    /// <summary>The contexts of a Revit year, read once from the embedded resource. Never throws: unreadable is empty.</summary>
    public static CensusContexts For(int year) => Cache.GetOrAdd(year, Load);

    private static CensusContexts Load(int year)
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

    /// <summary>Parses a contexts.tsv. The header, blank lines and rows without a context, kind or view type are skipped.</summary>
    public static CensusContexts Parse(int year, string? tsv)
    {
        var rows = new List<CensusContextRow>();
        if (string.IsNullOrEmpty(tsv)) return new CensusContexts(year, rows);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in tsv.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith("context\t", StringComparison.Ordinal)) continue;
            var f = line.Split('\t');
            if (f.Length < 5) continue;
            var (context, kind, viewType) = (f[0].Trim(), f[1].Trim(), f[4].Trim());
            if (context.Length == 0 || kind.Length == 0 || viewType.Length == 0 || !seen.Add(context)) continue;
            rows.Add(new CensusContextRow(context, kind, f[2].Trim(), f[3].Trim(), viewType));
        }
        return new CensusContexts(year, rows);
    }

    /// <summary>
    /// The census context of the open file and view, or null when the census has none for it:
    /// <list type="bullet">
    /// <item>Project: <c>project/&lt;ViewType&gt;</c> when the census ran that view type, else the
    /// <see cref="ProjectFallback"/> view type's key, else null.</item>
    /// <item>Family: the exact key when the census ran it, else the first context in file order with the same category
    /// and placement, else null.</item>
    /// </list>
    /// </summary>
    public CensusContextKey? Resolve(CensusDocument document)
    {
        var view = (document.ViewType ?? "").Trim();
        if (view.Length == 0) return null;
        if (!document.IsFamily)
        {
            var asked = CensusContext.Project(view);
            if (Contains(asked)) return new CensusContextKey(asked, asked);
            if (ProjectFallback.TryGetValue(view, out var mapped) && CensusContext.Project(mapped) is var key && Contains(key))
                return new CensusContextKey(key, asked);
            return null;
        }
        var category = (document.Category ?? "").Trim();
        var placement = (document.Placement ?? "").Trim();
        if (category.Length == 0 || placement.Length == 0) return null;
        var familyAsked = CensusContext.Family(category, placement, view);
        if (Contains(familyAsked)) return new CensusContextKey(familyAsked, familyAsked);
        var first = Rows.FirstOrDefault(r => r.IsFamily
            && string.Equals(r.Category, category, StringComparison.Ordinal)
            && string.Equals(r.Placement, placement, StringComparison.Ordinal));
        return first is null ? null : new CensusContextKey(first.Context, familyAsked);
    }

    /// <summary>
    /// The contexts of the same file kind as <paramref name="key"/>: every project context for a project, the contexts
    /// with the same category and placement for a family (spec #35, "Which views count"). The key itself included.
    /// </summary>
    public IEnumerable<CensusContextRow> SameFile(CensusContextKey key)
    {
        var row = Rows.FirstOrDefault(r => r.Context == key.Key);
        if (row is null) yield break;
        foreach (var r in Rows)
        {
            if (r.Kind != row.Kind) continue;
            if (row.IsFamily && (r.Category != row.Category || r.Placement != row.Placement)) continue;
            yield return r;
        }
    }
}
