// rst-c's own (not vendored): a C# port of profile_loader.html's RSTify rules (spec #16, WU3):
// CORE_TABS / getSourceTabs / renderTabToggles' filter (1061-1112), profileHasPresets and the
// "Adopt RSTify Presets?" text (1394-1412), and failedDisableMessage (1579-1589).

using RST.Core.Builder;
using RST.Core.Profiles;
using RST.Core.Scanning;

namespace RST.Core.Loader;

/// <summary>
/// What the "RSTify: Hide These Tabs" list shows: nothing at all (<see cref="Blank"/>: no profile, or
/// no ribbon tabs were captured), the "No tabs available to hide" line (not blank, no tabs), or the
/// tabs the user may hide.
/// </summary>
public sealed record RstifyTabList(bool Blank, IReadOnlyList<string> Tabs)
{
    public bool ShowsEmptyState => !Blank && Tabs.Count == 0;
}

public static class RstifyRules
{
    /// <summary>Tabs the RSTify list never offers (<c>CORE_TABS</c>).</summary>
    public static readonly IReadOnlySet<string> CoreTabs = new HashSet<string>(StringComparer.Ordinal)
    {
        "Modify", "Manage", "View", "Annotate", "Add-Ins", "Create",
    };

    /// <summary>Tabs that are a saved tool's only home; unknown commands use their saved source tab.</summary>
    public static ISet<string> SourceTabs(Profile? profile, IReadOnlyList<ScannedCommand>? catalog = null) =>
        ToolHomes(profile, catalog).Where(t => t.Homes.Count == 1)
            .Select(t => t.Homes.Single()).ToHashSet(StringComparer.Ordinal);

    /// <summary>The ribbon's tabs in order, minus core tabs, single homes and the profile's own tab.</summary>
    public static RstifyTabList TabsToOffer(Profile? profile, IReadOnlyList<string>? allTabs,
                                          IReadOnlyList<ScannedCommand>? catalog = null)
    {
        if (profile is null || allTabs is null || allTabs.Count == 0) return new RstifyTabList(true, []);
        var locked = SourceTabs(profile, catalog);
        locked.Add(profile.Tab ?? "");
        var tabs = allTabs.Where(t => !CoreTabs.Contains(t) && !locked.Contains(t)).ToList();
        return new RstifyTabList(false, tabs);
    }

    /// <summary>Whether hiding this tab would leave a tool without a visible home, and its label.</summary>
    public static bool BlockingTool(Profile profile, IReadOnlyList<string> allTabs,
                                    IReadOnlyList<ScannedCommand>? catalog,
                                    IReadOnlyCollection<string> hiddenTabs, string tab, out string? label)
    {
        foreach (var tool in ToolHomes(profile, catalog))
            if (tool.Homes.Contains(tab) && !tool.Homes.Any(home =>
                    allTabs.Contains(home) && (CoreTabs.Contains(home) || (home != tab && !hiddenTabs.Contains(home)))))
            {
                label = tool.Name;
                return true;
            }
        label = null;
        return false;
    }

    /// <summary>Apply saved ticks in ribbon order, dropping ticks that would leave a tool homeless.</summary>
    public static IReadOnlyList<string> ReconcileHiddenTabs(Profile profile, IReadOnlyList<string> allTabs,
                                                          IReadOnlyList<ScannedCommand>? catalog,
                                                          IReadOnlyCollection<string> savedTabs)
    {
        var hidden = new List<string>();
        foreach (var tab in TabsToOffer(profile, allTabs, catalog).Tabs)
            if (savedTabs.Contains(tab) && !BlockingTool(profile, allTabs, catalog, hidden, tab, out _))
                hidden.Add(tab);
        return hidden;
    }

    private static IEnumerable<(string? Name, HashSet<string> Homes)> ToolHomes(
        Profile? profile, IReadOnlyList<ScannedCommand>? catalog)
    {
        if (profile is null) yield break;
        var byId = new Dictionary<string, ScannedCommand>(StringComparer.Ordinal);
        foreach (var c in catalog ?? [])
            if (c is not null && !string.IsNullOrEmpty(c.Id)) byId.TryAdd(c.Id, c);
        foreach (var slot in profile.Panels.SelectMany(p => p.Slots))
        {
            var tools = PanelStacks.IsStack(slot) ? PanelStacks.ToolsOf(profile, slot) : [slot];
            foreach (var tool in tools)
            {
                if (!PanelStacks.IsTool(tool) || CustomTools.IsCustom(tool)) continue;
                var command = tool.CommandId is { } id ? byId.GetValueOrDefault(id) : null;
                var homes = command?.Placements is { Count: > 0 } placements
                    ? placements.Select(p => p.Tab).ToHashSet(StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal) { tool.SourceTab == "Custom" ? "" : tool.SourceTab ?? "" };
                homes.Remove("");
                var label = !string.IsNullOrWhiteSpace(tool.Name) ? tool.Name
                    : !string.IsNullOrWhiteSpace(command?.DisplayName) ? command.DisplayName : tool.CommandId;
                yield return (label, homes);
            }
        }
    }

    /// <summary><c>profileHasPresets</c>: the admin set hidden tabs or the disable-unused default.</summary>
    public static bool HasPresets(Profile? profile) =>
        profile?.Presets is { } p && (p.HiddenTabs is { Count: > 0 } || p.DisableUnusedAddins);

    public const string AdoptPresetsTitle = "Adopt RSTify Presets?";

    /// <summary>The "Adopt RSTify Presets?" confirmation's body.</summary>
    public static string AdoptPresetsMessage(Profile profile)
    {
        var presets = profile.Presets ?? new ProfilePresets();
        var n = presets.HiddenTabs?.Count ?? 0;
        var summary = "Admin defaults: "
                      + (n == 0 ? "no tabs hidden" : n + " tab" + (n == 1 ? "" : "s") + " to hide")
                      + (presets.DisableUnusedAddins ? ", disable unused add-ins on." : ".");
        return "RSTify hides selected ribbon tabs to declutter Revit while this profile is active. "
               + "Disable Unused Add-ins additionally switches off the add-ins this profile does not need, for your Windows user, from the next time Revit starts.\n\n"
               + summary + "\n\n"
               + "Adopt these defaults for \"" + profile.ProfileName + "\"? You can change them anytime.";
    }
}
