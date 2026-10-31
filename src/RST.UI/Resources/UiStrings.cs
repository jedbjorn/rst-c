// Adapted from splash-it src/SplashIt.UI/Resources/UiStrings.cs (splash-it commit ba86a71), MIT, same owner.
// Changes: namespaces (SplashIt.* to RST.*); only the kit's keys (Common_, Dialog_, Edit_, Color_, Pick_); splash-it's Settings_, Status_ and Preview_ keys are left out; rst-c's Health window keys (Health_) are added in the block at the end.

using System.Globalization;
using System.Resources;

namespace RST.UI.Resources;

/// <summary>
/// Typed accessors for UiStrings.resx, public so XAML can bind them with
/// <c>{x:Static res:UiStrings.Key}</c>. English only (spec #16, Out of Scope: localization): lookups use the
/// neutral resources and formatting is culture-invariant. This replaces gantt-it's
/// localization plumbing (TrExtension, RevitLanguage), which RST leaves out.
/// </summary>
public static class UiStrings
{
    internal static readonly ResourceManager ResourceManager =
        new("RST.UI.Resources.UiStrings", typeof(UiStrings).Assembly);

    /// <summary>The string <paramref name="key"/>; the key itself when it is missing.</summary>
    public static string Get(string key) =>
        ResourceManager.GetString(key, CultureInfo.InvariantCulture) ?? key;

