namespace RST.Core.Ribbon;

/// <summary>
/// Which ribbon buttons belong to the Designs/OS sister apps, so RST can gather them from each app's own
/// Add-Ins panel into one "RST Accessories" panel (decision #79). The apps are unchanged: this only names
/// what they already put on the ribbon.
/// </summary>
public static class AccessoryPanels
{
    public const string PanelTitle = "RST Accessories";

    public const string PanelId = "RST_Accessories";

    // In companions/companions.json order, which is the order the buttons take on the panel. Panel: the title
    // of the panel the app creates on Add-Ins (its App.PanelName). ButtonPrefix: how its PushButtonData names
    // start (GanttIt.Open, SplashIt.OpenSettings, SplashIt.GroupTabs, PickIt.Pick).
    private static readonly (string Panel, string ButtonPrefix)[] Apps =
    [
        ("Gantt-It", "GanttIt."),
        ("Splash-It", "SplashIt."),
        ("Pick-It", "PickIt."),
    ];

    /// <summary>The sister apps' own panel titles, in the order their buttons are gathered.</summary>
    public static IReadOnlyList<string> SisterPanelTitles { get; } = Apps.Select(a => a.Panel).ToArray();

    /// <summary>
    /// True when <paramref name="itemId"/> (an AdWindows item id such as
    /// <c>CustomCtrl_%CustomCtrl_%Add-Ins%Gantt-It%GanttIt.Open</c>) is a button of the sister app that owns the
    /// panel titled <paramref name="panelTitle"/>. Both must agree, so another add-in's panel that happens to
    /// share a title keeps its buttons.
    /// </summary>
    public static bool IsSisterButton(string? panelTitle, string? itemId)
    {
        if (string.IsNullOrEmpty(panelTitle) || string.IsNullOrEmpty(itemId)) return false;
        var button = itemId[(itemId.LastIndexOf('%') + 1)..];
        foreach (var (panel, prefix) in Apps)
            if (panelTitle == panel)
                return button.Length > prefix.Length && button.StartsWith(prefix, StringComparison.Ordinal);
        return false;
    }
}
