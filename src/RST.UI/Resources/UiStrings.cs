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
}
