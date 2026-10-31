// StartTray.cs: starts the RST tray for the signed-in user once an install, repair or upgrade has succeeded (SC-044;
// feature #17, spec #20 §1, decision #45).
//
// Two callers, one rule:
//   - RST.msi and RST-Machine.msi (Tray.wxs, RstStartTray): an immediate custom action after InstallFinalize, from the
//     MSI's Binary table, Return="ignore". Arguments: [TrayDir]RST.Tray.exe and the log's base path
//     ([TempFolder]RST-StartTray.log). Each run writes its own RST-StartTray-<yyyyMMdd-HHmmss>-<pid>.log beside it.
//     Not on an uninstall (REMOVE~="ALL"), and not in RST-Machine.msi run by RST-Setup.exe (RSTFROMSETUP): there the
//     bundle's RemoveUserRst helper starts it, after it has removed the user's own copy.
//   - RST-Setup.exe's RemoveUserRst helper (installer-bundle/helper/RemoveUserRst.cs, compiled with this file): after
//     "Everyone on this PC" has installed RST-Machine.msi and removed the user's own RST, it starts the per-machine tray
//     with Run(). Removing the user's RST.msi closes every RST.Tray.exe on the desktop by name (its
//     util:CloseApplication), so a tray RST-Machine.msi had started would not survive it.
//
// What it does, in order; every "starting none" leaves the tray to the Run value at the user's next sign-in, or to
// Revit (src/RST.Engine/Tray/TrayLauncher.cs):
//   1. The user this process runs as. A service account (SYSTEM, LocalService, NetworkService: a deployment tool such
//      as SCCM or Intune) or session 0 -> starting none. There is no user's desktop to start it on.
//   2. The tray's single-instance mutex Local\RST.Tray.<UserSid> (src/RST.Core/Tray/TrayPaths.cs) exists -> a tray, or
//      RST's self-updater (which holds that mutex while it runs msiexec, then starts the tray itself), already has it:
//      starting none. So a reinstall over a running tray, and a self-update, never start a second tray.
//   3. The user's desktop shell: the Explorer that owns this desktop's shell window (GetShellWindow), or else an
//      explorer.exe in this session, running as this same user (under runas the desktop belongs to someone else: that
//      one is never used), and not elevated (with UAC on; review N2). The tray is created with that Explorer as its
//      parent (PROC_THREAD_ATTRIBUTE_PARENT_PROCESS): Windows then gives it Explorer's token, session and job, so it
//      runs as the user, at Explorer's own (normal, unelevated) rights, outside the installer's process tree, exactly
//      as it does when the Run value starts it at sign-in. The handle opened for that is checked again (user, session,
//      elevation, explorer.exe image), so a process id reused in between is never used (review N2). The tray gets the
//      user's own environment (CreateEnvironmentBlock from Explorer's token) and the interactive desktop.
//   4. No such shell: this process is elevated (TokenElevation, or a full UAC token: msiexec from an elevated prompt,
//      or a per-machine install's elevated engine) and UAC is on -> starting none: a tray started from here would run
//      elevated, and an elevated tray loses its icon to UIPI after Explorer restarts (F17 WU1 review N8). Otherwise
//      (not elevated, or UAC off, EnableLUA=0, where every administrator's process has the full token and there is no
//      UIPI to lose to; review N1) it is started directly, on the interactive desktop (winsta0\default; review N3).
//   5. The start is "<tray>" --startup: hidden in the notification area, no window. This waits (up to 20 s) until that
//      tray holds its mutex, or exits because another launch already holds it. Restart Manager closes a running tray
//      during a repair or upgrade but never restarts it (RST.msi: MSIDISABLERMRESTART=1; RST-Machine.msi: Restart
//      Manager off), so this is the one start after an install (review B1).
// Each run also deletes this helper's own logs older than 7 days beside its log (review N7).
//
// Exit codes, for the MSI log only (the custom action is Return="ignore": it never fails or rolls back an install):
// 0 handled (started, already running, or starting none on purpose; the log says which); 1603 an unexpected failure.
//
// Built with the .NET Framework 4 compiler that ships in Windows (%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe,
// C# 5), by RST.Installer.wixproj (RST.StartTray.exe, the Binary table only: nothing of it is installed) and by
// RST.Setup.wixproj (into RST.RemoveUserRst.exe, /main:RemoveUserRst). No SDK, no package. Keep it to C# 5.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32;

