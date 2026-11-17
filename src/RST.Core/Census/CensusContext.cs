// rst-c's own (not vendored): the census context key (Feature #22, spec #34, "Contexts").
//
// A context is what the census index is keyed on, in terms RST can read at run time from the active document and
// view. The same string is the `context` column of census/<year>/full.tsv, exceptions.tsv and contexts.tsv, so the
// loader needs no second table to look a row up:
//
//   project/<ViewType>                                   e.g. project/FloorPlan
//   family/<BuiltInCategory>/<FamilyPlacementType>/<ViewType>   e.g. family/OST_GenericModel/OneLevelBased/FloorPlan
//
// The parts are Revit enum names (Autodesk.Revit.DB.ViewType, BuiltInCategory, FamilyPlacementType), passed in as
// strings so this stays pure Core. A project template (.rte) uses the project key (decision #105).

namespace RST.Core.Census;

public static class CensusContext
{
    public const string ProjectKind = "project";
    public const string FamilyKind = "family";

    /// <summary>The key of a project (or project template) document in a view of <paramref name="viewType"/>.</summary>
    public static string Project(string viewType) => ProjectKind + "/" + Part(viewType);

    /// <summary>The key of a family document of a category and placement type, in a view of <paramref name="viewType"/>.</summary>
    public static string Family(string builtInCategory, string placementType, string viewType) =>
        FamilyKind + "/" + Part(builtInCategory) + "/" + Part(placementType) + "/" + Part(viewType);

    /// <summary>The key without its view part: the document kind a census run opens one scratch document for.</summary>
    public static string DocumentPart(string key)
    {
        var i = (key ?? "").LastIndexOf('/');
        return i <= 0 ? key ?? "" : key![..i];
    }

    /// <summary>The key's view part: "FloorPlan" for project/FloorPlan.</summary>
    public static string ViewPart(string key)
    {
        var i = (key ?? "").LastIndexOf('/');
        return i < 0 ? key ?? "" : key![(i + 1)..];
    }

    private static string Part(string? s)
    {
        var t = (s ?? "").Trim();
        return t.Length == 0 ? "Invalid" : t.Replace('/', '_');
    }
}
