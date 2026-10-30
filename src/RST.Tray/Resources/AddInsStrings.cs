// rst-c's own (not vendored): typed accessors for AddInsStrings.resx, the RST tray's Add-ins page (spec #20 §3;
// F17 WU2), in TrayStrings' shape (public so XAML binds them with {x:Static res:AddInsStrings.Key}).

using System.Globalization;
using System.Resources;

namespace RST.Tray.Resources;

/// <summary>The Add-ins page's own strings. The plans and reports use the Loader's wording, not these.</summary>
public static class AddInsStrings
{
    internal static readonly ResourceManager ResourceManager =
        new("RST.Tray.Resources.AddInsStrings", typeof(AddInsStrings).Assembly);

    /// <summary>The string <paramref name="key"/>; the key itself when it is missing.</summary>
    public static string Get(string key) =>
        ResourceManager.GetString(key, CultureInfo.InvariantCulture) ?? key;

    private static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.InvariantCulture, Get(key), args);

    public static string Heading => Get(nameof(Heading));

    public static string Description => Get(nameof(Description));

    public static string ActiveProfileLabel => Get(nameof(ActiveProfileLabel));

    public static string ActiveProfileNone => Get(nameof(ActiveProfileNone));

    public static string ApplyHeading => Get(nameof(ApplyHeading));

    public static string ProfileLabel => Get(nameof(ProfileLabel));

    public static string ApplyButton => Get(nameof(ApplyButton));

    public static string ApplyHint => Get(nameof(ApplyHint));

    public static string NoProfiles => Get(nameof(NoProfiles));

    public static string RestoreHeading => Get(nameof(RestoreHeading));

    public static string YearsHeading => Get(nameof(YearsHeading));

    public static string YearRunning => Get(nameof(YearRunning));

    public static string YearSwitchedOff => Get(nameof(YearSwitchedOff));

    public static string YearCouldNotChange => Get(nameof(YearCouldNotChange));

    public static string YearClear => Get(nameof(YearClear));

    public static string NoRevit => Get(nameof(NoRevit));

    public static string Loading => Get(nameof(Loading));

    public static string BusyPlanning => Get(nameof(BusyPlanning));

    public static string BusyApplying(object? profile) => Format(nameof(BusyApplying), profile);

    public static string BusyRestoring => Get(nameof(BusyRestoring));

    public static string Applied(object? profile) => Format(nameof(Applied), profile);

    public static string Restored => Get(nameof(Restored));

    public static string ReadFailed(object? reason) => Format(nameof(ReadFailed), reason);

    public static string ApplyFailed(object? reason) => Format(nameof(ApplyFailed), reason);

    public static string Refresh => Get(nameof(Refresh));
}
