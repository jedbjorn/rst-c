// rst-c's own (not vendored): RstifyRules and RibbonPreviewRules (spec #16, WU3, AC5), the C#
// ports of profile_loader.html's RSTify filter, presets prompt and hexToRgba.

using FluentAssertions;
using RST.Core.Loader;
using RST.Core.Profiles;
using Xunit;

namespace RST.Tests.Loader;

public class RstifyRulesTests
{
    private static readonly string[] Ribbon =
    [
        "Architecture", "Structure", "Modify", "Manage", "View", "Annotate", "Add-Ins", "Create",
        "Analyze", "Collaborate", "Enscape", "Kinship", "Studio Arch", "Custom",
    ];

    private static Profile Arch() => new()
    {
        ProfileName = "Architecture",
        Tab = "Studio Arch",
        Panels =
        [
            new Panel
            {
                Slots =
                [
                    new Slot { SlotType = "tool", Name = "Wall", SourceTab = "Architecture" },
                    new Slot { SlotType = "tool", Name = "Wiki", SourceTab = "Custom" },
                    new Slot { SlotType = "stack", Name = "Renders" },
                ],
            },
        ],
        Stacks = new() { ["Renders"] = new Stack { Tools = [new Slot { Name = "Enscape", SourceTab = "Enscape" }, new Slot { Name = "Help", SourceTab = "Custom" }] } },
    };

    [Fact]
    public void Source_tabs_come_from_slots_and_stack_tools_but_never_Custom()
    {
        RstifyRules.SourceTabs(Arch()).Should().BeEquivalentTo(["Architecture", "Enscape"]);
        RstifyRules.SourceTabs(null).Should().BeEmpty();
    }

    [Fact]
    public void Offered_tabs_drop_core_source_and_own_tabs_in_ribbon_order()
    {
        var list = RstifyRules.TabsToOffer(Arch(), Ribbon);
        list.Blank.Should().BeFalse();
        list.Tabs.Should().Equal("Structure", "Analyze", "Collaborate", "Kinship", "Custom");
        list.ShowsEmptyState.Should().BeFalse();
    }

    [Fact]
    public void Core_tabs_are_the_six_the_loader_locks()
    {
        RstifyRules.CoreTabs.Should().BeEquivalentTo(["Modify", "Manage", "View", "Annotate", "Add-Ins", "Create"]);
    }

    [Fact]
    public void No_profile_or_no_captured_tabs_is_blank_not_the_empty_state()
    {
        RstifyRules.TabsToOffer(null, Ribbon).Should().Be(new RstifyTabList(true, []));
        var blank = RstifyRules.TabsToOffer(Arch(), []);
        blank.Blank.Should().BeTrue();
        blank.ShowsEmptyState.Should().BeFalse();
    }

    [Fact]
    public void Nothing_left_to_hide_shows_the_empty_state()
    {
        var list = RstifyRules.TabsToOffer(Arch(), ["Modify", "Architecture", "Studio Arch"]);
        list.Tabs.Should().BeEmpty();
        list.ShowsEmptyState.Should().BeTrue();
    }

    [Fact]
    public void Presets_count_when_tabs_are_hidden_or_disable_is_on()
    {
        RstifyRules.HasPresets(new Profile()).Should().BeFalse();
        RstifyRules.HasPresets(new Profile { Presets = new ProfilePresets() }).Should().BeFalse();
        RstifyRules.HasPresets(new Profile { Presets = new ProfilePresets { HiddenTabs = ["Analyze"] } }).Should().BeTrue();
        RstifyRules.HasPresets(new Profile { Presets = new ProfilePresets { DisableUnusedAddins = true } }).Should().BeTrue();
        RstifyRules.HasPresets(null).Should().BeFalse();
    }

