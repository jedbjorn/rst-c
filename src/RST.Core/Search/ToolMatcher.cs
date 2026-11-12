// rst-c's own (not vendored): how well a typed text matches a tool's name (Search-It, v3.4).
//
// Tiers, best first: the whole name, the start of the name, the start of a word in it, anywhere in
// it, then fuzzy. Fuzzy tolerates skipped letters ("algn" finds Align: the typed letters appear in
// order, starting at a word) and typos ("aling", "allign", "wsll": a small edit distance, with a swap
// of two neighbours counted once, against the start of the name or of any word in it). Everything is
// case-insensitive (invariant lower case, so accented and non-Latin letters match themselves in
// either case) and ignores extra spaces. The names are normalized once, when the search is built;
// a keystroke normalizes the typed text and reuses three per-thread buffers for the edit distance,
// so matching itself allocates nothing per name.

namespace RST.Core.Search;

/// <summary>How a text matched a name; lower is better.</summary>
public enum MatchKind
{
    Exact = 0,
    Prefix = 1,
    WordStart = 2,
    Substring = 3,
    Fuzzy = 4,
}

/// <summary>A match: its tier, and a penalty that orders matches inside the tier (lower is better).</summary>
public readonly record struct ToolMatch(MatchKind Kind, int Penalty) : IComparable<ToolMatch>
{
    public int CompareTo(ToolMatch other) =>
        Kind != other.Kind ? Kind.CompareTo(other.Kind) : Penalty.CompareTo(other.Penalty);
}

