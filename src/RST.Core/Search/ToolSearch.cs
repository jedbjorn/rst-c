// rst-c's own (not vendored): Search-It's search (v3.4): chips filter exactly, the text ranks fuzzily.

namespace RST.Core.Search;

/// <summary>
/// One result row: the tool, the place to show (the one whose name matched best; the first when
/// nothing was typed) and how the typed text matched it (null when only chips, or nothing, were given).
/// </summary>
public sealed record SearchHit(SearchTool Tool, SearchPlace Place, ToolMatch? Match)
{
    /// <summary>The name the row shows: the label that matched.</summary>
    public string Name => Place.Label;
}

/// <summary>
/// Searches a fixed set of tools by name. Build once per window; <see cref="Search"/> per keystroke.
/// </summary>
public sealed class ToolSearch
{
    /// <summary>One distinct name of a tool, normalized, and the first place that carries it.</summary>
    private readonly record struct Label(string Name, int Place);

    private readonly SearchTool[] _tools;
    private readonly Label[][] _labels;

    public ToolSearch(IEnumerable<SearchTool>? tools)
    {
        _tools = (tools ?? []).Where(t => t is not null).ToArray();
        _labels = _tools.Select(Labels).ToArray();
    }

    private static Label[] Labels(SearchTool tool)
    {
        var labels = new List<Label>(tool.Places.Count);
        for (var i = 0; i < tool.Places.Count; i++)
        {
            var name = ToolMatcher.Normalize(tool.Places[i].Label);
            if (!labels.Exists(l => l.Name == name)) labels.Add(new Label(name, i));
        }
        return labels.ToArray();
    }

    /// <summary>How many tools can be searched.</summary>
    public int Count => _tools.Length;

    /// <summary>
    /// The tools with a name that holds every chip (an exact, case-insensitive substring; chips are
    /// ANDed) and, when <paramref name="text"/> is not blank, matches it (<see cref="ToolMatcher"/>).
    /// A tool the ribbon names differently in different places is tried under each name and shown
    /// once, under the one that matched best. With text, the order is exact, prefix, word start,
    /// substring, fuzzy, then by name; without text it is by name.
    /// </summary>
    public IReadOnlyList<SearchHit> Search(IEnumerable<string>? chips, string? text)
    {
        var filters = SearchChips.Normalize(chips);
        var query = ToolMatcher.Normalize(text);
        var hits = new List<(SearchHit Hit, string Name, int Index)>();
        for (var i = 0; i < _tools.Length; i++)
        {
            Label? best = null;
            ToolMatch? bestMatch = null;
            foreach (var label in _labels[i])
            {
                if (!HoldsAll(label.Name, filters)) continue;
                if (query.Length == 0)
                {
                    best = label; // the first name the chips keep
                    break;
                }
                if (!ToolMatcher.TryMatch(query, label.Name, out var match)) continue;
                if (bestMatch is { } current && match.CompareTo(current) >= 0) continue;
                best = label;
                bestMatch = match;
            }
            if (best is not { } shown) continue;
            hits.Add((new SearchHit(_tools[i], _tools[i].Places[shown.Place], bestMatch), shown.Name, i));
        }
        hits.Sort((a, b) =>
        {
            var byMatch = Nullable.Compare(a.Hit.Match, b.Hit.Match);
            if (byMatch != 0) return byMatch;
            var byLength = query.Length == 0 ? 0 : a.Name.Length.CompareTo(b.Name.Length);
            if (byLength != 0) return byLength;
            var byName = string.CompareOrdinal(a.Name, b.Name);
            if (byName != 0) return byName;
            var byPlace = string.Compare(a.Hit.Place.Location, b.Hit.Place.Location, StringComparison.OrdinalIgnoreCase);
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
