// rst-c's own (not vendored): when the Search-It window closes itself (v3.4).
//
// The window is modeless so the user can pan, zoom and orbit the Revit view behind it. It closes on
// Escape (the window's own key handling), on a left or right press anywhere outside it, and when
// another application comes to the front. A middle press or the wheel outside it never closes it:
// those navigate the view. A press that starts inside the window (dragging its edge to resize, a
// click on a result) never closes it either, wherever the pointer goes before the release.
//
// The window samples the mouse on a short timer (SearchItWindow's watch; why a timer and not a
// low-level mouse hook is explained there). The decisions are here, on plain values, so they are
// unit-tested without a window.

namespace RST.Core.Search;

/// <summary>What the pointer did outside or inside the window.</summary>
public enum PointerInput
{
    LeftDown,
    RightDown,
    MiddleDown,
    MiddleUp,
    Wheel,
    Move,
}

/// <summary>One reading of the mouse and the desktop, taken by the window's timer.</summary>
/// <param name="LeftDown">The left button is down now.</param>
/// <param name="LeftPressedSince">The left button went down at some point since the last reading (a click that fell between two readings).</param>
/// <param name="RightDown">The right button is down now.</param>
/// <param name="RightPressedSince">As <paramref name="LeftPressedSince"/>, for the right button.</param>
/// <param name="PointerInside">The pointer is over the window's rectangle.</param>
/// <param name="HostInFront">The foreground window belongs to this process (Revit or the Search-It window itself).</param>
public readonly record struct PointerSample(
    bool LeftDown, bool LeftPressedSince, bool RightDown, bool RightPressedSince, bool PointerInside, bool HostInFront);

/// <summary>What the watch remembers between readings: which buttons were down.</summary>
public readonly record struct PointerWatchState(bool LeftDown, bool RightDown)
{
    /// <summary>
    /// The state to start from: the buttons as they are when the window opens, so the click that opened
    /// it (still held, or only just released) is never taken for a click outside.
    /// </summary>
    public static PointerWatchState StartingFrom(PointerSample sample) => new(sample.LeftDown, sample.RightDown);
}

public static class SearchItCloseRules
{
    /// <summary>True when <paramref name="input"/> closes the window: a left or right press outside it, and nothing else.</summary>
    public static bool Closes(PointerInput input, bool insideWindow) =>
        !insideWindow && input is PointerInput.LeftDown or PointerInput.RightDown;

    /// <summary>
    /// One timer reading: whether the window closes now, and the state for the next reading. It closes
    /// when another application is in front (Alt+Tab, a click on another program), or when the left or
    /// right button went down since the last reading while the pointer is outside the window.
    /// </summary>
    public static bool Step(ref PointerWatchState state, PointerSample sample)
    {
        var leftPress = sample.LeftPressedSince || (sample.LeftDown && !state.LeftDown);
        var rightPress = sample.RightPressedSince || (sample.RightDown && !state.RightDown);
        state = new PointerWatchState(sample.LeftDown, sample.RightDown);
        if (!sample.HostInFront) return true;
        return (leftPress && Closes(PointerInput.LeftDown, sample.PointerInside))
            || (rightPress && Closes(PointerInput.RightDown, sample.PointerInside));
    }

    /// <summary>True when the screen point is inside the rectangle (left and top inclusive, right and bottom exclusive).</summary>
    public static bool Contains(int left, int top, int right, int bottom, int x, int y) =>
        x >= left && x < right && y >= top && y < bottom;
}

/// <summary>The window's default size: how many result cards show before the list scrolls.</summary>
public static class SearchItLayout
{
    /// <summary>Result cards visible at the default size; the rest scroll.</summary>
    public const int VisibleRows = 8;

    /// <summary>One result card's height: the name and, under it, where the tool lives.</summary>
    public const double RowHeight = 44;

    /// <summary>The result list's default height.</summary>
    public const double ListHeight = VisibleRows * RowHeight;

    /// <summary>The window's default width.</summary>
    public const double DefaultWidth = 440;
}
