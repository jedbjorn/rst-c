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

    // Schemes we are willing to hand to ShellExecute. Deliberately narrow:
    // a profile is shareable/importable (untrusted), and ShellExecute on a
    // file:///UNC/ftp:// target runs an arbitrary local or remote executable.
    // Only "open a web page / start an email or call" is permitted.
    private static readonly System.Collections.Generic.HashSet<string> LaunchableSchemes =
        new(StringComparer.OrdinalIgnoreCase) { "http", "https", "mailto", "tel" };

    /// <summary>
    /// Validate-before-launch gate for URL slots. Canonicalises the payload
    /// via <see cref="Normalize"/>, then parses it as an absolute URI and
    /// returns true ONLY when the scheme is one we are willing to pass to
    /// <c>Process.Start(UseShellExecute=true)</c> (http/https/mailto/tel).
    /// Returns false for file://, ftp://, UNC paths ("\\server\share"),
    /// drive/file paths ("C:\…"), javascript:/data:/vbscript:/ms-* and
    /// anything else — the caller must refuse to launch and surface an
    /// "unsupported link" message rather than execute it.
    /// </summary>
    public static bool TryGetLaunchTarget(string? input, out string target)
    {
        target = string.Empty;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var trimmed = input!.Trim();

        // A backslash never appears in a legitimate http/mailto/tel target or
        // a bare hostname/email — but it is the signature of a UNC share,
        // drive path, or Windows file path. Refuse outright; those are exactly
        // the inputs that turn ShellExecute into arbitrary code execution.
        if (trimmed.IndexOf('\\') >= 0) return false;

        var normalized = Normalize(trimmed);
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)) return false;
        if (!LaunchableSchemes.Contains(uri.Scheme)) return false;

        target = normalized;
        return true;
    }
}
