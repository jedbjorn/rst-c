// SC-027: the ribbon walk's decisions on plain data (RST.Core.Scanning.RibbonWalker). The shapes
// below copy the C1 dumps (shared/evidence/v31-main/passB/c1-dumps/20xx/ribbon.tsv): Manage >
// Settings > Additional Settings (a split button of menu items with an "Annotations" sub-menu),
// View > Windows > User Interface (a checklist of toggles plus two commands), the Show Mass radio
// group, the Select panel's check boxes and a panel dialog launcher. The AdWindows mapping itself
// (RibbonScanner.Describe) is covered by RST.RuntimeTests/Ribbon/RibbonScannerWalkTests.

using FluentAssertions;
using RST.Core.Scanning;
using Xunit;

namespace RST.Tests.Scanning;

public sealed class RibbonWalkTests
{
    private sealed class N
    {
        public RibbonNodeRole Role { get; init; }
        public RibbonContainerKind Kind { get; init; }
        public RibbonLeafKind Leaf { get; init; }
        public string? Id { get; init; }
        public string? Text { get; init; }
        public bool Checkable { get; init; }
        public bool ThrowOnDescribe { get; init; }
        public bool KidsThrow { get; init; }
        public List<N?> Kids { get; } = new();

        public N With(params N?[] kids) { Kids.AddRange(kids); return this; }

        public IEnumerable<N?> Children()
        {
            foreach (var kid in Kids) yield return kid;
            if (KidsThrow) throw new InvalidOperationException("items collection broke");
        }
    }

    private static N Leaf(RibbonLeafKind kind, string id, string? text = null) =>
        new() { Role = RibbonNodeRole.Leaf, Leaf = kind, Id = id, Text = text ?? id };

    private static N Button(string id) => Leaf(RibbonLeafKind.Button, id);
    private static N Toggle(string id) => Leaf(RibbonLeafKind.ToggleButton, id);
    private static N CheckBox(string id) => Leaf(RibbonLeafKind.CheckBox, id);
    private static N MenuItem(string id) => Leaf(RibbonLeafKind.MenuItem, id);
    private static N Separator() => new() { Role = RibbonNodeRole.Ignore };

    private static N Box(RibbonContainerKind kind, string? id = null, string? text = null) =>
        new() { Role = RibbonNodeRole.Container, Kind = kind, Id = id, Text = text };

    private static RibbonNode<N> Describe(N n)
    {
        if (n.ThrowOnDescribe) throw new InvalidOperationException("item property broke");
        return n.Role switch
        {
            RibbonNodeRole.Container => RibbonNode<N>.Container(n.Kind, n.Children(), n.Id, n.Text),
            RibbonNodeRole.Leaf => RibbonNode<N>.Leaf(n.Leaf, n.Id, n.Text, n.Checkable),
            _ => RibbonNode<N>.Ignored,
        };
    }

    private static RibbonWalker<N> Walker(Func<string, bool>? isPostable = null) =>
        new(Describe, isPostable ?? (_ => true));

    // Manage > Settings, as dumped: a plain button, a fold panel, and Additional Settings.
    private static N[] ManageSettings() =>
    [
        Button("ID_SETTINGS_MATERIALS"),
        Box(RibbonContainerKind.Stack).With(Button("ID_SETTINGS_OBJECTSTYLES"), Button("ID_SETTINGS_SNAPPING")),
        Box(RibbonContainerKind.SplitButton, "HID_SETTINGS_RibbonListButton", "Additional  Settings").With(
            MenuItem("ID_SETTINGS_LINE_STYLES"),
            MenuItem("ID_SETTINGS_PENS"),
            Box(RibbonContainerKind.SubMenu, "ID_SETTINGS_ANNOTATIONS_MENU", "Annotations").With(
                MenuItem("ID_SETTINGS_ANNOTATIONS_LEADERS"),
                MenuItem("ID_VIEWTAGSTYLES_SECTIONTAGS")),
            MenuItem("ID_SETTINGS_HALFTONE")),
    ];