internal static class StartTray
{
    private const int ExitHandled = 0;
    private const int ExitFailure = 1603; // ERROR_INSTALL_FAILURE

    private const string MutexPrefix = @"Local\RST.Tray.";  // TrayPaths.MutexName
    private const string StartupArgument = "--startup";      // TrayPaths.StartupArgument
    private const int MutexWaitMilliseconds = 20000;

    private const uint ProcessCreateProcess = 0x0080, ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008, TokenDuplicate = 0x0002;
    private const int TokenUserClass = 1, TokenSessionIdClass = 12, TokenElevationTypeClass = 18, TokenElevationClass = 20, TokenIntegrityLevelClass = 25;
    private const int TokenElevationTypeDefault = 1, TokenElevationTypeFull = 2, TokenElevationTypeLimited = 3;
    private const uint ExtendedStartupInfoPresent = 0x00080000, CreateUnicodeEnvironment = 0x00000400;
    private static readonly IntPtr ProcThreadAttributeParentProcess = new IntPtr(0x00020000);
    private const uint WaitObject0 = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW")]
    private static extern bool CreateProcess(string applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory, ref StartupInfoEx startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previousValue, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    /// <summary>A process's token, as far as it could be read.</summary>
    private sealed class TokenFacts
    {
        public string Sid;
        public int Session = -1;
        public int ElevationType;
        public bool Elevated;
        public string Integrity;

        public override string ToString()
        {
            string type = ElevationType == TokenElevationTypeFull ? "full (elevated under UAC)"
                : ElevationType == TokenElevationTypeLimited ? "limited (UAC filtered)"
                : ElevationType == TokenElevationTypeDefault ? "default (no split token)" : "unknown";
            return "user " + (Sid ?? "(unknown)") + ", session " + Session + ", elevated " + (Elevated ? "yes" : "no")
                + ", token " + type + ", integrity " + (Integrity ?? "(unknown)");
        }
    }

    private static int Main(string[] args)
    {
        string log = RunLog(args.Length > 1 ? args[1].Trim() : "");
        DeleteOldLogs(log);
        if (args.Length < 1 || args[0].Trim().Length == 0)
        {
            Write(log, "usage: RST.StartTray.exe <tray exe> [<log>]");
            return ExitFailure;
        }
        return Run(args[0].Trim(), delegate(string line) { Write(log, line); });
    }

    /// <summary>
    /// Starts the tray at <paramref name="trayExe"/> with --startup for the user this process runs as, unless the rules
    /// in the header say to start none. Never throws; every decision is one line in <paramref name="log"/>.
    /// </summary>
    internal static int Run(string trayExe, Action<string> log)
    {
        try
        {
            trayExe = Path.GetFullPath(trayExe);
            TokenFacts me = Facts(GetCurrentProcess());
            log("RST: start the tray " + trayExe + " for the user who installed; this process (pid " + Process.GetCurrentProcess().Id + "): " + me);
            if (me.Sid == null)
            {
                log("this process's user could not be read: starting none");
                return ExitHandled;
            }
            if (me.Sid == "S-1-5-18" || me.Sid == "S-1-5-19" || me.Sid == "S-1-5-20")
            {
                log("runs as a service account (" + me.Sid + "; e.g. a deployment tool running msiexec as SYSTEM): starting none; each user's tray starts at their sign-in");
                return ExitHandled;
            }
            if (me.Session <= 0)
            {
                log("runs in session " + me.Session + ", which has no user's desktop: starting none; the tray starts at the next sign-in");
                return ExitHandled;
            }
            if (!File.Exists(trayExe))
            {
                log(trayExe + " does not exist: starting none");
                return ExitHandled;
            }
            string mutex = MutexPrefix + me.Sid;
            if (MutexExists(mutex, log))
            {
                log("a tray (or RST's updater) already holds " + mutex + ": starting none");
                return ExitHandled;
            }

            // Review N1: a direct start only from a process known not to be elevated at all (an unreadable token counts as
            // elevated), unless UAC is off (EnableLUA=0),
            // where every administrator's process, Explorer included, has the full token.
            bool uacOff = UacOff();
            bool mayStartDirectly = uacOff || (me.ElevationType != 0 && !me.Elevated && me.ElevationType != TokenElevationTypeFull);
            log("UAC " + (uacOff ? "off (EnableLUA=0)" : "on") + "; a direct start from this process is " + (mayStartDirectly ? "allowed" : "refused (it would run elevated)"));

            int shellPid = FindShell(me, uacOff, log);
            ProcessInformation pi;
            string route;
            if (shellPid > 0)
            {
                string error;
                if (CreateTray(trayExe, shellPid, me, uacOff, out pi, out error))
                {
                    route = "through the desktop shell (explorer.exe pid " + shellPid + ", as its child, with its token)";
                }
                else if (!mayStartDirectly)
                {
                    log("could not start it through the desktop shell (" + error + "), and this process is elevated: starting none (it would run elevated); the tray starts at the next sign-in");
                    return ExitHandled;
                }
                else if (CreateTray(trayExe, 0, me, uacOff, out pi, out error))
                {
                    route = "directly (this process is not elevated; the desktop shell route failed: " + error + ")";
                }
                else
                {
                    log("could not start " + trayExe + ": " + error);
                    return ExitFailure;
                }
            }
            else if (!mayStartDirectly)
            {
                log("this process is elevated and no unelevated Explorer of this user runs in session " + me.Session
                    + ": starting none (a tray started from here would run elevated); the tray starts at the next sign-in");
                return ExitHandled;
            }
            else
            {
                string error;
                if (!CreateTray(trayExe, 0, me, uacOff, out pi, out error))
                {
                    log("could not start " + trayExe + ": " + error);
                    return ExitFailure;
                }
                route = "directly (no Explorer of this user in this session; this process " + (uacOff ? "runs with UAC off" : "is not elevated") + ")";
            }

            try
            {
                log("started RST.Tray pid " + pi.dwProcessId + " " + route + ": \"" + trayExe + "\" " + StartupArgument + "; it runs as " + Facts(pi.hProcess));
                Stopwatch watch = Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < MutexWaitMilliseconds)
                {
                    if (WaitForSingleObject(pi.hProcess, 100) == WaitObject0)
                    {
                        uint code;
                        GetExitCodeProcess(pi.hProcess, out code);
                        log("RST.Tray pid " + pi.dwProcessId + " exited (code " + code + ") after " + watch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)
                            + " s" + (code == 0 ? ": another launch already held the tray's mutex" : ""));
                        return ExitHandled;
                    }
                    if (MutexExists(mutex, null))
                    {
                        log("a tray holds " + mutex + " " + watch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s after the start (pid " + pi.dwProcessId + " is running)");
                        return ExitHandled;
                    }
                }
                log("RST.Tray pid " + pi.dwProcessId + " runs, but " + mutex + " did not appear within " + (MutexWaitMilliseconds / 1000) + " s; left running");
                return ExitHandled;
            }
            finally
            {
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
            }
        }
        catch (Exception ex)
        {
            log("failed: " + ex);
            return ExitFailure;
        }
    }

