// rst-c's own (not vendored): the Builder's icon picker as a plain model (spec #16, WU5), a port
// of profile_builder.html's ICON PICKER section (3194-3431) without the DOM. Behavior is unchanged:
//   - the first tile is "∅ none", then one tile per catalogue design (its blue thumbnail);
//   - opening restores the selection: the slot's pack value matched to a design case-insensitively,
//     its color (a legacy bare value shows as blue); an unknown design or color selects nothing,
//     and a non-pack value selects None; opening never changes the slot;
//   - a design tile opens its variant row (clicking it again closes it); only one row is open;
//   - the variant row lists the design's colors in canonical palette order (PACK_COLORS);
//   - a variant click commits "pack:<name>_<color>" (blue too, never a bare value); None commits
//     null; Cancel, an outside click or Escape change nothing;
//   - Escape closes the variant row first (focus back on its tile), then the picker;
//   - in the row, the arrow keys move between colors (wrapping), Home and End go to the ends,
//     and focus starts on the selected color;
//   - a variant whose image is missing is disabled and can never commit.
// The page put the variant row in a CSS grid with dense flow; the model lays the tiles out in
// rows and inserts the variant row as a full row right under the clicked tile's row, which is
// what the dense flow drew (spec #16, Architecture: a row-based model).

using RST.Core.Ribbon;

namespace RST.Core.Builder;

/// <summary>How the picker ended: a value committed (null clears the icon), or cancelled.</summary>
public sealed record IconPickerOutcome(bool Committed, string? Value)
{
    public static readonly IconPickerOutcome Cancelled = new(false, null);

    public static IconPickerOutcome Commit(string? value) => new(true, value);
}

/// <summary>The keys the variant row handles.</summary>
public enum PickerKey
{
    Left,
    Right,
    Up,
    Down,
    Home,
    End,
}

/// <summary>One laid-out row: tiles (null entry = the None tile), or the open design's variant row.</summary>
public sealed record IconPickerRow(bool IsVariantRow, IReadOnlyList<IconPackEntry?> Tiles, IReadOnlyList<string> Colors);

public sealed class IconPickerModel
{
    private readonly HashSet<string> _broken = new(StringComparer.Ordinal);
    private readonly IconPackValue? _parsed;

    /// <param name="entries">The icon pack catalogue (one entry per design with a complete color set).</param>
    /// <param name="currentIconFile">The slot's <c>iconFile</c> when the picker opened.</param>
    public IconPickerModel(IReadOnlyList<IconPackEntry>? entries, string? currentIconFile)
    {
        Entries = (entries ?? []).Where(e => e is not null && !string.IsNullOrEmpty(e.Name) && e.Colors is not null).ToList();
        CurrentIconFile = currentIconFile;
        _parsed = IconPack.TryParseValue(currentIconFile, out var parsed) ? parsed : null;
        if (_parsed is not null)
        {
            var want = _parsed.Name.ToLowerInvariant();
            SelectedDesign = Entries.FirstOrDefault(e => e.Name.ToLowerInvariant() == want)?.Name;
        }
        // Legacy bare values display as blue without being rewritten.
        SelectedColor = SelectedDesign is not null ? _parsed!.Color ?? "blue" : null;
    }

    /// <summary>The canonical palette order of the variant row (PACK_COLORS).</summary>
    public static IReadOnlyList<string> PackColors => IconPack.CanonicalColors;

    public IReadOnlyList<IconPackEntry> Entries { get; }

    /// <summary>The value the slot had (never rewritten by opening or cancelling).</summary>
    public string? CurrentIconFile { get; }

    /// <summary>The design the slot uses (canonical name), or null.</summary>
    public string? SelectedDesign { get; }

    /// <summary>The selected design's color ("blue" for a legacy bare value), or null.</summary>
    public string? SelectedColor { get; }

    /// <summary>The None tile is selected when the slot has no pack value.</summary>
    public bool NoneSelected => _parsed is null;

    /// <summary>The design whose variant row is open, or null.</summary>
    public string? OpenDesign { get; private set; }

    /// <summary>The open design's colors, in palette order.</summary>
    public IReadOnlyList<string> OpenColors =>
        OpenDesign is null ? [] : PackColors.Where(c => Entries.First(e => e.Name == OpenDesign).Colors.Contains(c)).ToList();

    /// <summary>The focused variant in the open row, or null.</summary>
    public string? FocusedColor { get; private set; }

