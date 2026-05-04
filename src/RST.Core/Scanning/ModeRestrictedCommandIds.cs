// ModeRestrictedCommandIds.cs — built-in Revit command IDs that only operate
// inside a contextual editing mode (Family Editor / In-Place Mass / In-Place
// Model / Zone). PostableCommand enumerates them regardless of mode, so we
// filter them out by Id at catalog-merge time.
//
// Seeded from the v1 ribbon-probe scan against Revit 2026 build 26.4.10.51.
// Extend on user complaint — no enum-typed denylist because PostableCommand
// values shift between Revit majors.

using System;
using System.Collections.Generic;

namespace RST.Core.Scanning;

public static class ModeRestrictedCommandIds
{
    private static readonly HashSet<string> _ids = new(StringComparer.Ordinal)
    {
        "ID_COUPLER_STATUS",
        "ID_END_INPLACE_FAMILY",
        "ID_END_INPLACE_MASS",
        "ID_END_INPLACE_ZONE",
        "ID_LOAD_INTO_PROJECTS",
        "ID_LOAD_INTO_PROJECTS_CLOSE",
        "ID_LOAD_INTO_PROJECTS_CLOSE_REBAR_SHAPE",
        "ID_LOAD_INTO_PROJECTS_REBAR_SHAPE",
        "ID_QUIT_INPLACE_FAMILY",
        "ID_QUIT_INPLACE_MASS",
        "ID_QUIT_INPLACE_ZONE",
        "ID_SHAPE_STATUS",
        "ID_TURN_ON_MULTI_PLANAR_SHAPE",
    };

    public static IReadOnlyCollection<string> Ids => _ids;

    public static bool Contains(string? commandId) =>
        commandId is not null && _ids.Contains(commandId);
}