    /// <summary>True when the named mutex exists, including one this process may not open (an elevated tray's).</summary>
    private static bool MutexExists(string name, Action<string> log)
    {
        try
        {
            Mutex m;
            if (!Mutex.TryOpenExisting(name, MutexRights.Synchronize, out m)) return false;
            m.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            if (log != null) log(name + " exists but cannot be opened by this process (a tray running with other rights holds it)");
            return true;
        }
    }

    /// <summary>The pid of this user's desktop shell in this session, or 0.</summary>
    private static int FindShell(TokenFacts me, bool uacOff, Action<string> log)
    {
        int pid = 0;
        IntPtr shell = GetShellWindow();
        if (shell != IntPtr.Zero) GetWindowThreadProcessId(shell, out pid);
        if (pid > 0)
        {
            TokenFacts f = Facts(pid);
            if (IsUsersShell(f, me, uacOff))
            {
                log("desktop shell: pid " + pid + " (this desktop's shell window): " + f);
                return pid;
            }
            log("this desktop's shell window belongs to pid " + pid + " (" + f + "): not this user's unelevated shell in this session");
        }
        Process[] explorers = Process.GetProcessesByName("explorer");
        try
        {
            foreach (Process p in explorers)
            {
                if (p.SessionId != me.Session) continue;
                TokenFacts f = Facts(p.Id);
                if (IsUsersShell(f, me, uacOff))
                {
                    log("desktop shell: explorer.exe pid " + p.Id + " in session " + me.Session + ": " + f);
                    return p.Id;
                }
                log("explorer.exe pid " + p.Id + " in session " + me.Session + " runs as another user, elevated, or could not be read (" + f + "): not used");
            }
        }
        finally
        {
            foreach (Process p in explorers) p.Dispose();
        }
        log("no Explorer of this user runs in session " + me.Session);
        return 0;
    }

