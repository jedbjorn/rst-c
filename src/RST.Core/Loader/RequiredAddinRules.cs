// rst-c's own (not vendored): a C# port of profile_loader.html's required-add-in rules
// (spec #16, WU3): normalizeRequiredAddins (955-968), BUILTIN_TABS / isNativeTab (986-1059), the
// add-in row (1297-1365), getNotFoundAddins (1560-1568) and the not-found warning (1667-1675).
//
// Spec #18 (feature #16, In Scope 7; decision #16.6) replaced the status logic: the get_user_config
// stub is gone, and the row's state comes from the add-in inventory of the running Revit year,
// matched by the one required-add-in matcher (RST.Core.Activation.RequiredAddinMatch):
//   Native     a built-in Revit tab;
//   Locked     every matching add-in is locked off by the administrator (AdminSettings.json);
//   Disabled   every matching add-in is switched off (the settings file, or a v3.0 rename);
//   Loaded     a matching add-in is on, or (with no installed match) its tab is on the ribbon;
//   Not Found  nothing matches.
// A switched-off add-in never shows "Loaded", even while its tab is still on this session's ribbon.

using System.Text.Json;
using RST.Core.Activation;
using RST.Core.Profiles;

namespace RST.Core.Loader;

/// <summary>A required add-in in the Loader's shape: <c>{ tabName, addinFile, url, native?, addinId? }</c>.</summary>
public sealed record NormalizedRequiredAddin(string TabName, string? AddinFile, string Url, bool Native = false, string? AddinId = null);

/// <summary>The status pill on a required add-in row.</summary>
public enum AddinRowStatus
{
    Native,
    Loaded,
    Disabled,
    NotFound,
    Locked,
}

/// <summary>
/// One "Required Add-ins for this profile" row: the tab name, the file column, the "info ↗" link
/// (null when hidden), the origin badge (null when hidden), and the status pill with its tooltip.
/// </summary>
public sealed record RequiredAddinRow(
    string TabName,
    string FileColumn,
    string? InfoUrl,
    string? Origin,
    AddinRowStatus Status,
    string StatusLabel,
    string StatusTooltip);

/// <summary>What the Loader knows about this Revit session's add-ins (its boot data).</summary>
public sealed class AddinSessionContext
{
    public AddinSessionContext(
        IReadOnlyList<string>? allTabs = null,
        IReadOnlyDictionary<string, AddinLookupEntry>? lookup = null,
        int? runningYear = null,
        IReadOnlyList<InventoryItem>? installed = null)
    {
        AllTabs = allTabs ?? [];
        Lookup = lookup ?? new Dictionary<string, AddinLookupEntry>();
        RunningYear = runningYear;
        Installed = installed ?? [];
    }

    /// <summary>The ribbon's tab titles, captured before the window opened (<c>ALL_TABS</c>).</summary>
    public IReadOnlyList<string> AllTabs { get; }

    /// <summary>addin_lookup.json (<c>ADDIN_LOOKUP</c>).</summary>
    public IReadOnlyDictionary<string, AddinLookupEntry> Lookup { get; }

    /// <summary>The running Revit year, when known.</summary>
    public int? RunningYear { get; }

    /// <summary>The running year's add-ins from the inventory.</summary>
    public IReadOnlyList<InventoryItem> Installed { get; }

    public AddinLookupEntry? LookupFor(string tabName) =>
        Lookup.TryGetValue(tabName, out var entry) ? entry : null;

    /// <summary>The context for <paramref name="revitVersion"/> ("2026") over <paramref name="inventory"/>.</summary>
    public static AddinSessionContext For(string? revitVersion, AddinInventory? inventory, IReadOnlyList<string>? allTabs, IReadOnlyDictionary<string, AddinLookupEntry>? lookup)
    {
        int? year = int.TryParse(revitVersion, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var y) ? y : null;
        var items = year is { } yr && inventory is not null ? inventory.For(yr).ToList() : [];
        return new AddinSessionContext(allTabs, lookup, year, items);
    }
}

