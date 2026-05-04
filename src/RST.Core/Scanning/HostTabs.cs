// HostTabs.cs — Revit-shipped tabs that are *hosts* for third-party panels
// rather than logical groupings of related commands.
//
// Distinct from BuiltinTabs (commands also covered by PostableCommand):
// these tabs Revit creates as a parking spot for any add-in that doesn't
// register its own tab. Several unrelated vendors typically share one
// host tab — Kinship, Enscape, Ideate, Diroots and a dozen others all
// drop their panels onto "Add-Ins" by default.
//
// For catalog grouping the host tab itself is uninformative ("Add-Ins"
// would be a soup of every vendor); the panel name IS the addin
// identity. Port of the pyRevit RST `addin_panels` rule
// (RST/app/user_config.py step 5): when sourceTab is a host tab, treat
// the panel name as the catalog group.

using System;
using System.Collections.Generic;

namespace RST.Core.Scanning;

public static class HostTabs
{
    private static readonly HashSet<string> _names = new(StringComparer.Ordinal)
    {
        "Add-Ins",
        "FormIt",
        "FormIt Converter",
        "eTransmit",
    };

    public static IReadOnlyCollection<string> Names => _names;

    public static bool Contains(string? tabName) =>
        tabName is not null && _names.Contains(tabName);
}
