// IconAssetsResolveTests.cs — production-path resolver tests for spec #9
// (Colored Icon Pack Picker). These invoke the real
// IconAssets.ResolveSlotIcon against the vendored pack as deployed next
// to the assembly — explicit variants, legacy blue aliases, malformed
// and path-like rejection, missing-file fallback, cache identity, and
// the SC-045 cache-key-collision regression. Windows-only (RST.Engine is
// WPF); the platform-agnostic parse matrix stays in RST.Tests.

using FluentAssertions;
using RST.Engine.Ribbon;
using Xunit;

namespace RST.RuntimeTests;

public sealed class IconAssetsResolveTests
{
    [Fact]
    public void Explicit_color_value_resolves_the_variant_png()
    {
        IconAssets.ResolveSlotIcon("pack:move_green").Should().NotBeNull(
            "pack:move_green must load icons/pack/32_move_green.png");
    }

    [Fact]
    public void Legacy_bare_value_resolves_the_blue_alias()
    {
        IconAssets.ResolveSlotIcon("pack:move").Should().NotBeNull(
            "pack:move must keep loading the 32_move.png blue compatibility alias");
    }

    [Fact]
    public void Underscore_name_resolves_bare_and_explicit()
    {
        IconAssets.ResolveSlotIcon("pack:link_external").Should().NotBeNull();
        IconAssets.ResolveSlotIcon("pack:link_external_purple").Should().NotBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pack:")]
    [InlineData("notpack")]
    [InlineData("pack:move|green")]
    [InlineData("pack:../escape")]
    [InlineData("pack:C:\\icons\\x.png")]
    [InlineData("pack:a/b")]
    public void Malformed_or_path_like_values_return_null(string? value)
    {
        IconAssets.ResolveSlotIcon(value).Should().BeNull(
            "unresolved values fall back to the caller's Default32");
    }

    [Fact]
    public void Missing_variant_file_returns_null_without_throwing()
    {
        // Parses as a bare name but ships no file — the resolver must log
        // and fall back, never break the ribbon build.
        IconAssets.ResolveSlotIcon("pack:move_fuchsia").Should().BeNull();
    }

    [Fact]
    public void Malformed_lookup_does_not_suppress_the_valid_icon()
    {
        // SC-045 regression: the malformed value must be rejected before
        // any filesystem lookup, so it can neither load nor poison the
        // normalized cache key it would have shared with the valid form.
        IconAssets.ResolveSlotIcon("pack:move|green").Should().BeNull();
        IconAssets.ResolveSlotIcon("pack:move_green").Should().NotBeNull(
            "a prior malformed lookup must not cache-suppress the valid icon");
    }

    [Fact]
    public void Resolved_images_are_cached_case_insensitively()
    {
        var a = IconAssets.ResolveSlotIcon("pack:move_green");
        var b = IconAssets.ResolveSlotIcon("pack:MOVE_GREEN");
        b.Should().BeSameAs(a, "the process cache keys by normalized pack key, OrdinalIgnoreCase");
    }
}