public static class RequiredAddinRules
{
    /// <summary>
    /// The Loader's built-in tabs (<c>BUILTIN_TABS</c>): a required add-in on one of these is
    /// "Native". Not the same set as <see cref="Scanning.BuiltinTabs"/>: it also counts the
    /// Add-Ins host tab, the RST tab and Autodesk's FormIt / eTransmit companions.
    /// </summary>
    public static readonly IReadOnlySet<string> BuiltinTabs = new HashSet<string>(StringComparer.Ordinal)
    {
        "Architecture", "Structure", "Systems", "Steel", "Precast", "Concrete",
        "Insert", "Annotate", "Analyze", "Massing & Site", "Collaborate",
        "View", "Manage", "Modify", "Add-Ins", "Create", "RST",
        "FormIt", "FormIt Converter", "eTransmit",
    };

    public const string NativeFileColumn = "Revit Built-in";

    public static bool IsNativeTab(string? tabName) => tabName is not null && BuiltinTabs.Contains(tabName);

    /// <summary>The object format (what <see cref="Profile.RequiredAddins"/> holds).</summary>
    public static IReadOnlyList<NormalizedRequiredAddin> Normalize(IEnumerable<RequiredAddin>? requiredAddins) =>
        (requiredAddins ?? []).Where(r => r is not null).Select(r => new NormalizedRequiredAddin(
            string.IsNullOrEmpty(r.TabName) ? "" : r.TabName,
            string.IsNullOrEmpty(r.AddinFile) ? null : r.AddinFile,
            string.IsNullOrEmpty(r.Url) ? "" : r.Url,
            r.Native == true,
            string.IsNullOrEmpty(r.AddinId) ? null : r.AddinId)).ToList();

