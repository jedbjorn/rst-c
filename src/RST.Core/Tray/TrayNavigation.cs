// rst-c's own: the tray's tab order and open rule (Feature #17, decision #76), without Windows dependencies.

namespace RST.Core.Tray;

/// <summary>The window's pages, in tab order. AddIns is labelled RST Profiles in the UI.</summary>
public enum TrayPage
{
    AddIns,
    Updates,
    Apps,
    Health,
    Settings,
}

public static class TrayNavigation
{
    /// <summary>
    /// An explicit target always wins. An ordinary open (start, icon, menu Open or show signal) starts on
    /// RST Profiles when no window is shown; activating a shown window keeps its current page.
    /// </summary>
    public static TrayPage PageOnOpen(TrayPage? requested, bool windowShown, TrayPage current) =>
        requested ?? (windowShown ? current : TrayPage.AddIns);
}
