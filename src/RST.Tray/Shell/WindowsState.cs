// rst-c's own (not vendored): the bits of Windows state the RST tray reads (spec #20 §1, §4; F17 WU1): the
// Windows app theme, high contrast, and the IT preset HKMU\Software\Designs-OS\RST\Tray\UpdateChecks. Every
// read is best effort; the decisions themselves live in RST.Core.Tray (TrayTheme, UpdateChecksPolicy).

using System.Windows;
using Microsoft.Win32;
using RST.Core.Tray;

namespace RST.Tray.Shell;

internal static class WindowsState
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>HKCU …\Themes\Personalize\AppsUseLightTheme: true light, false dark, null when unset or unreadable.</summary>
    public static bool? AppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int value ? value != 0 : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool HighContrast => SystemParameters.HighContrast;

    /// <summary>
    /// The UpdateChecks preset under HKLM and HKCU (HKMU is HKCU for a per-user install and HKLM for a per-machine
    /// one, so both are read). A DWORD, or a string holding a number; anything else counts as unset.
    /// </summary>
    public static IReadOnlyList<int?> UpdateChecksPresets() =>
        new[] { ReadPreset(RegistryHive.LocalMachine), ReadPreset(RegistryHive.CurrentUser) };

    private static int? ReadPreset(RegistryHive hive)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(TrayPaths.PresetKey);
            return key?.GetValue(TrayPaths.UpdateChecksValueName) switch
            {
                int dword => dword,
                long qword => (int)Math.Clamp(qword, int.MinValue, int.MaxValue),
                string text when int.TryParse(text.Trim(), out var parsed) => parsed,
                _ => null,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }
}