    // View > Windows > User Interface, as dumped.
    private static N UserInterfaceChecklist() =>
        Box(RibbonContainerKind.Checklist, "HID_APPLICATION_ELEMENTS_RibbonListButton", "User  Interface").With(
            Toggle("ID_SHOW_VIEWCUBE"),
            Toggle("ID_NAVIGATION_BAR"),
            Toggle("ID_VIEW_STATUS_BAR"),
            Separator(),
            MenuItem("ID_BROWSER_ORGANIZATION"),
            MenuItem("ID_KEYBOARD_SHORTCUT_DIALOG"));

    [Fact]
    public void Revit_pulldown_menu_items_and_its_sub_menu_are_collected()
    {
        var walker = Walker();

        var ids = walker.WalkPanel(ManageSettings(), null).Select(h => h.Id).ToList();

        ids.Should().Equal(
            "ID_SETTINGS_MATERIALS", "ID_SETTINGS_OBJECTSTYLES", "ID_SETTINGS_SNAPPING",
            "ID_SETTINGS_LINE_STYLES", "ID_SETTINGS_PENS",
            "ID_SETTINGS_ANNOTATIONS_LEADERS", "ID_VIEWTAGSTYLES_SECTIONTAGS",
            "ID_SETTINGS_HALFTONE");
        ids.Should().NotContain("HID_SETTINGS_RibbonListButton", "a split button only opens its menu");
        ids.Should().NotContain("ID_SETTINGS_ANNOTATIONS_MENU", "a sub-menu header only opens the sub-menu");
        walker.Counts.Collected(RibbonContainerKind.SplitButton).Should().Be(3);
        walker.Counts.Collected(RibbonContainerKind.SubMenu).Should().Be(2);
        walker.Counts.MenuItemsCollected.Should().Be(5);
    }

    [Fact]
    public void A_checklist_gives_its_commands_and_leaves_out_its_ui_toggles()
    {
        var walker = Walker();

        var ids = walker.WalkPanel([UserInterfaceChecklist()], null).Select(h => h.Id).ToList();

        ids.Should().Equal(
            // Emitted before SC-027 as a plain button; kept so no catalog entry disappears.
            "HID_APPLICATION_ELEMENTS_RibbonListButton",
            "ID_BROWSER_ORGANIZATION", "ID_KEYBOARD_SHORTCUT_DIALOG");
        walker.Counts.Collected(RibbonContainerKind.Checklist).Should().Be(2);
        walker.Counts.ExcludedAsToggle(RibbonContainerKind.Checklist).Should().Be(3);
    }

    [Fact]
    public void Radio_group_entries_and_check_boxes_are_left_out_as_toggles()
    {
        var walker = Walker();
        N[] items =
        [
            Box(RibbonContainerKind.RadioGroup).With(
                Toggle("ID_SHOW_MASS_NO_OVERRIDE"), Toggle("ID_SHOW_MASS_STANDARD"),
                Toggle("ID_SHOW_MASS_SURFACES"), Toggle("ID_SHOW_MASS_ZONES")),
            Box(RibbonContainerKind.Stack).With(
                CheckBox("ID_TOGGLE_ALLOW_LINK_SELECTION"), CheckBox("ID_TOGGLE_ALLOW_PINNED_SELECTION"),
                Toggle("ID_SHOW_MASS"), Button("ID_INPLACE_MASS")),
        ];

        var ids = walker.WalkPanel(items, null).Select(h => h.Id).ToList();

        ids.Should().Equal(new[] { "ID_SHOW_MASS", "ID_INPLACE_MASS" },
            "a toggle button in a stack was collected before SC-027 and still is");
        walker.Counts.ExcludedAsToggle(RibbonContainerKind.RadioGroup).Should().Be(4);
        walker.Counts.ExcludedAsToggle(RibbonContainerKind.Stack).Should().Be(2);
        walker.Counts.ExcludedTotal.Should().Be(6);
    }