    [Theory]
    [InlineData(0, false, "Admin defaults: no tabs hidden.")]
    [InlineData(1, false, "Admin defaults: 1 tab to hide.")]
    [InlineData(3, true, "Admin defaults: 3 tabs to hide, disable unused add-ins on.")]
    public void Adopt_prompt_text_matches_the_loader(int tabs, bool disable, string summary)
    {
        var profile = new Profile
        {
            ProfileName = "Structure",
            Presets = new ProfilePresets { HiddenTabs = Enumerable.Range(0, tabs).Select(i => "T" + i).ToList(), DisableUnusedAddins = disable },
        };
        RstifyRules.AdoptPresetsTitle.Should().Be("Adopt RSTify Presets?");
        RstifyRules.AdoptPresetsMessage(profile).Should().Be(
            "RSTify hides selected ribbon tabs to declutter Revit while this profile is active. "
            + "Disable Unused Add-ins additionally renames non-required .addin files so they don’t load on the next launch.\n\n"
            + summary + "\n\n"
            + "Adopt these defaults for \"Structure\"? You can change them anytime.");
    }
}

public class RibbonPreviewRulesTests
{
    [Theory]
    [InlineData("#4f8ef7", 100, "rgba(79,142,247,1.000)")]
    [InlineData("4F8EF7", 50, "rgba(79,142,247,0.500)")]
    [InlineData("  #10b981 ", 85, "rgba(16,185,129,0.850)")]
    [InlineData("#ffffff", 150, "rgba(255,255,255,1.000)")]
    [InlineData("#000000", -5, "rgba(0,0,0,0.000)")]
    [InlineData("#000000", null, "rgba(0,0,0,1.000)")]
    public void Hex_to_rgba_matches_the_html(string hex, int? opacity, string css) =>
        RibbonPreviewRules.PanelColor(hex, opacity).ToCss().Should().Be(css);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#abc")]
    [InlineData("#4f8ef7aa")]
    [InlineData("#zzzzzz")]
    public void Malformed_colors_fall_back_to_opaque_blue_whatever_the_opacity(string? hex)
    {
        var c = RibbonPreviewRules.PanelColor(hex, 20);
        c.Should().Be(RibbonPreviewRules.Fallback);
        c.ToCss().Should().Be("rgba(79,142,247,1.000)");
    }

    [Fact]
    public void Alpha_byte_rounds_the_fraction()
    {
        RibbonPreviewRules.PanelColor("#102030", 50).Alpha.Should().Be(128);
        RibbonPreviewRules.PanelColor("#102030", 100).Alpha.Should().Be(255);
    }

    [Fact]
    public void Effective_opacity_is_the_override_else_the_profile()
    {
        var p = new Profile { PanelOpacity = 85 };
        RibbonPreviewRules.EffectiveOpacity(p, null).Should().Be(85);
        RibbonPreviewRules.EffectiveOpacity(p, 40).Should().Be(40);
    }

    [Fact]
    public void Tool_label_is_baseName_then_name_in_the_loader_and_name_in_the_builder()
    {
        var slot = new Slot { Name = "Wall (Arch)", BaseName = "Wall" };
        RibbonPreviewRules.ToolLabel(slot, RibbonPreviewMode.Loader).Should().Be("Wall");
        RibbonPreviewRules.ToolLabel(slot, RibbonPreviewMode.Builder).Should().Be("Wall (Arch)");
        RibbonPreviewRules.ToolLabel(new Slot { Name = "Door" }, RibbonPreviewMode.Loader).Should().Be("Door");
    }

    [Fact]
    public void Pack_icons_parse_like_the_ribbon_and_other_values_are_none()
    {
        RibbonPreviewRules.PackIcon(new Slot { IconFile = "pack:camera_orange" })!.RelativePath.Should().Be("icons/pack/32_camera_orange.png");
        RibbonPreviewRules.PackIcon(new Slot { IconFile = "pack:link_external" })!.RelativePath.Should().Be("icons/pack/32_link_external.png");
        RibbonPreviewRules.PackIcon(new Slot { IconFile = "icon.png" }).Should().BeNull();
        RibbonPreviewRules.PackIcon(new Slot { IconFile = null }).Should().BeNull();
        RibbonPreviewRules.PackIcon(new Slot { IconFile = "pack:../x" }).Should().BeNull();
    }
}
