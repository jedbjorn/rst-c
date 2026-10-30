// Adapted from splash-it src/SplashIt.Core/Theming/UiPalette.cs (splash-it commit ba86a71; originally gantt-it src/GanttIt.Core/Theming/UiPalette.cs, gantt-it commit a96d0a1), MIT, same owner.
// Changes: namespaces (SplashIt.* to RST.*); doc comments name RST; rst-c additions: Success/SuccessSoft/SuccessBorder and Scrim keys and their contrast requirements (card "active" border, Loaded badge, success banner, busy overlay), and the requirements for the new ToggleSwitch, TabItem, TreeViewItem, card ListBoxItem, GridSplitter, busy indicator and transient banner styles; the Health window's CautionText key and HealthRequirements (severity colors, range pills).

using System.Globalization;

namespace RST.Core.Theming;

/// <summary>The RST window theme: Revit's UI theme, detected by the Revit side and passed in (spec #16, Theme following).</summary>
public enum UiTheme
{
    Light,
    Dark,
}

/// <summary>An 8-bit ARGB UI color, parsed from <c>#RRGGBB</c> or <c>#AARRGGBB</c>.</summary>
public readonly record struct UiColor(byte A, byte R, byte G, byte B)
{
    public bool IsOpaque => A == 255;

    public override string ToString() => IsOpaque ? $"#{R:X2}{G:X2}{B:X2}" : $"#{A:X2}{R:X2}{G:X2}{B:X2}";

    /// <summary>Parses <c>#RRGGBB</c> or <c>#AARRGGBB</c>; throws <see cref="FormatException"/> on bad input.</summary>
    public static UiColor Parse(string text)
    {
        var s = text.StartsWith('#') ? text[1..] : text;
        if ((s.Length != 6 && s.Length != 8)
            || !uint.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v))
            throw new FormatException($"'{text}' is not a #RRGGBB or #AARRGGBB color.");
        var a = s.Length == 8 ? (byte)(v >> 24) : (byte)255;
        return new UiColor(a, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    /// <summary>This color composited over an opaque <paramref name="background"/>.</summary>
    public UiColor Over(UiColor background)
    {
        if (IsOpaque) return this;
        var a = A / 255.0;
        byte Mix(byte f, byte b) => (byte)Math.Round(f * a + b * (1 - a));
        return new UiColor(255, Mix(R, background.R), Mix(G, background.G), Mix(B, background.B));
    }

    /// <summary>WCAG 2.x relative luminance of the (opaque) color, 0 to 1.</summary>
    public double RelativeLuminance()
    {
        static double Channel(byte c)
        {
            var s = c / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(R) + 0.7152 * Channel(G) + 0.0722 * Channel(B);
    }

    /// <summary>WCAG 2.x contrast ratio (1 to 21) of <paramref name="foreground"/> drawn on an opaque <paramref name="background"/>.</summary>
    public static double ContrastRatio(UiColor foreground, UiColor background)
    {
        var f = foreground.Over(background).RelativeLuminance();
        var b = background.RelativeLuminance();
        return (Math.Max(f, b) + 0.05) / (Math.Min(f, b) + 0.05);
    }
}

/// <summary>A foreground/background pair of palette keys that must keep <see cref="Minimum"/> contrast in both themes.</summary>
public sealed record ContrastRequirement(string Foreground, string Background, double Minimum, string Use);

/// <summary>
/// The two RST window palettes, as in gantt-it. The keys are the WPF resource keys
/// the add-in's XAML uses; the Revit add-in turns each entry into a frozen brush
/// when a window opens. Kept here as plain data, with no WPF reference, so the
/// Core tests can check every text/background pair against WCAG AA.
/// </summary>
public static class UiPalette
{
    public const string Window = "WindowBrush";
    public const string Surface = "SurfaceBrush";
    public const string SurfaceAlt = "SurfaceAltBrush";
    public const string Border = "BorderBrush";
    public const string StrongBorder = "StrongBorderBrush";
    public const string Divider = "DividerBrush";
    public const string Text = "TextBrush";
    public const string MutedText = "MutedTextBrush";
    public const string Accent = "AccentBrush";
    public const string AccentHover = "AccentHoverBrush";
    public const string AccentSoft = "AccentSoftBrush";
    public const string AccentForeground = "AccentForegroundBrush";
    public const string OnAccent = "OnAccentBrush";
    public const string Danger = "DangerBrush";
    public const string DangerHover = "DangerHoverBrush";
    public const string DangerForeground = "DangerForegroundBrush";
    public const string DangerBorder = "DangerBorderBrush";
    public const string OnDanger = "OnDangerBrush";
    public const string Error = "ErrorBrush";
    public const string ErrorSoft = "ErrorSoftBrush";
    public const string ErrorBorder = "ErrorBorderBrush";
    public const string ErrorRow = "ErrorRowBrush";
    public const string WarningSoft = "WarningSoftBrush";
    public const string WarningBorder = "WarningBorderBrush";
    public const string WarningText = "WarningTextBrush";
    public const string InfoSoft = "InfoSoftBrush";
    public const string InfoBorder = "InfoBorderBrush";
    public const string Hover = "HoverBrush";
    public const string Pressed = "PressedBrush";
    public const string ChipBorder = "ChipBorderBrush";
    public const string ChipHoverBorder = "ChipHoverBorderBrush";
    public const string ToolTip = "ToolTipBrush";
    public const string ToolTipText = "ToolTipTextBrush";
    public const string ToolTipBorder = "ToolTipBorderBrush";
    public const string ScrollTrack = "ScrollTrackBrush";
    public const string ScrollThumb = "ScrollThumbBrush";
    public const string ScrollThumbHover = "ScrollThumbHoverBrush";
    public const string ScrollThumbPressed = "ScrollThumbPressedBrush";
    public const string ScrollGlyph = "ScrollGlyphBrush";
    public const string CheckFill = "CheckFillBrush";
    public const string CheckFillHover = "CheckFillHoverBrush";
    public const string CheckMark = "CheckMarkBrush";

    // rst-c additions (not in splash-it or gantt-it).

    /// <summary>Success text and strokes: the Loader's "Loaded" badge, the active profile card's border, success pills.</summary>
    public const string Success = "SuccessBrush";

    /// <summary>Success fill behind text: the success banner and pill.</summary>
    public const string SuccessSoft = "SuccessSoftBrush";

    /// <summary>The success banner's and pill's outline (decorative, like InfoBorder).</summary>
    public const string SuccessBorder = "SuccessBorderBrush";

    /// <summary>The translucent wash behind a window's busy overlay (decorative; never under text).</summary>
    public const string Scrim = "ScrimBrush";

    // Health window additions (spec #16, WU2).

    /// <summary>The Health tab's soft-warning severity (warn-soft): text, dots and sub-lines, between OK and WarningText.</summary>
    public const string CautionText = "CautionTextBrush";

    /// <summary>
    /// Light: gantt-it's original palette. Accent and danger keep one hue for fills
    /// and for text; tooltips and scroll bars keep the Windows (Aero2) colors they had.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, UiColor> LightPalette = Build(
        (Window, "#F4F5F7"),
        (Surface, "#FFFFFF"),
        (SurfaceAlt, "#F9FAFB"),
        (Border, "#D3D7DE"),
        (StrongBorder, "#6B7380"),
        (Divider, "#E6E8EC"),
        (Text, "#1F2328"),
        (MutedText, "#5E6672"),
        (Accent, "#2F6AD0"),
        (AccentHover, "#2559B4"),
        (AccentSoft, "#E8F0FD"),
        (AccentForeground, "#2F6AD0"),
        (OnAccent, "#FFFFFF"),
        (Danger, "#B3261E"),
        (DangerHover, "#931E18"),
        (DangerForeground, "#B3261E"),
        (DangerBorder, "#E3B5B1"),
        (OnDanger, "#FFFFFF"),
        (Error, "#C42B1C"),
        (ErrorSoft, "#FDEDEC"),
        (ErrorBorder, "#E7AFA9"),
        (ErrorRow, "#FFFBFA"),
        (WarningSoft, "#FFF5E0"),
        (WarningBorder, "#E9C27A"),
        (WarningText, "#8A5A00"),
        (InfoSoft, "#EAF1FC"),
        (InfoBorder, "#A9C3EC"),
        (Hover, "#EEF0F3"),
        (Pressed, "#E2E5EA"),
        (ChipBorder, "#40000000"),
        (ChipHoverBorder, "#A0000000"),
        (ToolTip, "#F1F2F7"),
        (ToolTipText, "#575757"),
        (ToolTipBorder, "#767676"),
        (ScrollTrack, "#F0F0F0"),
        (ScrollThumb, "#CDCDCD"),
        (ScrollThumbHover, "#A6A6A6"),
        (ScrollThumbPressed, "#606060"),
        (ScrollGlyph, "#606060"),
        (CheckFill, "#2F6AD0"),
        (CheckFillHover, "#2559B4"),
        (CheckMark, "#FFFFFF"),
        (Success, "#187534"),
        (SuccessSoft, "#EAF6EE"),
        (SuccessBorder, "#9CD1AC"),
        (Scrim, "#59000000"),
        // Health window additions.
        (CautionText, "#716500"));

    /// <summary>
    /// Dark: neutral blue-greys that sit with Revit's dark frame. Filled accent and
    /// danger buttons keep the light hues (white text stays above 4.5:1); accent and
    /// danger used as text or strokes on dark surfaces, and the checked check box
    /// (with a dark tick), are lighter tints.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, UiColor> DarkPalette = Build(
        (Window, "#1F2329"),
        (Surface, "#2A2F37"),
        (SurfaceAlt, "#252A31"),
        (Border, "#525B67"),
        (StrongBorder, "#8C96A3"),
        (Divider, "#3A414B"),
        (Text, "#E6E9ED"),
        (MutedText, "#AEB6C1"),
        (Accent, "#2F6AD0"),
        (AccentHover, "#2559B4"),
        (AccentSoft, "#213454"),
        (AccentForeground, "#79AEFF"),
        (OnAccent, "#FFFFFF"),
        (Danger, "#B3261E"),
        (DangerHover, "#931E18"),
        (DangerForeground, "#FF9C94"),
        (DangerBorder, "#7A3B37"),
        (OnDanger, "#FFFFFF"),
        (Error, "#FF8F85"),
        (ErrorSoft, "#3E2627"),
        (ErrorBorder, "#84413C"),
        (ErrorRow, "#342829"),
        (WarningSoft, "#3A3123"),
        (WarningBorder, "#806633"),
        (WarningText, "#EDB95A"),
        (InfoSoft, "#1E2D44"),
        (InfoBorder, "#3E5C88"),
        (Hover, "#353B44"),
        (Pressed, "#3E454F"),
        (ChipBorder, "#70FFFFFF"),
        (ChipHoverBorder, "#E0FFFFFF"),
        (ToolTip, "#2F353E"),
        (ToolTipText, "#E6E9ED"),
        (ToolTipBorder, "#5F6874"),
        (ScrollTrack, "#252A31"),
        (ScrollThumb, "#5E6773"),
        (ScrollThumbHover, "#76808C"),
        (ScrollThumbPressed, "#949EAA"),
        (ScrollGlyph, "#AEB6C1"),
        (CheckFill, "#79AEFF"),
        (CheckFillHover, "#9CC3FF"),
        (CheckMark, "#1F2329"),
        (Success, "#5CC98A"),
        (SuccessSoft, "#1F3A2B"),
        (SuccessBorder, "#3C7A55"),
        (Scrim, "#8C000000"),
        // Health window additions.
        (CautionText, "#DDC650"));

    /// <summary>The palette for <paramref name="theme"/>: resource key to color.</summary>
    public static IReadOnlyDictionary<string, UiColor> For(UiTheme theme) =>
        theme == UiTheme.Dark ? DarkPalette : LightPalette;

    /// <summary>
    /// rst-c additions (declared before <see cref="Requirements"/>, which includes them: static
    /// initializers run in order): the pairings drawn by the styles rst-c adds to Theme.xaml (ToggleSwitch,
    /// TabItem, TreeViewItem, CardListBoxItem, GridSplitter, the busy indicator, TransientBanner
    /// and the pills). Every new palette key that sits under or over text is listed here.
    /// </summary>
    public static IReadOnlyList<ContrastRequirement> RstRequirements { get; } =
    [
        .. Text4(Success, [Window, Surface, SurfaceAlt, Hover, SuccessSoft],
            "Loaded badge, success pill, active card border (also 3:1 as a stroke)"),
        .. Text4(Text, [SuccessSoft], "success banner and transient banner text"),
        .. Text4(MutedText, [AccentSoft, SuccessSoft], "secondary text in a selected tree row, pill or banner"),
        .. Text4(WarningText, [WarningSoft], "warning pill"),
        .. Text4(Error, [ErrorSoft], "error pill"),
        .. NonText3(StrongBorder, [Window, SurfaceAlt, Hover], "toggle switch track and knob when off, splitter under the pointer"),
        .. NonText3(CheckFill, [Window, SurfaceAlt, Hover], "toggle switch track when on"),
        .. NonText3(CheckFillHover, [Window, SurfaceAlt, Hover], "toggle switch track when on, under the pointer"),
        .. NonText3(AccentForeground, [Hover, SurfaceAlt], "selected tab underline, selected card border, busy indicator"),
    ];

    /// <summary>
    /// Health window additions (spec #16, WU2; declared before <see cref="Requirements"/>, which includes
    /// them): the severity colors on the section rows, stat cards and section headers (dots, also under
    /// the pointer), and the range pills' selected state.
    /// </summary>
    public static IReadOnlyList<ContrastRequirement> HealthRequirements { get; } =
    [
        .. Text4(CautionText, [Window, Surface, SurfaceAlt, Hover], "soft-warning values and sub-lines, severity dot"),
        .. NonText3(WarningText, [Hover], "warning severity dot in a section header under the pointer"),
        .. NonText3(Error, [Hover], "danger severity dot in a section header under the pointer"),
        .. NonText3(AccentForeground, [AccentSoft], "selected range pill outline"),
    ];

    /// <summary>
    /// Every text (4.5:1, WCAG AA body text) and non-text (3:1, WCAG 1.4.11 focus
    /// indicators, glyphs and control boundaries) pairing the XAML draws, checked
    /// for both palettes by the Core tests.
    /// </summary>
    public static IReadOnlyList<ContrastRequirement> Requirements { get; } =
    [
        .. Text4(Text, [Window, Surface, SurfaceAlt, Hover, Pressed, AccentSoft, InfoSoft, WarningSoft, ErrorSoft, ErrorRow], "body text"),
        .. Text4(MutedText, [Window, Surface, SurfaceAlt, Hover, Pressed, ErrorRow, InfoSoft, WarningSoft, ErrorSoft],
            "labels, hints, icon buttons, menu shortcuts"),
        .. Text4(Error, [Window, Surface, ErrorSoft, ErrorRow], "validation messages"),
        .. Text4(DangerForeground, [Surface, Hover, Pressed], "outlined delete buttons"),
        .. Text4(WarningText, [Surface], "card status line"),
        .. Text4(OnAccent, [Accent, AccentHover], "primary buttons, selected calendar day (and its today ring)"),
        .. Text4(OnDanger, [Danger, DangerHover], "filled delete button"),
        new(ToolTipText, ToolTip, 4.5, "tooltips"),
        new(AccentForeground, Window, 3, "focus ring"),
        new(AccentForeground, Surface, 3, "focus ring, today in the calendar, drop marker"),
        new(AccentForeground, SurfaceAlt, 3, "New chart glyph"),
        new(StrongBorder, Surface, 3, "check box outline"),
        new(CheckFill, Surface, 3, "checked check box"),
        new(CheckFill, ErrorRow, 3, "checked check box in an invalid row"),
        new(CheckFillHover, Surface, 3, "checked check box under the pointer"),
        new(CheckMark, CheckFill, 3, "check mark"),
        new(CheckMark, CheckFillHover, 3, "check mark under the pointer"),
        new(ScrollGlyph, ScrollTrack, 3, "scroll bar arrows"),
        .. RstRequirements,
        .. HealthRequirements,
    ];

    private static IEnumerable<ContrastRequirement> NonText3(string foreground, string[] backgrounds, string use) =>
        backgrounds.Select(b => new ContrastRequirement(foreground, b, 3, use));

    private static IEnumerable<ContrastRequirement> Text4(string foreground, string[] backgrounds, string use) =>
        backgrounds.Select(b => new ContrastRequirement(foreground, b, 4.5, use));

    private static Dictionary<string, UiColor> Build(params (string Key, string Hex)[] entries) =>
        entries.ToDictionary(e => e.Key, e => UiColor.Parse(e.Hex), StringComparer.Ordinal);
}
