// rst-c's own (not vendored): the Builder's small rules (spec #16, WU4), ported from
// profile_builder.html: shortId (1936-1940), addPanel's defaults (1943-1951), the slot shapes of
// addToolToSelectedPanel (1974-1986) and the URL button editor (2170-2209), normalizeUrl
// (2214-2225, the same rule as Core's UrlNormalizer), normalizeHex (2230-2237), the panel card's
// "N slot(s)" count, the Pick-icon button's label (3237-3256), and the RST Presets and Junk
// Cleanup summaries (2629-2638, 2727-2734). Behavior and text are unchanged.

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

    /// <summary>The tool slot a catalogue command becomes (double-click, or a drop).</summary>
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

    /// <summary>The URL button the inline editor adds: a tool slot whose command id is <c>URL:&lt;url&gt;</c>.</summary>
    public static Slot UrlSlot(string label, string normalizedUrl) => new()
    {
        SlotType = "tool",
        Name = label,
        CommandId = UrlCommandPrefix + normalizedUrl,
    };

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

    /// <summary>The panel card's count: "1 slot", "3 slots".</summary>
    public static string SlotCount(int count) => count + " slot" + (count == 1 ? "" : "s");

    /// <summary>
    /// The Pick-icon button's text: the pack icon's design name, or "Icon" when the slot has no pack
    /// icon (the button then shows "+" instead of a thumbnail).
    /// </summary>
    public static string IconButtonLabel(Slot slot) =>
        IconPack.TryParseValue(slot?.IconFile, out var value) ? value.Name : "Icon";

    /// <summary>The RST Presets summary: "No tabs hidden  •  Disable Unused: off", "2 tabs hidden  •  …".</summary>
    public static string PresetsSummary(ProfilePresets? presets)
    {
        var p = presets ?? new ProfilePresets();
        var n = p.HiddenTabs?.Count ?? 0;
        var hidden = n == 0 ? "No tabs hidden" : n + " tab" + (n == 1 ? "" : "s") + " hidden";
        return hidden + "  •  " + "Disable Unused: " + (p.DisableUnusedAddins ? "on" : "off");
    }

    /// <summary>The Junk Cleanup summary: "5 of 6 targets enabled.", or "Defaults will be used." with no list.</summary>
    public static string CleanupSummary(IReadOnlyList<CleanupTarget>? targets)
    {
        if (targets is null) return "Defaults will be used.";
        var enabled = targets.Count(t => t is not null && t.Enabled);
        return enabled + " of " + targets.Count + " target" + (targets.Count == 1 ? "" : "s") + " enabled.";
    }

    [GeneratedRegex("^#([0-9a-f])([0-9a-f])([0-9a-f])$")]
    private static partial Regex ShortHex();

    [GeneratedRegex("^#[0-9a-f]{6}$")]
    private static partial Regex LongHex();
}
