// rst-c's own (not vendored): registers the RST tray with Windows' Restart Manager (spec #20 §1; review N1). When
// Windows Installer closes a running tray to replace its files (a full-UI repair through the Files in Use dialog,
// an RST-Setup.exe upgrade, a silent install with Restart Manager on), it then restarts registered apps; this
// brings the tray back, hidden (--startup). Crash, hang and reboot restarts are excluded, so a failing tray never
// restart-loops. util:CloseApplication's WM_CLOSE path is unchanged.

using System.Runtime.InteropServices;
using RST.Core.Tray;

namespace RST.Tray.Shell;

internal static class AppRestart
{
    private const int RestartNoCrash = 0x1;
    private const int RestartNoHang = 0x2;
    private const int RestartNoReboot = 0x8;

    /// <summary>The flags: restart only after an update (Restart Manager), never after a crash, hang or reboot.</summary>
    public const int Flags = RestartNoCrash | RestartNoHang | RestartNoReboot;

    /// <summary>The command line Restart Manager passes back (the exe path is added by Windows).</summary>
    public const string CommandLine = TrayPaths.StartupArgument;

    /// <summary>Registers once per process. Never throws; a failure is logged.</summary>
    public static void Register(Action<string> log)
    {
        try
        {
            var hr = RegisterApplicationRestart(CommandLine, Flags);
            if (hr != 0) log($"restart registration refused (HRESULT 0x{hr:X8}); after an installer closes the tray it returns at the next sign-in");
        }
        catch (Exception ex)
        {
            log("restart registration failed: " + ex.Message);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string commandLine, int flags);
}
