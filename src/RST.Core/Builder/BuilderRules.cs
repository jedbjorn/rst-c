// rst-c's own (not vendored): the Builder's small rules (spec #16, WU4), ported from
// profile_builder.html: shortId (1936-1940), addPanel's defaults (1943-1951), the slot shapes of
// addToolToSelectedPanel (1974-1986), normalizeUrl
// (2214-2225, the same rule as Core's UrlNormalizer), normalizeHex (2230-2237).
// Behavior and text are unchanged. Feature #18 (WU3) replaces the card's "N slot(s)" count and the
// Pick-icon button's label (3237-3256) with the tool count and the tiles' source line and tooltip.
// The RST Presets and Junk Cleanup summaries went with the Properties pane (feature #18, spec #19):
// the Profile pane's tabs show the settings themselves. The URL button editor's slot (UrlSlot) is
// retired with it: Custom Tools' Create new makes URL and email tools (feature #18, WU4).

using System.Text.RegularExpressions;
using RST.Core.Profiles;
using RST.Core.Ribbon;

namespace RST.Core.Builder;

public static partial class BuilderRules
{
    /// <summary>A new panel's color (and the color a malformed one shows as): <c>#4f8ef7</c>.</summary>
    public const string DefaultPanelColor = "#4f8ef7";

    /// <summary>A URL button's command id prefix: the slot is <c>{ type:"tool", commandId:"URL:&lt;url&gt;" }</c>.</summary>
    public const string UrlCommandPrefix = "URL:";

    /// <summary>
    /// <c>shortId</c>: an id up to 18 characters is shown whole; a longer one as its first 8 and last
    /// 8 characters around an ellipsis. Empty for null.
    /// </summary>
    public static string ShortId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        if (id.Length <= 18) return id;
        return id[..8] + "…" + id[^8..];
    }

    /// <summary>The panel <c>addPanel</c> appends when the profile has <paramref name="existing"/> panels.</summary>
    public static Panel NewPanel(int existing) => new()
    {
        Name = "Panel " + (existing + 1),
        Color = DefaultPanelColor,
        Slots = [],
    };

    /// <summary>The tool slot the dragged catalogue placement becomes, keeping its own label, tab and panel.</summary>
    public static Slot ToolSlot(CatalogCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return new Slot
        {
            SlotType = "tool",
            Name = command.Label,
            CommandId = command.Id,
            SourceTab = string.IsNullOrEmpty(command.SourceTab) ? null : command.SourceTab,
            SourcePanel = string.IsNullOrEmpty(command.SourcePanel) ? null : command.SourcePanel,
        };
    }

    /// <summary>
    /// <c>normalizeUrl</c>: empty stays empty; a known scheme passes through; a bare e-mail address
    /// gets <c>mailto:</c>; anything else gets <c>https://</c>. Trimmed first. The rule is Core's
    /// <see cref="UrlNormalizer"/> (the JS said it mirrors it); only the whitespace-only case differs
    /// there, so the input is trimmed before it is handed over.
    /// </summary>
    public static string NormalizeUrl(string? value)
    {
        var s = (value ?? "").Trim();
        return s.Length == 0 ? "" : UrlNormalizer.Normalize(s);
    }

    /// <summary>
    /// <c>normalizeHex</c>: "#abc" and "#AABBCC" become "#aabbcc"; anything else is null (the picker
    /// then starts from the default blue).
    /// </summary>
    public static string? NormalizeHex(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var s = value.Trim().ToLowerInvariant();
        var m3 = ShortHex().Match(s);
        if (m3.Success) return "#" + m3.Groups[1].Value + m3.Groups[1].Value + m3.Groups[2].Value + m3.Groups[2].Value + m3.Groups[3].Value + m3.Groups[3].Value;
        return LongHex().IsMatch(s) ? s : null;
    }

    /// <summary>
    /// The panel card's count (feature #18): every tool on the panel, loose or in a stack, "1 tool",
    /// "5 tools". A stack itself is not counted.
    /// </summary>
    public static int ToolCount(Profile profile, Panel panel)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(panel);
        return panel.Slots.Sum(s => PanelStacks.IsStack(s) ? PanelStacks.ToolsOf(profile, s).Count : 1);
    }

    /// <summary>The count's text: "1 tool", "3 tools".</summary>
    public static string ToolCountText(int count) => count + " tool" + (count == 1 ? "" : "s");

    /// <summary>
    /// A tile's muted source line (feature #18): "tab · panel" for a command (just the tab when it
    /// has no panel; the short command id when it has neither), and the link for a URL or email tool,
    /// without its scheme ("wiki.example.com", "bim@studio.example"). The full id or link is the
    /// tile's tooltip.
    /// </summary>
    public static string SourceLine(Slot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        if (CustomTools.IsCustom(slot)) return CustomTools.ShortLink(CustomTools.LinkOf(slot));
        var tab = slot.SourceTab ?? "";
        var panel = slot.SourcePanel ?? "";
        if (tab.Length > 0 && panel.Length > 0) return tab + " · " + panel;
        if (tab.Length > 0 || panel.Length > 0) return tab + panel;
        return ShortId(slot.CommandId);
    }

    /// <summary>A tile's tooltip: the full link of a URL or email tool, or the full command id.</summary>
    public static string ToolTipOf(Slot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        return CustomTools.IsCustom(slot) ? CustomTools.LinkOf(slot) : slot.CommandId ?? "";
    }

    [GeneratedRegex("^#([0-9a-f])([0-9a-f])([0-9a-f])$")]
    private static partial Regex ShortHex();

    [GeneratedRegex("^#[0-9a-f]{6}$")]
    private static partial Regex LongHex();
}
