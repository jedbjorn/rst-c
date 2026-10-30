// rst-c's own (not vendored): the ribbon preview's rules shared by the Loader and the Builder
// (spec #16, WU3): hexToRgba (profile_loader.html 1161-1171, profile_builder.html 2243-2253),
// the effective opacity, and the slot label and icon rules of renderTabPreview / renderPreview.

using RST.Core.Profiles;
using RST.Core.Ribbon;

namespace RST.Core.Loader;

/// <summary>A panel's preview tint: the profile color at the effective opacity (alpha 0-1).</summary>
public readonly record struct PreviewColor(byte R, byte G, byte B, double A)
{
    /// <summary>The CSS form the HTML used, e.g. <c>rgba(79,142,247,0.500)</c>.</summary>
    public string ToCss() => FormattableString.Invariant($"rgba({R},{G},{B},{A:0.000})");

    /// <summary>The alpha as a byte (0-255).</summary>
    public byte Alpha => (byte)Math.Round(Math.Clamp(A, 0, 1) * 255);
}

/// <summary>Which page's preview: the Loader's (read only) or the Builder's (live, with stubs for empty stacks).</summary>
public enum RibbonPreviewMode
{
    Loader,
    Builder,
}

public static class RibbonPreviewRules
{
    /// <summary>The tint for a malformed color: the default panel blue, fully opaque.</summary>
    public static readonly PreviewColor Fallback = new(79, 142, 247, 1);

    /// <summary>
    /// <c>hexToRgba</c>: "#RRGGBB" (the # optional, surrounding spaces ignored) at
    /// <paramref name="opacityPercent"/> (null is 100; clamped 0-100). Anything that is not six hex
    /// digits gives <see cref="Fallback"/> (opaque, whatever the opacity).
    /// </summary>
    public static PreviewColor PanelColor(string? hex, int? opacityPercent)
    {
        var h = (hex ?? "").Trim();
        if (h.StartsWith('#')) h = h[1..];
        if (h.Length != 6 || !h.All(Uri.IsHexDigit)) return Fallback;
        var r = Convert.ToByte(h[..2], 16);
        var g = Convert.ToByte(h[2..4], 16);
        var b = Convert.ToByte(h[4..6], 16);
        var pct = opacityPercent ?? 100;
        return new PreviewColor(r, g, b, Math.Max(0, Math.Min(100, pct)) / 100.0);
    }

    /// <summary>
    /// The opacity the preview draws with: the Loader's override when set, else the profile's
    /// <see cref="Profile.PanelOpacity"/>.
    /// </summary>
    public static int EffectiveOpacity(Profile profile, int? overrideOpacity) => overrideOpacity ?? profile.PanelOpacity;

    /// <summary>A tool slot's label: the Loader shows <c>baseName || name</c>, the Builder <c>name</c>.</summary>
    public static string ToolLabel(Slot slot, RibbonPreviewMode mode) =>
        mode == RibbonPreviewMode.Loader && !string.IsNullOrEmpty(slot.BaseName) ? slot.BaseName! : slot.Name ?? "";

    /// <summary>
    /// A tool slot's pack icon (spec #16, Planner default 9: the Loader renders pack icons too), or
    /// null for a non-pack value (drawn as ⬜). Same parser as the ribbon (<see cref="IconPack"/>).
    /// </summary>
    public static IconPackValue? PackIcon(Slot slot) =>
        IconPack.TryParseValue(slot.IconFile, out var value) ? value : null;

    /// <summary>The panel color the Builder falls back to when a panel has none.</summary>
    public const string DefaultPanelColor = "#4f8ef7";
}
