// RemoveUserRst.cs: RST-Setup.exe's helper for "Everyone on this PC" (feature #17, spec #20 §7; F17 WU5).
//
// Removes the installing user's OWN RST before RST-Machine.msi installs: Revit lets a per-user manifest of the same
// name win, so a copy left behind would shadow the shared one for this user. "Own RST" is every per-user product of
//   AF0331FC-5280-492A-94BD-95EDF108BE74  RST.msi, any version
//   02BE4399-A563-4B15-BD12-ECF5D8486FB3  the retired "RST (Revit 2027 preview)", RST-R27.msi (v1.0.0-v2.2.0), which
//                                         RST.msi's own Upgrade row already treats as RST (review B2)
// Burn (Bundle.wxs, ExePackage RemoveUserRst, PerMachine="no") runs it in the bundle's unelevated process, as that
// user, so it only ever touches that user's own per-user products:
//   - MsiEnumRelatedProducts finds every product with those UpgradeCodes the user can see;
//   - MsiGetProductInfoEx in MSIINSTALLCONTEXT_USERUNMANAGED keeps only this user's per-user ones;
//   - MsiConfigureProductEx(INSTALLSTATE_ABSENT) removes each, silently, with REBOOT=ReallySuppress.
// Burn's own RST package (InstallCondition false on this path) removes a copy of exactly the bundled version first;
// this covers every other version and the preview.
//
// Nothing is removed unless all of it can be (review B2: a failure must never strand the user without RST):
//   1. Revit must be closed in this session (spec §5's rule; review N2). With the full UI it asks "Close Revit,
//      then Retry"; Cancel, or any quieter UI, stops with exit 32 (ERROR_SHARING_VIOLATION).
//   2. Every product to remove must be removable: its cached package (LocalPackage) is present. Otherwise it stops
//      with exit 1612 (ERROR_INSTALL_SOURCE_ABSENT).
// Either stop happens before anything is removed, and the bundle then stops before RST-Machine.msi: the user keeps
// their RST. The preview goes first, then RST.msi, so a later failure still leaves RST.msi in place.
//
// Arguments: the log path (Burn passes [WixBundleLog_RemoveUserRst]; empty -> %TEMP%) and the bundle's UI level
// ([WixBundleUILevel]: 4 = full UI). Each removal's Windows Installer log goes beside the log. Exit codes: 0 nothing
// to remove or all removed; 3010 removed, a restart is needed; 32 Revit is running; 1602 cancelled; 1612 a product
// cannot be removed; anything else is the Windows Installer error that stopped it.
//
// Built by RST.Setup.wixproj with the .NET Framework 4 compiler that ships in Windows
// (%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe, C# 5), so it needs no SDK or package and runs on the .NET
// Framework 4.8 every supported Windows has. Keep it to C# 5.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

internal static class RemoveUserRst
{
    // Removal order: the preview first, RST.msi last.
    private static readonly string[][] Products =
    {
        new[] { "{02BE4399-A563-4B15-BD12-ECF5D8486FB3}", "RST (Revit 2027 preview)" },
        new[] { "{AF0331FC-5280-492A-94BD-95EDF108BE74}", "RST" },
    };

    private const int MsiInstallContextUserUnmanaged = 2; // MSIINSTALLCONTEXT_USERUNMANAGED (1 is USERMANAGED, 4 MACHINE)
    private const int InstallUiLevelNone = 2;
    private const int InstallLevelDefault = 0;
    private const int InstallStateAbsent = 2;
    private const int BundleUiFull = 4; // BOOTSTRAPPER_DISPLAY_FULL

    private const int ExitSuccess = 0;
    private const int ExitRevitRunning = 32;      // ERROR_SHARING_VIOLATION
    private const int ExitCancelled = 1602;       // ERROR_INSTALL_USEREXIT
    private const int ExitInstallFailure = 1603;  // ERROR_INSTALL_FAILURE
    private const int ExitNotRemovable = 1612;    // ERROR_INSTALL_SOURCE_ABSENT

    private const uint ErrorSuccess = 0;
    private const uint ErrorNoMoreItems = 259;
    private const uint ErrorUnknownProduct = 1605;
    private const uint ErrorSuccessRebootRequired = 3010;

    // INSTALLLOGMODE_*: everything verbose logging (/l*v) writes.
    private const uint LogModeVerbose = 0x1 | 0x2 | 0x4 | 0x8 | 0x10 | 0x40 | 0x80 | 0x100 | 0x200 | 0x400 | 0x800 | 0x1000;
    private const uint LogAttributesFlushEachLine = 0x2;

