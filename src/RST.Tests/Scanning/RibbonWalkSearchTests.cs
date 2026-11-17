// Spec #35 (Search-It: runs per file type): what the walk adds for Search-It's catalogue. Each hit carries its place
// path in the census's form, so the census's not-drawn rows can be matched to it, and the ids Revit won't let add-ins
// post are kept, marked, for Search-It only, without changing the scan's counts (the Builder's catalogue and the
// summary line stay what they were).

using FluentAssertions;
using RST.Core.Scanning;
using Xunit;

namespace RST.Tests.Scanning;

public sealed class RibbonWalkSearchTests
{
    private sealed class N
    {
        public RibbonNodeRole Role { get; init; }
        public RibbonContainerKind Kind { get; init; }
        public string? Id { get; init; }
        public string? Segment { get; init; }
        public bool Break { get; init; }
        public List<N?> Kids { get; } = new();
        public N With(params N?[] kids) { Kids.AddRange(kids); return this; }
    }

    private static N Button(string id) => new() { Role = RibbonNodeRole.Leaf, Id = id };
    private static N Box(RibbonContainerKind kind, string segment) => new() { Role = RibbonNodeRole.Container, Kind = kind, Segment = segment };
    private static N Break() => new() { Break = true };

    private static RibbonNode<N> Describe(N n) =>
        n.Break ? RibbonNode<N>.PanelBreak
        : n.Role == RibbonNodeRole.Container ? RibbonNode<N>.Container(n.Kind, n.Kids) with { PathSegment = n.Segment }
        : n.Role == RibbonNodeRole.Leaf ? RibbonNode<N>.Leaf(RibbonLeafKind.Button, n.Id, n.Id)
        : RibbonNode<N>.Ignored;

    private static N[] Panel() =>
    [
        Button("ID_ON_PANEL"),
        Box(RibbonContainerKind.Stack, "/RibbonFoldPanel()").With(
            Box(RibbonContainerKind.SplitButton, "/RibbonSplitButton(ID_WALL_RibbonListButton)").With(Button("ID_IN_SPLIT"))),
        Break(),
        Box(RibbonContainerKind.Stack, "/RibbonRowPanel()").With(Button("ID_IN_SLIDE_OUT")),
    ];

    [Fact]
    public void Each_hit_carries_its_census_place_path()
    {
        var walker = new RibbonWalker<N>(Describe, _ => true, recordPaths: true);

        var hits = walker.WalkItems(Panel());
        hits.AddRange(walker.WalkDialogLauncher(Button("ID_LAUNCHER")));

        hits.Select(h => (h.Id, h.Path)).Should().Equal(
            ("ID_ON_PANEL", ""),
            ("ID_IN_SPLIT", "/RibbonFoldPanel()/RibbonSplitButton(ID_WALL_RibbonListButton)"),
            ("ID_IN_SLIDE_OUT", "/SlideOut/RibbonRowPanel()"),
            ("ID_LAUNCHER", "DialogLauncher"));
    }

    [Fact]
    public void Without_paths_the_walk_is_as_before()
    {
        var hits = new RibbonWalker<N>(Describe, _ => true).WalkItems(Panel());
        hits.Select(h => h.Id).Should().Equal("ID_ON_PANEL", "ID_IN_SPLIT", "ID_IN_SLIDE_OUT");
        hits.Should().OnlyContain(h => h.Path == null && h.Postable);
    }

    [Fact]
    public void Ids_Revit_wont_post_are_kept_marked_for_Search_It_and_counted_as_before()
    {
        Func<string, bool> postable = id => id != "ID_IN_SPLIT";
        var before = new RibbonScanCounts();
        var after = new RibbonScanCounts();

        var builder = new RibbonWalker<N>(Describe, _ => true, before, postable).WalkItems(Panel());
        var search = new RibbonWalker<N>(Describe, _ => true, after, postable, keepNotPostable: true, recordPaths: true).WalkItems(Panel());

        builder.Select(h => h.Id).Should().Equal("ID_ON_PANEL", "ID_IN_SLIDE_OUT");
        search.Select(h => (h.Id, h.Postable)).Should().Equal(("ID_ON_PANEL", true), ("ID_IN_SPLIT", false), ("ID_IN_SLIDE_OUT", true));
        search.Where(h => h.Postable).Select(h => h.Id).Should().Equal(builder.Select(h => h.Id), "the Builder gets the hits it always got");
        after.Summary().Should().Be(before.Summary(), "the kept id is counted as left out, never as collected");
        after.OutsidePostableCommandIds.Should().Equal("ID_IN_SPLIT");
    }

    [Fact]
    public void A_kept_id_still_needs_the_lookup_where_the_walk_asks_for_it()
    {
        // A menu item is a kind the walk reaches since SC-027: it is emitted only when Revit can look its id up.
        N MenuItem(string id) => new() { Role = RibbonNodeRole.Leaf, Id = id };
        RibbonNode<N> DescribeMenu(N n) => n.Role == RibbonNodeRole.Leaf
            ? RibbonNode<N>.Leaf(RibbonLeafKind.MenuItem, n.Id, n.Id)
            : Describe(n);
        var walker = new RibbonWalker<N>(DescribeMenu, id => id == "ID_KNOWN", null, _ => false, keepNotPostable: true);

        walker.WalkItems([MenuItem("ID_KNOWN"), MenuItem("ID_UNKNOWN")]).Select(h => h.Id).Should().Equal("ID_KNOWN");
    }
}
