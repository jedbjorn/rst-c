// rst-c's own (not vendored): Search-It's search (v3.4): ranking, chips (exact, ANDed), chips with
// fuzzy text, and that it stays fast for a few thousand tools.

using System.Diagnostics;
using FluentAssertions;
using RST.Core.Search;
using Xunit;

namespace RST.Tests.Search;

public class ToolSearchTests
{
    private static SearchTool Tool(string name, string tab = "Tab", string panel = "Panel", string? id = null) =>
        new(id ?? "ID_" + name.ToUpperInvariant().Replace(' ', '_'), name, tab, panel);

    private static readonly SearchTool[] Sample =
    [
        Tool("Align", "Modify", "Modify"),
        Tool("Aligned Dimension", "Annotate", "Dimension"),
        Tool("Realign Views", "View", "Sheet Composition"),
        Tool("Spot Alignment", "Annotate", "Dimension"),
        Tool("Wall: Architectural", "Architecture", "Build"),
        Tool("Wall: Structural", "Structure", "Structure"),
        Tool("Curtain Wall", "Architecture", "Build"),
        Tool("Wall Opening", "Architecture", "Opening"),
        Tool("Door", "Architecture", "Build"),
        Tool("Floor: Structural", "Structure", "Foundation"),
        Tool("Angular Dimension", "Annotate", "Dimension"),
    ];

    private static string[] Names(IEnumerable<SearchHit> hits) => hits.Select(h => h.Tool.DisplayName).ToArray();

    private static ToolSearch Search() => new(Sample);

    [Fact]
    public void Results_rank_exact_then_prefix_then_word_start_then_substring_then_fuzzy()
    {
        var hits = Search().Search(null, "align");
        Names(hits).Should().Equal("Align", "Aligned Dimension", "Spot Alignment", "Realign Views");
        hits.Select(h => h.Match!.Value.Kind).Should().Equal(
            MatchKind.Exact, MatchKind.Prefix, MatchKind.WordStart, MatchKind.Substring);

        // A fuzzy hit comes after every plain one: "walk" is one letter off "wall".
        var tools = Sample.Append(Tool("Walkthrough", "View", "Create")).ToArray();
        var walk = new ToolSearch(tools).Search(null, "walk");
        Names(walk).First().Should().Be("Walkthrough");
        walk.Skip(1).Should().OnlyContain(h => h.Match!.Value.Kind == MatchKind.Fuzzy);
        Names(walk.Skip(1)).Should().Contain("Curtain Wall").And.Contain("Wall Opening");
    }

    [Theory]
    [InlineData("algn", "Align")]
    [InlineData("aling", "Align")]
    [InlineData("allign", "Align")]
    [InlineData("dor", "Door")]
    [InlineData("curtan wall", "Curtain Wall")]
    [InlineData("wall opning", "Wall Opening")]
    public void A_typo_or_a_skipped_letter_still_puts_the_tool_first(string text, string expected) =>
        Names(Search().Search(null, text)).First().Should().Be(expected);

    [Fact]
    public void Shorter_names_come_first_inside_a_tier_then_alphabetical()
    {
        var hits = Search().Search(null, "wall");
        Names(hits).Take(3).Should().Equal("Wall Opening", "Wall: Structural", "Wall: Architectural");
        Names(hits).Last().Should().Be("Curtain Wall");
    }

