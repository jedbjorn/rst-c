// rst-c's own (not vendored): the xUnit port of tests/ui/icon-picker.test.mjs (spec #16, WU5,
// AC5), every case, against IconPickerModel (the C# port of profile_builder.html's ICON PICKER
// section) and Core's IconPack, which the page's parsePackIcon / packIconUrl mirrored.
// The Node test drove the real page JS through a stub DOM; the DOM-only cases map as follows:
//   - "popdown inserts in-flow right after its tile" and "CSS pins the popdown as an in-flow
//     full-width row" become the row model: the variant row is a full row right under its tile's
//     row, and moves with the tile (Rows_*);
//   - "bridge rejection caches an empty list" becomes a missing pack folder (only None shows);
//   - "slot button shows exact variant thumbnail and logical name" checks the thumbnail file and
//     the label (BuilderRules.IconButtonLabel).
// The view-model and window side (focus, Escape keys, outside click) is in RST.RuntimeTests.

using System.IO;
using FluentAssertions;
using RST.Core.Builder;
using RST.Core.Profiles;
using RST.Core.Ribbon;
using Xunit;

namespace RST.Tests.Builder;

public class IconPickerModelTests
{
    private static readonly string[] Colors = ["light_grey", "dark_grey", "blue", "purple", "green", "orange", "red"];

    private static IconPackEntry Entry(string name) => new(name, Colors);

    private static readonly IconPackEntry[] Catalogue = [Entry("apple"), Entry("move"), Entry("link_external")];

    private static IconPickerModel Open(string? iconFile) => new(Catalogue, iconFile);

    // ── parsePackIcon mirror of the Core contract ──

    [Theory]
    [InlineData("pack:move", "move", null)]
    [InlineData("pack:move_green", "move", "green")]
    [InlineData("pack:link_external", "link_external", null)]
    [InlineData("pack:link_external_purple", "link_external", "purple")]
    [InlineData("PACK:Move_GREEN", "Move", "green")]
    [InlineData("  pack:move_blue  ", "move", "blue")]
    [InlineData("pack:move_Light_Grey", "move", "light_grey")]
    [InlineData("pack:move_fuchsia", "move_fuchsia", null)]
    public void Parse_bare_explicit_underscore_names_case_whitespace(string value, string name, string? color)
    {
        IconPack.TryParseValue(value, out var parsed).Should().BeTrue();
        parsed!.Name.Should().Be(name);
        parsed.Color.Should().Be(color);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("pack:")]
    [InlineData("pack:   ")]
    [InlineData("pack:_blue")]
    [InlineData("move")]
    [InlineData("packs:move")]
    [InlineData("pack:a/b")]
    [InlineData("pack:a\\b")]
    [InlineData("pack:../x")]
    [InlineData("pack:a..b")]
    [InlineData("pack:/abs")]
    [InlineData("pack:C:\\x")]
    [InlineData("pack:move|green")]
    public void Parse_rejects_malformed_and_path_like_values(string? value) =>
        IconPack.TryParseValue(value, out _).Should().BeFalse();

    [Fact]
    public void Pack_paths_bare_to_the_blue_alias_explicit_to_the_variant_file()
    {
        new IconPackValue("move", null).RelativePath.Should().Be("icons/pack/32_move.png");
        new IconPackValue("move", "green").RelativePath.Should().Be("icons/pack/32_move_green.png");
        new IconPackValue("link_external", "purple").RelativePath.Should().Be("icons/pack/32_link_external_purple.png");
    }

    // ── slot button: exact thumbnail, logical-name label ──

    [Fact]
    public void The_slot_button_shows_the_exact_variant_and_the_logical_name()
    {
        IconPack.TryParseValue("pack:move_red", out var red).Should().BeTrue();
        red!.RelativePath.Should().Be("icons/pack/32_move_red.png");
        BuilderRules.IconButtonLabel(new Slot { IconFile = "pack:move_red" }).Should().Be("move");
        IconPack.TryParseValue("pack:move", out var bare).Should().BeTrue();
        bare!.RelativePath.Should().Be("icons/pack/32_move.png");
        BuilderRules.IconButtonLabel(new Slot { IconFile = null }).Should().Be("Icon", "no icon → the + placeholder and \"Icon\"");
    }

    // ── open without mutation ──

    [Fact]
    public void Opening_the_picker_and_a_variant_row_changes_nothing()
    {
        var m = Open("pack:move_green");
        m.IsOpen.Should().BeTrue();
        m.ClickTile("move");
        m.Outcome.Should().BeNull("a variant row never writes");
        m.CurrentIconFile.Should().Be("pack:move_green");
        m.OpenColors.Should().HaveCount(7);
    }