    [Fact]
    public void A_panel_dialog_launcher_is_collected()
    {
        var walker = Walker();

        var hits = walker.WalkPanel([Button("ID_TEXT")], Leaf(RibbonLeafKind.Button, "ID_SETTINGS_TEXT", "Text Types"));

        hits.Should().ContainSingle(h => h.Id == "ID_SETTINGS_TEXT")
            .Which.Should().Be(new RibbonHit("ID_SETTINGS_TEXT", "Text Types", RibbonContainerKind.DialogLauncher, RibbonLeafKind.Button));
        walker.Counts.Collected(RibbonContainerKind.DialogLauncher).Should().Be(1);
    }

    [Theory]
    [InlineData(RibbonLeafKind.CheckBox, RibbonContainerKind.Panel, RibbonLeafDecision.ExcludeToggle)]
    [InlineData(RibbonLeafKind.CheckBox, RibbonContainerKind.Stack, RibbonLeafDecision.ExcludeToggle)]
    [InlineData(RibbonLeafKind.CheckBox, RibbonContainerKind.Checklist, RibbonLeafDecision.ExcludeToggle)]
    [InlineData(RibbonLeafKind.ToggleButton, RibbonContainerKind.RadioGroup, RibbonLeafDecision.ExcludeToggle)]
    [InlineData(RibbonLeafKind.Button, RibbonContainerKind.RadioGroup, RibbonLeafDecision.ExcludeToggle)]
    [InlineData(RibbonLeafKind.ToggleButton, RibbonContainerKind.Checklist, RibbonLeafDecision.ExcludeToggle)]
    [InlineData(RibbonLeafKind.MenuItem, RibbonContainerKind.Checklist, RibbonLeafDecision.Collect)]
    [InlineData(RibbonLeafKind.Button, RibbonContainerKind.Checklist, RibbonLeafDecision.Collect)]
    [InlineData(RibbonLeafKind.ToggleButton, RibbonContainerKind.Panel, RibbonLeafDecision.Collect)]
    [InlineData(RibbonLeafKind.ToggleButton, RibbonContainerKind.Stack, RibbonLeafDecision.Collect)]
    [InlineData(RibbonLeafKind.ToggleButton, RibbonContainerKind.SplitButton, RibbonLeafDecision.Collect)]
    [InlineData(RibbonLeafKind.MenuItem, RibbonContainerKind.SplitButton, RibbonLeafDecision.Collect)]
    [InlineData(RibbonLeafKind.MenuItem, RibbonContainerKind.SubMenu, RibbonLeafDecision.Collect)]
    [InlineData(RibbonLeafKind.CommandItem, RibbonContainerKind.DialogLauncher, RibbonLeafDecision.Collect)]
    [InlineData(RibbonLeafKind.Button, RibbonContainerKind.MenuButton, RibbonLeafDecision.Collect)]
    [InlineData(RibbonLeafKind.Button, RibbonContainerKind.ListButton, RibbonLeafDecision.Collect)]
    public void Decide_leaves_out_only_on_off_state_controls(RibbonLeafKind leaf, RibbonContainerKind container, RibbonLeafDecision expected)
    {
        RibbonWalkRules.Decide(leaf, container).Should().Be(expected);
    }

    [Theory]
    [InlineData(RibbonLeafKind.MenuItem, RibbonContainerKind.Checklist, RibbonLeafDecision.ExcludeToggle)]
    [InlineData(RibbonLeafKind.Button, RibbonContainerKind.Checklist, RibbonLeafDecision.ExcludeToggle)]
    [InlineData(RibbonLeafKind.MenuItem, RibbonContainerKind.SplitButton, RibbonLeafDecision.Collect)]
    [InlineData(RibbonLeafKind.Button, RibbonContainerKind.Panel, RibbonLeafDecision.Collect)]
    [InlineData(RibbonLeafKind.CommandItem, RibbonContainerKind.DialogLauncher, RibbonLeafDecision.Collect)]
    public void Decide_treats_a_checkable_item_as_a_toggle_only_inside_a_checklist(RibbonLeafKind leaf, RibbonContainerKind container, RibbonLeafDecision expected)
    {
        RibbonWalkRules.Decide(leaf, container, isCheckable: true).Should().Be(expected);
    }

