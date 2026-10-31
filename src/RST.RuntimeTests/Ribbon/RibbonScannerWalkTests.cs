// SC-027: RibbonScanner's AdWindows mapping, on real Autodesk.Windows items built outside Revit (the
// Nice3point AdWindows reference copied next to the tests, as RibbonItemFactoryTests does). The fake
// ribbon copies the shapes in the C1 dumps (shared/evidence/v31-main/passB/c1-dumps/20xx/ribbon.tsv).
// Revit's own UIFramework types are not referenceable here, so each is stood in for by a subclass of
// the same AdWindows base it derives from (checked in UIFramework.dll 2026.0.4):
//   UIFramework.RvtMenuSplitButton : RibbonSplitButton    (Manage > Settings > Additional Settings)
//   UIFramework.RvtLargeMenuItem   : RibbonMenuItem       (its entries and its "Annotations" sub-menu)
//   UIFramework.SplitRadioGroup    : RibbonRadioButtonGroup (Massing & Site > Show Mass modes)
// The decisions themselves are unit-tested on plain data in RST.Tests/Scanning/RibbonWalkTests.

using Autodesk.Windows;
using FluentAssertions;
using RST.Core.Scanning;
using RST.Engine.Scanning;
using RST.RuntimeTests.Support;
using Xunit;

namespace RST.RuntimeTests.Ribbon;

public sealed class RibbonScannerWalkTests
{
    private sealed class RvtMenuSplitButtonStandIn : RibbonSplitButton { }
    private sealed class RvtLargeMenuItemStandIn : RibbonMenuItem { }
    private sealed class SplitRadioGroupStandIn : RibbonRadioButtonGroup { }

    private static T With<T>(T item, string id, string text) where T : RibbonItem
    {
        item.Id = id;
        item.Text = text;
        return item;
    }

    private static RvtLargeMenuItemStandIn MenuItem(string id, string text) => With(new RvtLargeMenuItemStandIn(), id, text);
    private static RibbonButton Button(string id, string text) => With(new RibbonButton(), id, text);
    private static RibbonToggleButton Toggle(string id, string text) => With(new RibbonToggleButton(), id, text);

    private static RibbonPanel Panel(string title, RibbonCommandItem? dialogLauncher, params RibbonItem[] items)
    {
        var source = new RibbonPanelSource { Title = title, DialogLauncher = dialogLauncher };
        foreach (var item in items) source.Items.Add(item);
        return new RibbonPanel { Source = source };
    }

    private static RibbonTab Tab(string title, params RibbonPanel[] panels)
    {
        var tab = new RibbonTab { Title = title, Id = title };
        foreach (var panel in panels) tab.Panels.Add(panel);
        return tab;
    }

    private static RibbonTab Manage()
    {
        var annotations = MenuItem("ID_SETTINGS_ANNOTATIONS_MENU", "Annotations");
        annotations.Items.Add(MenuItem("ID_SETTINGS_ANNOTATIONS_LEADERS", "Arrowheads"));
        annotations.Items.Add(MenuItem("ID_VIEWTAGSTYLES_SECTIONTAGS", "Section Tags"));

        var additional = With(new RvtMenuSplitButtonStandIn(), "HID_SETTINGS_RibbonListButton", "Additional  Settings");
        additional.Items.Add(MenuItem("ID_SETTINGS_LINE_STYLES", "Line Styles"));
        additional.Items.Add(MenuItem("ID_SETTINGS_PENS", "Line Weights"));
        additional.Items.Add(annotations);
        additional.Items.Add(MenuItem("ID_SETTINGS_HALFTONE", "Halftone / Underlay"));

        var fold = new RibbonFoldPanel();
        fold.Items.Add(Button("ID_SETTINGS_OBJECTSTYLES", "Object  Styles"));
        fold.Items.Add(Button("ID_SETTINGS_SNAPPING", "Snaps"));

        return Tab("Manage", Panel("Settings", null, Button("ID_SETTINGS_MATERIALS", "Materials"), fold, new RibbonSeparator(), additional));
    }

