// rst-c's own (not vendored): typed accessors for TrayStrings.resx, the RST tray's strings (spec #20 §1; F17 WU1),
// in the same shape as RST.UI's UiStrings (public so XAML binds them with {x:Static res:TrayStrings.Key}).

using System.Globalization;
using System.Resources;
using RST.Core.Updates;

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

    public static string Tab_Health => Get(nameof(Tab_Health));

    public static string Tab_Settings => Get(nameof(Tab_Settings));

    public static string Health_TabDevice => Get(nameof(Health_TabDevice));

    public static string Health_TabModels => Get(nameof(Health_TabModels));

    public static string Health_DeviceHeading => Get(nameof(Health_DeviceHeading));

    public static string Health_DeviceDescription => Get(nameof(Health_DeviceDescription));

    // Health → Models (spec #25 WU3).

    public static string Models_SearchPlaceholder => Get(nameof(Models_SearchPlaceholder));

    public static string Models_SearchingAllTime => Get(nameof(Models_SearchingAllTime));

    public static string Models_ClearSearch => Get(nameof(Models_ClearSearch));

    public static string Models_TimeFrame => Get(nameof(Models_TimeFrame));

    public static string Models_ChipRecent => Get(nameof(Models_ChipRecent));

    public static string Models_Chip3M => Get(nameof(Models_Chip3M));

    public static string Models_Chip6M => Get(nameof(Models_Chip6M));

    public static string Models_Chip1Y => Get(nameof(Models_Chip1Y));

    public static string Models_ChipRecentTip => Get(nameof(Models_ChipRecentTip));

    public static string Models_Chip3MTip => Get(nameof(Models_Chip3MTip));

    public static string Models_Chip6MTip => Get(nameof(Models_Chip6MTip));

    public static string Models_Chip1YTip => Get(nameof(Models_Chip1YTip));

    public static string Models_VersionLabel => Get(nameof(Models_VersionLabel));

    public static string Models_VersionAll => Get(nameof(Models_VersionAll));

    public static string Models_VersionSelected(string list) => Format(nameof(Models_VersionSelected), list);

    public static string Models_VersionTip => Get(nameof(Models_VersionTip));

    public static string Models_GroupByLabel => Get(nameof(Models_GroupByLabel));

    public static string Models_GroupByMajor => Get(nameof(Models_GroupByMajor));

    public static string Models_GroupByMinor => Get(nameof(Models_GroupByMinor));

    public static string Models_GroupByNone => Get(nameof(Models_GroupByNone));

    public static string Models_StatModels => Get(nameof(Models_StatModels));

    public static string Models_StatActive => Get(nameof(Models_StatActive));

    public static string Models_StatOpens => Get(nameof(Models_StatOpens));

    public static string Models_StatAvgOpen => Get(nameof(Models_StatAvgOpen));

    public static string Models_StatSyncs => Get(nameof(Models_StatSyncs));

    public static string Models_StatAvgSync => Get(nameof(Models_StatAvgSync));

    public static string Models_GroupVersion(string version) => Format(nameof(Models_GroupVersion), version);

    public static string Models_UnknownMinor(string major) => Format(nameof(Models_UnknownMinor), major);

    public static string Models_UnknownVersion => Get(nameof(Models_UnknownVersion));

    public static string Models_CountOne => Get(nameof(Models_CountOne));

    public static string Models_CountMany(int count) => Format(nameof(Models_CountMany), count);

    public static string Models_ActiveFormat(string duration) => Format(nameof(Models_ActiveFormat), duration);

    public static string Models_CardVersion(string version) => Format(nameof(Models_CardVersion), version);

    public static string Models_CardNoVersion => Get(nameof(Models_CardNoVersion));

    public static string Models_CardLastActive(string date) => Format(nameof(Models_CardLastActive), date);

    public static string Models_Workshared => Get(nameof(Models_Workshared));

    public static string Models_Cloud => Get(nameof(Models_Cloud));

    public static string Models_HealthGreen => Get(nameof(Models_HealthGreen));

    public static string Models_HealthYellow => Get(nameof(Models_HealthYellow));

    public static string Models_HealthAmber => Get(nameof(Models_HealthAmber));

    public static string Models_HealthRed => Get(nameof(Models_HealthRed));

    public static string Models_HealthGrey => Get(nameof(Models_HealthGrey));

    public static string Models_HealthTip => Get(nameof(Models_HealthTip));

    public static string Models_CardHelp => Get(nameof(Models_CardHelp));

    public static string Models_NoPreview => Get(nameof(Models_NoPreview));

    public static string Models_Loading => Get(nameof(Models_Loading));

    public static string Models_EmptyNoModels => Get(nameof(Models_EmptyNoModels));

    public static string Models_EmptyTimeFrame => Get(nameof(Models_EmptyTimeFrame));

    public static string Models_SwitchToOneYear => Get(nameof(Models_SwitchToOneYear));

    public static string Models_EmptyNoMatches => Get(nameof(Models_EmptyNoMatches));

    public static string Detail_Back => Get(nameof(Detail_Back));

    public static string Detail_BackHelp => Get(nameof(Detail_BackHelp));

    public static string Detail_LocalPath => Get(nameof(Detail_LocalPath));

    public static string Detail_CentralPath => Get(nameof(Detail_CentralPath));

    public static string Detail_CloudPath => Get(nameof(Detail_CloudPath));

    public static string Detail_Versions(string versions) => Format(nameof(Detail_Versions), versions);

    public static string Detail_AsOf(string date) => Format(nameof(Detail_AsOf), date);

    public static string Detail_Loading => Get(nameof(Detail_Loading));

    public static string Detail_Gone => Get(nameof(Detail_Gone));

    public static string Detail_Ranges => Get(nameof(Detail_Ranges));


    public static string Updates_Heading => Get(nameof(Updates_Heading));

    public static string Updates_Description => Get(nameof(Updates_Description));

    public static string Apps_Heading => Get(nameof(Apps_Heading));

    public static string Apps_Description => Get(nameof(Apps_Description));



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

    /// <summary>The title of the tray's one-time notices (now only the "installed twice" one).</summary>
    public static string Notice_Title => Get(nameof(Notice_Title));

    // The one-time "your own copy is used" notice (F17 WU5, spec #20 §7).

    public static string Shadow_Heading => Get(nameof(Shadow_Heading));

    public static string Shadow_Message => Get(nameof(Shadow_Message));

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

    public static string Updates_CheckFailed(string reason) => Format(nameof(Updates_CheckFailed), reason);

    public static string Updates_LastSuccessToday(string time) => Format(nameof(Updates_LastSuccessToday), time);

    public static string Updates_LastSuccessYesterday(string time) => Format(nameof(Updates_LastSuccessYesterday), time);

    public static string Updates_LastSuccessOn(string date, string time) => Format(nameof(Updates_LastSuccessOn), date, time);

    public static string Updates_NoSuccessYet => Get(nameof(Updates_NoSuccessYet));

    public static string Apps_DetectionFailed => Get(nameof(Apps_DetectionFailed));

    public static string App_LatestFailedKnown(string version) => Format(nameof(App_LatestFailedKnown), version);

    public static string App_Unknown => Get(nameof(App_Unknown));

    public static string State_Unknown => Get(nameof(State_Unknown));

    public static string Updates_RstUpdateNote => Get(nameof(Updates_RstUpdateNote));

    public static string Updates_OtherApps => Get(nameof(Updates_OtherApps));

    public static string Updates_OtherAppsHint => Get(nameof(Updates_OtherAppsHint));

    public static string Apps_InstallsNote => Get(nameof(Apps_InstallsNote));

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

    public static string App_InstallWhenRevitCloses => Get(nameof(App_InstallWhenRevitCloses));

    public static string App_UpdateWhenRevitCloses => Get(nameof(App_UpdateWhenRevitCloses));

    public static string App_ReinstallPrevious(string version) => Format(nameof(App_ReinstallPrevious), version);

    public static string App_Cancel => Get(nameof(App_Cancel));

    public static string App_OpenLog => Get(nameof(App_OpenLog));

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

    // Installs (F17 WU4): {0} the app, {1} the version.

    public static string Install_Downloading(string app, string version) => Format(nameof(Install_Downloading), app, version);

    public static string Install_WaitingForRevit(string app, string version) => Format(nameof(Install_WaitingForRevit), app, version);

    public static string Install_Installing(string app, string version) => Format(nameof(Install_Installing), app, version);

    public static string Install_RetryLater(string app, string version) => Format(nameof(Install_RetryLater), app, version);

    public static string Install_Queued(string app, string version) => Format(nameof(Install_Queued), app, version);

    public static string Install_QueuedRst(string app, string version) => Format(nameof(Install_QueuedRst), app, version);

    public static string App_ReinstallPreviousWhenRevitCloses(string version) => Format(nameof(App_ReinstallPreviousWhenRevitCloses), version);

    public static string Install_HandingOff(string app, string version) => Format(nameof(Install_HandingOff), app, version);

    public static string Install_Done(string app, string version) => Format(nameof(Install_Done), app, version);

    public static string Install_DoneRestart(string app, string version) => Format(nameof(Install_DoneRestart), app, version);

    public static string Install_Refused(string app, string version, string reason) => Format(nameof(Install_Refused), app, version, reason);

    public static string Install_Failed(string app, string version, int code) => Format(nameof(Install_Failed), app, version, code);

    public static string Install_FailedGone(string app, string version, int code) => Format(nameof(Install_FailedGone), app, version, code);

    public static string Install_Busy(string app, string version) => Format(nameof(Install_Busy), app, version);

    public static string Install_UpdaterFailed(string app, string version) => Format(nameof(Install_UpdaterFailed), app, version);

    public static string Install_PerMachineRst(string app, string version) => Format(nameof(Install_PerMachineRst), app, version);

    public static string Install_PerMachine(string app, string version) => Format(nameof(Install_PerMachine), app, version);

    /// <summary>
    /// A refusal or failure in plain words (WU3 review D3): never an exception's text. The detail is in the tray log.
    /// <paramref name="net"/> and <paramref name="status"/> refine <see cref="InstallProblem.Network"/>.
    /// </summary>
    public static string Problem(InstallProblem problem, NetFailure? net = null, int? status = null) => problem switch
    {
        InstallProblem.Network => Net(net ?? NetFailure.Unexpected, status),
        InstallProblem.None or InstallProblem.PerMachine or InstallProblem.InstallFailed or InstallProblem.InstallerBusy
            or InstallProblem.UpdaterFailed => Get("Net_Unexpected"),
        _ => Get("Problem_" + problem),
    };

    /// <summary>A network failure in plain words: "couldn't reach GitHub", "GitHub answered HTTP 503", ….</summary>
    public static string Net(NetFailure failure, int? status = null) => failure switch
    {
        NetFailure.HttpStatus when status is { } code => Format("Net_HttpStatus", code),
        NetFailure.HttpStatus => Get("Net_Unexpected"),
        _ => Get("Net_" + failure),
    };
}
