// RemoveUserRst.cs: RST-Setup.exe's helper for "Everyone on this PC" (feature #17, spec #20 §7; F17 WU5).
//
// Removes the installing user's OWN RST once RST-Machine.msi has installed (the chain runs RST-Machine first, review
// D1): Revit lets a per-user manifest of the same name win, so a copy left behind would shadow the shared one for
// this user. "Own RST" is every per-user product of
//   AF0331FC-5280-492A-94BD-95EDF108BE74  RST.msi, any version
//   02BE4399-A563-4B15-BD12-ECF5D8486FB3  the retired "RST (Revit 2027 preview)", RST-R27.msi (v1.0.0-v2.2.0), which
//                                         RST.msi's own Upgrade row already treats as RST (review B2)
// Burn (Bundle.wxs, ExePackage RemoveUserRst, PerMachine="no") runs it in the bundle's unelevated process, as that
// user, so it only ever touches that user's own per-user products:
//   - MsiEnumRelatedProducts finds every product with those UpgradeCodes the user can see;
//   - MsiGetProductInfoEx in MSIINSTALLCONTEXT_USERUNMANAGED keeps only this user's per-user ones;
//   - MsiConfigureProductEx(INSTALLSTATE_ABSENT) removes each, silently, with REBOOT=ReallySuppress.
// It removes every version, the bundled one included; Burn's own RST package, after it in the chain, then finds that
// one gone and goes on.
//
// Nothing is removed unless all of it can be (review B2: a failure must never strand the user without RST):
//   1. Revit must be closed in this session (spec §5's rule; review N2). With the full UI it asks "Close Revit,
//      then Retry"; Cancel, or any quieter UI, stops with exit 32 (ERROR_SHARING_VIOLATION).
//   2. Every product to remove must be removable: its cached package (LocalPackage) is present. Otherwise it stops
//      with exit 1612 (ERROR_INSTALL_SOURCE_ABSENT).
// Either stop happens before anything is removed. The package is vital, so the bundle then rolls RST-Machine.msi back:
// the user keeps their own RST. The preview goes first, then RST.msi, so a failure midway still leaves RST.msi in
// place (and RST-Machine.msi is rolled back too).
//
// Its prompts belong to the setup window (SC-029, VM Pass B N2: an unowned box opened behind Revit and setup, so
// setup looked hung). Burn runs the helper from its engine process and the setup window belongs to the bootstrapper
// application, another child of that engine; so the owner is the visible top-level window of the engine or of a
// sibling process (or, failing that, the window titled "RST Setup" in this session). Owned, the box stays above setup
// and keeps setup disabled while it is open. Windows only lets the foreground process take the foreground, so once
// the box is up the helper briefly attaches to the foreground window's input and brings the box to the front. The log
// says which owner it found and whether the box reached the front.
//
// Then (SC-044), once nothing stopped it, it starts the per-machine tray for this user with StartTray.Run
// (installer/helper/StartTray.cs, compiled into this exe), and logs what that did; it never changes the exit code.
//
// Arguments: the log path (Burn passes [WixBundleLog_RemoveUserRst]; empty -> %TEMP%), the bundle's UI level
// ([WixBundleUILevel]: 4 = full UI) and the per-machine tray ([ProgramFiles64Folder]RST\Tray\RST.Tray.exe; empty or
// missing -> no tray is started). Each removal's Windows Installer log goes beside the log. Exit codes: 0 nothing
// to remove or all removed; 3010 removed, a restart is needed; 32 Revit is running; 1602 cancelled; 1612 a product
// cannot be removed; anything else is the Windows Installer error that stopped it.
//
// Built by RST.Setup.wixproj with the .NET Framework 4 compiler that ships in Windows
// (%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe, C# 5), so it needs no SDK or package and runs on the .NET
// Framework 4.8 every supported Windows has; with installer/helper/StartTray.cs and /main:RemoveUserRst. Keep it to C# 5.

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW", SetLastError = true)]
    private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);

    // SC-029: the prompt's owner and foreground.
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    private delegate void TimerProc(IntPtr hwnd, uint msg, UIntPtr id, uint time);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);

    [DllImport("user32.dll")]
    private static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint milliseconds, TimerProc proc);

    [DllImport("user32.dll")]
    private static extern bool KillTimer(IntPtr hwnd, UIntPtr id);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    private const uint GwOwner = 4;
    private const int SwRestore = 9;
    private const uint Th32csSnapProcess = 0x2;
    private const string SetupCaption = "RST Setup";

    // The prompt's state while it is open (one at a time, on this thread).
    private static string promptLog;
    private static int promptTicks;
    private static TimerProc promptTimer;   // kept alive while the timer can fire
    private static EnumWindowsProc promptFinder;
    private static IntPtr promptBox;
    private static int promptError;          // GetLastError when MessageBox returned 0

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
        int result = Remove(log, fullUi);

        // SC-044 (decision #45): RST is now installed for everyone, and this user's own copy is gone: start the
        // per-machine tray for this user (installer/helper/StartTray.cs: hidden, as this user, never elevated; none
        // while a tray runs). Here, not in RST-Machine.msi: removing the user's RST.msi above closes every
        // RST.Tray.exe on this desktop by name (its util:CloseApplication), a tray RST-Machine.msi had started too.
        // Never on a stop or a failure (Burn then rolls RST-Machine back), and it never changes the exit code.
        if ((result == ExitSuccess || result == (int)ErrorSuccessRebootRequired) && args.Length > 2 && args[2].Trim().Length > 0)
            StartTray.Run(args[2].Trim(), delegate(string line) { Write(log, "tray: " + line); });
        return result;
    }

    private static int Remove(string log, bool fullUi)
    {
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
                int answer = Prompt(log,
                    "Revit is running. Close Revit, then click Retry.\n\nYour own RST has not been changed. Click Cancel to stop setup: everything is put back as it was.",
                    MbRetryCancel | MbIconWarning);
                if (answer == 0)
                {
                    // Review N5: MessageBox failed (the setup window may have closed under it); not the user's Cancel.
                    Write(log, "the prompt could not be shown (MessageBox failed with error " + promptError + "; the setup window may have closed); stopping, nothing removed (exit " + ExitCancelled + ")");
                    return ExitCancelled;
                }
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
                        + "Setup will put everything back as it was. Remove it in Settings > Apps (or repair it from its original MSI), then run RST-Setup.exe again.";
                    Write(log, "stopped before removing anything: " + message + " (exit " + ExitNotRemovable + ")");
                    if (fullUi) Prompt(log, message, MbOk | MbIconWarning);
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

    /// <summary>
    /// A message box owned by the setup window and brought to the front (SC-029). Returns MessageBox's answer.
    /// </summary>
    private static int Prompt(string log, string text, uint type)
    {
        IntPtr owner = SetupWindow(log);
        if (owner != IntPtr.Zero && IsIconic(owner)) ShowWindow(owner, SwRestore);
        promptLog = log;
        promptTicks = 0;
        promptBox = IntPtr.Zero;
        promptTimer = OnPromptTimer;
        UIntPtr timer = SetTimer(IntPtr.Zero, UIntPtr.Zero, 100, promptTimer);
        try
        {
            int answer = MessageBox(owner, text, SetupCaption, type | MbSetForeground | MbTopmost);
            promptError = answer == 0 ? Marshal.GetLastWin32Error() : 0;
            Write(log, answer == 0 ? "the prompt failed (MessageBox returned 0, error " + promptError + ")" : "the prompt was answered (" + answer + ")");
            return answer;
        }
        finally
        {
            if (timer != UIntPtr.Zero) KillTimer(IntPtr.Zero, timer);
        }
    }

    /// <summary>
    /// The setup window: the first visible, unowned top-level window (in z-order) of the process that started this
    /// helper (Burn's engine) or of another process it started (the bootstrapper application); else the visible
    /// window titled "RST Setup" in this session; else none.
    /// </summary>
    private static IntPtr SetupWindow(string log)
    {
        int me = Process.GetCurrentProcess().Id;
        Dictionary<int, int> parents = ParentProcessIds();
        int engine;
        if (!parents.TryGetValue(me, out engine)) engine = -1;
        IntPtr found = IntPtr.Zero, byCaption = IntPtr.Zero;
        uint foundPid = 0;
        EnumWindowsProc visit = delegate(IntPtr hwnd, IntPtr unused)
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GwOwner) != IntPtr.Zero) return true;
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if ((int)pid == me) return true;
            int parent;
            if (engine > 0 && ((int)pid == engine || (parents.TryGetValue((int)pid, out parent) && parent == engine)))
            {
                found = hwnd;
                foundPid = pid;
                return false;
            }
            if (byCaption == IntPtr.Zero && WindowText(hwnd) == SetupCaption)
            {
                try
                {
                    if (Process.GetProcessById((int)pid).SessionId == Process.GetCurrentProcess().SessionId) byCaption = hwnd;
                }
                catch
                {
                    // A process that ended while we looked owns no window.
                }
            }
            return true;
        };
        EnumWindows(visit, IntPtr.Zero);
        GC.KeepAlive(visit);
        if (found != IntPtr.Zero)
        {
            Write(log, "the prompt's owner is the setup window 0x" + found.ToString("X") + " '" + WindowText(found) + "' (pid " + foundPid + "; this helper's parent is pid " + engine + ")");
            return found;
        }
        if (byCaption != IntPtr.Zero)
        {
            Write(log, "the prompt's owner is the window titled '" + SetupCaption + "' (0x" + byCaption.ToString("X") + "); none found beside this helper (parent pid " + engine + ")");
            return byCaption;
        }
        Write(log, "no setup window found (parent pid " + engine + "); the prompt has no owner");
        return IntPtr.Zero;
    }

    /// <summary>Runs inside MessageBox's loop: finds the box once it is shown and brings it to the front.</summary>
    private static void OnPromptTimer(IntPtr hwnd, uint msg, UIntPtr id, uint time)
    {
        if (promptBox != IntPtr.Zero || ++promptTicks > 50) return;
        IntPtr box = IntPtr.Zero;
        promptFinder = delegate(IntPtr w, IntPtr unused)
        {
            StringBuilder cls = new StringBuilder(32);
            GetClassName(w, cls, cls.Capacity);
            if (cls.ToString() == "#32770" && IsWindowVisible(w))
            {
                box = w;
                return false;
            }
            return true;
        };
        EnumThreadWindows(GetCurrentThreadId(), promptFinder, IntPtr.Zero);
        if (box == IntPtr.Zero) return;
        promptBox = box;
        IntPtr front = GetForegroundWindow();
        if (front != box)
        {
            uint unusedPid;
            uint frontThread = front == IntPtr.Zero ? 0 : GetWindowThreadProcessId(front, out unusedPid);
            uint thisThread = GetCurrentThreadId();
            bool attached = frontThread != 0 && frontThread != thisThread && AttachThreadInput(thisThread, frontThread, true);
            try
            {
                BringWindowToTop(box);
                SetForegroundWindow(box);
            }
            finally
            {
                if (attached) AttachThreadInput(thisThread, frontThread, false);
            }
        }
        Write(promptLog, GetForegroundWindow() == box
            ? "the prompt is shown in front (0x" + box.ToString("X") + ")"
            : "the prompt is shown (0x" + box.ToString("X") + "), but Windows kept another window in front");
    }

    private static string WindowText(IntPtr hwnd)
    {
        StringBuilder text = new StringBuilder(256);
        GetWindowText(hwnd, text, text.Capacity);
        return text.ToString();
    }

    /// <summary>Every running process's parent process id.</summary>
    private static Dictionary<int, int> ParentProcessIds()
    {
        Dictionary<int, int> parents = new Dictionary<int, int>();
        IntPtr snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return parents;
        try
        {
            ProcessEntry32 entry = new ProcessEntry32();
            entry.Size = (uint)Marshal.SizeOf(typeof(ProcessEntry32));
            for (bool more = Process32First(snapshot, ref entry); more; more = Process32Next(snapshot, ref entry))
                parents[(int)entry.ProcessId] = (int)entry.ParentProcessId;
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return parents;
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
