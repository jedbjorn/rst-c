// IconAssetsResolveTests.cs — production-path resolver tests for spec #9
// (Colored Icon Pack Picker). These invoke the real
// IconAssets.ResolveSlotIcon against the vendored pack as deployed next
// to the assembly — explicit variants, legacy blue aliases, malformed
// and path-like rejection, missing-file fallback, cache identity, and
// the SC-045 cache-key-collision regression. Windows-only (RST.Engine is
// WPF); the platform-agnostic parse matrix stays in RST.Tests.

using System.Windows.Media.Imaging;
using FluentAssertions;
using RST.Core.Profiles;
using RST.Core.Ribbon;
using RST.UI.Controls;
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

    [Theory]
    [InlineData("GanttIt.Open", "gantt")]
    [InlineData("SplashIt.OpenSettings", "splash")]
    [InlineData("SplashIt.GroupTabs", "group")]
    public void Companion_artwork_loads_at_both_sizes_and_themes_on_ribbon_and_builder(string command, string icon)
    {
        var slot = new Slot { CommandId = "CustomCtrl_%CustomCtrl_%Add-Ins%Companion%" + command };
        var button = new PlannedButton(slot, SlotTarget.Parse(slot.CommandId, "Tool"), "Tool", "id", PlannedButtonSize.Large);
        foreach (var size in new[] { 16, 32 })
            foreach (var dark in new[] { false, true })
            {
                var expected = $"{icon}_{size}{(dark ? "_dark" : "")}.png";
                var image = (size == 16 ? ProfileTabBuilder.Icon16(button, dark) : ProfileTabBuilder.Icon32(button, dark))
                    .Should().BeOfType<BitmapImage>().Subject;
                image.PixelWidth.Should().Be(size);
                image.PixelHeight.Should().Be(size);
                image.IsFrozen.Should().BeTrue();
                image.UriSource.LocalPath.Should().EndWith(expected);
                IconAssets.ResolveCompanionIcon(slot, size, dark).Should().BeSameAs(image);

                var builder = PackIcons.ForSlot(slot, size: size, dark: dark).Should().BeOfType<BitmapImage>().Subject;
                builder.PixelWidth.Should().Be(size);
                builder.PixelHeight.Should().Be(size);
                builder.UriSource.LocalPath.Should().EndWith(expected);
                builder.IsFrozen.Should().BeTrue();
            }
    }

    [Theory]
    [InlineData("pack:move_green", true)]
    [InlineData("pack:missing_companion_override", false)]
    [InlineData("malformed", false)]
    public void Explicit_icons_win_even_when_unresolvable(string stored, bool resolves)
    {
        var slot = new Slot { CommandId = "CustomCtrl_%Tab%Panel%GanttIt.Open", IconFile = stored };
        var button = new PlannedButton(slot, SlotTarget.Parse(slot.CommandId, "Tool"), "Tool", "id", PlannedButtonSize.Large);
        foreach (var dark in new[] { false, true })
        {
            ProfileTabBuilder.Icon32(button, dark).Should().BeSameAs(resolves ? IconAssets.ResolveSlotIcon(stored) : IconAssets.Default32);
            ProfileTabBuilder.Icon16(button, dark).Should().BeSameAs(resolves ? IconAssets.ResolveSlotIcon16(stored) : IconAssets.Default16);
            foreach (var size in new[] { 16, 32 })
            {
                IconAssets.ResolveCompanionIcon(slot, size, dark).Should().BeNull();
                var builder = PackIcons.ForSlot(slot, size: size, dark: dark);
                if (resolves)
                    builder.Should().BeSameAs(PackIcons.Load(new IconPackValue("move", "green"), size: size));
                else
                    builder.Should().BeNull();
            }
        }
        slot.IconFile.Should().Be(stored);
    }

}
