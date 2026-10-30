// rst-c's own (not vendored): typed accessors for TrayStrings.resx, the RST tray's strings (spec #20 §1; F17 WU1),
// in the same shape as RST.UI's UiStrings (public so XAML binds them with {x:Static res:TrayStrings.Key}).

using System.Globalization;
using System.Resources;

namespace RST.Tray.Resources;

/// <summary>The tray's strings. English only: lookups use the neutral resources and culture-invariant formatting.</summary>
public static class TrayStrings
{
    internal static readonly ResourceManager ResourceManager =
        new("RST.Tray.Resources.TrayStrings", typeof(TrayStrings).Assembly);

    /// <summary>The string <paramref name="key"/>; the key itself when it is missing.</summary>
    public static string Get(string key) =>
        ResourceManager.GetString(key, CultureInfo.InvariantCulture) ?? key;

    private static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.InvariantCulture, Get(key), args);

    public static string Tray_Tooltip => Get(nameof(Tray_Tooltip));

    public static string Menu_Open => Get(nameof(Menu_Open));

    public static string Menu_CheckForUpdates => Get(nameof(Menu_CheckForUpdates));

    public static string Menu_Settings => Get(nameof(Menu_Settings));

    public static string Menu_Quit => Get(nameof(Menu_Quit));

    public static string Window_Title => Get(nameof(Window_Title));

    public static string Window_Heading => Get(nameof(Window_Heading));

    public static string Tab_Updates => Get(nameof(Tab_Updates));

    public static string Tab_Apps => Get(nameof(Tab_Apps));

    public static string Tab_AddIns => Get(nameof(Tab_AddIns));

    public static string Tab_Settings => Get(nameof(Tab_Settings));

    public static string Page_ComingLater => Get(nameof(Page_ComingLater));

    public static string Updates_Heading => Get(nameof(Updates_Heading));

    public static string Updates_Description => Get(nameof(Updates_Description));

    public static string Apps_Heading => Get(nameof(Apps_Heading));

    public static string Apps_Description => Get(nameof(Apps_Description));

    public static string AddIns_Heading => Get(nameof(AddIns_Heading));

    public static string AddIns_Description => Get(nameof(AddIns_Description));

    public static string Settings_Appearance => Get(nameof(Settings_Appearance));

    public static string Settings_Theme => Get(nameof(Settings_Theme));

    public static string Settings_ThemeSystem => Get(nameof(Settings_ThemeSystem));

    public static string Settings_ThemeLight => Get(nameof(Settings_ThemeLight));

    public static string Settings_ThemeDark => Get(nameof(Settings_ThemeDark));

    public static string Settings_ThemeHint => Get(nameof(Settings_ThemeHint));

    public static string Settings_UpdatesHeading => Get(nameof(Settings_UpdatesHeading));

    public static string Settings_CheckForUpdates => Get(nameof(Settings_CheckForUpdates));

    public static string Settings_CheckForUpdatesHint => Get(nameof(Settings_CheckForUpdatesHint));

    public static string Settings_CheckForUpdatesLocked => Get(nameof(Settings_CheckForUpdatesLocked));

    public static string Settings_SaveFailed => Get(nameof(Settings_SaveFailed));

    public static string Settings_About => Get(nameof(Settings_About));

    public static string Settings_VersionFormat(string value) => Format(nameof(Settings_VersionFormat), value);

    public static string Settings_StartupHint => Get(nameof(Settings_StartupHint));

    public static string Settings_Logs => Get(nameof(Settings_Logs));

    public static string Settings_OpenLogFolder => Get(nameof(Settings_OpenLogFolder));

    public static string Footer_RunsInTray => Get(nameof(Footer_RunsInTray));

    public static string Footer_Close => Get(nameof(Footer_Close));

    public static string Notice_Title => Get(nameof(Notice_Title));

    public static string Notice_Heading => Get(nameof(Notice_Heading));

    public static string Notice_Message => Get(nameof(Notice_Message));
}