    [Fact]
    public void Kinds_reached_before_SC027_are_not_asked_about_postability()
    {
        var asked = new List<string>();
        var walker = Walker(id => { asked.Add(id); return true; });
        N[] items =
        [
            Button("ID_A"),
            Toggle("ID_THIN_LINES"),
            Box(RibbonContainerKind.Stack).With(Button("ID_B")),
            Box(RibbonContainerKind.SplitButton).With(Button("ID_C"), MenuItem("ID_MENU")),
            Box(RibbonContainerKind.MenuButton, "manage_views", "Switch  Windows"),
            UserInterfaceChecklist(),
        ];

        walker.WalkPanel(items, Button("ID_LAUNCHER"));

        asked.Should().BeEquivalentTo("ID_MENU", "ID_BROWSER_ORGANIZATION", "ID_KEYBOARD_SHORTCUT_DIALOG", "ID_LAUNCHER");
    }

    [Fact]
    public void A_newly_reached_id_Revit_cannot_post_is_left_out_and_counted()
    {
        var walker = Walker(id => id != "ID_SETTINGS_PENS" && id != "ID_SETTINGS_TEXT"
            ? true
            : throw new InvalidOperationException("lookup failed"));

        var ids = walker.WalkPanel(ManageSettings(), Button("ID_SETTINGS_TEXT")).Select(h => h.Id).ToList();

        ids.Should().NotContain(new[] { "ID_SETTINGS_PENS", "ID_SETTINGS_TEXT" });
        ids.Should().Contain("ID_SETTINGS_LINE_STYLES");
        walker.Counts.NotPostable.Should().Be(2);
    }

    [Fact]
    public void A_cycle_is_walked_once_and_counted()
    {
        var menu = Box(RibbonContainerKind.SplitButton);
        var sub = Box(RibbonContainerKind.SubMenu).With(MenuItem("ID_DEEP"));
        menu.With(MenuItem("ID_TOP"), sub);
        sub.With(menu); // sub-menu holds its own parent

        var walker = Walker();
        var ids = walker.WalkPanel([menu], null).Select(h => h.Id).ToList();

        ids.Should().Equal("ID_TOP", "ID_DEEP");
        walker.Counts.Cycles.Should().Be(1);
    }

    [Fact]
    public void The_same_container_twice_side_by_side_is_not_a_cycle()
    {
        var shared = Box(RibbonContainerKind.Stack).With(Button("ID_X"));
        var walker = Walker();

        walker.WalkPanel([shared, shared], null).Select(h => h.Id).Should().Equal("ID_X", "ID_X");
        walker.Counts.Cycles.Should().Be(0);
    }

    [Fact]
    public void Nesting_deeper_than_MaxDepth_is_cut_and_counted()
    {
        // Container depth d holds a leaf "ID_AT_<d+1>" and the next container.
        N Chain(int depth)
        {
            var box = Box(RibbonContainerKind.SubMenu).With(MenuItem($"ID_AT_{depth + 1}"));
            if (depth < 12) box.With(Chain(depth + 1));
            return box;
        }

        var walker = Walker();
        var ids = walker.WalkPanel([Chain(0)], null).Select(h => h.Id).ToList();

        ids.Should().HaveCount(RibbonWalkRules.MaxDepth);
        ids.Should().Contain($"ID_AT_{RibbonWalkRules.MaxDepth}").And.NotContain($"ID_AT_{RibbonWalkRules.MaxDepth + 1}");
        walker.Counts.DepthCapped.Should().Be(1);
    }

