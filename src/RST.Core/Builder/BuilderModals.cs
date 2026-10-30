// rst-c's own (not vendored): the rules behind the Builder's two Configure modals (spec #16, WU5),
// ported from profile_builder.html: openPresetsModal / savePresetsModal (2640-2696) and
// ensureCleanupTargets / restoreCleanupDefaults / saveCleanupModal (2719-2827). Behavior is
// unchanged, except Planner default 4: the Cleanup modal edits a copy, so Cancel reverts.

using RST.Core.Loader;
using RST.Core.Profiles;

namespace RST.Core.Builder;

public static class BuilderModals
{
    /// <summary>
    /// The RST Presets modal's tab list: every ribbon tab except the core tabs and the profile's
    /// own tab, then the preset tabs that are not on this machine's ribbon (so they stay visible
    /// and removable instead of being dropped silently).
    /// </summary>
    public static IReadOnlyList<string> PresetTabs(IReadOnlyList<string>? allTabs, ProfilePresets? presets, string? profileTab)
    {
        var locked = (profileTab ?? "").Trim();
        var fromRibbon = (allTabs ?? []).Where(t => !string.IsNullOrEmpty(t) && !RstifyRules.CoreTabs.Contains(t) && t != locked).ToList();
        var fromPreset = (presets?.HiddenTabs ?? []).Where(t =>
            !string.IsNullOrEmpty(t) && !fromRibbon.Contains(t) && t != locked && !RstifyRules.CoreTabs.Contains(t)).ToList();
        return [.. fromRibbon, .. fromPreset];
    }

    /// <summary>savePresetsModal: the switched-on tabs in list order and the disable flag.</summary>
    public static ProfilePresets ApplyPresets(IEnumerable<(string Tab, bool Hidden)> rows, bool disableUnused) => new()
    {
        HiddenTabs = rows.Where(r => r.Hidden && !string.IsNullOrEmpty(r.Tab)).Select(r => r.Tab).ToList(),
        DisableUnusedAddins = disableUnused,
    };

    /// <summary>
    /// The Cleanup modal's starting rows (ensureCleanupTargets): a copy of the profile's targets, or
    /// the defaults when it has none (null or empty).
    /// </summary>
    public static List<CleanupTarget> CleanupRowsFor(IReadOnlyList<CleanupTarget>? targets) =>
        targets is null || targets.Count == 0 ? CleanupDefaults.Build() : targets.Select(Copy).ToList();

    /// <summary>saveCleanupModal: drops blank rows (no name and no path).</summary>
    public static List<CleanupTarget> ApplyCleanup(IEnumerable<CleanupTarget> rows) =>
        rows.Where(t => t is not null && (!string.IsNullOrWhiteSpace(t.Name) || !string.IsNullOrWhiteSpace(t.Path))).Select(Copy).ToList();

    public static CleanupTarget Copy(CleanupTarget t) => new() { Id = t.Id, Name = t.Name, Path = t.Path, Kind = t.Kind, Enabled = t.Enabled };
}
