// rst-c's own (not vendored): a C# port of profile_loader.html's RSTify rules (spec #16, WU3):
// CORE_TABS / getSourceTabs / renderTabToggles' filter (1061-1112), profileHasPresets and the
// "Adopt RSTify Presets?" text (1394-1412), and failedDisableMessage (1579-1589).

using RST.Core.Profiles;

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

    /// <summary>
    /// The tabs <paramref name="profile"/>'s commands come from (<c>getSourceTabs</c>): every slot's
    /// <c>sourceTab</c>, and every tool's in a referenced stack, except "Custom".
    /// </summary>
    public static ISet<string> SourceTabs(Profile? profile)
    {
        var tabs = new HashSet<string>(StringComparer.Ordinal);
        if (profile is null) return tabs;
        foreach (var panel in profile.Panels)
        {
            foreach (var slot in panel.Slots)
            {
                if (slot is null) continue;
                if (!string.IsNullOrEmpty(slot.SourceTab) && slot.SourceTab != "Custom") tabs.Add(slot.SourceTab);
                if (slot.SlotType == "stack" && profile.Stacks.TryGetValue(slot.Name, out var stack) && stack is not null)
                {
                    foreach (var tool in stack.Tools)
                        if (tool is not null && !string.IsNullOrEmpty(tool.SourceTab) && tool.SourceTab != "Custom")
                            tabs.Add(tool.SourceTab);
                }
            }
        }
        return tabs;
    }

    /// <summary>
    /// The RSTify list for <paramref name="profile"/>: the ribbon's tabs, in ribbon order, minus the
    /// core tabs, the profile's source tabs and the profile's own tab.
    /// </summary>
    public static RstifyTabList TabsToOffer(Profile? profile, IReadOnlyList<string>? allTabs)
    {
        if (profile is null || allTabs is null || allTabs.Count == 0) return new RstifyTabList(true, []);
        var locked = SourceTabs(profile);
        locked.Add(profile.Tab ?? "");
        var tabs = allTabs.Where(t => !CoreTabs.Contains(t) && !locked.Contains(t)).ToList();
        return new RstifyTabList(false, tabs);
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
               + "Disable Unused Add-ins additionally renames non-required .addin files so they don’t load on the next launch.\n\n"
               + summary + "\n\n"
               + "Adopt these defaults for \"" + profile.ProfileName + "\"? You can change them anytime.";
    }
}

/// <summary>
/// An add-in the disable pass could not rename. <see cref="ProfileLoadService"/> reports its file
/// name (or, when the whole pass threw, <c>"(disable subsystem failure)"</c> with the error); the
/// v2.x bridge's older shape used <c>{ name }</c>.
/// </summary>
public sealed record FailedDisable(string? FileName, string? Name = null, string? Error = null);

public static class FailedDisableMessages
{
    public const string Title = "Some Add-ins Were Not Disabled";

    /// <summary>
    /// <c>failedDisableMessage</c>: the acknowledgement text for add-ins the disable pass could not
    /// rename, or null when there is nothing to report (flag #15: the bridge's <c>fileName</c> is read).
    /// </summary>
    public static string? Message(IReadOnlyList<FailedDisable>? failed)
    {
        if (failed is null || failed.Count == 0) return null;
        var names = failed
            .Select(f => f is null ? "" : !string.IsNullOrEmpty(f.FileName) ? f.FileName! : f.Name ?? "")
            .Where(n => n.Length > 0)
            .ToList();
        if (names.Count == 0)
            return "Some add-ins could not be disabled. Administrator permissions are required to disable add-ins installed for all users.";
        return names.Count == 1
            ? names[0] + " was not disabled. Administrator permissions are required to disable add-ins installed for all users."
            : "The following add-ins were not disabled (administrator permissions required): " + string.Join(", ", names);
    }
}