    [Fact]
    public void Items_without_an_id_and_ignored_items_are_skipped()
    {
        var walker = Walker();
        N?[] items = [null, Separator(), Leaf(RibbonLeafKind.Button, ""), Box(RibbonContainerKind.Checklist).With(MenuItem("ID_OK"))];

        walker.WalkPanel(items, null).Select(h => h.Id).Should().Equal("ID_OK");
        walker.Counts.CollectedTotal.Should().Be(1);
    }

    [Theory]
    [InlineData("Line Styles", "LineStyles", "ID_X", "Line Styles")]
    [InlineData("", "Text Types", "ID_X", "Text Types")]
    [InlineData("  ", null, "ID_X", "ID_X")]
    [InlineData(null, "", "ID_X", "ID_X")]
    public void DisplayNameFor_takes_the_first_non_blank_of_text_name_and_id(string? text, string? name, string id, string expected)
    {
        RibbonWalkRules.DisplayNameFor(text, name, id).Should().Be(expected);
    }

    [Fact]
    public void A_radio_group_header_with_an_id_is_still_emitted_and_its_entries_are_not()
    {
        // Review B2: every RibbonRadioButtonGroup is a RibbonButton, so the old walk emitted an
        // add-in's group header (one with an Id). Revit's SplitRadioGroup has a blank Id.
        var walker = Walker();
        N[] items =
        [
            Box(RibbonContainerKind.RadioGroup, "CustomCtrl_%CustomCtrl_%Addin%Panel%Mode", "Mode").With(
                Toggle("CustomCtrl_%CustomCtrl_%Addin%Panel%ModeA"), Toggle("CustomCtrl_%CustomCtrl_%Addin%Panel%ModeB")),
            Box(RibbonContainerKind.RadioGroup).With(Toggle("ID_SHOW_MASS_STANDARD")),
        ];

        var hits = walker.WalkItems(items);

        hits.Select(h => h.Id).Should().Equal("CustomCtrl_%CustomCtrl_%Addin%Panel%Mode");
        hits[0].Container.Should().Be(RibbonContainerKind.Panel);
        walker.Counts.ExcludedAsToggle(RibbonContainerKind.RadioGroup).Should().Be(3);
    }

    [Fact]
    public void A_checkable_checklist_entry_is_a_toggle_but_a_checkable_item_elsewhere_is_not()
    {
        var walker = Walker();
        N[] items =
        [
            Box(RibbonContainerKind.Checklist).With(
                new N { Role = RibbonNodeRole.Leaf, Leaf = RibbonLeafKind.MenuItem, Id = "ID_CHECKED_ENTRY", Checkable = true },
                MenuItem("ID_KEYBOARD_SHORTCUT_DIALOG")),
            Box(RibbonContainerKind.SplitButton).With(
                new N { Role = RibbonNodeRole.Leaf, Leaf = RibbonLeafKind.MenuItem, Id = "ID_CHECKABLE_IN_MENU", Checkable = true }),
        ];

        walker.WalkItems(items).Select(h => h.Id).Should().Equal("ID_KEYBOARD_SHORTCUT_DIALOG", "ID_CHECKABLE_IN_MENU");
        walker.Counts.ExcludedAsToggle(RibbonContainerKind.Checklist).Should().Be(1);
    }

    [Fact]
    public void An_item_that_throws_is_skipped_reported_and_the_walk_goes_on()
    {
        var skipped = new List<string>();
        var walker = Walker();
        N[] items =
        [
            Button("ID_BEFORE"),
            new N { Role = RibbonNodeRole.Leaf, Leaf = RibbonLeafKind.Button, Id = "ID_BROKEN", ThrowOnDescribe = true },
            Box(RibbonContainerKind.SplitButton).With(
                MenuItem("ID_IN_MENU"),
                new N { Role = RibbonNodeRole.Container, Kind = RibbonContainerKind.SubMenu, ThrowOnDescribe = true }),
            Button("ID_AFTER"),
        ];

        var ids = walker.WalkItems(items, (_, what) => skipped.Add(what)).Select(h => h.Id).ToList();

        ids.Should().Equal("ID_BEFORE", "ID_IN_MENU", "ID_AFTER");
        skipped.Should().HaveCount(2);
        walker.Counts.ItemsSkipped.Should().Be(2);
        walker.Counts.CollectedTotal.Should().Be(3);
    }

