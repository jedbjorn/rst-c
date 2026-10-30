// rst-c's own (not vendored): how the Revit add-in starts the RST tray so that it outlives Revit (spec #20 §2; F17
// WU2 review N1). Revit may run inside a Windows job object: a deployment launcher, an app-virtualisation wrapper,
// a sandbox or Task Scheduler. A plain start puts the tray in that job too, and a job that ends its processes when
// it closes (JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE) would end the tray with Revit.
//   - Revit is in no job: the shell starts it (ShellExecuteEx: no handles inherited).
//   - Revit is in a job: CreateProcess with CREATE_BREAKAWAY_FROM_JOB, which the job allows only with
//     JOB_OBJECT_LIMIT_BREAKAWAY_OK, and with the user's own default environment (CreateEnvironmentBlock), not
//     Revit's, which an add-in may have changed.
//   - The job refuses a breakaway: the desktop shell (Explorer) starts it, through its own ShellExecute
//     (IShellDispatch2), so the tray is Explorer's child: outside Revit's process tree and job, at Explorer's normal
//     integrity, with --startup kept (explorer.exe's command line cannot pass arguments).
//   - No desktop shell either: the shell starts it inside the job, as before; the log line says so.
// Only the BCL here (RuntimeTests loads this type into a process it puts in a job).

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace RST.Engine.Tray;

internal static class TrayProcessStart
{
    /// <summary>How the tray was started, and its process id when known (the desktop shell does not say).</summary>
    internal sealed record Started(string Route, int? ProcessId);

    public static Started Start(string path, string arguments)
    {
        if (!InJob()) return new("the shell", ShellStart(path, arguments));

        string breakaway;
        try
        {
            return new("a breakaway from Revit's job", CreateBreakaway(path, arguments));
        }
        catch (Exception ex)
        {
            breakaway = ex.Message;
        }

        string? desktop = DesktopShellStart(path, arguments);
        if (desktop is null) return new("the desktop shell, outside Revit's job (the job allows no breakaway: " + breakaway + ")", null);

        return new("the shell, inside Revit's job: no breakaway (" + breakaway + ") and no desktop shell (" + desktop + ")",
            ShellStart(path, arguments));
    }