    private static RibbonTab View()
    {
        var ui = With(new RibbonChecklistButton(), "HID_APPLICATION_ELEMENTS_RibbonListButton", "User  Interface");
        ui.Items.Add(Toggle("ID_SHOW_VIEWCUBE", "ViewCube"));
        ui.Items.Add(Toggle("ID_NAVIGATION_BAR", "Navigation Bar"));
        ui.Items.Add(Toggle("ID_VIEW_STATUS_BAR", "Status Bar"));
        ui.Items.Add(new RibbonSeparator());
        ui.Items.Add(With(new RibbonMenuItem(), "ID_BROWSER_ORGANIZATION", "Browser Organization"));
        ui.Items.Add(With(new RibbonMenuItem(), "ID_KEYBOARD_SHORTCUT_DIALOG", "Keyboard Shortcuts"));

        // A dialog launcher carries its label in Name, not Text (C1 dump).
        var launcher = new RibbonButton { Id = "ID_GRAPHIC_DISPLAY_OPTIONS", Name = "Graphic Display Options" };

        return Tab("View",
            Panel("Graphics", launcher, Toggle("ID_THIN_LINES", "Thin  Lines")),
            Panel("Windows", null, Button("ID_VIEW_TAB", "Tab  Views"), ui));
    }

    private static RibbonTab MassingAndSite()
    {
        var showMass = new SplitRadioGroupStandIn();
        showMass.Items.Add(Toggle("ID_SHOW_MASS_NO_OVERRIDE", "Show Mass  by View Settings"));
        showMass.Items.Add(Toggle("ID_SHOW_MASS_STANDARD", "Show Mass  Form and Floors"));
        showMass.Items.Add(Toggle("ID_SHOW_MASS_SURFACES", "Show Mass  Surface Types"));
        showMass.Items.Add(Toggle("ID_SHOW_MASS_ZONES", "Show Mass  Zones and Shades"));

        var stack = new RibbonRowPanel();
        stack.Items.Add(With(new RibbonCheckBox(), "ID_TOGGLE_SMOOTH_TOPOSOLID", "Toposolid Smooth Shading"));
        stack.Items.Add(Toggle("ID_SHOW_MASS", "Show  Mass"));
        stack.Items.Add(Button("ID_INPLACE_MASS", "In-Place  Mass"));

        var siteSettings = new RibbonCommandItem { Id = "ID_SITE_SETTINGS", Name = "Site Settings" };

        return Tab("Massing & Site", Panel("Conceptual Mass", siteSettings, showMass, stack));
    }

    private static (List<ScannedCommand> Commands, RibbonScanCounts Counts) Scan(Func<string, bool>? isPostable = null)
    {
        var counts = new RibbonScanCounts();
        var commands = RibbonScanner
            .EnumerateTabs(new[] { Manage(), View(), MassingAndSite() }, isPostable ?? (_ => true), counts)
            .ToList();
        return (commands, counts);
    }

    [Fact]
    public void Revit_menu_items_checklist_commands_and_dialog_launchers_reach_the_scan() => Screenshots.RunSta(() =>
    {
        var (commands, _) = Scan();

        commands.Select(c => c.Id).Should().Contain(new[]
        {
            // Manage > Settings > Additional Settings and its Annotations sub-menu
            "ID_SETTINGS_LINE_STYLES", "ID_SETTINGS_PENS", "ID_SETTINGS_HALFTONE",
            "ID_SETTINGS_ANNOTATIONS_LEADERS", "ID_VIEWTAGSTYLES_SECTIONTAGS",
            // View > Windows > User Interface
            "ID_BROWSER_ORGANIZATION", "ID_KEYBOARD_SHORTCUT_DIALOG",
            // panel arrows
            "ID_GRAPHIC_DISPLAY_OPTIONS", "ID_SITE_SETTINGS",
        });

        var lineStyles = commands.Single(c => c.Id == "ID_SETTINGS_LINE_STYLES");
        lineStyles.SourceTab.Should().Be("Manage");
        lineStyles.SourcePanel.Should().Be("Settings");
        lineStyles.DisplayName.Should().Be("Line Styles");
        commands.Single(c => c.Id == "ID_GRAPHIC_DISPLAY_OPTIONS").DisplayName.Should().Be("Graphic Display Options");
        commands.Single(c => c.Id == "ID_SITE_SETTINGS").SourcePanel.Should().Be("Conceptual Mass");
    });