    [Fact]
    public void A_container_whose_items_throw_is_skipped_whole_header_included()
    {
        var walker = Walker();
        N[] items =
        [
            new N { Role = RibbonNodeRole.Container, Kind = RibbonContainerKind.Checklist, Id = "HID_BROKEN_LIST", KidsThrow = true }
                .With(MenuItem("ID_WOULD_BE_LOST")),
            Button("ID_AFTER"),
        ];

        walker.WalkItems(items).Select(h => h.Id).Should().Equal("ID_AFTER");
        walker.Counts.ItemsSkipped.Should().Be(1);
        walker.Counts.CollectedTotal.Should().Be(1, "nothing from the skipped container is counted");
    }

    [Fact]
    public void A_panel_items_collection_that_throws_skips_only_that_panel()
    {
        var walker = Walker();

        walker.WalkItems(Throwing()).Should().BeEmpty();
        walker.WalkItems([Button("ID_NEXT_PANEL")]).Select(h => h.Id).Should().Equal("ID_NEXT_PANEL");
        walker.Counts.ItemsSkipped.Should().Be(1);
        walker.Counts.CollectedTotal.Should().Be(1, "the broken panel's partial items are not counted");

        static IEnumerable<N?> Throwing()
        {
            yield return Button("ID_PARTIAL");
            throw new InvalidOperationException("panel items broke");
        }
    }

    [Fact]
    public void Panel_items_and_the_dialog_launcher_walk_separately()
    {
        var walker = Walker();

        walker.WalkItems([Button("ID_ITEM")]).Select(h => h.Id).Should().Equal("ID_ITEM");
        walker.WalkDialogLauncher(Button("ID_ARROW")).Should().ContainSingle()
            .Which.Container.Should().Be(RibbonContainerKind.DialogLauncher);
        walker.WalkDialogLauncher(null).Should().BeEmpty();
    }

    [Fact]
    public void Summary_lists_every_kind_so_two_scans_compare_field_by_field()
    {
        var counts = new RibbonScanCounts();
        var walker = new RibbonWalker<N>(Describe, _ => true, counts);
        counts.TabsTotal = 3;
        counts.TabsWalked = 2;

        walker.WalkPanel(ManageSettings(), Button("ID_SETTINGS_TEXT"));
        walker.WalkPanel([UserInterfaceChecklist(), Box(RibbonContainerKind.RadioGroup).With(Toggle("ID_R1"))], null);

        counts.Summary().Should().Be(
            "tabs 2/3; collected 12 (panel=2, stack=2, splitButton=3, menuButton=0, checklist=2, radioGroup=0, " +
            "listButton=0, subMenu=2, dialogLauncher=1; menu items 7); excluded as toggles 4 (panel=0, stack=0, " +
            "splitButton=0, menuButton=0, checklist=3, radioGroup=1, listButton=0, subMenu=0, dialogLauncher=0); " +
            "not postable 0 (outside PostableCommand 0, unresolved 0); cycles 0; depth capped 0; skipped tabs 0, panels 0, items 0");
    }

    // ---- SC-051 (decision #63): only what Revit lets an add-in post --------------------------------

