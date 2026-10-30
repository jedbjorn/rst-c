// rst-c's own (not vendored): the plain words every add-in report uses (spec #18, In Scope 5), in
// RST.Core so the Loader and the v3.1 tray say the same thing. Lines name the add-in and the year,
// for example "DiRoots.One (2027): locked on by your administrator".

using System.Globalization;

namespace RST.Core.Activation;

public static class ActivationWording
{
    /// <summary>Every report and the Confirm dialog end with this (decision #13: inform the user).</summary>
    public const string NextStart = "Add-in changes apply the next time Revit starts.";

    /// <summary>Why a change was not made, in plain words.</summary>
    public static string Reason(ChangeReason reason, PlanAction action, string? detail = null) => reason switch
    {
        ChangeReason.AdminLocked => action == PlanAction.Disable ? "locked on by your administrator" : "locked off by your administrator",
        ChangeReason.NeedsAdmin => "installed for all users; changing it needs administrator rights",
        ChangeReason.InUse => "a file was in use by another program; try again",
        ChangeReason.Vanished => "no longer installed",
        ChangeReason.FileError => "Revit's add-in settings file could not be read or written" + (string.IsNullOrWhiteSpace(detail) ? "" : " (" + detail + ")"),
        _ => string.IsNullOrWhiteSpace(detail) ? "an unexpected error" : detail!,
    };

    /// <summary>"DiRoots.One (2027): locked on by your administrator".</summary>
    public static string NotChangedLine(ActivationOutcome o) =>
        o.Name + " (" + Year(o.Year) + "): " + (o.Migrated ? "could not finish moving it from RST 3.0: " : "") + Reason(o.Reason ?? ChangeReason.Other, o.Action, o.Detail);

    /// <summary>The Confirm dialog's line for a change already known to be blocked.</summary>
    public static string BlockedLine(PlanEntry e) => e.Name + " (" + Year(e.Year) + "): " + Reason(e.ExpectedBlock ?? ChangeReason.Other, e.Action);

    /// <summary>A required add-in rst-c will not turn on, and what the user can do.</summary>
    public static string RequiredOffLine(PlanEntry e) => e.NotManaged == NotManagedReason.LockedOffByAdmin
        ? e.Name + " (" + Year(e.Year) + ") is required by this profile, but your administrator has switched it off."
        : e.Name + " (" + Year(e.Year) + ") is required by this profile, but it is switched off in Autodesk's Add-Ins Manager. Turn it on there (Manage tab › Add-Ins Manager).";

    /// <summary>
    /// "RST-Probe (2026, 2027)": one line per add-in name, with the years it applies to, in first-seen
    /// order (the de-duplicated per-year view of the Confirm dialog and the report).
    /// </summary>
    public static IReadOnlyList<string> GroupByName<T>(IEnumerable<T> rows, Func<T, string> name, Func<T, int> year)
    {
        var order = new List<string>();
        var years = new Dictionary<string, SortedSet<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var n = name(row);
            if (!years.TryGetValue(n, out var set))
            {
                set = [];
                years[n] = set;
                order.Add(n);
            }
            set.Add(year(row));
        }
        return order.Select(n => n + " (" + string.Join(", ", years[n].Select(Year)) + ")").ToList();
    }

    /// <summary>"Revit 2026 and 2027", "Revit 2025, 2026 and 2027".</summary>
    public static string Years(IReadOnlyList<int> years)
    {
        if (years.Count == 0) return "Revit";
        if (years.Count == 1) return "Revit " + Year(years[0]);
        return "Revit " + string.Join(", ", years.Take(years.Count - 1).Select(Year)) + " and " + Year(years[^1]);
    }

    public static string SettingsUnreadableNote(int year) =>
        "Revit " + Year(year) + ": its add-in settings file could not be read, so rst-c left it unchanged. Revit rebuilds it the next time it starts.";

    public static string UserDisableAllNote(int year) =>
        "Revit " + Year(year) + ": “Disable all Add-Ins” is ticked in Autodesk's Add-Ins Manager, so no add-in loads there.";

    public static string AdminDisableAllNote(int year) =>
        "Revit " + Year(year) + ": your administrator has switched off all add-ins.";

    public const string LedgerSaveFailed = "rst-c could not update its record of the add-ins it switched off";

    private static string Year(int year) => year.ToString(CultureInfo.InvariantCulture);
}