    /// <summary>
    /// This user's shell in this session, and not elevated (review N2: an elevated Explorer would give the tray its
    /// elevated token), unless UAC is off, where every administrator's Explorer has the full token.
    /// </summary>
    private static bool IsUsersShell(TokenFacts shell, TokenFacts me, bool uacOff)
    {
        return shell.Sid != null && string.Equals(shell.Sid, me.Sid, StringComparison.OrdinalIgnoreCase) && shell.Session == me.Session
            && (uacOff || (shell.ElevationType != 0 && !shell.Elevated && shell.ElevationType != TokenElevationTypeFull));
    }

    /// <summary>UAC is off: EnableLUA is 0 (HKLM\...\Policies\System). Missing or unreadable counts as on.</summary>
    private static bool UacOff()
    {
        try
        {
            object v = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA", null);
            return v is int && (int)v == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The process's image path (Win32 form), or null.</summary>
    private static string ImagePath(IntPtr process)
    {
        uint size = 1024;
        StringBuilder name = new StringBuilder((int)size);
        return QueryFullProcessImageName(process, 0, name, ref size) ? name.ToString() : null;
    }

    /// <summary>Starts "trayExe" --startup, with the given process (Explorer) as its parent, or this process when 0.</summary>
    private static bool CreateTray(string trayExe, int parentPid, TokenFacts me, bool uacOff, out ProcessInformation pi, out string error)
    {
        pi = new ProcessInformation();
        error = null;
        IntPtr parent = IntPtr.Zero, parentToken = IntPtr.Zero, environment = IntPtr.Zero, list = IntPtr.Zero, parentBox = IntPtr.Zero;
        bool listReady = false;
        try
        {
            StartupInfoEx si = new StartupInfoEx();
            uint flags = 0;
            if (parentPid > 0)
            {
                parent = OpenProcess(ProcessCreateProcess | ProcessQueryLimitedInformation, false, parentPid);
                if (parent == IntPtr.Zero)
                {
                    error = "OpenProcess(explorer.exe pid " + parentPid + ") failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                    return false;
                }
                // Review N2: the pid was found earlier; check the process behind this handle again, so a pid reused in
                // between is never the tray's parent.
                TokenFacts now = Facts(parent);
                string image = ImagePath(parent);
                if (!IsUsersShell(now, me, uacOff) || image == null || !string.Equals(Path.GetFileName(image), "explorer.exe", StringComparison.OrdinalIgnoreCase))
                {
                    error = "pid " + parentPid + " is no longer this user's unelevated explorer.exe (" + (image ?? "(path unknown)") + "; " + now + ")";
                    return false;
                }
                // The user's own environment, from Explorer's token: not the installer's.
                if (OpenProcessToken(parent, TokenQuery | TokenDuplicate, out parentToken) && CreateEnvironmentBlock(out environment, parentToken, false))
                    flags |= CreateUnicodeEnvironment;
                else
                    environment = IntPtr.Zero;

                IntPtr size = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                list = Marshal.AllocHGlobal(size);
                if (!InitializeProcThreadAttributeList(list, 1, 0, ref size))
                {
                    error = "InitializeProcThreadAttributeList failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                    return false;
                }
                listReady = true;
                parentBox = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(parentBox, parent);
                if (!UpdateProcThreadAttribute(list, 0, ProcThreadAttributeParentProcess, parentBox, new IntPtr(IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                {
                    error = "UpdateProcThreadAttribute(PARENT_PROCESS) failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                    return false;
                }
                si.lpAttributeList = list;
                si.StartupInfo.lpDesktop = @"winsta0\default";
                flags |= ExtendedStartupInfoPresent;
                si.StartupInfo.cb = Marshal.SizeOf(typeof(StartupInfoEx));
            }
            else
            {
                // Review N3: the interactive desktop here too, not whatever this process was started on.
                si.StartupInfo.lpDesktop = @"winsta0\default";
                si.StartupInfo.cb = Marshal.SizeOf(typeof(StartupInfo));
            }
            StringBuilder commandLine = new StringBuilder("\"" + trayExe + "\" " + StartupArgument);
            if (!CreateProcess(trayExe, commandLine, IntPtr.Zero, IntPtr.Zero, false, flags, environment, Path.GetDirectoryName(trayExe), ref si, out pi))
            {
                error = "CreateProcess failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }
            return true;
        }
        finally
        {
            if (listReady) DeleteProcThreadAttributeList(list);
            if (list != IntPtr.Zero) Marshal.FreeHGlobal(list);
            if (parentBox != IntPtr.Zero) Marshal.FreeHGlobal(parentBox);
            if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
            if (parentToken != IntPtr.Zero) CloseHandle(parentToken);
            if (parent != IntPtr.Zero) CloseHandle(parent);
        }
    }

    private static TokenFacts Facts(int pid)
    {
        IntPtr h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return new TokenFacts();
        try
        {
            return Facts(h);
        }
        finally
        {
            CloseHandle(h);
        }
    }

    private static TokenFacts Facts(IntPtr process)
    {
        TokenFacts f = new TokenFacts();
        IntPtr token;
        if (!OpenProcessToken(process, TokenQuery, out token)) return f;
        try
        {
            IntPtr user = Info(token, TokenUserClass);
            if (user != IntPtr.Zero)
            {
                try { f.Sid = new SecurityIdentifier(Marshal.ReadIntPtr(user)).Value; }
                finally { Marshal.FreeHGlobal(user); }
            }
            IntPtr session = Info(token, TokenSessionIdClass);
            if (session != IntPtr.Zero)
            {
                try { f.Session = Marshal.ReadInt32(session); }
                finally { Marshal.FreeHGlobal(session); }
            }
            IntPtr type = Info(token, TokenElevationTypeClass);
            if (type != IntPtr.Zero)
            {
                try { f.ElevationType = Marshal.ReadInt32(type); }
                finally { Marshal.FreeHGlobal(type); }
            }
            IntPtr elevation = Info(token, TokenElevationClass);
            if (elevation != IntPtr.Zero)
            {
                try { f.Elevated = Marshal.ReadInt32(elevation) != 0; }
                finally { Marshal.FreeHGlobal(elevation); }
            }
            IntPtr label = Info(token, TokenIntegrityLevelClass);
            if (label != IntPtr.Zero)
            {
                try
                {
                    string sid = new SecurityIdentifier(Marshal.ReadIntPtr(label)).Value;
                    string rid = sid.Substring(sid.LastIndexOf('-') + 1);
                    f.Integrity = rid == "4096" ? "low" : rid == "8192" ? "medium" : rid == "8448" ? "medium-plus" : rid == "12288" ? "high" : rid == "16384" ? "system" : sid;
                }
                finally { Marshal.FreeHGlobal(label); }
            }
        }
        finally
        {
            CloseHandle(token);
        }
        return f;
    }

    /// <summary>One token information class, in memory the caller frees (Marshal.FreeHGlobal), or IntPtr.Zero.</summary>
    private static IntPtr Info(IntPtr token, int infoClass)
    {
        int length;
        GetTokenInformation(token, infoClass, IntPtr.Zero, 0, out length);
        if (length <= 0) return IntPtr.Zero;
        IntPtr buffer = Marshal.AllocHGlobal(length);
        if (GetTokenInformation(token, infoClass, buffer, length, out length)) return buffer;
        Marshal.FreeHGlobal(buffer);
        return IntPtr.Zero;
    }

    /// <summary>This helper's own logs older than 7 days, beside this run's (review N7). Never throws.</summary>
    private static void DeleteOldLogs(string log)
    {
        try
        {
            DateTime cutoff = DateTime.UtcNow.AddDays(-7);
            foreach (string f in Directory.GetFiles(Path.GetDirectoryName(log), "RST-StartTray-*-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f);
                }
                catch
                {
                    // In use or not ours to delete: left alone.
                }
            }
        }
        catch
        {
            // Clean-up must never stop the install.
        }
    }

    /// <summary>This run's own log: RST-StartTray-yyyyMMdd-HHmmss-pid.log beside the base path.</summary>
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
        return Path.Combine(dir, "RST-StartTray-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
            + "-" + Process.GetCurrentProcess().Id + ".log");
    }

    private static void Write(string log, string line)
    {
        try
        {
            File.AppendAllText(log, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + line + Environment.NewLine);
        }
        catch
        {
            // Logging must never stop the install.
        }
    }
}
