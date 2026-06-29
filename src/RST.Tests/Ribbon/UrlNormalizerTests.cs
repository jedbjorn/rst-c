// UrlNormalizerTests.cs — coverage for the runtime URL coercion that
// keeps Process.Start (UseShellExecute=true) from treating bare
// hostnames or emails as filenames.

using FluentAssertions;
using RST.Core.Ribbon;
using Xunit;

namespace RST.Tests.Ribbon;

public sealed class UrlNormalizerTests
{
    [Theory]
    [InlineData("http://example.com")]
    [InlineData("https://example.com/path?q=1")]
    [InlineData("HTTPS://EXAMPLE.COM")]
    [InlineData("mailto:foo@bar.com")]
    [InlineData("MAILTO:foo@bar.com")]
    [InlineData("ftp://files.example.com")]
    [InlineData("file:///C:/temp/x.txt")]
    [InlineData("tel:+15551234567")]
    public void Already_prefixed_passes_through(string input)
    {
        UrlNormalizer.Normalize(input).Should().Be(input.Trim());
    }

    [Theory]
    [InlineData("gmail.com", "https://gmail.com")]
    [InlineData("example.com/path", "https://example.com/path")]
    [InlineData("  example.com  ", "https://example.com")]
    [InlineData("internal-server", "https://internal-server")]
    public void Bare_hostnames_get_https(string input, string expected)
    {
        UrlNormalizer.Normalize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("support@example.com", "mailto:support@example.com")]
    [InlineData("a.b.c@example.co.uk", "mailto:a.b.c@example.co.uk")]
    public void Bare_emails_get_mailto(string input, string expected)
    {
        UrlNormalizer.Normalize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("user@host/path", "https://user@host/path")] // userinfo URL — '/' wins
    public void At_with_slash_treated_as_url(string input, string expected)
    {
        UrlNormalizer.Normalize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_passes_through(string? input)
    {
        UrlNormalizer.Normalize(input).Should().Be(input ?? string.Empty);
    }

    // ── TryGetLaunchTarget: the validate-before-ShellExecute gate ──────────
    // Note: Normalize() still passes file:// through unchanged (it is only
    // canonicalisation). The security boundary is TryGetLaunchTarget, which a
    // shareable/importable profile's URL must clear before Process.Start.

    [Theory]
    [InlineData("http://example.com", "http://example.com")]
    [InlineData("https://example.com/p?q=1", "https://example.com/p?q=1")]
    [InlineData("HTTPS://EXAMPLE.COM", "HTTPS://EXAMPLE.COM")]
    [InlineData("mailto:foo@bar.com", "mailto:foo@bar.com")]
    [InlineData("tel:+15551234567", "tel:+15551234567")]
    [InlineData("gmail.com", "https://gmail.com")]
    [InlineData("  example.com/path  ", "https://example.com/path")]
    [InlineData("support@example.com", "mailto:support@example.com")]
    public void Launch_allows_web_email_phone(string input, string expected)
    {
        UrlNormalizer.TryGetLaunchTarget(input, out var target).Should().BeTrue();
        target.Should().Be(expected);
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")] // local exe — RCE if launched
    [InlineData("file://attacker-host/share/payload.exe")] // remote UNC exe
    [InlineData("ftp://files.example.com")]
    [InlineData(@"\\server\share\Standards.pdf")] // bare UNC
    [InlineData(@"C:\Windows\System32\calc.exe")] // drive path
    [InlineData("vbscript:msgbox(1)")]
    [InlineData(null)]
    [InlineData("")]
    public void Launch_refuses_file_unc_and_other_schemes(string? input)
    {
        UrlNormalizer.TryGetLaunchTarget(input, out var target).Should().BeFalse();
        target.Should().BeEmpty();
    }
}
