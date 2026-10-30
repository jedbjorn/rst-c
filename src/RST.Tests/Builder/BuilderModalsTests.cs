// rst-c's own (not vendored): the RST Presets and Junk Cleanup modal rules (spec #16, WU5, AC5),
// against profile_builder.html openPresetsModal / savePresetsModal and the cleanup editor.

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

    [Fact]
    public void Cleanup_rows_copy_the_profile_s_targets_or_seed_the_defaults()
    {
        CleanupDefaults.Build().Should().BeEquivalentTo(BuilderModals.CleanupRowsFor(null));
        CleanupDefaults.Build().Should().BeEquivalentTo(BuilderModals.CleanupRowsFor([]));
        var own = new List<CleanupTarget> { new() { Id = "rst.cleanup.temp", Name = "Temp", Path = @"%LocalAppData%\Temp", Enabled = false } };
        var rows = BuilderModals.CleanupRowsFor(own);
        rows.Should().BeEquivalentTo(own);
        rows[0].Should().NotBeSameAs(own[0], "the modal edits a copy, so Cancel reverts (Planner default 4)");
    }

    [Fact]
    public void Apply_drops_rows_with_no_name_and_no_path()
    {
        var rows = new List<CleanupTarget>
        {
            new() { Id = "a", Name = "Temp", Path = "" },
            new() { Id = "b", Name = " ", Path = " " },
            new() { Id = "c", Name = "", Path = @"%AppData%\RST\logs" },
        };
        BuilderModals.ApplyCleanup(rows).Select(t => t.Id).Should().Equal("a", "c");
    }
}
