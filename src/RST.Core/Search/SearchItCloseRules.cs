// rst-c's own (not vendored): what the modeless Search-It window does about the mouse outside it (v3.4).
//
// The window is modeless so the user can pan, zoom and orbit the Revit view behind it. It closes on
// Escape (the window's own key handling), on a left or right press anywhere outside it, and when
// another application comes to the front. A middle press or the wheel outside it never closes it:
// those navigate the view. A middle-button pan or orbit does make Revit the active window, though,
// and keystrokes would then go to Revit as shortcuts; so when the middle button comes up with Revit's
// main window in front, the window takes the keyboard back (the user was peeking at the model). A
// press on the window itself (a result, its title bar, its resize border) never closes it, wherever
// the pointer is by the time the mouse is read.
//
// The window reads the mouse on a short timer (SearchItWindow's watch; why a timer and not a
// low-level mouse hook is explained there): only the left, right and middle buttons' state, never
// the wheel. The decision is here, on plain values, so it is unit-tested without a window.

namespace RST.Core.Search;

/// <summary>One reading of the mouse and the desktop, taken by the window's timer.</summary>
/// <param name="LeftDown">The left button is down now.</param>
/// <param name="LeftPressedSince">The left button went down at some point since the last reading (a click that fell between two readings).</param>
/// <param name="RightDown">The right button is down now.</param>
/// <param name="RightPressedSince">As <paramref name="LeftPressedSince"/>, for the right button.</param>
/// <param name="MiddleDown">The middle button is down now.</param>
/// <param name="PointerInside">The pointer is over the window's rectangle (its frame and resize border included).</param>
/// <param name="PressOnWindow">The press belongs to the window: its own mouse-down saw it, or it holds the mouse capture (a drag of its edge or title bar).</param>
/// <param name="HostInFront">The foreground window belongs to this process (Revit or the Search-It window itself).</param>
/// <param name="OwnerInFront">The foreground window is the window's owner (Revit's main window).</param>
public readonly record struct PointerSample(
    bool LeftDown = false, bool LeftPressedSince = false, bool RightDown = false, bool RightPressedSince = false,
    bool MiddleDown = false, bool PointerInside = false, bool PressOnWindow = false,
    bool HostInFront = true, bool OwnerInFront = false);

/// <summary>What the watch remembers between readings: which buttons were down.</summary>
public readonly record struct PointerWatchState(bool LeftDown, bool RightDown, bool MiddleDown)
{
    /// <summary>
    /// The state to start from: the buttons as they are now, so a click from before (the one that
    /// opened the window, or one queued while the tools were read) is never taken for a click outside.
    /// </summary>
    public static PointerWatchState StartingFrom(PointerSample sample) => new(sample.LeftDown, sample.RightDown, sample.MiddleDown);
}

/// <summary>What the window does after a reading.</summary>
public enum PointerWatchAction
{
    /// <summary>Nothing.</summary>
    None,

    /// <summary>Close the window.</summary>
    Close,

    /// <summary>Take the keyboard back: activate the window and focus the search box, text and caret as they were.</summary>
    Refocus,
}

public static class SearchItCloseRules
{
    /// <summary>
    /// One timer reading: what the window does now, and the state for the next reading.
    /// <list type="bullet">
    /// <item>Another application in front (Alt+Tab, a click on another program): close.</item>
    /// <item>The left or right button went down since the last reading, with the pointer outside the
    /// window and the press not the window's own: close.</item>
    /// <item>The middle button came up with Revit's main window in front: refocus. Never when another
    /// application is in front (that closed above), so focus is never taken from another program.</item>
    /// <item>Anything else (the middle button going down or held, the pointer moving, the wheel, which
    /// is not read at all): nothing.</item>
    /// </list>
    /// </summary>
    public static PointerWatchAction Step(ref PointerWatchState state, PointerSample sample)
    {
        var leftPress = sample.LeftPressedSince || (sample.LeftDown && !state.LeftDown);
        var rightPress = sample.RightPressedSince || (sample.RightDown && !state.RightDown);
        var middleReleased = state.MiddleDown && !sample.MiddleDown;
        state = PointerWatchState.StartingFrom(sample);
        if (!sample.HostInFront) return PointerWatchAction.Close;
        if ((leftPress || rightPress) && !sample.PointerInside && !sample.PressOnWindow) return PointerWatchAction.Close;
        if (middleReleased && sample.OwnerInFront) return PointerWatchAction.Refocus;
        return PointerWatchAction.None;
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
