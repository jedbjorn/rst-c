// rst-c's own (not vendored): Search-It's matcher (v3.4): the tiers, typo tolerance and skipped letters.

using FluentAssertions;
using RST.Core.Search;
using Xunit;

namespace RST.Tests.Search;

public class ToolMatcherTests
{
    private static ToolMatch? Match(string query, string name) =>
        ToolMatcher.TryMatch(ToolMatcher.Normalize(query), ToolMatcher.Normalize(name), out var m) ? m : null;

    [Theory]
    [InlineData("  Align  ", "align")]
    [InlineData("Wall:   Architectural", "wall: architectural")]
    [InlineData("\tA B\n", "a b")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void Normalize_lowers_trims_and_folds_spaces(string? text, string expected) =>
        ToolMatcher.Normalize(text).Should().Be(expected);

    [Theory]
    [InlineData("align", "Align", MatchKind.Exact)]
    [InlineData("ALIGN", "align", MatchKind.Exact)]
    [InlineData("ali", "Align", MatchKind.Prefix)]
    [InlineData("wall", "Wall: Architectural", MatchKind.Prefix)]
    [InlineData("arch", "Wall: Architectural", MatchKind.WordStart)]
    [InlineData("dim", "Aligned Dimension", MatchKind.WordStart)]
    [InlineData("wall", "Curtain Wall", MatchKind.WordStart)]
    [InlineData("lig", "Align", MatchKind.Substring)]
    [InlineData("mension", "Aligned Dimension", MatchKind.Substring)]
    [InlineData("algn", "Align", MatchKind.Fuzzy)]
    public void Each_tier_is_recognised(string query, string name, MatchKind kind) =>
        Match(query, name)!.Value.Kind.Should().Be(kind);

    [Fact]
    public void A_word_start_later_in_the_name_beats_an_earlier_mid_word_hit()
    {
        // "all" is inside "wall" (index 1) and starts "all" (index 5): the word start wins.
        var match = Match("all", "Wall All Views")!.Value;
        match.Kind.Should().Be(MatchKind.WordStart);
        match.Penalty.Should().Be(5);
    }

    [Theory]
    [InlineData("algn", "Align")]                 // a skipped letter
    [InlineData("aln", "Align")]                  // two skipped letters... one allowed per two typed
    [InlineData("aling", "Align")]                // two neighbours swapped
    [InlineData("allign", "Align")]               // an extra letter
    [InlineData("alugn", "Align")]                // a wrong letter
    [InlineData("wsll", "Wall")]                  // a wrong letter
    [InlineData("wlal", "Wall: Architectural")]   // swapped, at the start of a longer name
    [InlineData("dimensoin", "Aligned Dimension")] // a typo in a later word
    [InlineData("dimnsion", "Aligned Dimension")] // a skipped letter in a later word
    [InlineData("cw", "Curtain Wall")]            // initials
    [InlineData("lagin", "Align")]                // first two letters swapped, and a second edit: too far
    public void Fuzzy_tolerates_typos_and_skipped_letters(string query, string name)
    {
        var match = Match(query, name);
        if (query == "lagin")
        {
            match.Should().BeNull();
            return;
        }
        match.Should().NotBeNull($"\"{query}\" should find \"{name}\"");
        match!.Value.Kind.Should().Be(MatchKind.Fuzzy);
    }

    [Theory]
    [InlineData("xyzq", "Align")]
    [InlineData("door", "Wall")]
    [InlineData("zz", "Align")]
    [InlineData("al", "Wall")]        // inside a word is a substring, never fuzzy: covered below
    [InlineData("gn", "Align")]
    [InlineData("roof", "Floor")]
    [InlineData("a", "Door")]
    [InlineData("alignment tool", "Wall")]
    public void Unrelated_text_does_not_match_fuzzily(string query, string name)
    {
        var match = Match(query, name);
        if (match is { } m) m.Kind.Should().NotBe(MatchKind.Fuzzy);
        if (query is "xyzq" or "door" or "zz" or "roof" or "a" or "alignment tool") match.Should().BeNull();
    }

    [Fact]
    public void Two_typed_letters_never_match_by_typo()
    {
        Match("wl", "Wall").Should().NotBeNull("skipped letters still work");
        Match("ws", "Wall").Should().BeNull("two letters are too few to forgive a wrong one");
    }

    [Fact]
    public void One_skipped_letter_ranks_before_one_typo_and_both_after_a_substring()
    {
        var skipped = Match("algn", "Align")!.Value;
        var typo = Match("alugn", "Align")!.Value;
        var substring = Match("lig", "Align")!.Value;
        skipped.CompareTo(typo).Should().BeNegative();
        substring.CompareTo(skipped).Should().BeNegative();
    }

    [Fact]
    public void An_empty_query_or_name_matches_nothing()
    {
        Match("", "Align").Should().BeNull();
        Match("align", "").Should().BeNull();
    }

    [Theory]
    [InlineData("ALIGN", "align", MatchKind.Exact)]
    [InlineData("aLiGn", "ALIGN", MatchKind.Exact)]
    [InlineData("WALL", "Wall: Architectural", MatchKind.Prefix)]
    [InlineData("tür", "Tür", MatchKind.Exact)]                    // German: door
    [InlineData("TÜR", "Tür einfügen", MatchKind.Prefix)]
    [InlineData("école", "École", MatchKind.Exact)]
    [InlineData("ÉCOLE", "Plan de l'école", MatchKind.WordStart)]
    [InlineData("ñ", "Diseño", MatchKind.Substring)]
    [InlineData("стена", "Стена: Архитектурная", MatchKind.Prefix)]  // Cyrillic, either case
    [InlineData("АРХ", "Стена: Архитектурная", MatchKind.WordStart)]
    [InlineData("壁", "壁: 意匠", MatchKind.Prefix)]                  // no case at all
    [InlineData("意匠", "壁: 意匠", MatchKind.WordStart)]
    [InlineData("fenstr", "Fenster", MatchKind.Fuzzy)]
    [InlineData("tüer", "Tür einfügen", MatchKind.Fuzzy)]            // a typo next to a non-ASCII letter
    public void Case_never_matters_and_non_ASCII_names_match(string query, string name, MatchKind kind) =>
        Match(query, name)!.Value.Kind.Should().Be(kind);

    [Fact]
    public void An_accent_is_a_different_letter()
    {
        // No accent folding: Revit's names are matched as written. A wrong accent inside a word is one typo;
        // a wrong first letter is not forgiven, accented or not.
        Match("fenêtre", "Fenetre")!.Value.Kind.Should().Be(MatchKind.Fuzzy);
        Match("ecole", "École").Should().BeNull();
    }

    [Fact]
    public void Repeated_matching_reuses_its_buffers_and_stays_correct()
    {
        // Short then long then short again: the per-thread rows grow and are reused.
        Match("wsll", "Wall")!.Value.Kind.Should().Be(MatchKind.Fuzzy);
        var longName = string.Join(' ', Enumerable.Repeat("structural", 12));
        Match(string.Join(' ', Enumerable.Repeat("structural", 11)) + " strucural", longName)!.Value.Kind.Should().Be(MatchKind.Fuzzy);
        Match("wsll", "Wall")!.Value.Kind.Should().Be(MatchKind.Fuzzy);
        Match("aling", "Align")!.Value.Kind.Should().Be(MatchKind.Fuzzy);
    }
}
