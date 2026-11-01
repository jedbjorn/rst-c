// BuiltinTabs.cs — Revit's stock ribbon tabs whose COMMANDS are also
// authoritatively enumerated by BuiltinCommandScanner (PostableCommand).
//
// Used by OriginClassifier (commands on these tabs are CommandOrigin.Native)
// and by RibbonScanner (skipped because BuiltinCommandScanner already
// covers them — walking them would only add duplicates).
//
// **NOT included**:
//   - "Add-Ins" — Revit creates this tab, but it's a HOST for third-party
//     buttons. Their commands are NOT in PostableCommand. Skipping it
//     here drops every addin like Kinship that lives there. (Removed in
//     scan-addins-tab fix after FnB hit this with Kinship on first verify.)
//   - "FormIt", "FormIt Converter", "eTransmit" — Autodesk-shipped
//     companions, not core Revit. Their commands aren't in
//     PostableCommand either. Walking them is correct.

using System;
using System.Collections.Generic;

namespace RST.Core.Scanning;

public static class BuiltinTabs
{
    private static readonly HashSet<string> _names = new(StringComparer.Ordinal)
    {
        "Architecture", "Structure", "Systems", "Steel", "Precast",
        "Insert", "Annotate", "Analyze", "Massing & Site", "Collaborate",
        "View", "Manage", "Modify", "Create", "RST",
        "Modify | Walls", "Modify | Floors", "Modify | Roofs",
        "Modify | Structural Framing", "Modify | Generic Models",
    };

    public static IReadOnlyCollection<string> Names => _names;

    // Tabs introduced after the earliest supported Revit year. Shared with Loader/Builder.
    private static readonly Dictionary<string, int> _introducedIn = new(StringComparer.Ordinal)
    {
        ["Concrete"] = 2027,
    };

    public static bool Contains(string? tabName, int? revitYear = null) =>
        tabName is not null && (_names.Contains(tabName) || IsIntroducedTab(tabName, revitYear));

    // An unknown year preserves the pre-2027 tab rules.
    internal static bool IsIntroducedTab(string? tabName, int? revitYear) =>
        tabName is not null && revitYear is { } year
        && _introducedIn.TryGetValue(tabName, out var firstYear) && year >= firstYear;
}
