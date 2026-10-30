// RemoveUserRst.cs: RST-Setup.exe's helper for "Everyone on this PC" (feature #17, spec #20 §7; F17 WU5).
//
// Removes the installing user's OWN RST, the per-user RST.msi of any version (UpgradeCode AF0331FC-...), before
// RST-Machine.msi installs: Revit lets a per-user manifest of the same name win, so a copy left behind would shadow
// the shared one for this user. Burn (Bundle.wxs, ExePackage RemoveUserRst, PerMachine="no") runs it in the
// bundle's unelevated process, as that user, so it only ever touches that user's own per-user products:
//   - MsiEnumRelatedProducts finds every product with RST.msi's UpgradeCode the user can see;
//   - MsiGetProductInfoEx in MSIINSTALLCONTEXT_USERUNMANAGED keeps only this user's per-user ones;
//   - MsiConfigureProductEx(INSTALLSTATE_ABSENT) removes each, silently, with REBOOT=ReallySuppress.
// Burn's own RST package (InstallCondition false on this path) removes a copy of exactly the bundled version first;
// this covers every other version (an RST.msi from v2.x or v3.0, installed bare or by an older bundle).
//
// Argument: the log path (Burn passes [WixBundleLog_RemoveUserRst]); empty or missing -> %TEMP%. Each removal's
// Windows Installer log goes beside it. Exit codes: 0 nothing to remove or all removed; 3010 removed, a restart is
// needed; anything else is the Windows Installer error that stopped it (the bundle then fails before RST-Machine.msi
// installs, and the user's copy stays).
//
// Built by RST.Setup.wixproj with the .NET Framework 4 compiler that ships in Windows
// (%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe, C# 5), so it needs no SDK or package and runs on the .NET
// Framework 4.8 every supported Windows has. Keep it to C# 5.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

internal static class RemoveUserRst
{
    private const string RstUpgradeCode = "{AF0331FC-5280-492A-94BD-95EDF108BE74}";

    private const int MsiInstallContextUserUnmanaged = 2; // MSIINSTALLCONTEXT_USERUNMANAGED (1 is USERMANAGED, 4 MACHINE)
    private const int InstallUiLevelNone = 2;
    private const int InstallLevelDefault = 0;
    private const int InstallStateAbsent = 2;

    private const uint ErrorSuccess = 0;
    private const uint ErrorNoMoreItems = 259;
    private const uint ErrorUnknownProduct = 1605;
    private const uint ErrorSuccessRebootRequired = 3010;
    private const uint ErrorInstallFailure = 1603;

    // INSTALLLOGMODE_*: everything verbose logging (/l*v) writes.
    private const uint LogModeVerbose = 0x1 | 0x2 | 0x4 | 0x8 | 0x10 | 0x40 | 0x80 | 0x100 | 0x200 | 0x400 | 0x800 | 0x1000;
    private const uint LogAttributesFlushEachLine = 0x2;

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiEnumRelatedProductsW")]
    private static extern uint MsiEnumRelatedProducts(string upgradeCode, uint reserved, uint index, StringBuilder productCode);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiGetProductInfoExW")]
    private static extern uint MsiGetProductInfoEx(string productCode, string userSid, int context, string property, StringBuilder value, ref uint length);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiSetInternalUI")]
    private static extern int MsiSetInternalUI(int level, IntPtr window);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiEnableLogW")]
    private static extern uint MsiEnableLog(uint mode, string logFile, uint attributes);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiConfigureProductExW")]
    private static extern uint MsiConfigureProductEx(string productCode, int installLevel, int installState, string commandLine);

    private static int Main(string[] args)
    {
        string log = args.Length > 0 && args[0].Trim().Length > 0
            ? args[0].Trim()
            : Path.Combine(Path.GetTempPath(), "RST-RemoveUserRst-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".log");
        try
        {
            Write(log, "RST-Setup: removing this user's own RST (RST.msi) before RST is installed for everyone on this PC");
            Write(log, "user " + Environment.UserDomainName + "\\" + Environment.UserName + "; administrator token: " + HasAdministratorToken());

            List<string> found = new List<string>();
            for (uint i = 0; ; i++)
            {
                StringBuilder code = new StringBuilder(39);
                uint r = MsiEnumRelatedProducts(RstUpgradeCode, 0, i, code);
                if (r == ErrorNoMoreItems) break;
                if (r != ErrorSuccess)
                {
                    Write(log, "MsiEnumRelatedProducts failed with " + r + "; nothing removed");
                    return (int)r;
                }
                found.Add(code.ToString());
            }

            int result = 0;
            int removed = 0;
            foreach (string productCode in found)
            {
                uint infoResult;
                string version = PerUserVersion(productCode, out infoResult);
                if (version == null)
                {
                    Write(log, productCode + ": not installed per user for this user (MsiGetProductInfoEx returned " + infoResult + "); left alone");
                    continue;
                }
                string msiLog = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(log)) ?? Path.GetTempPath(),
                    Path.GetFileNameWithoutExtension(log) + "_" + productCode.Trim('{', '}') + ".log");
                Write(log, productCode + ": RST " + version + " is installed for this user; removing it (Windows Installer log: " + msiLog + ")");
                MsiSetInternalUI(InstallUiLevelNone, IntPtr.Zero);
                MsiEnableLog(LogModeVerbose, msiLog, LogAttributesFlushEachLine);
                uint er = MsiConfigureProductEx(productCode, InstallLevelDefault, InstallStateAbsent, "REBOOT=ReallySuppress");
                Write(log, productCode + ": Windows Installer returned " + er);
                if (er == ErrorSuccess || er == ErrorUnknownProduct)
                {
                    removed++;
                    continue;
                }
                if (er == ErrorSuccessRebootRequired)
                {
                    removed++;
                    result = (int)ErrorSuccessRebootRequired;
                    continue;
                }
                Write(log, "stopped: this user's RST could not be removed, so RST is not installed for everyone");
                return (int)er;
            }
            Write(log, removed == 0 ? "no RST installed for this user: nothing to remove" : "removed " + removed + " per-user RST install(s)");
            return result;
        }
        catch (Exception ex)
        {
            Write(log, "failed: " + ex);
            return (int)ErrorInstallFailure;
        }
    }

    /// <summary>The product's version when it is installed per user for the current user, otherwise null.</summary>
    private static string PerUserVersion(string productCode, out uint result)
    {
        uint length = 64;
        StringBuilder value = new StringBuilder((int)length + 1);
        result = MsiGetProductInfoEx(productCode, null, MsiInstallContextUserUnmanaged, "VersionString", value, ref length);
        return result == ErrorSuccess ? value.ToString() : null;
    }

    /// <summary>True when this process holds the Administrators group (elevated, or UAC off).</summary>
    private static bool HasAdministratorToken()
    {
        try
        {
            return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static void Write(string log, string line)
    {
        try
        {
            File.AppendAllText(log, DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + line + Environment.NewLine);
        }
        catch
        {
            // Logging must never stop the removal.
        }
    }
}
