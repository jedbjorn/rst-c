// rst-c's own (not vendored): a C# port of profile_loader.html's required-add-in rules
// (spec #16, WU3): normalizeRequiredAddins (955-968), BUILTIN_TABS / isNativeTab /
// isAddinLoaded / getLoadedAddinName (986-1059), the four-state status logic and the add-in
// row (1297-1365), getNotFoundAddins (1560-1568) and the not-found warning (1667-1675).
// Behavior is unchanged; the matching is case-insensitive substring matching, the same rule as
// RequiredAddinQa's tier 3 (live ribbon tab, then manifest display name / file stem).

using System.Text.Json;
using RST.Core.Profiles;

namespace RST.Core.Loader;

/// <summary>A required add-in in the Loader's shape: <c>{ tabName, addinFile, url, native? }</c>.</summary>
public sealed record NormalizedRequiredAddin(string TabName, string? AddinFile, string Url, bool Native = false);

/// <summary>The status pill on a required add-in row.</summary>
public enum AddinRowStatus
{
    Native,
    Loaded,
    Installed,
    Disabled,
    NotFound,
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
        IReadOnlyList<LoadedAddin>? loadedAddins = null,
        IReadOnlyDictionary<string, AddinLookupEntry>? lookup = null,
        UserAddinConfig? userConfig = null)
    {
        AllTabs = allTabs ?? [];
        LoadedAddins = loadedAddins ?? [];
        Lookup = lookup ?? new Dictionary<string, AddinLookupEntry>();
        UserConfig = userConfig ?? UserAddinConfig.Empty;
    }

    /// <summary>The ribbon's tab titles, captured before the window opened (<c>ALL_TABS</c>).</summary>
    public IReadOnlyList<string> AllTabs { get; }

    /// <summary>The scanned manifests (<c>LOADED_ADDINS</c>).</summary>
    public IReadOnlyList<LoadedAddin> LoadedAddins { get; }

    /// <summary>addin_lookup.json (<c>ADDIN_LOOKUP</c>).</summary>
    public IReadOnlyDictionary<string, AddinLookupEntry> Lookup { get; }

    /// <summary>The get_user_config stub (<c>USER_CONFIG</c>).</summary>
    public UserAddinConfig UserConfig { get; }

    public AddinLookupEntry? LookupFor(string tabName) =>
        Lookup.TryGetValue(tabName, out var entry) ? entry : null;
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
        "Architecture", "Structure", "Systems", "Steel", "Precast",
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
            string.IsNullOrEmpty(r.Url) ? "" : r.Url)).ToList();

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
                entry.TryGetProperty("native", out var native) && native.ValueKind == JsonValueKind.True));
        }
        return result;
    }

    /// <summary>
    /// <c>isAddinLoaded</c>: native tabs count as loaded; then a live ribbon tab that contains the
    /// tab name or is contained by it; then a scanned manifest whose display name or file stem
    /// does the same, or contains the lookup file's stem. Case-insensitive.
    /// </summary>
    public static bool IsAddinLoaded(string tabName, AddinSessionContext context)
    {
        if (IsNativeTab(tabName)) return true;
        foreach (var raw in context.AllTabs)
        {
            var t = raw ?? "";
            if (t.Length == 0) continue;
            if (Contains(t, tabName) || Contains(tabName, t)) return true;
        }
        return MatchManifest(tabName, context) is not null;
    }

    /// <summary><c>getLoadedAddinName</c>: the first matching manifest's label, or "".</summary>
    public static string GetLoadedAddinName(string tabName, AddinSessionContext context)
    {
        var match = MatchManifest(tabName, context);
        if (match is null) return "";
        return FirstNonEmpty(match.DisplayName, match.FileName) ?? "";
    }

    /// <summary>The row the detail pane shows for one required add-in (the four-state logic).</summary>
    public static RequiredAddinRow Describe(NormalizedRequiredAddin addin, AddinSessionContext context)
    {
        var tabName = addin.TabName;
        var native = IsNativeTab(tabName) || addin.Native;
        var known = context.LookupFor(tabName);
        var loaded = !native && IsAddinLoaded(tabName, context);
        var matchedName = !native && loaded ? GetLoadedAddinName(tabName, context) : "";
        var fileColumn = native ? NativeFileColumn
            : addin.AddinFile is { Length: > 0 } file ? file
            : matchedName.Length > 0 ? matchedName
            : known is not null ? known.File ?? "" : "";

        // Four states: the user config decides Installed / Disabled (always empty: the stub).
        var config = context.UserConfig.For(tabName);
        var isDisabled = config is { Enabled: false };
        var isInstalled = config is { Enabled: true };
        var url = FirstNonEmpty(addin.Url, known?.Url) ?? "";

        AddinRowStatus status;
        string label, tooltip;
        if (native)
        {
            (status, label, tooltip) = (AddinRowStatus.Native, "Native", "Built-in Revit feature — always available");
        }
        else if (loaded)
        {
            (status, label) = (AddinRowStatus.Loaded, "Loaded");
            tooltip = "Loaded in current Revit session" + (matchedName.Length > 0 ? " (" + matchedName + ")" : "");
        }
        else if (isInstalled)
        {
            (status, label) = (AddinRowStatus.Installed, "Installed");
            tooltip = "Installed (" + (FirstNonEmpty(config!.AddinFile, tabName) ?? "") + ")";
        }
        else if (isDisabled)
        {
            (status, label, tooltip) = (AddinRowStatus.Disabled, "Disabled",
                "Add-in is disabled (.addin.RSTdisabled) — enable via Restore All Add-ins");
        }
        else
        {
            (status, label) = (AddinRowStatus.NotFound, "Not Found");
            tooltip = url.Length > 0 ? "Click to download" : "Not found on this machine";
        }

        var origin = config is not null ? config.Origin ?? "" : native ? "native" : "";
        var nativeOrigin = origin == "native";
        return new RequiredAddinRow(
            tabName,
            fileColumn,
            !nativeOrigin && url.Length > 0 ? url : null,
            origin.Length > 0 && !nativeOrigin ? origin : null,
            status,
            label,
            tooltip);
    }

    /// <summary>Every row of <paramref name="profile"/>, in profile order.</summary>
    public static IReadOnlyList<RequiredAddinRow> Rows(Profile profile, AddinSessionContext context) =>
        Normalize(profile.RequiredAddins).Select(a => Describe(a, context)).ToList();

    /// <summary>
    /// <c>getNotFoundAddins</c>: required add-ins that are not native, not loaded and have no
    /// user-config entry.
    /// </summary>
    public static IReadOnlyList<NormalizedRequiredAddin> NotFound(Profile profile, AddinSessionContext context) =>
        Normalize(profile.RequiredAddins)
            .Where(a => !(a.Native || IsNativeTab(a.TabName) || IsAddinLoaded(a.TabName, context)))
            .Where(a => context.UserConfig.For(a.TabName) is null)
            .ToList();

    /// <summary>The warning a not-found add-in adds to the "Loading …" text.</summary>
    public static string NotFoundWarning(string tabName, string? revitVersion) =>
        tabName + " not found. If you experience issues with using tools from this add-in please install for Revit "
        + (string.IsNullOrEmpty(revitVersion) ? "your version" : revitVersion) + " and retry.";

    private static LoadedAddin? MatchManifest(string tabName, AddinSessionContext context)
    {
        if (context.LoadedAddins.Count == 0) return null;
        var stem = context.LookupFor(tabName)?.File is { Length: > 0 } file ? StripSuffix(file, ".addin") : "";
        foreach (var addin in context.LoadedAddins)
        {
            foreach (var field in NameFields(addin))
            {
                if (field.Length == 0) continue;
                if (Contains(field, tabName) || Contains(tabName, field)) return addin;
                if (stem.Length > 0 && Contains(field, stem)) return addin;
            }
        }
        return null;
    }

    // _addinNameFields: the display name, then the file name without .addin / .addin.RSTdisabled.
    private static IEnumerable<string> NameFields(LoadedAddin addin)
    {
        if (!string.IsNullOrEmpty(addin.DisplayName)) yield return addin.DisplayName;
        if (!string.IsNullOrEmpty(addin.FileName))
            yield return addin.FileName.EndsWith(".addin.RSTdisabled", StringComparison.OrdinalIgnoreCase)
                ? addin.FileName[..^".addin.RSTdisabled".Length]
                : StripSuffix(addin.FileName, ".addin");
    }

    private static string StripSuffix(string s, string suffix) =>
        s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? s[..^suffix.Length] : s;

    // JS: haystack.toLowerCase().indexOf(needle.toLowerCase()) !== -1 (an empty needle matches).
    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrEmpty(v));

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static string? StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