    private const uint MbRetryCancel = 0x5, MbOk = 0x0, MbIconWarning = 0x30, MbSetForeground = 0x10000, MbTopmost = 0x40000;
    private const int IdRetry = 4;

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);

    private sealed class Found
    {
        public string ProductCode;
        public string Name;
        public string Version;
        public string LocalPackage;
    }

    private static int Main(string[] args)
    {
        string log = args.Length > 0 && args[0].Trim().Length > 0
            ? args[0].Trim()
            : Path.Combine(Path.GetTempPath(), "RST-RemoveUserRst-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".log");
        int uiLevel;
        bool fullUi = args.Length > 1 && int.TryParse(args[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out uiLevel) && uiLevel == BundleUiFull;
        try
        {
            Write(log, "RST-Setup: checking, then removing, this user's own RST before RST is installed for everyone on this PC");
            Write(log, "user " + Environment.UserDomainName + "\\" + Environment.UserName + "; administrator token: " + HasAdministratorToken() + "; full UI: " + fullUi);

            // 1. Revit closed in this session (the main install's rule, spec §5).
            while (true)
            {
                string running = RevitInThisSession();
                if (running == null) break;
                Write(log, "Revit is running in this session (" + running + ")");
                if (!fullUi)
                {
                    Write(log, "stopped before removing anything: close Revit, then run RST-Setup.exe again (exit " + ExitRevitRunning + ")");
                    return ExitRevitRunning;
                }
                int answer = MessageBox(IntPtr.Zero,
                    "Revit is running. Close Revit, then click Retry.\n\nNothing has been changed yet. Click Cancel to stop setup; RST stays as it is.",
                    "RST Setup", MbRetryCancel | MbIconWarning | MbSetForeground | MbTopmost);
                if (answer != IdRetry)
                {
                    Write(log, "cancelled by the user while Revit was running; nothing removed (exit " + ExitCancelled + ")");
                    return ExitCancelled;
                }
            }
            Write(log, "Revit is not running in this session");

            // 2. What this user has, and whether all of it can be removed.
            List<Found> found = new List<Found>();
            foreach (string[] product in Products)
            {
                for (uint i = 0; ; i++)
                {
                    StringBuilder code = new StringBuilder(39);
                    uint r = MsiEnumRelatedProducts(product[0], 0, i, code);
                    if (r == ErrorNoMoreItems) break;
                    if (r != ErrorSuccess)
                    {
                        Write(log, "MsiEnumRelatedProducts " + product[0] + " failed with " + r + "; nothing removed");
                        return (int)r;
                    }
                    uint infoResult;
                    string version = Info(code.ToString(), "VersionString", out infoResult);
                    if (version == null)
                    {
                        Write(log, code + " (" + product[1] + "): not installed per user for this user (MsiGetProductInfoEx returned " + infoResult + "); left alone");
                        continue;
                    }
                    Found f = new Found();
                    f.ProductCode = code.ToString();
                    f.Name = product[1];
                    f.Version = version;
                    f.LocalPackage = Info(f.ProductCode, "LocalPackage", out infoResult);
                    found.Add(f);
                }
            }
            if (found.Count == 0)
            {
                Write(log, "no RST installed for this user: nothing to remove");
                return ExitSuccess;
            }
            foreach (Found f in found)
            {
                bool removable = !string.IsNullOrEmpty(f.LocalPackage) && File.Exists(f.LocalPackage);
                Write(log, f.ProductCode + ": " + f.Name + " " + f.Version + " is installed for this user; cached package " + (f.LocalPackage ?? "(none)") + (removable ? " (present)" : " (MISSING)"));
                if (!removable)
                {
                    string message = f.Name + " " + f.Version + " is installed for your Windows account, but Windows can't remove it: its installer cache is missing. "
                        + "Nothing has been changed. Remove it in Settings > Apps (or repair it from its original MSI), then run RST-Setup.exe again.";
                    Write(log, "stopped before removing anything: " + message + " (exit " + ExitNotRemovable + ")");
                    if (fullUi) MessageBox(IntPtr.Zero, message, "RST Setup", MbOk | MbIconWarning | MbSetForeground | MbTopmost);
                    return ExitNotRemovable;
                }
            }

            // 3. Remove, the preview first.
            int result = ExitSuccess;
            foreach (Found f in found)
            {
                string msiLog = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(log)) ?? Path.GetTempPath(),
                    Path.GetFileNameWithoutExtension(log) + "_" + f.ProductCode.Trim('{', '}') + ".log");
                Write(log, f.ProductCode + ": removing " + f.Name + " " + f.Version + " (Windows Installer log: " + msiLog + ")");
                MsiSetInternalUI(InstallUiLevelNone, IntPtr.Zero);
                MsiEnableLog(LogModeVerbose, msiLog, LogAttributesFlushEachLine);
                uint er = MsiConfigureProductEx(f.ProductCode, InstallLevelDefault, InstallStateAbsent, "REBOOT=ReallySuppress");
                Write(log, f.ProductCode + ": Windows Installer returned " + er);
                if (er == ErrorSuccess || er == ErrorUnknownProduct) continue;
                if (er == ErrorSuccessRebootRequired)
                {
                    result = (int)ErrorSuccessRebootRequired;
                    continue;
                }
                Write(log, "stopped: " + f.Name + " could not be removed, so RST is not installed for everyone");
                return (int)er;
            }
            Write(log, "removed " + found.Count + " per-user RST install(s)");
            return result;
        }
        catch (Exception ex)
        {
            Write(log, "failed: " + ex);
            return ExitInstallFailure;
        }
    }

    /// <summary>A product property in the current user's per-user context, or null.</summary>
    private static string Info(string productCode, string property, out uint result)
    {
        uint length = 260;
        StringBuilder value = new StringBuilder((int)length + 1);
        result = MsiGetProductInfoEx(productCode, null, MsiInstallContextUserUnmanaged, property, value, ref length);
        return result == ErrorSuccess ? value.ToString() : null;
    }

    /// <summary>"Revit.exe (pid N)" for the first Revit in this process's session, or null.</summary>
    private static string RevitInThisSession()
    {
        int session = Process.GetCurrentProcess().SessionId;
        foreach (Process p in Process.GetProcessesByName("Revit"))
        {
            try
            {
                if (p.SessionId == session) return "Revit.exe, pid " + p.Id;
            }
            catch
            {
                // A process that ended while we looked is not running.
            }
        }
        return null;
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
