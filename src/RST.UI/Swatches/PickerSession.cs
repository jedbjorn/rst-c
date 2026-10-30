// Copied from splash-it src/SplashIt.UI/Swatches/PickerSession.cs (splash-it commit ba86a71), MIT, same owner.
// Changes: namespaces (SplashIt.* to RST.*); comments say RST.

using RST.Core.Colors;

namespace RST.UI.Swatches;

/// <summary>
/// Picker state kept for the Revit session, as gantt-it keeps it on its session object
/// (gantt-it src/GanttIt.Revit/UI/GanttItSession.cs, commit aa83f35): the recent-colors row
/// survives closing and reopening an RST window, and is lost when Revit closes.
/// </summary>
public static class PickerSession
{
    /// <summary>The picker's recent colors, most recent first (12 at most).</summary>
    public static RecentColors RecentColors { get; } = new();
}
