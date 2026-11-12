// rst-c's own (not vendored): what the modeless Search-It window does about the mouse outside it (v3.4).

using FluentAssertions;
using RST.Core.Search;
using Xunit;

namespace RST.Tests.Search;

public class SearchItCloseRulesTests
{
    private static readonly PointerSample Outside = new(HostInFront: true);                  // nothing down, pointer outside, Revit's process in front
    private static readonly PointerSample Inside = new(PointerInside: true, HostInFront: true);

    private static PointerWatchAction[] Run(PointerSample start, params PointerSample[] samples)
    {
        var state = PointerWatchState.StartingFrom(start);
        return samples.Select(s => SearchItCloseRules.Step(ref state, s)).ToArray();
    }

    private const PointerWatchAction None = PointerWatchAction.None;
    private const PointerWatchAction Close = PointerWatchAction.Close;
    private const PointerWatchAction Refocus = PointerWatchAction.Refocus;

    [Fact]
    public void A_left_or_right_press_outside_closes_on_the_reading_that_sees_it()
    {
        Run(Outside, Outside, Outside with { LeftDown = true }).Should().Equal(None, Close);
        Run(Outside, Outside with { RightDown = true }).Should().Equal(Close);
    }

    [Fact]
    public void A_press_inside_never_closes_even_when_the_pointer_then_leaves()
    {
        // Dragging the window's edge to resize it: down inside, held while outside, released outside.
        Run(Inside, Inside with { LeftDown = true }, Outside with { LeftDown = true }, Outside with { LeftDown = true }, Outside)
            .Should().Equal(None, None, None, None);
    }

    /// <summary>
    /// A reading says where the pointer is when it is taken, not where the button went down. A press on the
    /// window whose pointer is already outside at the first reading that sees it (a fast drag of the edge,
    /// a late timer) is still the window's own: its mouse-down saw it, or it holds the capture.
    /// </summary>
    [Fact]
    public void A_press_on_the_window_seen_with_the_pointer_already_outside_does_not_close()
    {
        Run(Inside, Outside with { LeftDown = true, PressOnWindow = true }, Outside with { LeftDown = true, PressOnWindow = true }, Outside)
            .Should().Equal(None, None, None);
        Run(Inside, Outside with { RightDown = true, PressOnWindow = true }).Should().Equal(None);
        // A whole click between two readings, on the window, pointer outside by now.
        Run(Inside, Outside with { LeftPressedSince = true, PressOnWindow = true }).Should().Equal(None);
        // The same readings without the window's claim on the press do close.
        Run(Inside, Outside with { LeftDown = true }).Should().Equal(Close);
    }

    [Fact]
    public void Idle_readings_and_pointer_moves_outside_do_nothing()
    {
        Run(Inside, Outside, Outside, Inside, Outside).Should().Equal(None, None, None, None);
    }

    [Fact]
    public void A_click_from_before_the_starting_point_is_not_a_click_outside()
    {
        // Still held when the window opens (the click on the ribbon button), then released.
        Run(Outside with { LeftDown = true }, Outside with { LeftDown = true }, Outside).Should().Equal(None, None);
    }

    [Fact]
    public void A_quick_click_between_two_readings_still_closes()
    {
        Run(Outside, Outside with { LeftPressedSince = true }).Should().Equal(Close);
        Run(Outside, Outside with { RightPressedSince = true }).Should().Equal(Close);
        Run(Inside, Inside with { LeftPressedSince = true }).Should().Equal(None);
    }

    [Fact]
    public void Another_application_in_front_closes_whatever_the_mouse_does()
    {
        Run(Inside, Inside with { HostInFront = false }).Should().Equal(Close);
        Run(Outside, Outside with { HostInFront = false }).Should().Equal(Close);
        // Revit itself in front (after a middle-button pan activated it) does not.
        Run(Outside, Outside with { OwnerInFront = true }).Should().Equal(None);
    }

    [Fact]
    public void The_middle_button_never_closes_down_held_or_up()
    {
        // A pan or an orbit in the view: down outside, held, released, with Revit's main window in front or not.
        foreach (var ownerInFront in new[] { false, true })
        {
            var down = Outside with { MiddleDown = true, OwnerInFront = ownerInFront };
            var up = Outside with { OwnerInFront = ownerInFront };
            Run(Outside, down, down, up, up).Should().NotContain(Close);
        }
        // And it does not hide a left click that comes with it.
        Run(Outside, Outside with { MiddleDown = true, LeftDown = true }).Should().Equal(Close);
    }

    [Fact]
    public void The_middle_button_coming_up_with_Revit_in_front_takes_the_keyboard_back_once()
    {
        var revit = Outside with { OwnerInFront = true };
        Run(Outside, revit with { MiddleDown = true }, revit with { MiddleDown = true }, revit, revit)
            .Should().Equal(None, None, Refocus, None);
        // Held since before the window opened: its release still counts.
        Run(revit with { MiddleDown = true }, revit).Should().Equal(Refocus);
    }

    [Fact]
    public void The_keyboard_is_never_taken_from_another_window()
    {
        // The window itself is in front (a middle click on it): nothing to take back.
        Run(Inside, Inside with { MiddleDown = true }, Inside).Should().Equal(None, None);
        // Another of Revit's own windows is in front (a dialog), not its main window: left alone.
        Run(Outside, Outside with { MiddleDown = true }, Outside).Should().Equal(None, None);
        // Another application in front when the button comes up: the window closes; it never refocuses.
        Run(Outside, Outside with { MiddleDown = true }, Outside with { HostInFront = false, OwnerInFront = false })
            .Should().Equal(None, Close);
        Run(Outside, Outside with { MiddleDown = true }, Outside with { HostInFront = false, OwnerInFront = true })
            .Should().Equal(None, Close);
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
