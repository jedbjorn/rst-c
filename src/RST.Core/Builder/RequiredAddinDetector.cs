// rst-c's own (not vendored): a C# port of profile_builder.html computeRequiredAddins (2846-2959)
// (spec #16, WU4), run at save time. Behavior is unchanged:
//   - a command's catalogue origin is authoritative: Native and Autodesk commands are never a
//     dependency, whatever tab or panel they sit on (the SC-001 fix);
//   - otherwise a tool on a non-built-in tab makes its tab a required add-in, and a tool on a
//     built-in tab makes its panel one, unless the panel is itself a built-in tab or panel name;
//   - "Custom" and "CustomCtrl_…" tabs are skipped; a tool resolves its origin through the catalogue
//     by command id and uses its own stored tab/panel; a stack's tools resolve tab, panel and origin
//     through the catalogue only (stack tools store no tab);
//   - SC-051: a tool slot whose command id is not in the catalogue gets the origin the ribbon scan
//     would give it (OriginClassifier.ClassifyRibbonButton): an add-in's CustomCtrl_ id is Custom,
//     any other id on one of Revit's own tabs is Native, so it is never an add-in. The catalogue now
//     leaves out Revit tools that Revit won't let add-ins run (Text Types, In-Place Wall, Modify …);
//     a profile saved earlier keeps their buttons, and without this a re-save would record their
//     panels ("Text", "Build", "Select") as required add-ins again (SC-007's bug);
//   - tabs come first, then panels, each in first-seen order, and each is enriched from
//     addin_lookup.json: an exact key, else the first entry whose key or display name contains the
//     name or is contained by it (case-insensitive), in the file's order.

using RST.Core.Loader;
using RST.Core.Profiles;

namespace RST.Core.Builder;

public static class RequiredAddinDetector
{
    /// <summary>
    /// The Builder's <c>BUILTIN_TABS</c>. The same 19 names as the Loader's
    /// (<see cref="RequiredAddinRules.BuiltinTabs"/>), so one set serves both.
    /// </summary>
    public static IReadOnlySet<string> BuiltinTabs => RequiredAddinRules.BuiltinTabs;

    /// <summary>Panels Revit ships on built-in tabs that are not add-ins (<c>BUILTIN_PANELS</c>).</summary>
    public static readonly IReadOnlySet<string> BuiltinPanels = new HashSet<string>(StringComparer.Ordinal)
    {
        "Align", "Macros", "External", "External Tools",
        "Analysis", "Selection", "Visual Programming",
    };

    private const string AutodeskOrigin = "Autodesk";

    private const string UnknownOrigin = nameof(RST.Core.Scanning.CommandOrigin.Unknown);

