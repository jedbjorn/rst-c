// rst-c's own (not vendored): the Builder's small rules (spec #16, WU4, AC5): shortId,
// normalizeUrl (with Core's UrlNormalizer), normalizeHex, the new panel and slot shapes, the slot
// count, the Pick-icon label and the two summaries, against profile_builder.html.

using FluentAssertions;
using RST.Core.Builder;
using RST.Core.Profiles;
using RST.Core.Ribbon;
using Xunit;

namespace RST.Tests.Builder;

public class BuilderRulesTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("ID_OBJECTS_WALL", "ID_OBJECTS_WALL")]
    [InlineData("123456789012345678", "123456789012345678")]
    [InlineData("1234567890123456789", "12345678…23456789")]
    [InlineData("CustomCtrl_%Add-Ins%Kinship%KinshipUploadThis2Library", "CustomCt…2Library")]
    public void ShortId_keeps_18_characters_else_8_and_8(string? id, string expected) =>
        BuilderRules.ShortId(id).Should().Be(expected);

    // The page's normalizeUrl cases; the port hands trimmed input to Core's UrlNormalizer.
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("example.com", "https://example.com")]
    [InlineData("  example.com/a b ", "https://example.com/a b")]
    [InlineData("http://x.org", "http://x.org")]
    [InlineData("HTTPS://X.org", "HTTPS://X.org")]
    [InlineData("mailto:a@b.c", "mailto:a@b.c")]
    [InlineData("ftp://f", "ftp://f")]
    [InlineData("file://C:/x", "file://C:/x")]
    [InlineData("tel:123", "tel:123")]
    [InlineData("a@b.com", "mailto:a@b.com")]
    [InlineData("a@b.com/x", "https://a@b.com/x")]
    public void NormalizeUrl_matches_the_page(string? input, string expected)
    {
        BuilderRules.NormalizeUrl(input).Should().Be(expected);
        if (!string.IsNullOrWhiteSpace(input))
            BuilderRules.NormalizeUrl(input).Should().Be(UrlNormalizer.Normalize(input.Trim()), "the same rule as Core's UrlNormalizer");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("#4F8EF7", "#4f8ef7")]
    [InlineData(" #abc ", "#aabbcc")]
    [InlineData("#ABC", "#aabbcc")]
    [InlineData("4f8ef7", null)]
    [InlineData("#4f8ef7aa", null)]
    [InlineData("#ggg", null)]
    [InlineData("red", null)]
    public void NormalizeHex_canonicalizes_or_rejects(string? input, string? expected) =>
        BuilderRules.NormalizeHex(input).Should().Be(expected);

    [Fact]
    public void A_new_panel_is_Panel_N_in_the_default_blue()
    {
        var p = BuilderRules.NewPanel(2);
        p.Name.Should().Be("Panel 3");
        p.Color.Should().Be("#4f8ef7");
        p.Slots.Should().BeEmpty();
        p.CornerRadius.Should().BeNull();
    }

    [Fact]
    public void A_catalogue_command_becomes_a_tool_slot_with_its_group()
    {
        var s = BuilderRules.ToolSlot(new CatalogCommand("Kin.Open", "Open", "ThirdParty", "Kinship", null));
        s.SlotType.Should().Be("tool");
        s.Name.Should().Be("Open");
        s.CommandId.Should().Be("Kin.Open");
        s.SourceTab.Should().Be("Kinship");
        s.SourcePanel.Should().BeNull();
        s.IconFile.Should().BeNull();
        BuilderRules.ToolSlot(new CatalogCommand("ID_X", "", "Native", "", "")).Should()
            .BeEquivalentTo(new Slot { SlotType = "tool", Name = "ID_X", CommandId = "ID_X" }, "displayName || id; empty groups are null");
    }

    [Fact]
    public void A_url_button_is_a_tool_slot_with_a_URL_command()
    {
        var s = BuilderRules.UrlSlot("Wiki", "https://wiki.example.com");
        s.Should().BeEquivalentTo(new Slot { SlotType = "tool", Name = "Wiki", CommandId = "URL:https://wiki.example.com" });
    }

    [Theory]
    [InlineData(0, "0 slots")]
    [InlineData(1, "1 slot")]
    [InlineData(2, "2 slots")]
    public void SlotCount_pluralizes(int n, string expected) => BuilderRules.SlotCount(n).Should().Be(expected);

    [Theory]
    [InlineData("pack:house_purple", "house")]
    [InlineData("pack:link_external", "link_external")]
    [InlineData("PACK: level_green ", "level")]
    [InlineData("custom.png", "Icon")]
    [InlineData(null, "Icon")]
    [InlineData("pack:../x", "Icon")]
    public void The_pick_icon_label_is_the_design_or_Icon(string? icon, string expected) =>
        BuilderRules.IconButtonLabel(new Slot { IconFile = icon }).Should().Be(expected);
}
