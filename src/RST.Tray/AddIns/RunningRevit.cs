// rst-c's own (not vendored): which Revit years are open in this Windows session (spec #20 §3, "Revit <year> is
// open: restart it to see the change"; F17 WU2). Installation is advisory-only (decision #73);
// this detection is only for the Add-ins page's per-year restart notes.
//
// The image path is read with PROCESS_QUERY_LIMITED_INFORMATION and QueryFullProcessImageName, which a
// non-elevated tray is granted on an elevated Revit too (Process.MainModule needs PROCESS_VM_READ, which it is not;
// review N5). The year comes from the folder (TrayAddinService.RevitYearOf), else from Revit.exe's file version.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using RST.Core.Activation;
using RST.Core.Tray;
using RST.Core.Logging;

namespace RST.Tray.AddIns;

internal static class RunningRevit
{
    /// <summary>The Revit years (2025+) open in this session, oldest first. Best effort: none when unreadable.</summary>
    public static IReadOnlyList<int> Years(Func<IReadOnlyList<RevitYear>>? installed = null)
    {
        var paths = ImagePaths("Revit");
        if (paths.Count == 0) return [];
        IReadOnlyList<RevitYear> years;
        try
        {
            years = (installed ?? TrayAddinService.InstalledRevit)();
        }
        catch (Exception)
        {
            years = [];
        }
        var found = new SortedSet<int>();
        foreach (var path in paths)
        {
            int? major = null;
            try
            {
                major = FileVersionInfo.GetVersionInfo(path).FileMajorPart;
            }
            catch (Exception)
            {
                // The folder alone may still say which year it is.
            }
            if (TrayAddinService.RevitYearOf(path, major, years) is { } year) found.Add(year);
        }
        return [.. found];
    }

    /// <summary>The image paths of the processes named <paramref name="name"/> in this session.</summary>
    internal static IReadOnlyList<string> ImagePaths(string name)
    {
        var result = new List<string>();
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(name);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "add-ins: running {Name} not readable", name);
            return result;
        }
        int session;
        using (var self = Process.GetCurrentProcess()) session = self.SessionId;
        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != session) continue;
                    if (ImagePath(process.Id) is { } path) result.Add(path);
                }
                catch (Exception ex)
                {
                    // Exited meanwhile.
                    Log.Debug(ex, "add-ins: a {Name} process was not readable", name);
                }
            }
        }
        return result;
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>The full image path of process <paramref name="pid"/>, with the least access Windows offers.</summary>
    internal static string? ImagePath(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
