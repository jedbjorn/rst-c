// rst-c's own (not vendored): what a key does in the Search-It window (v3.4, decision #83). What Enter
// does depends on where the keyboard is: in the search box it makes a chip; on a result card it runs
// that tool, the same as a click.

namespace RST.Core.Search;

/// <summary>Where the keyboard is.</summary>
public enum SearchFocus
{
    /// <summary>The search box.</summary>
    Box,

    /// <summary>A result card.</summary>
    Card,
}

/// <summary>The keys the window cares about.</summary>
public enum SearchKey
{
    Enter,
    Up,
    Down,
    Escape,
    Backspace,

    /// <summary>A printable character.</summary>
    Character,
    Other,
}

public enum SearchKeyAction
{
    /// <summary>Leave the key to the control that has it.</summary>
    None,

    /// <summary>The text in the box becomes a chip.</summary>
    MakeChip,

    /// <summary>The last chip goes.</summary>
    RemoveLastChip,

    /// <summary>The keyboard moves from the box to the first card.</summary>
    FocusFirstCard,

    /// <summary>The highlight moves up one card (the list scrolls to keep it visible).</summary>
    MoveUp,

    /// <summary>The highlight moves down one card.</summary>
    MoveDown,

    /// <summary>The keyboard goes back to the box; nothing else changes.</summary>
    FocusBox,

    /// <summary>The keyboard goes back to the box and the character is added to its text.</summary>
    TypeInBox,

    /// <summary>The highlighted tool runs, as a click on its card does.</summary>
    RunHighlighted,

    /// <summary>The window closes without running anything.</summary>
    Close,
}

public static class SearchItKeys
{
    /// <summary>What <paramref name="key"/> does.</summary>
    /// <param name="focus">Where the keyboard is.</param>
    /// <param name="index">The highlighted card's index (ignored in the box).</param>
    /// <param name="count">How many result cards there are.</param>
    /// <param name="boxEmpty">The search box holds no text.</param>
    /// <param name="isRepeat">The key is held down and repeating.</param>
    public static SearchKeyAction Decide(SearchFocus focus, SearchKey key, int index, int count, bool boxEmpty, bool isRepeat)
    {
        // A held Escape never closes anything (the Escape rules: a fresh press only).
        if (key == SearchKey.Escape) return isRepeat ? SearchKeyAction.None : SearchKeyAction.Close;

        if (focus == SearchFocus.Box)
        {
            return key switch
            {
                // A held Enter makes one chip, and a held Backspace removes one: holding Backspace to clear
                // the text must not go on to eat every chip.
                SearchKey.Enter => isRepeat ? SearchKeyAction.None : SearchKeyAction.MakeChip,
                SearchKey.Backspace when boxEmpty && !isRepeat => SearchKeyAction.RemoveLastChip,
                SearchKey.Down when count > 0 => SearchKeyAction.FocusFirstCard,
                _ => SearchKeyAction.None,
            };
        }

        return key switch
        {
            SearchKey.Up => index <= 0 ? SearchKeyAction.FocusBox : SearchKeyAction.MoveUp,
            SearchKey.Down => index < count - 1 ? SearchKeyAction.MoveDown : SearchKeyAction.None,
            // A held Enter must not run a tool: the press that made a chip or opened something else may still be down.
            SearchKey.Enter => isRepeat || index < 0 || index >= count ? SearchKeyAction.None : SearchKeyAction.RunHighlighted,
            SearchKey.Character => SearchKeyAction.TypeInBox,
            SearchKey.Backspace => SearchKeyAction.FocusBox,
            _ => SearchKeyAction.None,
        };
    }

    /// <summary>True for text that types into the box: not empty, and no control characters (Enter, Tab, Escape, Backspace).</summary>
    public static bool IsPrintable(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var c in text)
            if (char.IsControl(c)) return false;
        return true;
    }
}
