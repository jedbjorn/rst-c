// ButtonLabel.cs — formatting helpers for Revit ribbon button labels.

namespace RST.Core.Ribbon;

public static class ButtonLabel
{
    /// <summary>
    /// Insert a line break at the first space at or after index 6, so long
    /// button labels render on two lines in the Revit ribbon.
    /// PushButtonData.Text honors '\n' as a line break in Revit's rendering.
    ///
    /// Rules:
    ///   - Names with length ≤ 6 are returned as-is.
    ///   - The first space at index ≥ 6 is replaced with '\n'. If no such
    ///     space exists (single long word, or all spaces are early), the
    ///     name is returned as-is.
    ///   - Persistence keeps the original (single-line) name; this
    ///     transform is applied only at ribbon-build time so user-edited
    ///     names don't get littered with '\n' and the on-disk JSON stays
    ///     clean.
    /// </summary>
    public static string Wrap(string? text)
    {
        if (string.IsNullOrEmpty(text) || text!.Length <= 6) return text ?? "";
        var idx = text.IndexOf(' ', 6);
        if (idx < 0) return text;
        return text.Substring(0, idx) + "\n" + text.Substring(idx + 1);
    }
}
