// UrlNormalizer.cs — coerce user-typed URL slots into something
// ShellExecute will actually open.
//
// Without a recognized scheme prefix (http://, https://, mailto:, ...),
// ShellExecute treats the input as a filename and the call fails with
// Win32Exception(2) "system cannot find the file specified". The
// builder lets users type plain hostnames ("gmail.com") or bare email
// addresses ("support@example.com"), so the runtime URL launchers
// (SlotCommandBase URL slots, branding-panel UrlClickCommand) need to
// canonicalise before handing the value to Process.Start.
//
// Rules (deliberately simple — no validation):
//   - Already-prefixed (http://, https://, mailto:, ftp://, file://, tel:)
//     → pass through unchanged.
//   - Contains '@' and no '/'                → "mailto:" prefix.
//   - Anything else                          → "https://" prefix.

using System;

namespace RST.Core.Ribbon;

public static class UrlNormalizer
{
    private static readonly string[] KnownSchemes =
    {
        "http://", "https://", "mailto:", "ftp://", "file://", "tel:",
    };

    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return input ?? string.Empty;
        var trimmed = input!.Trim();

        foreach (var scheme in KnownSchemes)
        {
            if (trimmed.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                return trimmed;
        }

        if (trimmed.IndexOf('@') >= 0 && trimmed.IndexOf('/') < 0)
            return "mailto:" + trimmed;

        return "https://" + trimmed;
    }
}
