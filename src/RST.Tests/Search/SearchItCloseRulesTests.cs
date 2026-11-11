// rst-c's own (not vendored): when the modeless Search-It window closes itself (v3.4).

using FluentAssertions;
using RST.Core.Search;
using Xunit;

namespace RST.Tests.Search;

public class SearchItCloseRulesTests
{
    [Theory]
    [InlineData(PointerInput.LeftDown, false, true)]
    [InlineData(PointerInput.RightDown, false, true)]
    [InlineData(PointerInput.MiddleDown, false, false)]  // pan and orbit
    [InlineData(PointerInput.MiddleUp, false, false)]
    [InlineData(PointerInput.Wheel, false, false)]       // zoom
    [InlineData(PointerInput.Move, false, false)]
    [InlineData(PointerInput.LeftDown, true, false)]
    [InlineData(PointerInput.RightDown, true, false)]
    [InlineData(PointerInput.MiddleDown, true, false)]
    [InlineData(PointerInput.Wheel, true, false)]
    public void Only_a_left_or_right_press_outside_closes(PointerInput input, bool inside, bool closes) =>
        SearchItCloseRules.Closes(input, inside).Should().Be(closes);

    private static PointerSample Sample(bool left = false, bool right = false, bool inside = false, bool host = true,
        bool leftSince = false, bool rightSince = false) => new(left, leftSince, right, rightSince, inside, host);

    private static bool[] Run(PointerSample start, params PointerSample[] samples)
    {
        var state = PointerWatchState.StartingFrom(start);
        return samples.Select(s => SearchItCloseRules.Step(ref state, s)).ToArray();
    }

    [Fact]
    public void A_left_or_right_press_outside_closes_on_the_reading_that_sees_it()
    {
        Run(Sample(), Sample(), Sample(left: true)).Should().Equal(false, true);
        Run(Sample(), Sample(right: true)).Should().Equal(true);
    }

    [Fact]
    public void A_press_inside_never_closes_even_when_the_pointer_then_leaves()
    {
        // Dragging the window's edge to resize it: down inside, held while outside, released outside.
        Run(Sample(inside: true), Sample(left: true, inside: true), Sample(left: true), Sample(left: true), Sample())
            .Should().Equal(false, false, false, false);
    }

    [Fact]
    public void Idle_readings_and_pointer_moves_outside_do_nothing()
    {
        // Nothing but the left and right buttons is read: middle-button pans and the wheel look like this.
        Run(Sample(inside: true), Sample(), Sample(), Sample(inside: true), Sample()).Should().Equal(false, false, false, false);
    }

    [Fact]
    public void The_click_that_opened_the_window_is_not_a_click_outside()
    {
        // Still held when the window opens (outside it), then released.
        Run(Sample(left: true), Sample(left: true), Sample()).Should().Equal(false, false);
    }

    [Fact]
    public void A_quick_click_between_two_readings_still_closes()
    {
        Run(Sample(), Sample(leftSince: true)).Should().Equal(true);
        Run(Sample(), Sample(rightSince: true)).Should().Equal(true);
        Run(Sample(inside: true), Sample(leftSince: true, inside: true)).Should().Equal(false);
    }

    [Fact]
    public void Another_application_in_front_closes_whatever_the_mouse_does()
    {
        Run(Sample(inside: true), Sample(inside: true, host: false)).Should().Equal(true);
        Run(Sample(), Sample(host: false)).Should().Equal(true);
        // Revit itself in front (after a middle-button pan activated it) does not.
        Run(Sample(), Sample(host: true)).Should().Equal(false);
    }

    [Theory]
    [InlineData(100, 100, true)]
    [InlineData(299, 199, true)]
    [InlineData(300, 150, false)]
    [InlineData(150, 200, false)]
    [InlineData(99, 150, false)]
    [InlineData(-5, -5, false)]
    public void The_window_rectangle_is_left_top_inclusive(int x, int y, bool inside) =>
        SearchItCloseRules.Contains(100, 100, 300, 200, x, y).Should().Be(inside);

    [Fact]
    public void The_default_list_shows_eight_cards()
    {
        SearchItLayout.VisibleRows.Should().Be(8);
        SearchItLayout.ListHeight.Should().Be(8 * SearchItLayout.RowHeight);
    }
}
