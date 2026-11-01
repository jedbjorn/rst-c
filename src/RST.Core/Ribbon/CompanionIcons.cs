using RST.Core.Profiles;

namespace RST.Core.Ribbon;

/// <summary>Bundled companion artwork for uncustomized tool slots, independent of the catalog.</summary>
public static class CompanionIcons
{
    // Product ids from companions/companions.json; button names are the companions' PushButtonData names.
    private static readonly IReadOnlyDictionary<string, (string Button, string Icon)[]> Products =
        new Dictionary<string, (string, string)[]>
        {
            ["gantt-it"] = [("GanttIt.Open", "gantt")],
            ["splash-it"] = [("SplashIt.OpenSettings", "splash"), ("SplashIt.GroupTabs", "group")],
        };

    /// <summary>
    /// Assets-relative PNG path, or null when the slot has any stored icon or is not a companion
    /// command. Works for loose and stacked tools without changing the slot or its profile.
    /// </summary>
    public static string? Resolve(Slot slot, int size = 32, bool dark = false)
    {
        if (slot.SlotType != "tool" || !string.IsNullOrEmpty(slot.IconFile)) return null;
        if (size is not (16 or 32)) return null;

        const string prefix = "CustomCtrl_%";
        var id = slot.CommandId;
        if (id is null || !id.StartsWith(prefix, StringComparison.Ordinal)) return null;
        id = id[prefix.Length..];
        // AdWindows also exposes CustomCtrl_%CustomCtrl_%Tab%Panel%Button.
        if (id.StartsWith(prefix, StringComparison.Ordinal)) id = id[prefix.Length..];
        var parts = id.Split('%');
        if (parts.Length != 3 || parts.Any(string.IsNullOrEmpty)) return null;

        foreach (var commands in Products.Values)
            foreach (var (button, icon) in commands)
                if (parts[2] == button)
                    return $"icons/companions/{icon}_{size}{(dark ? "_dark" : "")}.png";
        return null;
    }
}
