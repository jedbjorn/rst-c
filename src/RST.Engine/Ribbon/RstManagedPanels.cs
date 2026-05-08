// RstManagedPanels.cs — names of ribbon panels RST itself put on a
// shared/built-in tab (e.g. Add-Ins). Tab-level filtering via
// RstManagedTabs is too coarse on a built-in tab — we'd skip every
// other add-in's buttons too. Panel-title filtering scoped to "RST"
// (and any future panel we add) is the right cut.
//
// Per-process static. Cleared on assembly unload (Revit shutdown);
// repopulated at next OnStartup.

using System;
using System.Collections.Generic;

namespace RST.Engine.Ribbon;

internal static class RstManagedPanels
{
    private static readonly HashSet<string> _titles = new(StringComparer.Ordinal);

    public static void Add(string? title)
    {
        if (string.IsNullOrEmpty(title)) return;
        _titles.Add(title!);
    }

    public static bool Contains(string? title) =>
        title is not null && _titles.Contains(title);

    public static IReadOnlyCollection<string> Titles => _titles;
}