    /// <summary>The design tile that takes focus back after its row closed (Escape, or a second click).</summary>
    public string? FocusedTile { get; private set; }

    /// <summary>Set once the picker ends; the window then closes.</summary>
    public IconPickerOutcome? Outcome { get; private set; }

    public bool IsOpen => Outcome is null;

    /// <summary>A variant that can be picked (its image exists).</summary>
    public bool IsVariantEnabled(string design, string color) => !_broken.Contains(design + "|" + color);

    /// <summary>The variant's image could not be loaded: it is disabled and never applies.</summary>
    public void MarkBroken(string design, string color)
    {
        _broken.Add(design + "|" + color);
        if (OpenDesign == design && FocusedColor == color) FocusedColor = EnabledOpenColors().FirstOrDefault();
    }

    /// <summary>A design tile: opens its variant row, or closes it when it is the open one. Never commits.</summary>
    public void ClickTile(string design)
    {
        if (!IsOpen) return;
        if (OpenDesign == design)
        {
            HideRow(refocus: true);
            return;
        }
        if (Entries.All(e => e.Name != design)) return;
        OpenDesign = design;
        FocusedTile = null;
        var enabled = EnabledOpenColors();
        FocusedColor = design == SelectedDesign && SelectedColor is not null && enabled.Contains(SelectedColor)
            ? SelectedColor
            : enabled.FirstOrDefault();
    }

    /// <summary>A variant: commits <c>pack:&lt;design&gt;_&lt;color&gt;</c> (a disabled variant does nothing).</summary>
    public void ClickVariant(string color)
    {
        if (!IsOpen || OpenDesign is null || !OpenColors.Contains(color) || !IsVariantEnabled(OpenDesign, color)) return;
        Outcome = IconPickerOutcome.Commit(IconPack.ValuePrefix + OpenDesign + "_" + color);
        OpenDesign = null;
    }

    /// <summary>The None tile: commits null (no icon).</summary>
    public void ClickNone()
    {
        if (!IsOpen) return;
        OpenDesign = null;
        Outcome = IconPickerOutcome.Commit(null);
    }

    /// <summary>Escape: closes the variant row (focus back on its tile), or else the picker.</summary>
    public void Escape()
    {
        if (!IsOpen) return;
        if (OpenDesign is not null) HideRow(refocus: true);
        else Cancel();
    }

    /// <summary>Cancel, or a click outside the card: nothing changes.</summary>
    public void Cancel()
    {
        if (!IsOpen) return;
        OpenDesign = null;
        Outcome = IconPickerOutcome.Cancelled;
    }

    /// <summary>The variant row's keys: arrows rove (wrapping), Home and End go to the ends.</summary>
    public void Key(PickerKey key)
    {
        var colors = EnabledOpenColors();
        if (OpenDesign is null || colors.Count == 0 || FocusedColor is null) return;
        var i = colors.IndexOf(FocusedColor);
        if (i < 0) return;
        FocusedColor = key switch
        {
            PickerKey.Right or PickerKey.Down => colors[(i + 1) % colors.Count],
            PickerKey.Left or PickerKey.Up => colors[(i + colors.Count - 1) % colors.Count],
            PickerKey.Home => colors[0],
            PickerKey.End => colors[^1],
            _ => FocusedColor,
        };
    }

    /// <summary>
    /// The grid in rows of <paramref name="columns"/> tiles (the None tile first), with the open
    /// design's variant row as a full row right under the row that holds its tile.
    /// </summary>
    public IReadOnlyList<IconPickerRow> Rows(int columns)
    {
        if (columns < 1) columns = 1;
        var tiles = new List<IconPackEntry?> { null };
        tiles.AddRange(Entries);
        var rows = new List<IconPickerRow>();
        for (var start = 0; start < tiles.Count; start += columns)
        {
            var chunk = tiles.Skip(start).Take(columns).ToList();
            rows.Add(new IconPickerRow(false, chunk, []));
            if (OpenDesign is not null && chunk.Any(t => t?.Name == OpenDesign))
                rows.Add(new IconPickerRow(true, [], OpenColors));
        }
        return rows;
    }

    private List<string> EnabledOpenColors() =>
        OpenDesign is null ? [] : OpenColors.Where(c => IsVariantEnabled(OpenDesign, c)).ToList();

    private void HideRow(bool refocus)
    {
        FocusedTile = refocus ? OpenDesign : null;
        OpenDesign = null;
        FocusedColor = null;
    }
}
