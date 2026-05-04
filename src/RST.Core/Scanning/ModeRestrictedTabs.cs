// ModeRestrictedTabs.cs — contextual Revit tabs visible only inside a mode
// (family editor, in-place model/mass, MEP zone). Their commands no-op or
// error when posted from the main app context, so RST excludes them from
// the catalog entirely. Hard exclusion — admins cannot opt in.

using System;
using System.Collections.Generic;

namespace RST.Core.Scanning;

public static class ModeRestrictedTabs
{
    private static readonly HashSet<string> _names = new(StringComparer.Ordinal)
    {
        "Family Editor",
        "In-Place Mass",
        "In-Place Model",
        "Zone",
    };

    public static IReadOnlyCollection<string> Names => _names;

    public static bool Contains(string? tabName) =>
        tabName is not null && _names.Contains(tabName);
}
