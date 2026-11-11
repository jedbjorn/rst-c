// rst-c's own (not vendored): what a key does in the Search-It window (v3.4, decision #83): Enter
// depends on where the keyboard is.

using FluentAssertions;
using RST.Core.Search;
using Xunit;

namespace RST.Tests.Search;

public class SearchItKeysTests
{
    private static SearchKeyAction Box(SearchKey key, int count = 5, bool boxEmpty = false, bool repeat = false) =>
        SearchItKeys.Decide(SearchFocus.Box, key, -1, count, boxEmpty, repeat);

    private static SearchKeyAction Card(SearchKey key, int index, int count = 5, bool repeat = false) =>
        SearchItKeys.Decide(SearchFocus.Card, key, index, count, boxEmpty: false, repeat);

    [Fact]
    public void In_the_box_Enter_makes_a_chip_and_never_runs_a_tool()
    {
        Box(SearchKey.Enter).Should().Be(SearchKeyAction.MakeChip);
        Box(SearchKey.Enter, count: 0).Should().Be(SearchKeyAction.MakeChip);
        Box(SearchKey.Enter, repeat: true).Should().Be(SearchKeyAction.None, "a held Enter makes one chip");
    }

    [Fact]
    public void In_the_box_Down_goes_to_the_first_card_when_there_is_one()
    {
        Box(SearchKey.Down).Should().Be(SearchKeyAction.FocusFirstCard);
        Box(SearchKey.Down, repeat: true).Should().Be(SearchKeyAction.FocusFirstCard);
        Box(SearchKey.Down, count: 0).Should().Be(SearchKeyAction.None);
        Box(SearchKey.Up).Should().Be(SearchKeyAction.None);
    }

    [Fact]
    public void Backspace_removes_one_chip_only_from_an_empty_box_and_never_while_held()
    {
        Box(SearchKey.Backspace, boxEmpty: true).Should().Be(SearchKeyAction.RemoveLastChip);
        Box(SearchKey.Backspace, boxEmpty: false).Should().Be(SearchKeyAction.None, "with text, Backspace is the box's own");
        // Holding Backspace to clear the text must stop at the empty box, not go on through every chip.
        Box(SearchKey.Backspace, boxEmpty: true, repeat: true).Should().Be(SearchKeyAction.None);
    }

    [Fact]
    public void On_a_card_Up_and_Down_move_and_Up_on_the_first_returns_to_the_box()
    {
        Card(SearchKey.Down, 0).Should().Be(SearchKeyAction.MoveDown);
        Card(SearchKey.Down, 3).Should().Be(SearchKeyAction.MoveDown);
        Card(SearchKey.Down, 4).Should().Be(SearchKeyAction.None, "the last card is the end");
        Card(SearchKey.Up, 4).Should().Be(SearchKeyAction.MoveUp);
        Card(SearchKey.Up, 1).Should().Be(SearchKeyAction.MoveUp);
        Card(SearchKey.Up, 0).Should().Be(SearchKeyAction.FocusBox);
        Card(SearchKey.Up, -1).Should().Be(SearchKeyAction.FocusBox);
        Card(SearchKey.Down, 0, count: 1).Should().Be(SearchKeyAction.None);
    }

    [Fact]
    public void On_a_card_Enter_runs_the_highlighted_tool()
    {
        Card(SearchKey.Enter, 0).Should().Be(SearchKeyAction.RunHighlighted);
        Card(SearchKey.Enter, 4).Should().Be(SearchKeyAction.RunHighlighted);
        Card(SearchKey.Enter, 2, repeat: true).Should().Be(SearchKeyAction.None, "a held Enter runs nothing");
        Card(SearchKey.Enter, -1).Should().Be(SearchKeyAction.None, "no highlighted card");
        Card(SearchKey.Enter, 5).Should().Be(SearchKeyAction.None);
    }

    [Fact]
    public void On_a_card_a_printable_character_goes_back_to_the_box_and_is_typed()
    {
        Card(SearchKey.Character, 2).Should().Be(SearchKeyAction.TypeInBox);
        Card(SearchKey.Backspace, 2).Should().Be(SearchKeyAction.FocusBox);
        Card(SearchKey.Other, 2).Should().Be(SearchKeyAction.None);
        Box(SearchKey.Character).Should().Be(SearchKeyAction.None, "in the box, typing is the box's own");
    }

    [Theory]
    [InlineData(SearchFocus.Box)]
    [InlineData(SearchFocus.Card)]
    public void Escape_closes_from_anywhere_on_a_fresh_press(SearchFocus focus)
    {
        SearchItKeys.Decide(focus, SearchKey.Escape, 2, 5, false, false).Should().Be(SearchKeyAction.Close);
        SearchItKeys.Decide(focus, SearchKey.Escape, 2, 5, false, true).Should().Be(SearchKeyAction.None);
    }

    [Theory]
    [InlineData("a", true)]
    [InlineData("É", true)]
    [InlineData(" ", true)]
    [InlineData("壁", true)]
    [InlineData("ab", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("\r", false)]
    [InlineData("\t", false)]
    [InlineData("\b", false)]
    [InlineData("\u001b", false)]
    public void Printable_text_is_anything_without_control_characters(string? text, bool printable) =>
        SearchItKeys.IsPrintable(text).Should().Be(printable);
}
