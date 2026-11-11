// rst-c's own (not vendored): Search-It's search (v3.4): chips filter exactly, the text ranks fuzzily.

namespace RST.Core.Search;

/// <summary>One result row: the tool, and how the typed text matched it (null when only chips, or nothing, were given).</summary>
public sealed record SearchHit(SearchTool Tool, ToolMatch? Match);

/// <summary>
/// Searches a fixed set of tools by name. Build once per window; <see cref="Search"/> per keystroke.
/// </summary>
public sealed class ToolSearch
{
    private readonly SearchTool[] _tools;
    private readonly string[] _names;

    public ToolSearch(IEnumerable<SearchTool>? tools)
    {
        _tools = (tools ?? []).Where(t => t is not null).ToArray();
        _names = _tools.Select(t => ToolMatcher.Normalize(t.DisplayName)).ToArray();
    }

    /// <summary>How many tools can be searched.</summary>
    public int Count => _tools.Length;

    /// <summary>
    /// The tools whose name holds every chip (an exact, case-insensitive substring; chips are ANDed)
    /// and, when <paramref name="text"/> is not blank, matches it (<see cref="ToolMatcher"/>). With
    /// text, the order is exact, prefix, word start, substring, fuzzy, then by name; without text it
    /// is by name.
    /// </summary>
    public IReadOnlyList<SearchHit> Search(IEnumerable<string>? chips, string? text)
    {
        var filters = SearchChips.Normalize(chips);
        var query = ToolMatcher.Normalize(text);
        var hits = new List<(SearchHit Hit, int Index)>();
        for (var i = 0; i < _tools.Length; i++)
        {
            var name = _names[i];
            if (!HoldsAll(name, filters)) continue;
            if (query.Length == 0)
            {
                hits.Add((new SearchHit(_tools[i], null), i));
            }
            else if (ToolMatcher.TryMatch(query, name, out var match))
            {
                hits.Add((new SearchHit(_tools[i], match), i));
            }
        }
        hits.Sort((a, b) =>
        {
            var byMatch = Nullable.Compare(a.Hit.Match, b.Hit.Match);
            if (byMatch != 0) return byMatch;
            var byLength = query.Length == 0 ? 0 : _names[a.Index].Length.CompareTo(_names[b.Index].Length);
            if (byLength != 0) return byLength;
            var byName = string.CompareOrdinal(_names[a.Index], _names[b.Index]);
            if (byName != 0) return byName;
            var byPlace = string.Compare(a.Hit.Tool.Location, b.Hit.Tool.Location, StringComparison.OrdinalIgnoreCase);
            return byPlace != 0 ? byPlace : a.Index.CompareTo(b.Index);
        });
        return hits.ConvertAll(h => h.Hit);
    }

    private static bool HoldsAll(string name, IReadOnlyList<string> filters)
    {
        foreach (var filter in filters)
            if (!name.Contains(filter, StringComparison.Ordinal)) return false;
        return true;
    }
}

/// <summary>The chip rules: what Enter turns the typed text into, and what a chip filters on.</summary>
public static class SearchChips
{
    /// <summary>The chip a typed text makes: trimmed, inner spaces folded, case kept for display. Empty when blank.</summary>
    public static string Label(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "" : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// True when Enter on <paramref name="text"/> adds a chip: it is not blank and no chip already says
    /// the same (ignoring case). <paramref name="label"/> is the chip's text either way.
    /// </summary>
    public static bool CanAdd(IEnumerable<string>? existing, string? text, out string label)
    {
        label = Label(text);
        if (label.Length == 0) return false;
        var key = ToolMatcher.Normalize(label);
        foreach (var chip in existing ?? [])
            if (ToolMatcher.Normalize(chip) == key) return false;
        return true;
    }

    /// <summary>The chips as filters: normalized, blanks and repeats dropped.</summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string>? chips)
    {
        var result = new List<string>();
        foreach (var chip in chips ?? [])
        {
            var key = ToolMatcher.Normalize(chip);
            if (key.Length > 0 && !result.Contains(key)) result.Add(key);
        }
        return result;
    }
}
