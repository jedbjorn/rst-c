// rst-c's own (not vendored): THE required-add-in matcher (spec #18, In Scope 2). The Confirm
// dialog's preview, the commit, the Add-in Check and the Loader's status pills all call this one
// function, so they can never disagree (v3.0 had three matchers, defect D3).
//
// Per requirement, per Revit year, in tiers; a tier that matches anything ends the search:
//   1. the manifest file name (requiredAddins[].addinFile), case-insensitive;
//   2. a ClientId/AddInId (requiredAddins[].addinId);
//   3. fuzzy: the requirement's tab name contains, or is contained by, the manifest's file stem or
//      an entry's Name (the v2.x isAddinLoaded rule), only when tiers 1 and 2 found nothing.
// Every manifest a tier matches counts, not just the first. A requirement on one of Revit's own
// tabs (RequiredAddinRules.BuiltinTabs, e.g. "Architecture") is never an add-in.

using RST.Core.Loader;
using RST.Core.Profiles;

namespace RST.Core.Activation;

public static class RequiredAddinMatch
{
    /// <summary>False for a requirement that names one of Revit's own tabs, or names nothing.</summary>
    public static bool IsAddinRequirement(RequiredAddin? required) =>
        required is not null
        && !RequiredAddinRules.IsNativeTab(required.TabName)
        && (!string.IsNullOrWhiteSpace(required.TabName) || !string.IsNullOrWhiteSpace(required.AddinFile) || !string.IsNullOrWhiteSpace(required.AddinId));

    /// <summary>The items of one year that satisfy <paramref name="required"/>.</summary>
    public static IReadOnlyList<InventoryItem> Match(RequiredAddin required, IEnumerable<InventoryItem> sameYear)
    {
        if (!IsAddinRequirement(required)) return [];
        var items = sameYear as IReadOnlyList<InventoryItem> ?? sameYear.ToList();

        if (!string.IsNullOrWhiteSpace(required.AddinFile))
        {
            var file = required.AddinFile.Trim();
            var byFile = items.Where(i => string.Equals(i.FileName, file, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byFile.Count > 0) return byFile;
        }
        if (AddinIds.Normalize(required.AddinId) is { } id)
        {
            var byId = items.Where(i => i.AddinIds.Contains(id)).ToList();
            if (byId.Count > 0) return byId;
        }
        if (!string.IsNullOrWhiteSpace(required.TabName))
        {
            var tab = required.TabName.Trim();
            return items.Where(i => FuzzyMatches(i, tab)).ToList();
        }
        return [];
    }

    /// <summary>Every inventory item, in every year, that some requirement matches.</summary>
    public static HashSet<InventoryItem> RequiredItems(IEnumerable<RequiredAddin>? required, AddinInventory inventory)
    {
        var set = new HashSet<InventoryItem>(ReferenceEqualityComparer.Instance);
        var list = (required ?? []).Where(IsAddinRequirement).ToList();
        if (list.Count == 0) return set;
        foreach (var year in inventory.Items.GroupBy(i => i.Year))
        {
            var items = year.ToList();
            foreach (var r in list)
                foreach (var hit in Match(r, items))
                    set.Add(hit);
        }
        return set;
    }

    private static bool FuzzyMatches(InventoryItem item, string tab)
    {
        var stem = Path.GetFileNameWithoutExtension(item.FileName);
        if (Contains(stem, tab) || Contains(tab, stem)) return true;
        foreach (var entry in item.Entries)
        {
            var name = entry.Name?.Trim();
            if (!string.IsNullOrEmpty(name) && (Contains(name, tab) || Contains(tab, name))) return true;
        }
        return false;
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Length > 0 && needle.Length > 0 && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
