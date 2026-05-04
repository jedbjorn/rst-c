// RstManagedTabs.cs — names of ribbon tabs RST itself put on the ribbon.
//
// Populated by RibbonBuilder at OnStartup with the always-present "RST"
// tab and (when an active profile resolves) the profile's chosen Tab
// name. RibbonScanner consults this set so the catalog doesn't include
// our own buttons — a profile button for "Wall" appearing in the
// catalog tree as a profile-buildable command is confusing and
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