    /// <summary>
    /// The profile's required add-ins, in the shape save writes:
    /// <c>{ tabName, addinFile (null when unknown), url ("" when unknown) }</c>.
    /// </summary>
    public static List<RequiredAddin> Compute(
        Profile profile,
        IEnumerable<CatalogCommand>? catalog,
        IReadOnlyDictionary<string, AddinLookupEntry>? lookup)
    {
        ArgumentNullException.ThrowIfNull(profile);
        // commandId → { sourceTab, sourcePanel, origin }; a later duplicate id wins, as in the JS object.
        var byId = new Dictionary<string, CatalogCommand>(StringComparer.Ordinal);
        foreach (var c in catalog ?? [])
            if (c is not null && !string.IsNullOrEmpty(c.Id)) byId[c.Id] = c;

        var tabs = new OrderedSet();
        var panels = new OrderedSet();

        void Classify(string? srcTab, string? srcPanel, string? origin)
        {
            if (origin is CatalogCommand.NativeOrigin or AutodeskOrigin) return;
            if (string.IsNullOrEmpty(srcTab)) return;
            // SC-007 (spec #18): a command of unknown origin on one of Revit's own tabs is never an
            // add-in. Host tabs (Add-Ins, FormIt, eTransmit) are where add-ins live, so they are not
            // covered; an add-in's CustomCtrl_ button is classified Custom by the ribbon scan anyway.
            if (origin == UnknownOrigin && BuiltinTabs.Contains(srcTab) && !RST.Core.Scanning.HostTabs.Contains(srcTab)) return;
            if (srcTab == "Custom" || srcTab.StartsWith("CustomCtrl_", StringComparison.Ordinal)) return;
            if (BuiltinTabs.Contains(srcTab))
            {
                if (string.IsNullOrEmpty(srcPanel)) return;
                if (BuiltinTabs.Contains(srcPanel) || BuiltinPanels.Contains(srcPanel)) return;
                panels.Add(srcPanel);
            }
            else
            {
                tabs.Add(srcTab);
            }
        }

        foreach (var panel in profile.Panels ?? [])
        {
            if (panel is null) continue;
            foreach (var slot in panel.Slots ?? [])
            {
                if (slot is null) continue;
                if (slot.SlotType == "tool")
                {
                    var info = !string.IsNullOrEmpty(slot.CommandId) && byId.TryGetValue(slot.CommandId, out var hit) ? hit : null;
                    // SC-051: a command the catalogue doesn't list gets the origin the ribbon scan gives it.
                    var origin = info?.Origin
                        ?? (string.IsNullOrEmpty(slot.CommandId)
                            ? null
                            : RST.Core.Scanning.OriginClassifier.ClassifyRibbonButton(slot.SourceTab, slot.CommandId).ToString());
                    Classify(slot.SourceTab, slot.SourcePanel, origin);
                }
                else if (slot.SlotType == "stack")
                {
                    if (profile.Stacks is null || !profile.Stacks.TryGetValue(slot.Name ?? "", out var stack) || stack is null) continue;
                    foreach (var tool in stack.Tools ?? [])
                    {
                        if (tool is null || string.IsNullOrEmpty(tool.CommandId) || !byId.TryGetValue(tool.CommandId, out var info)) continue;
                        Classify(info.SourceTab, info.SourcePanel, info.Origin);
                    }
                }
            }
        }

        var entries = new List<RequiredAddin>();
        foreach (var name in tabs.Concat(panels))
        {
            var (file, url) = Enrich(name, lookup);
            entries.Add(new RequiredAddin { TabName = name, AddinFile = file, Url = url });
        }
        return entries;
    }

    /// <summary>
    /// <c>enrich</c>: the lookup entry for <paramref name="name"/> (exact key, then the fuzzy pass),
    /// as <c>(file || null, url || "")</c>; <c>(null, "")</c> when nothing matches.
    /// </summary>
    public static (string? AddinFile, string Url) Enrich(string name, IReadOnlyDictionary<string, AddinLookupEntry>? lookup)
    {
        if (lookup is null || lookup.Count == 0) return (null, "");
        if (lookup.TryGetValue(name, out var direct) && direct is not null) return Pick(direct);
        var nameLower = name.ToLowerInvariant();
        foreach (var (key, value) in lookup)
        {
            var entry = value ?? new AddinLookupEntry(null, null, null);
            var dn = (entry.DisplayName ?? "").ToLowerInvariant();
            var kl = key.ToLowerInvariant();
            if (kl.Length > 0 && (kl.Contains(nameLower, StringComparison.Ordinal) || nameLower.Contains(kl, StringComparison.Ordinal)))
                return Pick(entry);
            if (dn.Length > 0 && (dn.Contains(nameLower, StringComparison.Ordinal) || nameLower.Contains(dn, StringComparison.Ordinal)))
                return Pick(entry);
        }
        return (null, "");
    }

    private static (string?, string) Pick(AddinLookupEntry e) =>
        (string.IsNullOrEmpty(e.File) ? null : e.File, string.IsNullOrEmpty(e.Url) ? "" : e.Url);

    /// <summary>A JS Set of strings: insertion order, no duplicates.</summary>
    private sealed class OrderedSet : IEnumerable<string>
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private readonly List<string> _order = [];

        public void Add(string value)
        {
            if (_seen.Add(value)) _order.Add(value);
        }

        public IEnumerator<string> GetEnumerator() => _order.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
