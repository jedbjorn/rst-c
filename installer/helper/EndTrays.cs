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
//      [TrayDir]RST.Tray.exe) is ended with TerminateProcess (exit code 1) and waited for (10 s each). The tray holds
//      no unsaved user data; it starts again at the user's next sign-in, or when Revit starts it. Any other
//      RST.Tray process (a per-user tray) is logged and left alone.
//   2. Restart Manager is asked which processes still use any file under the package folder (the second argument,
//      [RstFolder]: C:\Program Files\RST). Typically Revit, with RST's engine loaded. If any does, a restart is needed:
//      it sets the global atom "WcaDeferredActionRequiresReboot", WiX's own marker that its CheckRebootRequired action
//      (scheduled after InstallFinalize, on uninstall too in this package) turns into "restart required", so msiexec
//      returns 3010 instead of 0. That is how util:CloseApplication's RebootPrompt asks for a restart too.
//
// Arguments: the tray's path, the package folder, the log path (Tray.wxs passes [TempFolder]RST-Machine-EndTrays.log:
// as SYSTEM that is C:\Windows\Temp or C:\Windows\SystemTemp). The log is appended to; each run starts with a
// "RST-Machine:" line. Exit codes, for the log only (the custom action is Return="ignore": it never stops an
// uninstall): 0 done; 3010 a restart is needed; 1603 it failed.
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
        string log = args.Length > 2 && args[2].Trim().Length > 0
            ? args[2].Trim()
            : Path.Combine(Path.GetTempPath(), "RST-Machine-EndTrays.log");
        try
        {
            if (args.Length < 2) throw new ArgumentException("usage: RST.EndTrays.exe <tray exe> <package folder> [<log>]");
            string trayExe = Path.GetFullPath(args[0].Trim());
            string root = Path.GetFullPath(args[1].Trim());
            uint mySession;
            ProcessIdToSessionId(Process.GetCurrentProcess().Id, out mySession);
            Write(log, "RST-Machine: ending every tray that runs from " + trayExe + " (any user, any session), then checking " + root + " for files still in use");
            Write(log, "running as " + Environment.UserDomainName + "\\" + Environment.UserName + " in session " + mySession);

            // 1. This package's trays, in every session. Nothing else.
            int ended = 0, failed = 0, left = 0;
            foreach (Process p in Process.GetProcessesByName("RST.Tray"))
            {
                using (p)
                {
                    int pid = p.Id;
                    uint session;
                    string where = ProcessIdToSessionId(pid, out session) ? "session " + session : "session unknown";
                    string image = ImagePath(pid);
                    if (image == null || !string.Equals(image, trayExe, StringComparison.OrdinalIgnoreCase))
                    {
                        Write(log, "left alone: RST.Tray pid " + pid + " (" + where + ") runs from " + (image ?? "(path unknown)") + ", not from this package");
                        left++;
                        continue;
                    }
                    string error;
                    if (End(pid, out error))
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
            }
            Write(log, "trays: " + ended + " ended, " + failed + " could not be ended, " + left + " left alone");

            // 2. Anything still using the package's files means a restart is needed.
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

    /// <summary>The process's image path (Win32 form), or null.</summary>
    private static string ImagePath(int pid)
    {
        IntPtr h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            uint size = 1024;
            StringBuilder name = new StringBuilder((int)size);
            return QueryFullProcessImageName(h, 0, name, ref size) ? name.ToString() : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    private static bool End(int pid, out string error)
    {
        error = null;
        IntPtr h = OpenProcess(ProcessTerminate | Synchronize, false, pid);
        if (h == IntPtr.Zero)
        {
            error = "OpenProcess failed with " + Marshal.GetLastWin32Error();
            return false;
        }
        try
        {
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
        finally
        {
            CloseHandle(h);
        }
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
