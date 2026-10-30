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

    // Updates and Apps pages, notifications (F17 WU3).

    public static string Updates_CheckNow => Get(nameof(Updates_CheckNow));

    public static string Updates_Checking => Get(nameof(Updates_Checking));

    public static string Updates_LastCheckedToday(string time) => Format(nameof(Updates_LastCheckedToday), time);

    public static string Updates_LastCheckedYesterday(string time) => Format(nameof(Updates_LastCheckedYesterday), time);

    public static string Updates_LastCheckedOn(string date, string time) => Format(nameof(Updates_LastCheckedOn), date, time);

    public static string Updates_NeverChecked => Get(nameof(Updates_NeverChecked));

    public static string Updates_RstAvailable(string version) => Format(nameof(Updates_RstAvailable), version);

    public static string Updates_RstUpToDate => Get(nameof(Updates_RstUpToDate));

    public static string Updates_RstInstalled(string version) => Format(nameof(Updates_RstInstalled), version);

    public static string Updates_RstUnknown => Get(nameof(Updates_RstUnknown));

    public static string Updates_ChecksOff => Get(nameof(Updates_ChecksOff));

    public static string Updates_ChecksOffLocked => Get(nameof(Updates_ChecksOffLocked));

    public static string Updates_CheckFailed => Get(nameof(Updates_CheckFailed));

    public static string Updates_InstallComingSoon => Get(nameof(Updates_InstallComingSoon));

    public static string Updates_OtherApps => Get(nameof(Updates_OtherApps));

    public static string Updates_OtherAppsHint => Get(nameof(Updates_OtherAppsHint));

    public static string Apps_InstallsComingSoon => Get(nameof(Apps_InstallsComingSoon));

    public static string Apps_Detecting => Get(nameof(Apps_Detecting));

    public static string App_InstalledLabel => Get(nameof(App_InstalledLabel));

    public static string App_LatestLabel => Get(nameof(App_LatestLabel));

    public static string App_InstalledForYou(string version) => Format(nameof(App_InstalledForYou), version);

    public static string App_InstalledForYouManaged(string version) => Format(nameof(App_InstalledForYouManaged), version);

    public static string App_InstalledForEveryone(string version) => Format(nameof(App_InstalledForEveryone), version);

    public static string App_VersionUnknown => Get(nameof(App_VersionUnknown));

    public static string App_NotInstalled => Get(nameof(App_NotInstalled));

    public static string App_InstalledManually => Get(nameof(App_InstalledManually));

    public static string App_ManualNote => Get(nameof(App_ManualNote));

    public static string App_LatestNotChecked => Get(nameof(App_LatestNotChecked));

    public static string App_LatestFailed => Get(nameof(App_LatestFailed));

    public static string App_LatestNone => Get(nameof(App_LatestNone));

    public static string App_Install => Get(nameof(App_Install));

    public static string App_Update => Get(nameof(App_Update));

    public static string App_ComingSoon => Get(nameof(App_ComingSoon));

    public static string App_ReleasePage => Get(nameof(App_ReleasePage));

    public static string State_UpToDate => Get(nameof(State_UpToDate));

    public static string State_UpdateAvailable => Get(nameof(State_UpdateAvailable));

    public static string State_NotInstalled => Get(nameof(State_NotInstalled));

    public static string State_InstalledManually => Get(nameof(State_InstalledManually));

    public static string State_Installed => Get(nameof(State_Installed));

    public static string Notify_OneTitle(string product, string version) => Format(nameof(Notify_OneTitle), product, version);

    public static string Notify_OneText => Get(nameof(Notify_OneText));

    public static string Notify_ManyTitle => Get(nameof(Notify_ManyTitle));

    public static string Notify_ManyText(string list) => Format(nameof(Notify_ManyText), list);
}
