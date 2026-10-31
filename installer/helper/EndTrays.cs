// EndTrays.cs: RST-Machine.msi's custom action that ends every signed-in user's tray before the files go (SC-028;
// feature #17, spec #20 §7).
//
// A per-machine uninstall, repair or upgrade run as SYSTEM in session 0 (SCCM, Intune) has no user's desktop, so
// util:CloseApplication's WM_CLOSE (Tray.wxs) reaches no tray in a user's session, and Restart Manager is off in this
// package (Product.wxs, review N8). Windows Installer then renames the tray's files that are in use, the tray runs on
// from the renamed copies, and msiexec still exits 0. CloseApplication's own TerminateProcess cannot be used instead:
// it matches RST.Tray.exe by file name only, so it would also end trays that run from a user's own per-user RST
// (%LocalAppData%\Programs\RST\Tray\).
//
// So this runs as a deferred, not impersonated custom action (Tray.wxs: RstEndTrays, as SYSTEM, before RemoveFiles):
//   1. every RST.Tray process, in any session, whose image is exactly this package's tray (the first argument,
//      [TrayDir]RST.Tray.exe) is ended with TerminateProcess (exit code 1) and waited for (10 s each). The image is
//      checked and the process ended through one handle, so a process id reused in between is never ended unchecked
//      (review N1). The tray holds no unsaved user data; it starts again at the user's next sign-in, or when Revit
//      starts it. Any other RST.Tray process (a per-user tray) is logged and left alone.
//   2. On a full uninstall only (the fourth argument, [REMOVE], is ALL; review N2), Restart Manager is asked which
//      processes still use any file under the package folder (the second argument,
//      [RstFolder]: C:\Program Files\RST). Typically Revit, with RST's engine loaded. If any does, a restart is needed:
//      it sets the global atom "WcaDeferredActionRequiresReboot", WiX's own marker that its CheckRebootRequired action
//      (scheduled after InstallFinalize, on uninstall too in this package) turns into "restart required", so msiexec
//      returns 3010 instead of 0. That is how util:CloseApplication's RebootPrompt asks for a restart too. An
//      install, repair or upgrade skips this step: Windows Installer itself returns 3010 when it has to replace a
//      file in use, and a repair that replaces nothing must not ask deployment tools for a reboot.
//
// Arguments: the tray's path, the package folder, the log's base path (Tray.wxs passes
// [TempFolder]RST-Machine-EndTrays.log: as SYSTEM that is C:\Windows\Temp or C:\Windows\SystemTemp), and [REMOVE].
// Each run writes its own new log beside the base path, RST-Machine-EndTrays-<yyyyMMdd-HHmmss>-<pid>.log (review N7).
// Exit codes, for the log only (the custom action is Return="ignore": it never stops an uninstall): 0 done; 3010 a
// restart is needed; 1603 it failed.
//
// Built by RST.Installer.wixproj (machine scope) with the .NET Framework 4 compiler that ships in Windows
// (%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe, C# 5), like the bundle's RemoveUserRst helper. It sits in
// the MSI's Binary table only: nothing of it is installed. Keep it to C# 5.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

internal static class EndTrays
{
    private const int ExitSuccess = 0;
    private const int ExitRebootRequired = 3010; // ERROR_SUCCESS_REBOOT_REQUIRED
    private const int ExitFailure = 1603;        // ERROR_INSTALL_FAILURE

