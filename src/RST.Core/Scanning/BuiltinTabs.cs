// BuiltinTabs.cs — Revit's stock ribbon tabs.
//
// Used by OriginClassifier (commands on these tabs are CommandOrigin.Native)
// and by RSTify in RST-009 (these tabs are protected from hide rules unless
// explicitly opted in).

using System;
using System.Collections.Generic;

namespace RST.Core.Scanning;

public static class BuiltinTabs
{
    private static readonly HashSet<string> _names = new(StringComparer.Ordinal)
    {
        "Architecture", "Structure", "Systems", "Steel", "Precast",
        "Insert", "Annotate", "Analyze", "Massing & Site", "Collaborate",
        "View", "Manage", "Modify", "Add-Ins", "Create", "RST",
        "FormIt", "FormIt Converter", "eTransmit",
        "Modify | Walls", "Modify | Floors", "Modify | Roofs",
        "Modify | Structural Framing", "Modify | Generic Models",
    };

    public static IReadOnlyCollection<string> Names => _names;

    public static bool Contains(string? tabName) =>
        tabName is not null && _names.Contains(tabName);
}
