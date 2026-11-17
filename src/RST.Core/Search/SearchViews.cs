// rst-c's own (not vendored): the view names and badge texts of Search-It's Must Run In and NOT IN GUI labels
// (Feature #21, spec #35, "Must Run In" and "Running a Tool"). English only, like SlotMessages (spec #16, Out of
// Scope: localization); unit-tested here so the window only binds them.

namespace RST.Core.Search;

public static class SearchViews
{
    /// <summary>The view types Must Run In names, in the order it names them, with their names (spec #35's table).</summary>
    public static readonly IReadOnlyList<(string ViewType, string Name)> Names = new[]
    {
        ("FloorPlan", "Floor Plan"),
        ("CeilingPlan", "Ceiling Plan"),
        ("EngineeringPlan", "Structural Plan"),
        ("Elevation", "Elevation"),
        ("Section", "Section"),
        ("ThreeD", "3D View"),
        ("DrawingSheet", "Sheet"),
        ("Schedule", "Schedule"),
        ("DraftingView", "Drafting View"),
        ("Legend", "Legend"),
    };

    /// <summary>How many view names the badge shows before "+N".</summary>
    public const int BadgeNames = 2;

    /// <summary>The amber badge of a tool that runs here only from places Revit does not draw.</summary>
    public const string NotInGui = "NOT IN GUI";

    /// <summary>The name of a view type ("Floor Plan"); a view type the table does not hold keeps its enum name.</summary>
    public static string NameOf(string viewType)
    {
        foreach (var (type, name) in Names)
            if (string.Equals(type, viewType, StringComparison.Ordinal)) return name;
        return viewType;
    }

    /// <summary>
    /// The distinct view types, named and in the table's order; view types the table does not hold come last, by name.
    /// </summary>
    public static IReadOnlyList<string> Ordered(IEnumerable<string> viewTypes)
    {
        var distinct = viewTypes.Where(v => !string.IsNullOrEmpty(v)).Distinct(StringComparer.Ordinal).ToList();
        int Rank(string v)
        {
            for (var i = 0; i < Names.Count; i++)
                if (Names[i].ViewType == v) return i;
            return Names.Count;
        }
        return distinct.OrderBy(Rank).ThenBy(v => v, StringComparer.Ordinal).Select(NameOf).ToList();
    }

    /// <summary>"Must Run In Elevation, Section", or "Must Run In Floor Plan, Ceiling Plan +3"; empty for no names.</summary>
    public static string MustRunInBadge(IReadOnlyList<string> names)
    {
        if (names is null || names.Count == 0) return "";
        var shown = string.Join(", ", names.Take(BadgeNames));
        return names.Count > BadgeNames ? $"Must Run In {shown} +{names.Count - BadgeNames}" : $"Must Run In {shown}";
    }

    /// <summary>"Runs in: Elevation, Section. Switch to one of these views to run this tool." with every name.</summary>
    public static string MustRunInToolTip(IReadOnlyList<string> names) =>
        names is null || names.Count == 0 ? "" : $"Runs in: {string.Join(", ", names)}. Switch to one of these views to run this tool.";

    /// <summary>The amber message of a click that can't run (spec #35, "Running a Tool", 4), naming the tool's ribbon place.</summary>
    public static string CantRunHere(string? name, string? location)
    {
        var what = string.IsNullOrWhiteSpace(name) ? "This tool" : name!.Trim();
        return string.IsNullOrWhiteSpace(location)
            ? $"This tool can't be run with RST here: {what}."
            : $"This tool can't be run with RST here: {what} ({location!.Trim()}).";
    }
}