    // Ids the rc.3 dumps show as PostableCommand members (BuiltinCommandScanner's seed) in 2026 and
    // 2027 (shared/evidence/v31-rc3/c1/20xx/catalog.tsv): their catalogue names are enum names.
    private static readonly HashSet<string> PostableCommandIds = new(StringComparer.Ordinal)
    {
        "ID_OBJECTS_WALL", "ID_OBJECTS_TEXT_NOTE", "ID_SETTINGS_MATERIALS", "ID_SETTINGS_OBJECTSTYLES",
        "ID_SETTINGS_SNAPPING", "ID_SETTINGS_LINE_STYLES", "ID_SETTINGS_PENS", "ID_SETTINGS_ANNOTATIONS_LEADERS",
        "ID_VIEWTAGSTYLES_SECTIONTAGS", "ID_SETTINGS_HALFTONE", "ID_BROWSER_ORGANIZATION",
        "ID_KEYBOARD_SHORTCUT_DIALOG", "ID_STRUCTURAL_SETTINGS", "ID_THIN_LINES",
    };

    private static RibbonWalker<N> RuleWalker(Func<string, bool>? resolves = null, RibbonScanCounts? counts = null) =>
        new(Describe, resolves ?? (_ => true), counts, PostableCommandIds.Contains);

    [Fact]
    public void Text_Types_and_every_other_id_Revit_wont_let_add_ins_post_is_left_out_of_every_kind()
    {
        var walker = RuleWalker();
        N[] annotateText = [Button("ID_OBJECTS_TEXT_NOTE")];
        N[] architecture =
        [
            Button("ID_BUTTON_SELECT"),                                            // Modify, on a panel
            Box(RibbonContainerKind.SplitButton, null, "Wall").With(
                Button("ID_OBJECTS_WALL"), Button("ID_INPLACE_WALL")),             // In-Place Wall, in a split button
            Box(RibbonContainerKind.Stack).With(Toggle("ID_THIN_LINES"), Toggle("ID_SHOW_MASS")),
            Box(RibbonContainerKind.MenuButton, "manage_views", "Switch  Windows"), // a menu button's header
            UserInterfaceChecklist(),                                              // its header, and two commands
            Button("CustomCtrl_%CustomCtrl_%Add-Ins%Acme%Go"),                     // an add-in's button
            Box(RibbonContainerKind.SplitButton).With(MenuItem("CustomCtrl_%CustomCtrl_%Add-Ins%Acme%More")),
        ];

        var ids = walker.WalkPanel(annotateText, Button("ID_SETTINGS_TEXT")).Select(h => h.Id).ToList();
        ids.AddRange(walker.WalkPanel(ManageSettings(), Button("ID_STRUCTURAL_SETTINGS")).Select(h => h.Id));
        ids.AddRange(walker.WalkPanel(architecture, null).Select(h => h.Id));

        ids.Should().Equal(
            "ID_OBJECTS_TEXT_NOTE",
            "ID_SETTINGS_MATERIALS", "ID_SETTINGS_OBJECTSTYLES", "ID_SETTINGS_SNAPPING",
            "ID_SETTINGS_LINE_STYLES", "ID_SETTINGS_PENS",
            "ID_SETTINGS_ANNOTATIONS_LEADERS", "ID_VIEWTAGSTYLES_SECTIONTAGS", "ID_SETTINGS_HALFTONE",
            "ID_STRUCTURAL_SETTINGS",
            "ID_OBJECTS_WALL", "ID_THIN_LINES",
            "ID_BROWSER_ORGANIZATION", "ID_KEYBOARD_SHORTCUT_DIALOG",
            "CustomCtrl_%CustomCtrl_%Add-Ins%Acme%Go", "CustomCtrl_%CustomCtrl_%Add-Ins%Acme%More");
        walker.Counts.OutsidePostableCommand.Should().Be(6);
        walker.Counts.Unresolved.Should().Be(0);
        walker.Counts.NotPostable.Should().Be(6);
        walker.Counts.NotPostableIds.Should().Equal(
            "ID_SETTINGS_TEXT", "ID_BUTTON_SELECT", "ID_INPLACE_WALL", "ID_SHOW_MASS",
            "manage_views", "HID_APPLICATION_ELEMENTS_RibbonListButton");
        walker.Counts.Collected(RibbonContainerKind.DialogLauncher).Should().Be(1, "Structural Settings stays; Text Types goes");
        walker.Counts.ExcludedAsToggle(RibbonContainerKind.Checklist).Should().Be(3, "a toggle is still counted as a toggle first");
    }

