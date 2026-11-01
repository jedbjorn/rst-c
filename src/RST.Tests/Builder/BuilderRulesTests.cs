// rst-c's own (not vendored): the Builder's small rules (spec #16, WU4, AC5): shortId,
// normalizeUrl (with Core's UrlNormalizer), normalizeHex and the new panel and slot shapes, against
// profile_builder.html; and the panel cards' tool count, source line and tooltip (feature #18, WU3).

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
    public void Wall_dragged_from_Structure_saves_that_placement_and_label()
    {
        var entries = CatalogCommand.FromScanned(new RST.Tests.Scanning.RibbonCapture(2026).Merge());
        var wall = entries.Single(c => c.Id == "ID_OBJECTS_WALL" && c.SourceTab == "Structure");
        var slot = BuilderRules.ToolSlot(wall);
        slot.SourceTab.Should().Be("Structure");
        slot.SourcePanel.Should().Be("Structure");
        slot.Name.Should().Be(wall.Label).And.Be("Wall: Architectural");
        var profile = new Profile { ProfileName = "Placement", Tab = "Placement", Panels = [new Panel { Slots = [slot] }] };
        var json = ProfileSerializer.WriteString(profile);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var saved = document.RootElement.GetProperty("panels")[0].GetProperty("slots")[0];
        saved.GetProperty("sourceTab").GetString().Should().Be("Structure");
        saved.GetProperty("sourcePanel").GetString().Should().Be("Structure");
        saved.GetProperty("name").GetString().Should().Be(wall.Label);
        var loaded = ProfileSerializer.ReadString(json).Panels[0].Slots[0];
        loaded.Should().BeEquivalentTo(slot);
    }

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

    [Theory]
    [InlineData(0, "0 tools")]
    [InlineData(1, "1 tool")]
    [InlineData(5, "5 tools")]
    public void ToolCountText_pluralizes(int n, string expected) => BuilderRules.ToolCountText(n).Should().Be(expected);

    [Fact]
    public void The_tool_count_counts_every_tool_in_a_stack_but_not_the_stack()
    {
        var p = new Profile();
        p.Stacks["Stack 1"] = new Stack { Tools = [new Slot { Name = "W", CommandId = "W" }, new Slot { Name = "A", CommandId = "A" }, new Slot { Name = "P", CommandId = "P" }] };
        var panel = new Panel { Slots = [new Slot { Name = "K", CommandId = "K" }, PanelStacks.StackSlot("Stack 1"), new Slot { Name = "N", CommandId = "N" }] };
        p.Panels.Add(panel);
        BuilderRules.ToolCount(p, panel).Should().Be(5);
        BuilderRules.ToolCount(p, new Panel()).Should().Be(0);
    }

    [Theory]
    [InlineData("ID_OBJECTS_BEAM", "Structure", "Structure", "Structure · Structure")]
    [InlineData("ID_OBJECTS_BEAM", "Structure", null, "Structure")]
    [InlineData("ID_OBJECTS_BEAM", null, "Build", "Build")]
    [InlineData("CustomCtrl_%Add-Ins%Kinship%KinshipUploadThis2Library", null, null, "CustomCt…2Library")]
    [InlineData("URL:https://wiki.studio.example/", "Custom", "Custom", "wiki.studio.example")]
    [InlineData("URL:mailto:bim@studio.example", null, null, "bim@studio.example")]
    [InlineData("URL:file://server/share", null, null, "server/share")]
    public void The_source_line_is_tab_and_panel_or_the_link(string id, string? tab, string? panel, string expected) =>
        BuilderRules.SourceLine(new Slot { Name = "x", CommandId = id, SourceTab = tab, SourcePanel = panel }).Should().Be(expected);

    [Theory]
    [InlineData("ID_OBJECTS_BEAM", "ID_OBJECTS_BEAM")]
    [InlineData("URL:https://wiki.studio.example/", "https://wiki.studio.example/")]
    [InlineData("URL:BIM@studio.example", "mailto:BIM@studio.example")]
    public void The_tooltip_is_the_full_command_id_or_link(string id, string expected) =>
        BuilderRules.ToolTipOf(new Slot { Name = "x", CommandId = id }).Should().Be(expected);
}