    [Fact]
    public void On_off_state_controls_are_left_out_and_counted() => Screenshots.RunSta(() =>
    {
        var (commands, counts) = Scan();
        var ids = commands.Select(c => c.Id).ToList();

        ids.Should().NotContain(new[]
        {
            "ID_SHOW_VIEWCUBE", "ID_NAVIGATION_BAR", "ID_VIEW_STATUS_BAR",
            "ID_SHOW_MASS_NO_OVERRIDE", "ID_SHOW_MASS_STANDARD", "ID_SHOW_MASS_SURFACES", "ID_SHOW_MASS_ZONES",
            "ID_TOGGLE_SMOOTH_TOPOSOLID",
        });
        counts.ExcludedAsToggle(RibbonContainerKind.Checklist).Should().Be(3);
        counts.ExcludedAsToggle(RibbonContainerKind.RadioGroup).Should().Be(4);
        counts.ExcludedAsToggle(RibbonContainerKind.Stack).Should().Be(1);
    });

    [Fact]
    public void What_the_walk_emitted_before_SC027_is_still_emitted_and_containers_that_only_open_a_menu_are_not() => Screenshots.RunSta(() =>
    {
        var (commands, _) = Scan();
        var ids = commands.Select(c => c.Id).ToList();

        ids.Should().Contain(new[]
        {
            "ID_SETTINGS_MATERIALS", "ID_SETTINGS_OBJECTSTYLES", "ID_SETTINGS_SNAPPING",
            "ID_VIEW_TAB", "ID_INPLACE_MASS",
            // toggle buttons on a panel or in a stack were collected before SC-027 and still are
            "ID_THIN_LINES", "ID_SHOW_MASS",
            // a checklist was a plain RibbonButton to the old walk
            "HID_APPLICATION_ELEMENTS_RibbonListButton",
        });
        ids.Should().NotContain(new[] { "HID_SETTINGS_RibbonListButton", "ID_SETTINGS_ANNOTATIONS_MENU" });
        ids.Should().OnlyHaveUniqueItems();
    });

    [Fact]
    public void Per_kind_counts_match_the_fake_ribbon() => Screenshots.RunSta(() =>
    {
        var (commands, counts) = Scan();

        counts.TabsWalked.Should().Be(3);
        counts.Collected(RibbonContainerKind.Panel).Should().Be(4, "Materials, Thin Lines, Tab Views and the User Interface checklist");
        counts.Collected(RibbonContainerKind.Stack).Should().Be(4, "Object Styles, Snaps, Show Mass, In-Place Mass");
        counts.Collected(RibbonContainerKind.SplitButton).Should().Be(3);
        counts.Collected(RibbonContainerKind.SubMenu).Should().Be(2);
        counts.Collected(RibbonContainerKind.Checklist).Should().Be(2);
        counts.Collected(RibbonContainerKind.DialogLauncher).Should().Be(2);
        counts.MenuItemsCollected.Should().Be(7);
        counts.CollectedTotal.Should().Be(commands.Count);
        counts.Cycles.Should().Be(0);
        counts.DepthCapped.Should().Be(0);
    });

    [Fact]
    public void Newly_reached_ids_go_through_the_postability_check() => Screenshots.RunSta(() =>
    {
        var asked = new List<string>();
        var (commands, counts) = Scan(id => { asked.Add(id); return id != "ID_SETTINGS_PENS"; });

        asked.Should().BeEquivalentTo(new[]
        {
            "ID_SETTINGS_LINE_STYLES", "ID_SETTINGS_PENS", "ID_SETTINGS_HALFTONE",
            "ID_SETTINGS_ANNOTATIONS_LEADERS", "ID_VIEWTAGSTYLES_SECTIONTAGS",
            "ID_BROWSER_ORGANIZATION", "ID_KEYBOARD_SHORTCUT_DIALOG",
            "ID_GRAPHIC_DISPLAY_OPTIONS", "ID_SITE_SETTINGS",
        });
        commands.Select(c => c.Id).Should().NotContain("ID_SETTINGS_PENS");
        counts.NotPostable.Should().Be(1);
    });

