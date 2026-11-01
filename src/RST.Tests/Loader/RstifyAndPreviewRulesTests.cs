// rst-c's own (not vendored): RstifyRules and RibbonPreviewRules (spec #16, WU3, AC5), the C#
// ports of profile_loader.html's RSTify filter, presets prompt and hexToRgba.

using FluentAssertions;
using RST.Core.Loader;
using RST.Core.Profiles;
using RST.Core.Scanning;
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

    private static ScannedCommand Command(string id, params string[] tabs) => new(
        id, id, CommandOrigin.Native, null, null, tabs[0], "Build",
        tabs.Select(t => new CommandPlacement(t, "Build", id, null)).ToList());

    private static Profile Tools(params Slot[] tools) => new()
    {
        Tab = "Studio", Panels = [new Panel { Slots = [.. tools] }],
    };

    private static Slot Tool(string id, string tab = "Architecture") => new()
    {
        CommandId = id, Name = id, SourceTab = tab,
    };

    [Fact]
    public void Two_home_wall_is_offered_but_beams_only_home_is_not()
    {
        var profile = Tools(Tool("Wall"), Tool("Beam", "Structure"));
        ScannedCommand[] catalog = [Command("Wall", "Architecture", "Structure"), Command("Beam", "Structure")];
        RstifyRules.TabsToOffer(profile, ["Architecture", "Structure", "Modify"], catalog)
            .Tabs.Should().Equal("Architecture");
        RstifyRules.BlockingTool(profile, Ribbon, catalog, [], "Architecture", out _).Should().BeFalse();
    }

    [Fact]
    public void A_tick_disables_the_other_home_and_unticking_enables_it()
    {
        var profile = Tools(Tool("Wall"));
        ScannedCommand[] catalog = [Command("Wall", "Architecture", "Structure")];
        RstifyRules.TabsToOffer(profile, ["Architecture", "Structure"], catalog)
            .Tabs.Should().Equal("Architecture", "Structure");
        RstifyRules.BlockingTool(profile, Ribbon, catalog, ["Architecture"], "Structure", out var label).Should().BeTrue();
        label.Should().Be("Wall");
        RstifyRules.BlockingTool(profile, Ribbon, catalog, [], "Structure", out _).Should().BeFalse();
    }

    [Fact]
    public void Catalog_lookup_skips_null_entries_and_keeps_the_first_duplicate()
    {
        var profile = Tools(Tool("Wall"));
        ScannedCommand[] catalog = [null!, Command("Wall", "Architecture", "Structure"), Command("Wall", "Structure")];

        RstifyRules.TabsToOffer(profile, Ribbon, catalog).Tabs.Should().Contain(["Architecture", "Structure"]);
        RstifyRules.ReconcileHiddenTabs(profile, Ribbon, catalog, ["Structure", "Architecture"])
            .Should().Equal("Architecture");
    }

    [Theory]
    [InlineData(null, "Wall: Architectural", false, "Wall: Architectural")]
    [InlineData("", "Wall: Architectural", false, "Wall: Architectural")]
    [InlineData(null, null, false, "ID_OBJECTS_WALL")]
    [InlineData("", "", false, "ID_OBJECTS_WALL")]
    [InlineData(null, "Wall: Architectural", true, "Wall: Architectural")]
    [InlineData("", "Wall: Architectural", true, "Wall: Architectural")]
    public void Missing_names_still_block_the_last_home_and_reconcile_saved_ticks(
        string? name, string? primaryLabel, bool stacked, string expectedLabel)
    {
        var tool = Tool("ID_OBJECTS_WALL");
        tool.Name = name!;
        var profile = stacked ? Tools(new Slot { SlotType = "stack", Name = "Walls" }) : Tools(tool);
        if (stacked) profile.Stacks["Walls"] = new Stack { Tools = [tool] };
        ScannedCommand[] catalog = [Command(tool.CommandId!, "Architecture", "Structure") with { DisplayName = primaryLabel! }];

        RstifyRules.BlockingTool(profile, Ribbon, catalog, [], "Architecture", out _).Should().BeFalse();
        RstifyRules.BlockingTool(profile, Ribbon, catalog, ["Architecture"], "Structure", out var label).Should().BeTrue();
        label.Should().Be(expectedLabel);
        RstifyRules.ReconcileHiddenTabs(profile, Ribbon, catalog, ["Structure", "Architecture"])
            .Should().Equal("Architecture");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Missing_names_on_unknown_tools_use_the_command_id(string? name)
    {
        var tool = Tool("Unknown");
        tool.Name = name!;
        RstifyRules.BlockingTool(Tools(tool), Ribbon, [], [], "Architecture", out var label).Should().BeTrue();
        label.Should().Be("Unknown");
    }

    [Fact]
    public void Unknown_id_and_empty_catalog_keep_the_saved_source_tab_locked()
    {
        var profile = Tools(Tool("Unknown", "Enscape"));
        foreach (var catalog in new ScannedCommand[][] { [], [Command("Wall", "Architecture", "Structure")] })
            RstifyRules.TabsToOffer(profile, ["Enscape", "Structure"], catalog).Tabs.Should().Equal("Structure");
    }

    [Fact]
    public void Stack_tools_use_catalog_homes_and_core_homes_always_remain_visible()
    {
        var profile = Tools(new Slot { SlotType = "stack", Name = "Select" });
        profile.Stacks["Select"] = new Stack { Tools = [Tool("Select"), Tool("Unknown", "Enscape")] };
        ScannedCommand[] catalog = [Command("Select", "Architecture", "Structure", "Modify")];
        RstifyRules.TabsToOffer(profile, Ribbon, catalog).Tabs.Should().Contain(["Architecture", "Structure"]).And.NotContain("Enscape");
        RstifyRules.BlockingTool(profile, Ribbon, catalog, ["Architecture", "Modify"], "Structure", out _).Should().BeFalse();
        RstifyRules.ReconcileHiddenTabs(profile, Ribbon, catalog, ["Structure", "Modify", "Architecture"])
            .Should().Equal("Architecture", "Structure");
    }

    [Fact]
    public void A_home_missing_from_this_ribbon_does_not_allow_hiding_the_last_visible_home()
    {
        var profile = Tools(Tool("Wall"));
        ScannedCommand[] catalog = [Command("Wall", "Architecture", "Structure")];
        RstifyRules.TabsToOffer(profile, ["Architecture"], catalog).Tabs.Should().Equal("Architecture");
        RstifyRules.BlockingTool(profile, ["Architecture"], catalog, [], "Architecture", out var label).Should().BeTrue();
        label.Should().Be("Wall");
        RstifyRules.ReconcileHiddenTabs(profile, ["Architecture"], catalog, ["Architecture"]).Should().BeEmpty();
    }

    [Fact]
    public void Saved_ticks_are_reconciled_in_ribbon_order_not_saved_order()
    {
        var profile = Tools(Tool("Wall"));
        ScannedCommand[] catalog = [Command("Wall", "Architecture", "Structure")];
        RstifyRules.ReconcileHiddenTabs(profile, Ribbon, catalog, ["Structure", "Architecture", "Modify", "Gone"])
            .Should().Equal("Architecture");
        RstifyRules.ReconcileHiddenTabs(profile, ["Structure", "Architecture"], catalog, ["Architecture", "Structure"])
            .Should().Equal("Structure");
        profile.Panels[0].Slots[0].SourceTab.Should().Be("Architecture");
    }

    [Fact]
    public void Several_panels_on_one_tab_are_one_home_and_url_and_email_tools_do_not_lock_tabs()
    {
        var command = Command("Beam", "Structure") with
        {
            Placements = [new("Structure", "Build", "Beam", null), new("Structure", "Other", "Beam", null)],
        };
        var profile = Tools(Tool("Beam"), Tool("URL:https://example.com", "Architecture"), Tool("URL:mailto:a@example.com", "Analyze"));
        RstifyRules.TabsToOffer(profile, ["Structure", "Architecture", "Analyze"], [command])
            .Tabs.Should().Equal("Architecture", "Analyze");
    }

    [Fact]
    public void A_catalogued_tab_named_Custom_is_a_real_home()
    {
        RstifyRules.TabsToOffer(Tools(Tool("Addin")), ["Custom", "Architecture"], [Command("Addin", "Custom")])
            .Tabs.Should().Equal("Architecture");
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
            + "Disable Unused Add-ins additionally switches off the add-ins this profile does not need, for your Windows user, from the next time Revit starts.\n\n"
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