    [Fact]
    public void An_id_met_on_two_panels_is_counted_twice_and_listed_once()
    {
        // Text Types is the arrow of both Annotate > Text and the family editor's Create > Text.
        var walker = RuleWalker();

        walker.WalkPanel([Button("ID_OBJECTS_TEXT_NOTE")], Button("ID_SETTINGS_TEXT")).Should().ContainSingle();
        walker.WalkPanel([Button("ID_OBJECTS_TEXT_NOTE")], Button("ID_SETTINGS_TEXT")).Should().ContainSingle();

        walker.Counts.OutsidePostableCommand.Should().Be(2);
        walker.Counts.NotPostableIds.Should().Equal("ID_SETTINGS_TEXT");
    }

    [Fact]
    public void The_lookup_check_still_applies_to_newly_reached_ids_after_the_PostCommand_rule()
    {
        var asked = new List<string>();
        var walker = RuleWalker(id => { asked.Add(id); return id != "ID_SETTINGS_PENS" && id != "CustomCtrl_%Acme%Gone"; });
        N[] items =
        [
            Box(RibbonContainerKind.SplitButton).With(
                MenuItem("ID_SETTINGS_LINE_STYLES"), MenuItem("ID_SETTINGS_PENS"),
                MenuItem("ID_NOT_POSTABLE_MENU_ITEM"), MenuItem("CustomCtrl_%Acme%Gone")),
            Button("CustomCtrl_%Acme%Panel"),
        ];

        var ids = walker.WalkPanel(items, null).Select(h => h.Id).ToList();

        ids.Should().Equal("ID_SETTINGS_LINE_STYLES", "CustomCtrl_%Acme%Panel");
        asked.Should().Equal(new[] { "ID_SETTINGS_LINE_STYLES", "ID_SETTINGS_PENS", "CustomCtrl_%Acme%Gone" },
            "an id the rule refuses is not looked up, and kinds reached before SC-027 are not looked up at all");
        walker.Counts.OutsidePostableCommand.Should().Be(1);
        walker.Counts.Unresolved.Should().Be(2);
        walker.Counts.NotPostableIds.Should().Equal("ID_SETTINGS_PENS", "ID_NOT_POSTABLE_MENU_ITEM", "CustomCtrl_%Acme%Gone");
    }

    [Fact]
    public void Without_PostableCommand_ids_the_rule_is_off_and_the_walk_is_as_before()
    {
        var ids = Walker().WalkPanel([Button("ID_BUTTON_SELECT")], Button("ID_SETTINGS_TEXT")).Select(h => h.Id).ToList();

        ids.Should().Equal("ID_BUTTON_SELECT", "ID_SETTINGS_TEXT");
    }

    [Fact]
    public void A_PostableCommand_check_that_throws_leaves_the_item_out_and_walks_on()
    {
        var walker = new RibbonWalker<N>(Describe, _ => true, null,
            id => id == "ID_BROKEN" ? throw new InvalidOperationException("broke") : true);

        var ids = walker.WalkPanel([Button("ID_BROKEN"), Button("ID_FINE")], null).Select(h => h.Id).ToList();

        ids.Should().Equal("ID_FINE");
        walker.Counts.OutsidePostableCommand.Should().Be(1);
        walker.Counts.ItemsSkipped.Should().Be(0);
    }

    [Fact]
    public void The_summary_splits_not_postable_into_its_two_reasons()
    {
        var counts = new RibbonScanCounts();
        var walker = RuleWalker(id => id != "ID_SETTINGS_PENS", counts);

        walker.WalkPanel(ManageSettings(), Button("ID_SETTINGS_TEXT"));

        counts.Summary().Should().Contain("; not postable 2 (outside PostableCommand 1, unresolved 1); cycles 0;");
    }
}
