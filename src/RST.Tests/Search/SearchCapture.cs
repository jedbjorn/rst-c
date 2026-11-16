// The spec #33 Work Unit 1 captures (Fixtures/Ribbon/ribbon-2026-<context>.tsv) replayed as one scan:
// the hits in scan order after the scan's own rules, and each hit's tab and panel visibility in that
// context. The three contexts hold the same ribbon rows; only the visibility columns differ.

using RST.Core.Scanning;
using RST.Core.Search;
using RST.Tests.Scanning;

namespace RST.Tests.Search;

internal sealed class SearchCapture
{
    public const string Project = "project";
    public const string GenericModelFamily = "family-gm";
    public const string AnnotationFamily = "family-annotation";

    public SearchCapture(string context)
    {
        Seeds = Read("seed-2026-search.tsv").Select(c => new ScannedCommand(c[0], c[1], CommandOrigin.Native,
            null, null, null, null, EnumName: c[1])).ToArray();
        var seedIds = Seeds.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var kept = Read($"ribbon-2026-{context}.tsv")
            .Select(c => (Hit: RibbonCapture.ToHit(new RibbonCapture.Row(c[0], c[1], c[2], c[3], c[4], c[5], c[6], c[7],
                    Visible: bool.Parse(c[8]), Contextual: false)),
                State: new PlaceState(TabVisible: bool.Parse(c[8]), PanelVisible: bool.Parse(c[9]))))
            .Where(r => r.Hit is { } h && PostCommandRule.LetsAddinsPost(h.Id, seedIds.Contains)
                && RibbonWalkRules.Decide(h.Leaf, h.Container) == RibbonLeafDecision.Collect)
            .OrderBy(r => r.Hit!.Value.Container == RibbonContainerKind.DialogLauncher).ToArray();
        Hits = kept.Select(r => r.Hit!.Value).ToArray();
        States = kept.Select(r => r.State).ToArray();
    }

    public ScannedCommand[] Seeds { get; }
    public RibbonHit[] Hits { get; }
    /// <summary>Tab and panel visibility per hit: a place's state is <c>States[place.Handle]</c>.</summary>
    public PlaceState[] States { get; }

    public IReadOnlyList<SearchCatalogTool> Catalog(Func<string, bool>? isBanned = null) => SearchCatalog.Build(Seeds, Hits, isBanned);

    public PlaceState StateOf(SearchCatalogPlace place) => States[place.Handle];

    public SearchList List(bool isFamily) => SearchShowing.List(Catalog(), isFamily, StateOf);

    /// <summary>The Builder's catalogue from the same scan, as CommandCatalog builds it (no ban list).</summary>
    public IReadOnlyList<ScannedCommand> BuilderCatalog() =>
        CatalogMerge.Merge(Seeds, Hits).Where(c => !ModeRestrictedCommandIds.Contains(c.Id)).ToList();

    private static IEnumerable<string[]> Read(string file) => File.ReadLines(Path.Combine(AppContext.BaseDirectory,
        "Fixtures", "Ribbon", file)).Where(l => !l.StartsWith('#')).Skip(1).Select(l => l.Split('\t'));
}
