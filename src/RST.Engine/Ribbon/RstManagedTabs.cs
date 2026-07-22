// RstManagedTabs.cs — names of ribbon tabs RST itself put on the ribbon.
//
// Populated by ProfileTabBuilder when an active profile resolves, using the
// profile's chosen tab name. RibbonScanner consults this set so the catalog
// doesn't include generated profile buttons — a profile button for "Wall" in
// the catalog tree as a profile-buildable command is confusing and
// circular.
//
// Per-process static. Cleared on assembly unload (Revit shutdown);
// repopulated at next OnStartup.

using System;
using System.Collections.Generic;

namespace RST.Engine.Ribbon;

internal static class RstManagedTabs
{
    private static readonly HashSet<string> _names = new(StringComparer.Ordinal);

    public static void Add(string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        _names.Add(name!);
    }

    public static bool Contains(string? name) =>
        name is not null && _names.Contains(name);

    public static IReadOnlyCollection<string> Names => _names;
}