    private static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.InvariantCulture, Get(key), args);

    public static string Common_OK => Get(nameof(Common_OK));

    public static string Common_Cancel => Get(nameof(Common_Cancel));

    public static string Dialog_Items => Get(nameof(Dialog_Items));

    public static string Edit_Cut => Get(nameof(Edit_Cut));

    public static string Edit_Copy => Get(nameof(Edit_Copy));

    public static string Edit_Paste => Get(nameof(Edit_Paste));

    public static string Color_DialogTitle => Get(nameof(Color_DialogTitle));

    public static string Color_Palette => Get(nameof(Color_Palette));

    public static string Color_Recent => Get(nameof(Color_Recent));

    public static string Color_Hex => Get(nameof(Color_Hex));

    public static string Color_HexName => Get(nameof(Color_HexName));

    public static string Color_CurrentTip => Get(nameof(Color_CurrentTip));

    public static string Color_Apply => Get(nameof(Color_Apply));

    public static string Color_PickTip => Get(nameof(Color_PickTip));

    public static string Color_PickName => Get(nameof(Color_PickName));

    public static string Color_More => Get(nameof(Color_More));

    public static string Color_MoreName => Get(nameof(Color_MoreName));

    public static string Color_HexError => Get(nameof(Color_HexError));

    public static string Color_PickFailed(object? arg0) => Format(nameof(Color_PickFailed), arg0);

    public static string Pick_WindowTitle => Get(nameof(Pick_WindowTitle));

    public static string Pick_Hint => Get(nameof(Pick_Hint));

    public static string Pick_ListFailed => Get(nameof(Pick_ListFailed));

    public static string Pick_AreaTooLarge(object? arg0, object? arg1) => Format(nameof(Pick_AreaTooLarge), arg0, arg1);

    public static string Pick_NoAccess => Get(nameof(Pick_NoAccess));

    public static string Pick_CopyRefused => Get(nameof(Pick_CopyRefused));

    public static string Pick_PointerFailed => Get(nameof(Pick_PointerFailed));

    public static string Pick_NoMonitors => Get(nameof(Pick_NoMonitors));

    // Health window (spec #16, WU2).

    public static string Health_WindowTitle => Get(nameof(Health_WindowTitle));

    public static string Health_Heading => Get(nameof(Health_Heading));

    public static string Health_TabHealth => Get(nameof(Health_TabHealth));

    public static string Health_TabActivity => Get(nameof(Health_TabActivity));

    public static string Health_Device => Get(nameof(Health_Device));

    public static string Health_Captured => Get(nameof(Health_Captured));

    public static string Health_SectionHardware => Get(nameof(Health_SectionHardware));

    public static string Health_SectionDisplayNetwork => Get(nameof(Health_SectionDisplayNetwork));

    public static string Health_SectionRevit => Get(nameof(Health_SectionRevit));

    public static string Health_SectionOs => Get(nameof(Health_SectionOs));

    public static string Health_RowUser => Get(nameof(Health_RowUser));

    public static string Health_RowCpu => Get(nameof(Health_RowCpu));

    public static string Health_RowRam => Get(nameof(Health_RowRam));

    public static string Health_RowGpu => Get(nameof(Health_RowGpu));

    public static string Health_RowGpuN(object? arg0) => Format(nameof(Health_RowGpuN), arg0);

    public static string Health_RowDisk => Get(nameof(Health_RowDisk));

    public static string Health_RowJunkTotal => Get(nameof(Health_RowJunkTotal));

    public static string Health_RowResolution => Get(nameof(Health_RowResolution));

    public static string Health_RowMonitors => Get(nameof(Health_RowMonitors));

    public static string Health_RowAdapter => Get(nameof(Health_RowAdapter));

    public static string Health_RowSpeed => Get(nameof(Health_RowSpeed));

    public static string Health_RowVersion => Get(nameof(Health_RowVersion));

    public static string Health_RowUsername => Get(nameof(Health_RowUsername));

    public static string Health_RowHardwareAccel => Get(nameof(Health_RowHardwareAccel));

    public static string Health_RowModel => Get(nameof(Health_RowModel));

    public static string Health_RowWarnings => Get(nameof(Health_RowWarnings));

    public static string Health_RowOsName => Get(nameof(Health_RowOsName));

    public static string Health_RowOsBuild => Get(nameof(Health_RowOsBuild));

    public static string Health_Unnamed => Get(nameof(Health_Unnamed));

    public static string Health_CpuCores(object? arg0, object? arg1) => Format(nameof(Health_CpuCores), arg0, arg1);

    public static string Health_CpuUsed(object? arg0) => Format(nameof(Health_CpuUsed), arg0);

    public static string Health_RamUsed(object? arg0, object? arg1) => Format(nameof(Health_RamUsed), arg0, arg1);

    public static string Health_GpuDetails(object? arg0, object? arg1) => Format(nameof(Health_GpuDetails), arg0, arg1);

    public static string Health_DiskDetails(object? arg0, object? arg1, object? arg2, object? arg3) => Format(nameof(Health_DiskDetails), arg0, arg1, arg2, arg3);

    public static string Health_JunkEntries(object? arg0) => Format(nameof(Health_JunkEntries), arg0);

    public static string Health_JunkFiles(object? arg0) => Format(nameof(Health_JunkFiles), arg0);

    public static string Health_JunkPaths(object? arg0) => Format(nameof(Health_JunkPaths), arg0);

    public static string Health_JunkTotalDetails(object? arg0) => Format(nameof(Health_JunkTotalDetails), arg0);

    public static string Health_RevitBuild(object? arg0) => Format(nameof(Health_RevitBuild), arg0);

    public static string Health_ModelSize(object? arg0) => Format(nameof(Health_ModelSize), arg0);

    public static string Health_Enabled => Get(nameof(Health_Enabled));

    public static string Health_Disabled => Get(nameof(Health_Disabled));

    public static string Health_Unknown => Get(nameof(Health_Unknown));

    public static string Health_ScanSystem => Get(nameof(Health_ScanSystem));

    public static string Health_Scanning => Get(nameof(Health_Scanning));

    public static string Health_CleanJunkFiles => Get(nameof(Health_CleanJunkFiles));

    public static string Health_Close => Get(nameof(Health_Close));

    public static string Health_Collection => Get(nameof(Health_Collection));

    public static string Health_CollectionOn => Get(nameof(Health_CollectionOn));

    public static string Health_CollectionOff => Get(nameof(Health_CollectionOff));

    public static string Health_StatusFiles(object? arg0) => Format(nameof(Health_StatusFiles), arg0);

    public static string Health_StatusSince(object? arg0) => Format(nameof(Health_StatusSince), arg0);

    public static string Health_JunkTitle => Get(nameof(Health_JunkTitle));

    public static string Health_JunkSubtitle => Get(nameof(Health_JunkSubtitle));

    public static string Health_JunkEmptyTitle => Get(nameof(Health_JunkEmptyTitle));

    public static string Health_JunkEmptyHint => Get(nameof(Health_JunkEmptyHint));

    public static string Health_JunkCancel => Get(nameof(Health_JunkCancel));

    public static string Health_JunkRun => Get(nameof(Health_JunkRun));

    public static string Health_NothingSelectedTitle => Get(nameof(Health_NothingSelectedTitle));

    public static string Health_NothingSelected => Get(nameof(Health_NothingSelected));

    public static string Health_ConfirmCleanTitle => Get(nameof(Health_ConfirmCleanTitle));

    public static string Health_ConfirmClean => Get(nameof(Health_ConfirmClean));

    public static string Health_ConfirmContinue => Get(nameof(Health_ConfirmContinue));

    public static string Health_CleaningTitle => Get(nameof(Health_CleaningTitle));

    public static string Health_Cleaning => Get(nameof(Health_Cleaning));

    public static string Health_CleanupCompleteTitle => Get(nameof(Health_CleanupCompleteTitle));

    public static string Health_CleanupLine(object? arg0, object? arg1) => Format(nameof(Health_CleanupLine), arg0, arg1);

    public static string Health_CleanupLineLocked(object? arg0, object? arg1, object? arg2) => Format(nameof(Health_CleanupLineLocked), arg0, arg1, arg2);

    public static string Health_CleanupFailedTitle => Get(nameof(Health_CleanupFailedTitle));

    public static string Health_ScanFailedTitle => Get(nameof(Health_ScanFailedTitle));

    public static string Health_UnknownError => Get(nameof(Health_UnknownError));

    public static string Health_CollectionFailedTitle => Get(nameof(Health_CollectionFailedTitle));

    public static string Health_StatSessionTimer => Get(nameof(Health_StatSessionTimer));

    public static string Health_StatSessionTotal => Get(nameof(Health_StatSessionTotal));

    public static string Health_StatLongestSync => Get(nameof(Health_StatLongestSync));

    public static string Health_StatLongestOpen => Get(nameof(Health_StatLongestOpen));

    public static string Health_StatWarnings => Get(nameof(Health_StatWarnings));

    public static string Health_StatFileSize => Get(nameof(Health_StatFileSize));

    public static string Health_FileOpen(object? arg0) => Format(nameof(Health_FileOpen), arg0);

    public static string Health_ChartSession => Get(nameof(Health_ChartSession));

    public static string Health_ChartOpen => Get(nameof(Health_ChartOpen));

    public static string Health_ChartSync => Get(nameof(Health_ChartSync));

    public static string Health_ChartPoint(object? arg0, object? arg1) => Format(nameof(Health_ChartPoint), arg0, arg1);

    public static string Health_NoModelTitle => Get(nameof(Health_NoModelTitle));

    public static string Health_NoModelHint => Get(nameof(Health_NoModelHint));

    public static string Health_NoTimeTitle => Get(nameof(Health_NoTimeTitle));

    public static string Health_NoKeyHint => Get(nameof(Health_NoKeyHint));

    public static string Health_NoOpensTitle => Get(nameof(Health_NoOpensTitle));

    public static string Health_NoSyncsTitle => Get(nameof(Health_NoSyncsTitle));

    public static string Health_NotWorksharedTitle => Get(nameof(Health_NotWorksharedTitle));

    public static string Health_NotWorksharedHint => Get(nameof(Health_NotWorksharedHint));

    public static string Health_NotEnoughHistory => Get(nameof(Health_NotEnoughHistory));

    public static string Health_ActivityFailedTitle => Get(nameof(Health_ActivityFailedTitle));

    public static string Health_WorkRunning => Get(nameof(Health_WorkRunning));

    // ── Loader (spec #16, WU3: the Profile Loader window and the shared ribbon preview) ──

    public static string Loader_WindowTitle => Get(nameof(Loader_WindowTitle));

    public static string Loader_Title => Get(nameof(Loader_Title));

    public static string Loader_RevitLabel => Get(nameof(Loader_RevitLabel));

    public static string Loader_RevitVersion(object? arg0) => Format(nameof(Loader_RevitVersion), arg0);

    public static string Loader_RevitNotDetected => Get(nameof(Loader_RevitNotDetected));

    public static string Loader_ImportFromFile => Get(nameof(Loader_ImportFromFile));

    public static string Loader_Profiles => Get(nameof(Loader_Profiles));

    public static string Loader_NoProfiles => Get(nameof(Loader_NoProfiles));

    public static string Loader_NoProfilesHint => Get(nameof(Loader_NoProfilesHint));

    public static string Loader_ImportProfileFromFile => Get(nameof(Loader_ImportProfileFromFile));

    public static string Loader_LoadedBadge => Get(nameof(Loader_LoadedBadge));

    public static string Loader_Tab => Get(nameof(Loader_Tab));

    public static string Loader_MinRevit => Get(nameof(Loader_MinRevit));

    public static string Loader_MinRevitValue(object? arg0) => Format(nameof(Loader_MinRevitValue), arg0);

    public static string Loader_Panels => Get(nameof(Loader_Panels));

    public static string Loader_Updated => Get(nameof(Loader_Updated));

    public static string Loader_NoSelection => Get(nameof(Loader_NoSelection));

    public static string Loader_TabPreview => Get(nameof(Loader_TabPreview));

    public static string Loader_OverrideOpacity => Get(nameof(Loader_OverrideOpacity));

    public static string Loader_ReadOnly => Get(nameof(Loader_ReadOnly));

    public static string Loader_RstifyHeading => Get(nameof(Loader_RstifyHeading));

    public static string Loader_RstifyEmpty => Get(nameof(Loader_RstifyEmpty));

    public static string Loader_RequiredAddins => Get(nameof(Loader_RequiredAddins));

    public static string Loader_None => Get(nameof(Loader_None));

    public static string Loader_InfoLink => Get(nameof(Loader_InfoLink));

    public static string Loader_UnloadProfile => Get(nameof(Loader_UnloadProfile));

    public static string Loader_DeleteProfile => Get(nameof(Loader_DeleteProfile));

    public static string Loader_RestoreAll => Get(nameof(Loader_RestoreAll));

    public static string Loader_DisableUnused => Get(nameof(Loader_DisableUnused));

    public static string Loader_LoadProfile => Get(nameof(Loader_LoadProfile));

    public static string Loader_Close => Get(nameof(Loader_Close));

    public static string Loader_Continue => Get(nameof(Loader_Continue));

    public static string Loader_ImportDialogTitle => Get(nameof(Loader_ImportDialogTitle));

    public static string Loader_ImportFailed => Get(nameof(Loader_ImportFailed));

    public static string Loader_ImportError(object? arg0) => Format(nameof(Loader_ImportError), arg0);

    public static string Loader_Error => Get(nameof(Loader_Error));

    public static string Loader_Loading(object? arg0) => Format(nameof(Loader_Loading), arg0);

    public static string Loader_LoadError(object? arg0) => Format(nameof(Loader_LoadError), arg0);

    public static string Loader_LoadFailed => Get(nameof(Loader_LoadFailed));

    public static string Loader_LoadFailedText => Get(nameof(Loader_LoadFailedText));

    public static string Loader_RestartNeeded => Get(nameof(Loader_RestartNeeded));

    public static string Loader_TabWillBeBuilt(object? arg0) => Format(nameof(Loader_TabWillBeBuilt), arg0);

    public static string Loader_NoProfile => Get(nameof(Loader_NoProfile));

    public static string Loader_NoProfileText => Get(nameof(Loader_NoProfileText));

    public static string Loader_Unloading => Get(nameof(Loader_Unloading));

    public static string Loader_UnloadingText => Get(nameof(Loader_UnloadingText));

    public static string Loader_UnloadFailed => Get(nameof(Loader_UnloadFailed));

    public static string Loader_UnloadFailedText => Get(nameof(Loader_UnloadFailedText));

    public static string Loader_UnloadError(object? arg0) => Format(nameof(Loader_UnloadError), arg0);

    public static string Loader_DeleteTitle(object? arg0) => Format(nameof(Loader_DeleteTitle), arg0);

    public static string Loader_DeleteText => Get(nameof(Loader_DeleteText));

    public static string Loader_DeleteLoadedText => Get(nameof(Loader_DeleteLoadedText));

    public static string Loader_DeleteFailed => Get(nameof(Loader_DeleteFailed));

    public static string Loader_DeleteFailedText => Get(nameof(Loader_DeleteFailedText));

    public static string Loader_DeleteError(object? arg0) => Format(nameof(Loader_DeleteError), arg0);

    public static string Loader_RestoreFailed => Get(nameof(Loader_RestoreFailed));

    public static string Loader_RestoreFailedText(object? arg0) => Format(nameof(Loader_RestoreFailedText), arg0);

    public static string Loader_UnknownError => Get(nameof(Loader_UnknownError));

    public static string Loader_RestoreError(object? arg0) => Format(nameof(Loader_RestoreError), arg0);

    public static string Loader_ConfirmTitle => Get(nameof(Loader_ConfirmTitle));

    public static string Loader_ConfirmSubtitle(object? arg0) => Format(nameof(Loader_ConfirmSubtitle), arg0);

    public static string Loader_WillBeSwitchedOff => Get(nameof(Loader_WillBeSwitchedOff));

    public static string Loader_WillBeSwitchedOn => Get(nameof(Loader_WillBeSwitchedOn));

    public static string Loader_StayingOn => Get(nameof(Loader_StayingOn));

    public static string Loader_CantChange => Get(nameof(Loader_CantChange));

    public static string Loader_ConfirmRestart => Get(nameof(Loader_ConfirmRestart));

    public static string Loader_ConfirmAndLoad => Get(nameof(Loader_ConfirmAndLoad));

    public static string Loader_Unknown => Get(nameof(Loader_Unknown));

    public static string Loader_QaTitle => Get(nameof(Loader_QaTitle));

    public static string Loader_QaAllClear => Get(nameof(Loader_QaAllClear));

    public static string Loader_QaReview(object? arg0) => Format(nameof(Loader_QaReview), arg0);

    public static string Loader_ReportSwitchedOff => Get(nameof(Loader_ReportSwitchedOff));

    public static string Loader_ReportSwitchedOn => Get(nameof(Loader_ReportSwitchedOn));

    public static string Loader_ReportMoved => Get(nameof(Loader_ReportMoved));

    public static string Loader_ReportNotChanged => Get(nameof(Loader_ReportNotChanged));

    public static string Loader_ReportRequired(object? arg0) => Format(nameof(Loader_ReportRequired), arg0);

    public static string Loader_QaMissing => Get(nameof(Loader_QaMissing));

    public static string Loader_QaRequiredOff => Get(nameof(Loader_QaRequiredOff));

    public static string Loader_QaDownload => Get(nameof(Loader_QaDownload));

    public static string Loader_QaNoLink => Get(nameof(Loader_QaNoLink));

    public static string Loader_QaNothing => Get(nameof(Loader_QaNothing));

    public static string Loader_ReportNothing => Get(nameof(Loader_ReportNothing));

    public static string Loader_QaContinue => Get(nameof(Loader_QaContinue));

    public static string Loader_QaUnknown => Get(nameof(Loader_QaUnknown));

    public static string Loader_UnloadReportTitle => Get(nameof(Loader_UnloadReportTitle));

    public static string Loader_UnloadReportSubtitle => Get(nameof(Loader_UnloadReportSubtitle));

    public static string Loader_UnloadNoAddinChanges => Get(nameof(Loader_UnloadNoAddinChanges));

    public static string Loader_RestoreReportTitle => Get(nameof(Loader_RestoreReportTitle));

    public static string Loader_RestoreReportSubtitle => Get(nameof(Loader_RestoreReportSubtitle));

    public static string Loader_MigrateReportTitle => Get(nameof(Loader_MigrateReportTitle));

    public static string Loader_MigrateReportSubtitle => Get(nameof(Loader_MigrateReportSubtitle));

    public static string Loader_PlanFailed(object? arg0) => Format(nameof(Loader_PlanFailed), arg0);

    public static string Preview_NoTab => Get(nameof(Preview_NoTab));

    public static string Preview_NoPanels => Get(nameof(Preview_NoPanels));

    public static string Preview_NoPanelsBuilder => Get(nameof(Preview_NoPanelsBuilder));

    public static string Preview_Empty => Get(nameof(Preview_Empty));

    public static string Preview_EmptyStack(object? arg0) => Format(nameof(Preview_EmptyStack), arg0);

    // The Profile Builder (spec #16, WU4).

    public static string Builder_WindowTitle => Get(nameof(Builder_WindowTitle));

    public static string Builder_Title => Get(nameof(Builder_Title));

    public static string Builder_NewProfile => Get(nameof(Builder_NewProfile));

    public static string Builder_ProfileSelectorTip => Get(nameof(Builder_ProfileSelectorTip));

    public static string Builder_BrandingHeading => Get(nameof(Builder_BrandingHeading));

    public static string Builder_Logo => Get(nameof(Builder_Logo));

    public static string Builder_PickLogo => Get(nameof(Builder_PickLogo));

    public static string Builder_ClearLogo => Get(nameof(Builder_ClearLogo));

    public static string Builder_ClearLogoTip => Get(nameof(Builder_ClearLogoTip));

    public static string Builder_NoLogo => Get(nameof(Builder_NoLogo));

    public static string Builder_Url => Get(nameof(Builder_Url));

    public static string Builder_UrlPlaceholder => Get(nameof(Builder_UrlPlaceholder));

    public static string Builder_Done => Get(nameof(Builder_Done));

    public static string Builder_Name => Get(nameof(Builder_Name));

    public static string Builder_ExportZip => Get(nameof(Builder_ExportZip));

    public static string Builder_Save => Get(nameof(Builder_Save));

    public static string Builder_Exit => Get(nameof(Builder_Exit));

    public static string Builder_ExitTip => Get(nameof(Builder_ExitTip));

    public static string Builder_Commands => Get(nameof(Builder_Commands));

    public static string Builder_FilterPlaceholder => Get(nameof(Builder_FilterPlaceholder));

    public static string Builder_CatalogEmpty => Get(nameof(Builder_CatalogEmpty));

    public static string Builder_NoMatches(object? arg0) => Format(nameof(Builder_NoMatches), arg0);

    public static string Builder_Panels => Get(nameof(Builder_Panels));

    public static string Builder_AddPanel => Get(nameof(Builder_AddPanel));

    public static string Builder_CanvasEmpty => Get(nameof(Builder_CanvasEmpty));

    public static string Builder_PanelEmpty => Get(nameof(Builder_PanelEmpty));

    public static string Builder_MoveUp => Get(nameof(Builder_MoveUp));

    public static string Builder_MoveDown => Get(nameof(Builder_MoveDown));

    public static string Builder_DeletePanelTip => Get(nameof(Builder_DeletePanelTip));

    public static string Builder_RemoveSlotTip => Get(nameof(Builder_RemoveSlotTip));

    public static string Builder_PickIconTip => Get(nameof(Builder_PickIconTip));

    public static string Builder_DeletePanelTitle(object? arg0) => Format(nameof(Builder_DeletePanelTitle), arg0);

    public static string Builder_Delete => Get(nameof(Builder_Delete));

    public static string Builder_ProfileSection => Get(nameof(Builder_ProfileSection));

    public static string Builder_MinRevitVersion => Get(nameof(Builder_MinRevitVersion));

    public static string Builder_PanelOpacity => Get(nameof(Builder_PanelOpacity));

    public static string Builder_ColorDialogTitle => Get(nameof(Builder_ColorDialogTitle));

    public static string Builder_CleanupHelp => Get(nameof(Builder_CleanupHelp));

    public static string Builder_LivePreview => Get(nameof(Builder_LivePreview));

    public static string Builder_ReadOnly => Get(nameof(Builder_ReadOnly));

    public static string Builder_UnsavedTitle => Get(nameof(Builder_UnsavedTitle));

    public static string Builder_UnsavedText => Get(nameof(Builder_UnsavedText));

    public static string Builder_SaveAndExit => Get(nameof(Builder_SaveAndExit));

    public static string Builder_DiscardAndExit => Get(nameof(Builder_DiscardAndExit));

    public static string Builder_SaveAndSwitch => Get(nameof(Builder_SaveAndSwitch));

    public static string Builder_DiscardAndSwitch => Get(nameof(Builder_DiscardAndSwitch));

    public static string Builder_NameRequired => Get(nameof(Builder_NameRequired));

    public static string Builder_TabRequired => Get(nameof(Builder_TabRequired));

    public static string Builder_NameCollision(object? arg0) => Format(nameof(Builder_NameCollision), arg0);

    public static string Builder_SaveFailed(object? arg0) => Format(nameof(Builder_SaveFailed), arg0);

    public static string Builder_SavedAs(object? arg0) => Format(nameof(Builder_SavedAs), arg0);

    public static string Builder_SavedReloaded => Get(nameof(Builder_SavedReloaded));

    public static string Builder_SavedReloadFailed => Get(nameof(Builder_SavedReloadFailed));

    public static string Builder_ExportedTo(object? arg0) => Format(nameof(Builder_ExportedTo), arg0);

    public static string Builder_ExportFailed(object? arg0) => Format(nameof(Builder_ExportFailed), arg0);

    public static string Builder_SaveUrlFailed(object? arg0) => Format(nameof(Builder_SaveUrlFailed), arg0);

    public static string Builder_LogoPickFailed(object? arg0) => Format(nameof(Builder_LogoPickFailed), arg0);

    public static string Builder_ClearLogoFailed(object? arg0) => Format(nameof(Builder_ClearLogoFailed), arg0);

    // The Profile Builder's modals and icon picker (spec #16, WU5).

    public static string Builder_PresetsBody => Get(nameof(Builder_PresetsBody));

    public static string Builder_PresetsHideTabs => Get(nameof(Builder_PresetsHideTabs));

    public static string Builder_PresetsNoTabs => Get(nameof(Builder_PresetsNoTabs));

    public static string Builder_PresetsDisableUnused => Get(nameof(Builder_PresetsDisableUnused));

    public static string Builder_PresetsDisableUnusedHelp => Get(nameof(Builder_PresetsDisableUnusedHelp));

    public static string Builder_CleanupEmpty => Get(nameof(Builder_CleanupEmpty));

    public static string Builder_CleanupEnableTip => Get(nameof(Builder_CleanupEnableTip));

    public static string Builder_CleanupNamePlaceholder => Get(nameof(Builder_CleanupNamePlaceholder));

    public static string Builder_CleanupRemoveTip => Get(nameof(Builder_CleanupRemoveTip));

    public static string Builder_RestoreDefaults => Get(nameof(Builder_RestoreDefaults));

    public static string Builder_RestoreDefaultsTip => Get(nameof(Builder_RestoreDefaultsTip));

    public static string Builder_IconPickerTitle => Get(nameof(Builder_IconPickerTitle));

    public static string Builder_IconNone => Get(nameof(Builder_IconNone));

    public static string Builder_IconNoneGlyph => Get(nameof(Builder_IconNoneGlyph));

    public static string Builder_IconNoneTip => Get(nameof(Builder_IconNoneTip));

    public static string Builder_IconVariantTip(object? arg0, object? arg1) => Format(nameof(Builder_IconVariantTip), arg0, arg1);

    // The window shell (feature #18, spec #19, WU2): the header's ⚙ and profile menus, Rename and
    // Duplicate, the Branding dialog and the Profile pane's tabs.

    public static string Builder_SettingsTip => Get(nameof(Builder_SettingsTip));

    public static string Builder_SettingsHeading => Get(nameof(Builder_SettingsHeading));

    public static string Builder_BrandingMenu => Get(nameof(Builder_BrandingMenu));

    public static string Builder_BrandingMenuNote => Get(nameof(Builder_BrandingMenuNote));

    public static string Builder_BrandingHelp => Get(nameof(Builder_BrandingHelp));

    public static string Builder_SwitchProfileHeading => Get(nameof(Builder_SwitchProfileHeading));

    public static string Builder_LoadedMark => Get(nameof(Builder_LoadedMark));

    public static string Builder_RenameMenu => Get(nameof(Builder_RenameMenu));

    public static string Builder_RenameNote => Get(nameof(Builder_RenameNote));

    public static string Builder_DuplicateMenu => Get(nameof(Builder_DuplicateMenu));

    public static string Builder_DuplicateNote => Get(nameof(Builder_DuplicateNote));

    public static string Builder_UnsavedIndicator => Get(nameof(Builder_UnsavedIndicator));

    public static string Builder_UnnamedProfile => Get(nameof(Builder_UnnamedProfile));

    public static string Builder_UnnamedHint => Get(nameof(Builder_UnnamedHint));

    public static string Builder_TabGeneral => Get(nameof(Builder_TabGeneral));

    public static string Builder_TabLoaderDefaults => Get(nameof(Builder_TabLoaderDefaults));

    public static string Builder_TabJunkCleanup => Get(nameof(Builder_TabJunkCleanup));

    public static string Builder_ProfileName => Get(nameof(Builder_ProfileName));

    public static string Builder_RibbonTabName => Get(nameof(Builder_RibbonTabName));

    public static string Builder_PanelOpacityHelp => Get(nameof(Builder_PanelOpacityHelp));

    public static string Builder_MinRevitHelp => Get(nameof(Builder_MinRevitHelp));

    public static string Builder_RenameTitle => Get(nameof(Builder_RenameTitle));

    public static string Builder_RenameHelp => Get(nameof(Builder_RenameHelp));

    public static string Builder_RenameConfirm => Get(nameof(Builder_RenameConfirm));

    public static string Builder_DuplicateTitle => Get(nameof(Builder_DuplicateTitle));

    public static string Builder_DuplicateHelp => Get(nameof(Builder_DuplicateHelp));

    public static string Builder_DuplicateConfirm => Get(nameof(Builder_DuplicateConfirm));

    public static string Builder_ProfileRowLoaded(object? arg0) => Format(nameof(Builder_ProfileRowLoaded), arg0);

    public static string Builder_ProfileRowTab(object? arg0) => Format(nameof(Builder_ProfileRowTab), arg0);

    public static string Builder_EditingName(object? arg0) => Format(nameof(Builder_EditingName), arg0);

    public static string Builder_OpacityPercent(object? arg0) => Format(nameof(Builder_OpacityPercent), arg0);

    public static string Builder_Duplicated(object? arg0) => Format(nameof(Builder_Duplicated), arg0);

    public static string Builder_CleanupPathTip(object? arg0) => Format(nameof(Builder_CleanupPathTip), arg0);

    public static string Builder_SavedReloadFailedOldKept(object? arg0, object? arg1) => Format(nameof(Builder_SavedReloadFailedOldKept), arg0, arg1);

    public static string Builder_SavedOldFileKept(object? arg0, object? arg1, object? arg2) => Format(nameof(Builder_SavedOldFileKept), arg0, arg1, arg2);

    // The panel cards as tiles, stacks and the New / Edit Stack dialog (feature #18, spec #19, WU3).

    public static string Builder_PanelsOnTab => Get(nameof(Builder_PanelsOnTab));

    public static string Builder_TabNameTip => Get(nameof(Builder_TabNameTip));

    public static string Builder_NoTabName => Get(nameof(Builder_NoTabName));

    public static string Builder_DragPanelTip => Get(nameof(Builder_DragPanelTip));

    public static string Builder_PanelColorTip => Get(nameof(Builder_PanelColorTip));

    public static string Builder_PanelName => Get(nameof(Builder_PanelName));

    public static string Builder_ToolName => Get(nameof(Builder_ToolName));

    public static string Builder_NewStack => Get(nameof(Builder_NewStack));

    public static string Builder_NewStackTip => Get(nameof(Builder_NewStackTip));

    public static string Builder_EditCustomToolTip => Get(nameof(Builder_EditCustomToolTip));

    public static string Builder_StackHeader(object? arg0) => Format(nameof(Builder_StackHeader), arg0);

    public static string Builder_EditStackTip => Get(nameof(Builder_EditStackTip));

    public static string Builder_UnstackTip => Get(nameof(Builder_UnstackTip));

    /// <summary>The fourth tool's refusal; the same text as RST.Core's PanelStacks.FullMessage.</summary>
    public static string Builder_StackFull => Get(nameof(Builder_StackFull));

    public static string Builder_StacksRepaired(object? arg0) => Format(nameof(Builder_StacksRepaired), arg0);

    public static string Builder_NewStackTitle(object? arg0) => Format(nameof(Builder_NewStackTitle), arg0);

    public static string Builder_EditStackTitle(object? arg0) => Format(nameof(Builder_EditStackTitle), arg0);

    public static string Builder_NewStackHelp => Get(nameof(Builder_NewStackHelp));

    public static string Builder_EditStackHelp => Get(nameof(Builder_EditStackHelp));

    public static string Builder_StackPicked(object? arg0) => Format(nameof(Builder_StackPicked), arg0);

    public static string Builder_StackWillDissolve(object? arg0) => Format(nameof(Builder_StackWillDissolve), arg0);

    public static string Builder_CreateStack => Get(nameof(Builder_CreateStack));

    public static string Builder_SaveStack => Get(nameof(Builder_SaveStack));

    public static string Builder_StackEarlierTip => Get(nameof(Builder_StackEarlierTip));

    public static string Builder_StackLaterTip => Get(nameof(Builder_StackLaterTip));

    public static string Builder_StackPickInStack => Get(nameof(Builder_StackPickInStack));

    public static string Builder_StackPickReturns => Get(nameof(Builder_StackPickReturns));

    // Custom Tools: the Commands pane's list, + Create new and the Custom Tool dialog (feature #18, spec #19, WU4).

    public static string Builder_CreateNew => Get(nameof(Builder_CreateNew));

    public static string Builder_CreateNewTip => Get(nameof(Builder_CreateNewTip));

    public static string Builder_CustomTools => Get(nameof(Builder_CustomTools));

    public static string Builder_CustomToolsNote => Get(nameof(Builder_CustomToolsNote));

    public static string Builder_CustomToolsToggleTip => Get(nameof(Builder_CustomToolsToggleTip));

    public static string Builder_KindUrl => Get(nameof(Builder_KindUrl));

    public static string Builder_KindEmail => Get(nameof(Builder_KindEmail));

    public static string Builder_CustomToolUsedIn(object? arg0) => Format(nameof(Builder_CustomToolUsedIn), arg0);

    public static string Builder_EditToolEverywhereTip => Get(nameof(Builder_EditToolEverywhereTip));

    public static string Builder_RemoveToolEverywhereTip => Get(nameof(Builder_RemoveToolEverywhereTip));

    public static string Builder_CreateToolTitle => Get(nameof(Builder_CreateToolTitle));

    public static string Builder_EditToolTitle => Get(nameof(Builder_EditToolTitle));

    public static string Builder_ToolKindUrlOption => Get(nameof(Builder_ToolKindUrlOption));

    public static string Builder_ToolKindEmailOption => Get(nameof(Builder_ToolKindEmailOption));

    public static string Builder_ToolLabel => Get(nameof(Builder_ToolLabel));

    public static string Builder_ToolLabelPlaceholder => Get(nameof(Builder_ToolLabelPlaceholder));

    public static string Builder_ToolAddress => Get(nameof(Builder_ToolAddress));

    public static string Builder_ToolEmailAddress => Get(nameof(Builder_ToolEmailAddress));

    public static string Builder_ToolAddressPlaceholder => Get(nameof(Builder_ToolAddressPlaceholder));

    public static string Builder_ToolEmailPlaceholder => Get(nameof(Builder_ToolEmailPlaceholder));

    public static string Builder_ToolOpens(object? arg0) => Format(nameof(Builder_ToolOpens), arg0);

    public static string Builder_ToolIcon => Get(nameof(Builder_ToolIcon));

    public static string Builder_ToolNoIcon => Get(nameof(Builder_ToolNoIcon));

    public static string Builder_Create => Get(nameof(Builder_Create));

    public static string Builder_ToolLabelRequired => Get(nameof(Builder_ToolLabelRequired));

    public static string Builder_ToolAddressRequired => Get(nameof(Builder_ToolAddressRequired));

    public static string Builder_ToolCreateNote(object? arg0) => Format(nameof(Builder_ToolCreateNote), arg0);

    public static string Builder_ToolEditWarning(object? arg0) => Format(nameof(Builder_ToolEditWarning), arg0);

    public static string Builder_ToolNotChanged => Get(nameof(Builder_ToolNotChanged));

    public static string Builder_ProfileCountOne => Get(nameof(Builder_ProfileCountOne));

    public static string Builder_ProfileCountMany(object? arg0) => Format(nameof(Builder_ProfileCountMany), arg0);

    public static string Builder_UpdateIn(object? arg0) => Format(nameof(Builder_UpdateIn), arg0);

    public static string Builder_RemoveFrom(object? arg0) => Format(nameof(Builder_RemoveFrom), arg0);

    public static string Builder_ToolTargetUnsaved(object? arg0) => Format(nameof(Builder_ToolTargetUnsaved), arg0);

    public static string Builder_ToolRefusedNoId(object? arg0) => Format(nameof(Builder_ToolRefusedNoId), arg0);

    public static string Builder_ToolRefusedSharedId(object? arg0, object? arg1) => Format(nameof(Builder_ToolRefusedSharedId), arg0, arg1);

    public static string Builder_ToolRefusedUnreadable(object? arg0) => Format(nameof(Builder_ToolRefusedUnreadable), arg0);

    public static string Builder_RemoveToolTitle => Get(nameof(Builder_RemoveToolTitle));

    public static string Builder_RemoveToolText(object? arg0, object? arg1) => Format(nameof(Builder_RemoveToolText), arg0, arg1);

    public static string Builder_ToolNothingToChange => Get(nameof(Builder_ToolNothingToChange));

    public static string Builder_AddPanelFirst => Get(nameof(Builder_AddPanelFirst));

    public static string Builder_ToolAdded(object? arg0) => Format(nameof(Builder_ToolAdded), arg0);

    public static string Builder_ToolAddedExisting => Get(nameof(Builder_ToolAddedExisting));

    public static string Builder_ToolUpdated(object? arg0) => Format(nameof(Builder_ToolUpdated), arg0);

    public static string Builder_ToolRemoved(object? arg0) => Format(nameof(Builder_ToolRemoved), arg0);

    public static string Builder_ToolRibbonReloaded => Get(nameof(Builder_ToolRibbonReloaded));

    public static string Builder_ToolReloadFailed => Get(nameof(Builder_ToolReloadFailed));

    public static string Builder_ToolChangeFailed => Get(nameof(Builder_ToolChangeFailed));

    public static string Builder_ToolsPartialTitle => Get(nameof(Builder_ToolsPartialTitle));

    public static string Builder_ToolsPartialText(object? arg0) => Format(nameof(Builder_ToolsPartialText), arg0);

    public static string Builder_ToolFailedLine(object? arg0, object? arg1, object? arg2) => Format(nameof(Builder_ToolFailedLine), arg0, arg1, arg2);

    public static string Builder_ToolNotAnEmail => Get(nameof(Builder_ToolNotAnEmail));

    public static string Builder_ToolTargetOpenOnly(object? arg0) => Format(nameof(Builder_ToolTargetOpenOnly), arg0);

    public static string Builder_ToolTargetFile(object? arg0, object? arg1) => Format(nameof(Builder_ToolTargetFile), arg0, arg1);

    public static string Builder_ToolNothingYet => Get(nameof(Builder_ToolNothingYet));

    public static string Builder_ToolUnchanged(object? arg0) => Format(nameof(Builder_ToolUnchanged), arg0);
}