    private const uint TerminatedExitCode = 1;
    private const uint WaitMilliseconds = 10000;
    private const uint ProcessTerminate = 0x0001, ProcessQueryLimitedInformation = 0x1000, Synchronize = 0x00100000;
    private const uint WaitObject0 = 0;
    private const int ErrorMoreData = 234;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ProcessIdToSessionId(int processId, out uint sessionId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GlobalAddAtomW")]
    private static extern ushort GlobalAddAtom(string name);

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, int flags, StringBuilder sessionKey);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint session, uint fileCount, string[] files, uint applicationCount, RmUniqueProcess[] applications, uint serviceCount, string[] services);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] RmProcessInfo[] processes, out uint rebootReasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);

    private static int Main(string[] args)
    {
        string log = RunLog(args.Length > 2 ? args[2].Trim() : "");
        try
        {
            if (args.Length < 2) throw new ArgumentException("usage: RST.EndTrays.exe <tray exe> <package folder> [<log> [<REMOVE>]]");
            string trayExe = Path.GetFullPath(args[0].Trim());
            string root = Path.GetFullPath(args[1].Trim());
            string remove = args.Length > 3 ? args[3].Trim() : "";
            bool fullUninstall = string.Equals(remove, "ALL", StringComparison.OrdinalIgnoreCase);
            uint mySession;
            ProcessIdToSessionId(Process.GetCurrentProcess().Id, out mySession);
            Write(log, "RST-Machine: ending every tray that runs from " + trayExe + " (any user, any session)"
                + (fullUninstall ? ", then checking " + root + " for files still in use" : ""));
            Write(log, "running as " + Environment.UserDomainName + "\\" + Environment.UserName + " in session " + mySession + "; REMOVE=" + remove);

            // 1. This package's trays, in every session. Nothing else.
            int ended = 0, failed = 0, left = 0;
            foreach (Process p in Process.GetProcessesByName("RST.Tray"))
            {
                using (p)
                {
                    int pid = p.Id;
                    uint session;
                    string where = ProcessIdToSessionId(pid, out session) ? "session " + session : "session unknown";
                    // One handle for the check and the end (review N1).
                    IntPtr h = OpenProcess(ProcessQueryLimitedInformation | ProcessTerminate | Synchronize, false, pid);
                    if (h == IntPtr.Zero)
                    {
                        int e = Marshal.GetLastWin32Error();
                        Write(log, "left alone: RST.Tray pid " + pid + " (" + where + ") runs from " + (ImagePath(pid) ?? "(path unknown)") + "; it cannot be opened to end it (error " + e + ")");
                        left++;
                        continue;
                    }
                    try
                    {
                        string image = ImagePath(h);
                        if (image == null || !string.Equals(image, trayExe, StringComparison.OrdinalIgnoreCase))
                        {
                            Write(log, "left alone: RST.Tray pid " + pid + " (" + where + ") runs from " + (image ?? "(path unknown)") + ", not from this package");
                            left++;
                            continue;
                        }
                        string error;
                        if (End(h, out error))
                        {
                            Write(log, "ended: RST.Tray pid " + pid + " (" + where + ") from " + image);
                            ended++;
                        }
                        else
                        {
                            Write(log, "could not end RST.Tray pid " + pid + " (" + where + "): " + error);
                            failed++;
                        }
                    }
                    finally
                    {
                        CloseHandle(h);
                    }
                }
            }
            Write(log, "trays: " + ended + " ended, " + failed + " could not be ended, " + left + " left alone");

            // 2. On a full uninstall, anything still using the package's files means a restart is needed.
            if (!fullUninstall)
            {
                Write(log, "not a full uninstall (REMOVE=" + remove + "): in-use check skipped; Windows Installer reports a restart itself if it must replace a file in use (exit " + ExitSuccess + ")");
                return ExitSuccess;
            }
            List<string> users = FilesInUse(root, log);
            if (users.Count > 0)
            {
                foreach (string u in users) Write(log, "still in use: " + u);
                if (GlobalAddAtom("WcaDeferredActionRequiresReboot") == 0)
                    Write(log, "could not set the restart marker (error " + Marshal.GetLastWin32Error() + ")");
                Write(log, "files under " + root + " are still in use: a restart is needed (exit " + ExitRebootRequired + ")");
                return ExitRebootRequired;
            }
            Write(log, "no file under " + root + " is in use (exit " + ExitSuccess + ")");
            return ExitSuccess;
        }
        catch (Exception ex)
        {
            Write(log, "failed: " + ex);
            return ExitFailure;
        }
    }

    /// <summary>This run's own log: RST-Machine-EndTrays-yyyyMMdd-HHmmss-pid.log beside the base path (review N7).</summary>
    private static string RunLog(string basePath)
    {
        string dir = Path.GetTempPath();
        try
        {
            if (basePath.Length > 0) dir = Path.GetDirectoryName(Path.GetFullPath(basePath)) ?? dir;
        }
        catch
        {
            // An unusable base path: %TEMP%.
        }
        return Path.Combine(dir, "RST-Machine-EndTrays-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
            + "-" + Process.GetCurrentProcess().Id + ".log");
    }

    /// <summary>The process's image path (Win32 form), or null.</summary>
    private static string ImagePath(int pid)
    {
        IntPtr h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            return ImagePath(h);
        }
        finally
        {
            CloseHandle(h);
        }
    }

    private static string ImagePath(IntPtr process)
    {
        uint size = 1024;
        StringBuilder name = new StringBuilder((int)size);
        return QueryFullProcessImageName(process, 0, name, ref size) ? name.ToString() : null;
    }

    /// <summary>Ends the process behind the handle (opened with PROCESS_TERMINATE and SYNCHRONIZE) and waits.</summary>
    private static bool End(IntPtr h, out string error)
    {
        error = null;
        if (!TerminateProcess(h, TerminatedExitCode))
        {
            int e = Marshal.GetLastWin32Error();
            // ERROR_ACCESS_DENIED also comes back for a process that is already exiting; the wait below decides.
            error = "TerminateProcess failed with " + e;
        }
        if (WaitForSingleObject(h, WaitMilliseconds) == WaitObject0) return true;
        error = (error == null ? "" : error + "; ") + "still running after " + (WaitMilliseconds / 1000) + " s";
        return false;
    }

    /// <summary>"name (pid N, session S): path" for each process Restart Manager says uses a file under root.</summary>
    private static List<string> FilesInUse(string root, string log)
    {
        List<string> result = new List<string>();
        if (!Directory.Exists(root))
        {
            Write(log, root + " does not exist");
            return result;
        }
        string[] files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
        if (files.Length == 0) return result;

        uint session;
        StringBuilder key = new StringBuilder(64);
        int r = RmStartSession(out session, 0, key);
        if (r != 0)
        {
            Write(log, "Restart Manager could not start (error " + r + "); in-use check skipped");
            return result;
        }
        try
        {
            const int batch = 200;
            for (int i = 0; i < files.Length; i += batch)
            {
                int n = Math.Min(batch, files.Length - i);
                string[] part = new string[n];
                Array.Copy(files, i, part, 0, n);
                r = RmRegisterResources(session, (uint)n, part, 0, null, 0, null);
                if (r != 0)
                {
                    Write(log, "Restart Manager could not register the files (error " + r + "); in-use check skipped");
                    return result;
                }
            }
            RmProcessInfo[] infos = new RmProcessInfo[0];
            uint needed, count = 0, reasons;
            for (int attempt = 0; ; attempt++)
            {
                r = RmGetList(session, out needed, ref count, infos, out reasons);
                if (r == 0) break;
                if (r != ErrorMoreData || attempt > 4)
                {
                    Write(log, "Restart Manager could not list the processes (error " + r + "); in-use check skipped");
                    return result;
                }
                infos = new RmProcessInfo[needed + 4];
                count = (uint)infos.Length;
            }
            int me = Process.GetCurrentProcess().Id;
            for (int i = 0; i < count; i++)
            {
                RmProcessInfo info = infos[i];
                if (info.Process.ProcessId == me) continue;
                result.Add(info.AppName + " (pid " + info.Process.ProcessId + ", session " + info.TSSessionId + "): "
                    + (ImagePath(info.Process.ProcessId) ?? "(path unknown)"));
            }
            Write(log, "Restart Manager checked " + files.Length + " files under " + root + ": " + result.Count + " process(es) use them");
            return result;
        }
        finally
        {
            RmEndSession(session);
        }
    }

    private static void Write(string log, string line)
    {
        try
        {
            File.AppendAllText(log, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + line + Environment.NewLine);
        }
        catch
        {
            // Logging must never stop the uninstall.
        }
    }
}
