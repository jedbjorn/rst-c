// Copied from splash-it src/SplashIt.Core/Colors/Rgb.cs (splash-it commit ba86a71; originally gantt-it src/GanttIt.Core/Model/Rgb.cs, gantt-it commit 1e27832), MIT, same owner.
// Changes: namespaces (SplashIt.* to RST.*); the doc comment no longer points at splash-it's ColorMath, which is not vendored.

using System.Globalization;

namespace RST.Core.Colors;

/// <summary>An opaque 8-bit RGB color. Serialized as <c>#RRGGBB</c>.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary>Formats as upper-case <c>#RRGGBB</c>.</summary>
    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    public override string ToString() => ToHex();

    /// <summary>
    /// Parses <c>#RRGGBB</c> leniently: the leading <c>#</c> is optional, case and
    /// surrounding whitespace are ignored.
    /// </summary>
    public static bool TryParse(string? text, out Rgb color)
    {
        color = default;
        if (text is null) return false;
        var s = text.Trim();
        if (s.StartsWith('#')) s = s[1..];
        if (s.Length != 6 || !uint.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v))
            return false;
        color = new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    /// <summary>Parses <c>#RRGGBB</c>; throws <see cref="FormatException"/> on bad input.</summary>
    public static Rgb Parse(string text) =>
        TryParse(text, out var c) ? c : throw new FormatException($"'{text}' is not a #RRGGBB color.");
}