    // ── commit on color click ──

    [Fact]
    public void A_color_click_commits_the_explicit_value_and_closes()
    {
        var m = Open(null);
        m.ClickTile("move");
        m.ClickVariant("red");
        m.Outcome.Should().Be(IconPickerOutcome.Commit("pack:move_red"));
        m.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void Blue_commits_an_explicit_blue_value_never_a_bare_one()
    {
        var m = Open("pack:move");
        m.ClickTile("move");
        m.ClickVariant("blue");
        m.Outcome!.Value.Should().Be("pack:move_blue");
    }

    // ── None ──

    [Fact]
    public void None_commits_null_and_closes()
    {
        var m = Open("pack:move_green");
        m.ClickNone();
        m.Outcome.Should().Be(IconPickerOutcome.Commit(null));
        m.IsOpen.Should().BeFalse();
    }

    // ── Cancel / outside click / Escape ──

    [Fact]
    public void Cancel_closes_without_a_change()
    {
        var m = Open("pack:move_green");
        m.ClickTile("move");
        m.Cancel();
        m.Outcome.Should().Be(IconPickerOutcome.Cancelled);
        m.Outcome!.Committed.Should().BeFalse();
    }

    [Fact]
    public void Escape_closes_the_variant_row_first_then_the_picker()
    {
        var m = Open("pack:move_green");
        m.ClickTile("move");
        m.OpenDesign.Should().Be("move");
        m.Escape();
        m.OpenDesign.Should().BeNull("the first Escape closes only the row");
        m.IsOpen.Should().BeTrue("the picker is still open");
        m.FocusedTile.Should().Be("move", "focus goes back to the design tile");
        m.Escape();
        m.Outcome.Should().Be(IconPickerOutcome.Cancelled, "the second Escape closes the picker, changing nothing");
    }

    // ── keyboard navigation ──

    [Fact]
    public void Focus_starts_on_the_selected_variant_and_the_arrows_rove_in_palette_order()
    {
        var m = Open("pack:move_green");
        m.ClickTile("move");
        m.FocusedColor.Should().Be("green");
        m.Key(PickerKey.Right);
        m.FocusedColor.Should().Be("orange");
        m.Key(PickerKey.Left);
        m.FocusedColor.Should().Be("green");
        m.Key(PickerKey.Left);
        m.FocusedColor.Should().Be("purple");
        m.Key(PickerKey.End);
        m.FocusedColor.Should().Be("red");
        m.Key(PickerKey.Right);
        m.FocusedColor.Should().Be("light_grey", "it wraps to the first");
        m.Key(PickerKey.Home);
        m.FocusedColor.Should().Be("light_grey");
        m.Key(PickerKey.Up);
        m.FocusedColor.Should().Be("red", "up moves back, wrapping");
        m.Key(PickerKey.Down);
        m.FocusedColor.Should().Be("light_grey");
    }

    [Fact]
    public void Another_design_focuses_its_first_variant()
    {
        var m = Open("pack:move_green");
        m.ClickTile("apple");
        m.FocusedColor.Should().Be("light_grey");
    }

    // ── selected-state restore ──

    [Fact]
    public void An_explicit_value_restores_its_design_and_color()
    {
        var m = Open("pack:move_green");
        m.SelectedDesign.Should().Be("move");
        m.SelectedColor.Should().Be("green");
        m.NoneSelected.Should().BeFalse();
    }

    [Fact]
    public void A_legacy_bare_value_restores_as_blue_without_rewriting_it()
    {
        var m = Open("pack:move");
        m.SelectedDesign.Should().Be("move");
        m.SelectedColor.Should().Be("blue");
        m.Cancel();
        m.CurrentIconFile.Should().Be("pack:move");
    }

    [Fact]
    public void A_mixed_case_value_restores_like_the_runtime_resolver()
    {
        var m = Open("PACK:Move_Blue");
        m.SelectedDesign.Should().Be("move", "the canonical tile");
        m.SelectedColor.Should().Be("blue");
        m.Cancel();
        m.CurrentIconFile.Should().Be("PACK:Move_Blue", "the stored string is kept");
    }

    [Fact]
    public void An_unknown_color_or_design_selects_nothing()
    {
        var m = Open("pack:move_fuchsia");
        m.SelectedDesign.Should().BeNull();
        m.SelectedColor.Should().BeNull();
        m.NoneSelected.Should().BeFalse("a pack value that matches nothing selects no tile, not even None");
        m.Cancel();
        m.CurrentIconFile.Should().Be("pack:move_fuchsia");
    }

    [Fact]
    public void A_non_pack_value_selects_None()
    {
        Open(null).NoneSelected.Should().BeTrue();
        Open("custom.png").NoneSelected.Should().BeTrue();
    }

    // ── rapid design switching ──

    [Fact]
    public void Rapid_design_clicks_end_with_one_row_and_no_change()
    {
        var m = Open(null);
        m.ClickTile("apple");
        m.ClickTile("move");
        m.ClickTile("link_external");
        m.Rows(4).Count(r => r.IsVariantRow).Should().Be(1, "exactly one variant row");
        m.OpenDesign.Should().Be("link_external", "the last clicked design owns it");
        m.OpenColors.Should().HaveCount(7);
        m.Outcome.Should().BeNull();
    }

    [Fact]
    public void Clicking_the_same_design_closes_its_row()
    {
        var m = Open(null);
        m.ClickTile("move");
        m.OpenDesign.Should().Be("move");
        m.ClickTile("move");
        m.OpenDesign.Should().BeNull();
        m.FocusedTile.Should().Be("move");
    }

    // ── broken variant images ──

    [Fact]
    public void A_broken_variant_is_disabled_and_never_applies()
    {
        var m = Open(null);
        m.ClickTile("move");
        m.MarkBroken("move", "orange");
        m.IsVariantEnabled("move", "orange").Should().BeFalse();
        m.ClickVariant("orange");
        m.Outcome.Should().BeNull("a disabled variant cannot commit");
    }

    [Fact]
    public void The_arrows_skip_a_broken_variant()
    {
        var m = Open("pack:move_green");
        m.ClickTile("move");
        m.MarkBroken("move", "orange");
        m.Key(PickerKey.Right);
        m.FocusedColor.Should().Be("red");
    }

    // ── a missing pack / malformed entries ──

    [Fact]
    public void A_missing_pack_folder_leaves_only_None()
    {
        var catalogue = IconPack.BuildCatalogue(Path.Combine(Path.GetTempPath(), "rst-no-pack-" + Guid.NewGuid().ToString("N")));
        catalogue.DirectoryExists.Should().BeFalse();
        var m = new IconPickerModel(catalogue.Entries, null);
        var rows = m.Rows(6);
        rows.Should().ContainSingle();
        rows[0].Tiles.Should().Equal(new IconPackEntry?[] { null }, "only the None tile");
    }

    [Fact]
    public void Malformed_catalogue_entries_are_filtered_out()
    {
        var m = new IconPickerModel([Entry("move"), new IconPackEntry("", Colors), null!, new IconPackEntry("broken", null!)], null);
        m.Entries.Select(e => e.Name).Should().Equal("move");
    }

    // ── the variant row: a full row under its tile's row ──

    [Fact]
    public void Rows_put_None_first_and_chunk_the_tiles()
    {
        var m = new IconPickerModel(Enumerable.Range(0, 9).Select(i => Entry("d" + i)).ToList(), null);
        var rows = m.Rows(4);
        rows.Select(r => string.Join(",", r.Tiles.Select(t => t?.Name ?? "none"))).Should().Equal("none,d0,d1,d2", "d3,d4,d5,d6", "d7,d8");
        rows.Should().OnlyContain(r => !r.IsVariantRow);
    }

    [Fact]
    public void Rows_insert_the_variant_row_right_under_the_clicked_tile_s_row_and_move_it_with_the_tile()
    {
        var m = new IconPickerModel(Enumerable.Range(0, 9).Select(i => Entry("d" + i)).ToList(), null);
        m.ClickTile("d4");
        var rows = m.Rows(4);
        rows.Select(r => r.IsVariantRow ? "[variants]" : string.Join(",", r.Tiles.Select(t => t?.Name ?? "none")))
            .Should().Equal("none,d0,d1,d2", "d3,d4,d5,d6", "[variants]", "d7,d8");
        rows[2].Colors.Should().Equal(Colors);

        m.ClickTile("d0");
        m.Rows(4).Select(r => r.IsVariantRow).Should().Equal(false, true, false, false);
        m.ClickTile("d8");
        m.Rows(4).Select(r => r.IsVariantRow).Should().Equal(false, false, false, true);
    }

    // ── catalogue order in the row ──

    [Fact]
    public void Variants_follow_the_palette_order_whatever_the_catalogue_order()
    {
        var m = new IconPickerModel([new IconPackEntry("move", Enumerable.Reverse(Colors).ToList())], null);
        m.ClickTile("move");
        m.OpenColors.Should().Equal(Colors);
        IconPickerModel.PackColors.Should().Equal(Colors);
    }
}