    [Fact]
    public void A_panel_item_on_a_later_tab_wins_over_a_dialog_launcher_on_an_earlier_tab() => Screenshots.RunSta(() =>
    {
        // Review B1: Structure > Structure's arrow is Structural Settings, which also sits in a split
        // button on Manage > Settings. CommandCatalog keeps the first hit, so launchers come last.
        var structure = Tab("Structure",
            Panel("Structure", Button("ID_STRUCTURAL_SETTINGS", "Structural Settings"), Button("ID_OBJECTS_BEAM", "Beam")));
        var settings = With(new RibbonSplitButton(), "HID_STRUCTURAL_SETTINGS_GENERAL_RibbonListButton", "Structural  Settings");
        settings.Items.Add(Button("ID_STRUCTURAL_SETTINGS", "Structural Settings"));
        var manage = Tab("Manage", Panel("Settings", null, settings));
        var annotate = Tab("Annotate", Panel("Text", new RibbonButton { Id = "ID_SETTINGS_TEXT", Name = "Text Types" }, Button("ID_TEXT", "Text")));

        var commands = RibbonScanner.EnumerateTabs(new[] { structure, annotate, manage }, _ => true).ToList();

        var first = commands.First(c => c.Id == "ID_STRUCTURAL_SETTINGS");
        first.SourceTab.Should().Be("Manage");
        first.SourcePanel.Should().Be("Settings");
        // What CommandCatalog keeps: the first hit per id.
        var kept = commands.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
        kept["ID_STRUCTURAL_SETTINGS"].SourceTab.Should().Be("Manage");
        kept["ID_SETTINGS_TEXT"].SourceTab.Should().Be("Annotate", "a launcher-only command keeps its own tab");
        kept["ID_SETTINGS_TEXT"].SourcePanel.Should().Be("Text");
        commands.FindIndex(c => c.Id == "ID_OBJECTS_BEAM").Should().BeLessThan(commands.FindIndex(c => c.Id == "ID_SETTINGS_TEXT"));
    });

    [Fact]
    public void An_add_in_radio_group_header_with_an_id_is_still_emitted() => Screenshots.RunSta(() =>
    {
        // Review B2: RibbonRadioButtonGroup is a RibbonButton, so the old walk emitted it.
        var group = With(new RibbonRadioButtonGroup(), "CustomCtrl_%CustomCtrl_%Acme%Tools%Mode", "Mode");
        group.Items.Add(Toggle("CustomCtrl_%CustomCtrl_%Acme%Tools%ModeA", "A"));
        group.Items.Add(Toggle("CustomCtrl_%CustomCtrl_%Acme%Tools%ModeB", "B"));
        var counts = new RibbonScanCounts();

        var ids = RibbonScanner.EnumerateTabs(new[] { Tab("Acme", Panel("Tools", null, group)) }, _ => true, counts)
            .Select(c => c.Id).ToList();

        ids.Should().Equal("CustomCtrl_%CustomCtrl_%Acme%Tools%Mode");
        counts.ExcludedAsToggle(RibbonContainerKind.RadioGroup).Should().Be(2);
    });

    [Fact]
    public void A_checkable_checklist_entry_is_left_out_as_a_toggle() => Screenshots.RunSta(() =>
    {
        var list = With(new RibbonChecklistButton(), "HID_LIST", "List");
        var checkable = With(new RibbonMenuItem(), "ID_CHECKABLE", "Checkable");
        checkable.IsCheckable = true;
        list.Items.Add(checkable);
        list.Items.Add(With(new RibbonMenuItem(), "ID_KEYBOARD_SHORTCUT_DIALOG", "Keyboard Shortcuts"));
        var counts = new RibbonScanCounts();

        var ids = RibbonScanner.EnumerateTabs(new[] { Tab("View", Panel("Windows", null, list)) }, _ => true, counts)
            .Select(c => c.Id).ToList();

        ids.Should().Equal("HID_LIST", "ID_KEYBOARD_SHORTCUT_DIALOG");
        counts.ExcludedAsToggle(RibbonContainerKind.Checklist).Should().Be(1);
    });

    [Fact]
    public void Tabs_read_before_a_throw_are_kept_and_the_throw_is_counted() => Screenshots.RunSta(() =>
    {
        var counts = new RibbonScanCounts();

        var ids = RibbonScanner.EnumerateTabs(TabsThatBreak(), _ => true, counts).Select(c => c.Id).ToList();

        ids.Should().Equal("ID_SETTINGS_MATERIALS");
        counts.TabsSkipped.Should().Be(1);
        counts.Summary().Should().EndWith("skipped tabs 1, panels 0, items 0");

        static IEnumerable<RibbonTab?> TabsThatBreak()
        {
            yield return Tab("Manage", Panel("Settings", null, Button("ID_SETTINGS_MATERIALS", "Materials")));
            yield return null;
            throw new InvalidOperationException("tab collection broke");
        }
    });
}
