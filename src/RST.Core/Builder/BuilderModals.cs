// rst-c's own (not vendored): the rules behind the Builder's RST Presets (spec #16, WU5), ported
// from profile_builder.html openPresetsModal / savePresetsModal (2640-2696). Feature #18 (spec #19)
// moved the Presets and Junk Cleanup modals inline, onto the Profile pane's Loader defaults and Junk
// cleanup tabs, which edit the profile directly (an unsaved change, saved with Save); the cleanup
// modal's copy-and-apply rules went with the modal.

using RST.Core.Loader;
using RST.Core.Profiles;

namespace RST.Core.Builder;

public static class BuilderModals
{
    /// <summary>
    /// The Loader defaults tab's "Hide these tabs (RSTify)" list: every ribbon tab except the core tabs and the profile's
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
}