    /// <summary>This process is in a job object.</summary>
    internal static bool InJob() => IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out var inJob) && inJob;

    private static int? ShellStart(string path, string arguments)
    {
        var info = new ProcessStartInfo(path)
        {
            Arguments = arguments,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(path) ?? "",
        };
        using var started = Process.Start(info);
        return started?.Id;
    }

    private const uint CreateBreakawayFromJob = 0x01000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateDefaultErrorMode = 0x04000000;

    /// <summary>CreateProcess outside the job, with the user's default environment. Throws when the job refuses.</summary>
    private static int CreateBreakaway(string path, string arguments)
    {
        var env = IntPtr.Zero;
        try
        {
            using (var identity = WindowsIdentity.GetCurrent())
                if (!CreateEnvironmentBlock(out env, identity.Token, false)) env = IntPtr.Zero;
            var flags = CreateBreakawayFromJob | CreateDefaultErrorMode | (env != IntPtr.Zero ? CreateUnicodeEnvironment : 0);
            var startup = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
            var commandLine = new StringBuilder("\"" + path + "\" " + arguments);
            if (!CreateProcessW(path, commandLine, IntPtr.Zero, IntPtr.Zero, false, flags, env, Path.GetDirectoryName(path), ref startup, out var info))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            CloseHandle(info.hThread);
            CloseHandle(info.hProcess);
            return info.dwProcessId;
        }
        finally
        {
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
        }
    }

    /// <summary>
    /// Asks the desktop's Explorer window to ShellExecute the tray (Raymond Chen's "launch from the desktop shell").
    /// Null when started; else why not. Runs on its own STA thread, bounded to 10 s.
    /// </summary>
    internal static string? DesktopShellStart(string path, string arguments)
    {
        string? failure = "timed out";
        var thread = new Thread(() =>
        {
            try
            {
                failure = DesktopShellStartSta(path, arguments);
            }
            catch (Exception ex)
            {
                failure = ex.GetType().Name + ": " + ex.Message;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return thread.Join(TimeSpan.FromSeconds(10)) ? failure : "timed out";
    }

    private static readonly Guid ShellWindowsClsid = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid TopLevelBrowserSid = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid ShellBrowserIid = new("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid DispatchIid = new("00020400-0000-0000-C000-000000000046");

    private static string? DesktopShellStartSta(string path, string arguments)
    {
        var type = Type.GetTypeFromCLSID(ShellWindowsClsid, throwOnError: false);
        if (type is null || Activator.CreateInstance(type) is not IShellWindows windows) return "no ShellWindows";
        object location = 0; // CSIDL_DESKTOP
        object root = null!;
        var hr = windows.FindWindowSW(ref location, ref root, 8 /* SWC_DESKTOP */, out _, 1 /* SWFO_NEEDDISPATCH */, out var desktop);
        if (hr != 0 || desktop is not IComServiceProvider services) return "no desktop window (0x" + hr.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ")";
        var sid = TopLevelBrowserSid;
        var iid = ShellBrowserIid;
        hr = services.QueryService(ref sid, ref iid, out var browserObject);
        if (hr != 0 || browserObject is not IShellBrowser browser) return "no shell browser";
        hr = browser.QueryActiveShellView(out var view);
        if (hr != 0 || view is null) return "no shell view";
        var dispatchIid = DispatchIid;
        hr = view.GetItemObject(0 /* SVGIO_BACKGROUND */, ref dispatchIid, out var folderView);
        if (hr != 0 || folderView is null) return "no folder view";
        var application = folderView.GetType().InvokeMember("Application", BindingFlags.GetProperty, null, folderView, null);
        if (application is null) return "no shell application";
        application.GetType().InvokeMember("ShellExecute", BindingFlags.InvokeMethod, null, application,
            [path, arguments, Path.GetDirectoryName(path) ?? "", "open", 1 /* SW_SHOWNORMAL */]);
        return null;
    }

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        [PreserveSig] int GetCount();

        [PreserveSig] int Item();

        [PreserveSig] int NewEnum();

        [PreserveSig] int Register();

        [PreserveSig] int RegisterPending();

        [PreserveSig] int Revoke();

        [PreserveSig] int OnNavigate();

        [PreserveSig] int OnActivated();

        [PreserveSig]
        int FindWindowSW(ref object pvarLoc, ref object pvarLocRoot, int swClass, out int phwnd, int swfwOptions,
            [MarshalAs(UnmanagedType.IDispatch)] out object? ppdispOut);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IComServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid guidService, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? ppvObject);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        [PreserveSig] int GetWindow();

        [PreserveSig] int ContextSensitiveHelp();

        [PreserveSig] int InsertMenusSB();

        [PreserveSig] int SetMenuSB();

        [PreserveSig] int RemoveMenusSB();

        [PreserveSig] int SetStatusTextSB();

        [PreserveSig] int EnableModelessSB();

        [PreserveSig] int TranslateAcceleratorSB();

        [PreserveSig] int BrowseObject();

        [PreserveSig] int GetViewStateStream();

        [PreserveSig] int GetControlWindow();

        [PreserveSig] int SendControlMsg();

        [PreserveSig] int QueryActiveShellView([MarshalAs(UnmanagedType.Interface)] out IShellView? view);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        [PreserveSig] int GetWindow();

        [PreserveSig] int ContextSensitiveHelp();

        [PreserveSig] int TranslateAccelerator();

        [PreserveSig] int EnableModeless();

        [PreserveSig] int UIActivate();

        [PreserveSig] int Refresh();

        [PreserveSig] int CreateViewWindow();

        [PreserveSig] int DestroyViewWindow();

        [PreserveSig] int GetCurrentInfo();

        [PreserveSig] int AddPropertySheetPages();

        [PreserveSig] int SaveViewState();

        [PreserveSig] int SelectItem();

        [PreserveSig] int GetItemObject(uint uItem, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? ppv);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string applicationName, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, IntPtr environment,
        string? currentDirectory, ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>The pseudo handle of this process (never closed).</summary>
    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("userenv.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);
}
