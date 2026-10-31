// rst-c's own (not vendored): the RST Presets rules (spec #16, WU5, AC5; inline on the Profile pane
// since feature #18), against profile_builder.html openPresetsModal / savePresetsModal.

using FluentAssertions;
using RST.Core.Builder;
using RST.Core.Profiles;
using Xunit;

namespace RST.Tests.Builder;

public class BuilderModalsTests
{
    private static readonly string[] Ribbon = ["Architecture", "Modify", "Manage", "View", "Annotate", "Add-Ins", "Create", "Enscape", "Studio", "Kinship", ""];

    [Fact]
    public void Preset_tabs_are_the_ribbon_minus_core_and_own_tab_then_preset_tabs_off_the_ribbon()
    {
        var presets = new ProfilePresets { HiddenTabs = ["Kinship", "Lumion", "Modify", "Studio", ""] };
        BuilderModals.PresetTabs(Ribbon, presets, " Studio ").Should().Equal("Architecture", "Enscape", "Kinship", "Lumion");
    }

    [Fact]
    public void No_ribbon_and_no_presets_give_no_tabs()
    {
        BuilderModals.PresetTabs(null, null, null).Should().BeEmpty();
        BuilderModals.PresetTabs(["Modify", "View"], new ProfilePresets(), "T").Should().BeEmpty();
    }

    [Fact]
    public void Apply_keeps_the_switched_on_tabs_in_list_order_and_the_flag()
    {
        var p = BuilderModals.ApplyPresets([("A", true), ("B", false), ("C", true)], true);
        p.HiddenTabs.Should().Equal("A", "C");
        p.DisableUnusedAddins.Should().BeTrue();
    }
}