public static class ToolMatcher
{
    /// <summary>Lower-case, trimmed, with runs of white space folded to one space.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var buffer = new char[text.Length];
        var n = 0;
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = n > 0;
                continue;
            }
            if (space) buffer[n++] = ' ';
            space = false;
            buffer[n++] = char.ToLowerInvariant(c);
        }
        return new string(buffer, 0, n);
    }

    /// <summary>
    /// Matches <paramref name="query"/> against <paramref name="name"/>; both must already be
    /// <see cref="Normalize"/>d. An empty query matches nothing.
    /// </summary>
    public static bool TryMatch(string query, string name, out ToolMatch match)
    {
        match = default;
        if (query.Length == 0 || name.Length == 0) return false;

        var at = name.IndexOf(query, StringComparison.Ordinal);
        if (at == 0)
        {
            match = name.Length == query.Length
                ? new ToolMatch(MatchKind.Exact, 0)
                : new ToolMatch(MatchKind.Prefix, name.Length - query.Length);
            return true;
        }
        if (at > 0)
        {
            // The earliest occurrence at a word start, if there is one.
            for (var i = at; i >= 0; i = name.IndexOf(query, i + 1, StringComparison.Ordinal))
            {
                if (!IsWordStart(name, i)) continue;
                match = new ToolMatch(MatchKind.WordStart, i);
                return true;
            }
            match = new ToolMatch(MatchKind.Substring, at);
            return true;
        }

        var penalty = Math.Min(SkippedLetters(query, name), Typos(query, name));
        if (penalty == NoMatch) return false;
        match = new ToolMatch(MatchKind.Fuzzy, penalty);
        return true;
    }

    private const int NoMatch = int.MaxValue;

    private static bool IsWordStart(string name, int index) =>
        char.IsLetterOrDigit(name[index]) && (index == 0 || !char.IsLetterOrDigit(name[index - 1]));

    /// <summary>
    /// The typed letters in order inside the name, starting at a word ("algn" in "align"). The cost is
    /// the letters skipped; a jump to the start of a later word costs one ("cw" for "curtain wall").
    /// Allowed up to about half the typed length, so a short text cannot match almost anything.
    /// </summary>
    private static int SkippedLetters(string query, string name)
    {
        var letters = 0;
        foreach (var c in query) if (c != ' ') letters++;
        if (letters < 2) return NoMatch;
        var allowed = Math.Max(1, letters / 2);

        var best = NoMatch;
        for (var start = 0; start < name.Length; start++)
        {
            if (!IsWordStart(name, start)) continue;
            var cost = 0;
            var pos = start;
            var ok = true;
            var firstLetter = true;
            foreach (var c in query)
            {
                if (c == ' ') continue;
                if (firstLetter)
                {
                    if (name[start] != c) { ok = false; break; }
                    firstLetter = false;
                    pos = start + 1;
                    continue;
                }
                var found = name.IndexOf(c, pos);
                if (found < 0) { ok = false; break; }
                // Prefer the start of a later word when the letter is there too and the plain hit is far.
                var skipped = found - pos;
                if (skipped > 1)
                {
                    for (var w = pos; w < name.Length; w++)
                    {
                        if (name[w] != c || !IsWordStart(name, w)) continue;
                        found = w;
                        skipped = 1;
                        break;
                    }
                }
                cost += skipped;
                if (cost > allowed) { ok = false; break; }
                pos = found + 1;
            }
            if (ok && cost < best) best = cost;
        }
        // Rank below a one-typo match of the same size: skipped letters start at 1.
        return best == NoMatch ? NoMatch : Math.Max(1, best);
    }

    /// <summary>
    /// The smallest edit distance (insert, delete, replace, or swap two neighbours) between the query
    /// and the start of the name or of any word in it. One edit is allowed from 3 letters, two from 6.
    /// The first letter must be right or swapped with the second: that keeps the results sensible.
    /// </summary>
    private static int Typos(string query, string name)
    {
        var allowed = query.Length >= 6 ? 2 : query.Length >= 3 ? 1 : 0;
        if (allowed == 0) return NoMatch;

        var best = NoMatch;
        for (var start = 0; start < name.Length; start++)
        {
            if (!IsWordStart(name, start)) continue;
            var c0 = name[start];
            if (c0 != query[0] && !(query.Length > 1 && c0 == query[1])) continue;
            var d = PrefixDistance(query, name, start, allowed);
            if (d < best) best = d;
            if (best == 1) break;
        }
        // Two points per edit, so one typo (2) ranks after one skipped letter (1) and before many.
        return best == NoMatch ? NoMatch : best * 2;
    }

    [ThreadStatic] private static int[]? _rowA;
    [ThreadStatic] private static int[]? _rowB;
    [ThreadStatic] private static int[]? _rowC;

    private static int[] Buffer(ref int[]? buffer, int length)
    {
        if (buffer is null || buffer.Length < length) buffer = new int[Math.Max(length, 64)];
        return buffer;
    }

    /// <summary>
    /// The optimal-string-alignment distance between <paramref name="query"/> and the best prefix of
    /// <paramref name="name"/> from <paramref name="start"/>; <see cref="NoMatch"/> when above
    /// <paramref name="allowed"/>.
    /// </summary>
    private static int PrefixDistance(string query, string name, int start, int allowed)
    {
        var m = query.Length;
        var n = Math.Min(name.Length - start, m + allowed);
        if (n < m - allowed) return NoMatch;

        // Rows are prefixes of the name; columns are prefixes of the query.
        var prev2 = Buffer(ref _rowA, m + 1);
        var prev = Buffer(ref _rowB, m + 1);
        var row = Buffer(ref _rowC, m + 1);
        for (var j = 0; j <= m; j++) prev[j] = j;
        var best = prev[m] <= allowed ? prev[m] : NoMatch; // the empty prefix

        for (var i = 1; i <= n; i++)
        {
            var c = name[start + i - 1];
            row[0] = i;
            for (var j = 1; j <= m; j++)
            {
                var cost = c == query[j - 1] ? 0 : 1;
                var value = Math.Min(Math.Min(prev[j] + 1, row[j - 1] + 1), prev[j - 1] + cost);
                if (i > 1 && j > 1 && c == query[j - 2] && name[start + i - 2] == query[j - 1])
                    value = Math.Min(value, prev2[j - 2] + 1);
                row[j] = value;
            }
            if (row[m] <= allowed && row[m] < best) best = row[m];
            (prev2, prev, row) = (prev, row, prev2);
        }
        return best;
    }
}