    /// <summary>
    /// <c>normalizeRequiredAddins</c> over a raw <c>requiredAddins</c> JSON array, which may hold
    /// the legacy format (bare tab-name strings, resolved through the lookup) or objects (whose
    /// <c>native</c> flag is kept). Anything that is not an array gives an empty list.
    /// </summary>
    public static IReadOnlyList<NormalizedRequiredAddin> Normalize(JsonElement requiredAddins, IReadOnlyDictionary<string, AddinLookupEntry>? lookup)
    {
        var result = new List<NormalizedRequiredAddin>();
        if (requiredAddins.ValueKind != JsonValueKind.Array) return result;
        foreach (var entry in requiredAddins.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String)
            {
                var tab = entry.GetString() ?? "";
                AddinLookupEntry? known = null;
                lookup?.TryGetValue(tab, out known);
                result.Add(new NormalizedRequiredAddin(tab, NullIfEmpty(known?.File), known?.Url ?? ""));
                continue;
            }
            if (entry.ValueKind != JsonValueKind.Object) continue;
            result.Add(new NormalizedRequiredAddin(
                StringProperty(entry, "tabName") ?? "",
                NullIfEmpty(StringProperty(entry, "addinFile")),
                StringProperty(entry, "url") ?? "",
                entry.TryGetProperty("native", out var native) && native.ValueKind == JsonValueKind.True,
                NullIfEmpty(StringProperty(entry, "addinId"))));
        }
        return result;
    }

    /// <summary>The running year's add-ins that satisfy <paramref name="addin"/> (the one matcher).</summary>
    public static IReadOnlyList<InventoryItem> Matches(NormalizedRequiredAddin addin, AddinSessionContext context) =>
        RequiredAddinMatch.Match(ToRequirement(addin), context.Installed);

    /// <summary>The requirement the planner sees for this row: file, id, tab and native flag, all passed through (review N2).</summary>
    public static RequiredAddin ToRequirement(NormalizedRequiredAddin addin) => new()
    {
        TabName = addin.TabName,
        AddinFile = addin.AddinFile,
        AddinId = addin.AddinId,
        Native = addin.Native ? true : null,
    };

    /// <summary>A live ribbon tab that contains the tab name or is contained by it (case-insensitive).</summary>
    public static bool IsOnRibbon(string tabName, AddinSessionContext context)
    {
        if (string.IsNullOrEmpty(tabName)) return false;
        foreach (var raw in context.AllTabs)
        {
            var t = raw ?? "";
            if (t.Length == 0) continue;
            if (t.Contains(tabName, StringComparison.OrdinalIgnoreCase) || tabName.Contains(t, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>The row the detail pane shows for one required add-in.</summary>
    public static RequiredAddinRow Describe(NormalizedRequiredAddin addin, AddinSessionContext context)
    {
        var tabName = addin.TabName;
        var native = IsNativeTab(tabName) || addin.Native;
        var known = context.LookupFor(tabName);
        var matches = native ? [] : Matches(addin, context);
        var year = context.RunningYear?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
        var url = FirstNonEmpty(addin.Url, known?.Url) ?? "";
        var matchedName = matches.Count > 0 ? matches[0].Name : "";
        var fileColumn = native ? NativeFileColumn
            : addin.AddinFile is { Length: > 0 } file ? file
            : matchedName.Length > 0 ? matchedName
            : known is not null ? known.File ?? "" : "";

        AddinRowStatus status;
        string label, tooltip;
        if (native)
        {
            (status, label, tooltip) = (AddinRowStatus.Native, "Native", "Built-in Revit feature — always available");
        }
        else if (matches.Count > 0 && matches.All(m => m.Lock == AdminLock.AlwaysDisabled))
        {
            (status, label) = (AddinRowStatus.Locked, "Locked");
            tooltip = matchedName + " is locked off by your administrator" + (year.Length > 0 ? " for Revit " + year : "") + ".";
        }
        else if (matches.Count > 0 && matches.All(m => m.IsOff))
        {
            (status, label) = (AddinRowStatus.Disabled, "Disabled");
            tooltip = matchedName + " is switched off" + (year.Length > 0 ? " for Revit " + year : "")
                      + ". It loads again once it is switched back on (Restore All Add-ins, or Autodesk's Add-Ins Manager) and Revit restarts.";
        }
        else if (matches.Count > 0)
        {
            var on = matches.First(m => !m.IsOff);
            (status, label) = (AddinRowStatus.Loaded, "Loaded");
            tooltip = "Installed and on" + (year.Length > 0 ? " for Revit " + year : "") + " (" + on.Name + ")"
                      + (on.Lock == AdminLock.AlwaysEnabled ? "; locked on by your administrator" : "");
        }
        else if (IsOnRibbon(tabName, context))
        {
            (status, label, tooltip) = (AddinRowStatus.Loaded, "Loaded", "Loaded in current Revit session");
        }
        else
        {
            (status, label) = (AddinRowStatus.NotFound, "Not Found");
            tooltip = url.Length > 0 ? "Click to download" : "Not found on this machine";
        }

        // The origin badge came only from the retired get_user_config stub, so it stays hidden.
        return new RequiredAddinRow(
            tabName,
            fileColumn,
            !native && url.Length > 0 ? url : null,
            null,
            status,
            label,
            tooltip);
    }

    /// <summary>Every row of <paramref name="profile"/>, in profile order.</summary>
    public static IReadOnlyList<RequiredAddinRow> Rows(Profile profile, AddinSessionContext context) =>
        Normalize(profile.RequiredAddins).Select(a => Describe(a, context)).ToList();

    /// <summary><c>getNotFoundAddins</c>: required add-ins that are not native and not found (installed or on the ribbon).</summary>
    public static IReadOnlyList<NormalizedRequiredAddin> NotFound(Profile profile, AddinSessionContext context) =>
        Normalize(profile.RequiredAddins)
            .Where(a => Describe(a, context).Status == AddinRowStatus.NotFound)
            .ToList();

    /// <summary>The warning a not-found add-in adds to the "Loading …" text.</summary>
    public static string NotFoundWarning(string tabName, string? revitVersion) =>
        tabName + " not found. If you experience issues with using tools from this add-in please install for Revit "
        + (string.IsNullOrEmpty(revitVersion) ? "your version" : revitVersion) + " and retry.";

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrEmpty(v));

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static string? StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
