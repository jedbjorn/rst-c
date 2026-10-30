// Adapted from splash-it src/SplashIt.UI/Resources/UiStrings.cs (splash-it commit ba86a71), MIT, same owner.
// Changes: namespaces (SplashIt.* to RST.*); only the kit's keys (Common_, Dialog_, Edit_, Color_, Pick_); splash-it's Settings_, Status_ and Preview_ keys are left out.

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
}