    [Fact]
    public void No_text_and_no_chips_lists_every_tool_by_name()
    {
        var hits = Search().Search([], "  ");
        hits.Should().HaveCount(Sample.Length);
        hits.Should().OnlyContain(h => h.Match == null);
        Names(hits).Should().BeInAscendingOrder(StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_chip_is_an_exact_case_insensitive_substring_of_the_name()
    {
        Names(Search().Search(["WALL"], null)).Should().BeEquivalentTo(
            "Wall: Architectural", "Wall: Structural", "Curtain Wall", "Wall Opening");
        // Exact: a chip forgives nothing, unlike typed text.
        Search().Search(["algn"], null).Should().BeEmpty();
        Search().Search(null, "algn").Should().NotBeEmpty();
    }

    [Fact]
    public void Chips_are_ANDed()
    {
        Names(Search().Search(["wall", "struct"], null)).Should().Equal("Wall: Structural");
        Names(Search().Search(["struct"], null)).Should().BeEquivalentTo("Wall: Structural", "Floor: Structural");
        Search().Search(["wall", "door"], null).Should().BeEmpty();
        // Order does not matter, and neither do blanks or repeats.
        Names(Search().Search(["struct", " ", "Wall", "WALL"], null)).Should().Equal("Wall: Structural");
    }

    [Fact]
    public void Text_keeps_fuzzy_ranking_inside_the_chip_filtered_set()
    {
        // The chip keeps the Dimension tools; the text, typo and all, ranks them.
        var hits = Search().Search(["dimension"], "algned");
        Names(hits).Should().Equal("Aligned Dimension");
        hits[0].Match!.Value.Kind.Should().Be(MatchKind.Fuzzy);

        // Without the chip the same text finds tools the chip left out.
        Names(Search().Search(null, "wall")).Should().Contain("Curtain Wall");
        Names(Search().Search(["struct"], "wall")).Should().Equal("Wall: Structural");
        // A chip never widens: the text must match too.
        Search().Search(["wall"], "door").Should().BeEmpty();
    }

    [Fact]
    public void The_chip_rules_trim_fold_and_refuse_blanks_and_repeats()
    {
        SearchChips.CanAdd([], "  Wall   Opening ", out var label).Should().BeTrue();
        label.Should().Be("Wall Opening");
        SearchChips.CanAdd(["Wall Opening"], "wall  opening", out _).Should().BeFalse();
        SearchChips.CanAdd(["wall"], "   ", out var blank).Should().BeFalse();
        blank.Should().BeEmpty();
        SearchChips.CanAdd(null, "door", out _).Should().BeTrue();
        SearchChips.Normalize(["Wall", " ", "WALL", "Door "]).Should().Equal("wall", "door");
    }

    [Fact]
    public void Same_named_tools_all_show_ordered_by_where_they_live()
    {
        var search = new ToolSearch([Tool("Settings", "Zeta", "Tools", "B"), Tool("Settings", "Alpha", "Tools", "A")]);
        search.Search(null, "settings").Select(h => h.Tool.Id).Should().Equal("A", "B");
    }

    [Fact]
    public void Null_input_is_an_empty_search()
    {
        var search = new ToolSearch(null);
        search.Count.Should().Be(0);
        search.Search(null, "wall").Should().BeEmpty();
        search.Search(null, null).Should().BeEmpty();
    }

    [Fact]
    public void A_few_thousand_tools_search_fast()
    {
        string[] words = ["Wall", "Door", "Align", "Dimension", "Structural", "Beam", "View", "Sheet", "Tag", "Room",
            "Schedule", "Duct", "Pipe", "Family", "Load", "Export", "Link", "Grid", "Level", "Section"];
        var tools = Enumerable.Range(0, 5000)
            .Select(i => Tool($"{words[i % 20]} {words[i / 20 % 20]} {words[i / 400 % 20]} {i}", id: "ID_" + i))
            .ToArray();
        var search = new ToolSearch(tools);
        string[] typed = ["a", "al", "alg", "algn", "wsll", "dimensoin", "structral beam", "zzzzzz", "sheet view 12"];
        foreach (var text in typed) search.Search(["wall"], text); // warm up

        var sw = Stopwatch.StartNew();
        const int rounds = 5;
        for (var i = 0; i < rounds; i++)
            foreach (var text in typed)
            {
                search.Search(null, text);
                search.Search(["wall"], text);
            }
        var perSearch = sw.Elapsed.TotalMilliseconds / (rounds * typed.Length * 2);
        // A keystroke should feel instant; this bound is loose enough for a busy CI machine.
        perSearch.Should().BeLessThan(50, "one search over 5,000 tools must stay well inside a keystroke");
    }
}
