// rst-c's own (not vendored): RST.Tray.exe's entry point (spec #20 §1; F17 WU1).
//
//   RST.Tray.exe              open the window (or, if a tray already runs for this user and session, ask it to
//                             show its window, and exit)
//   RST.Tray.exe --startup    start hidden in the notification area (the HKMU Run value RST.msi writes); if a tray
//                             already runs, exit quietly
//
// Exit code 0 in every normal case, including a second launch.

using System.Windows;
using System.Windows.Threading;
using RST.Core.Tray;
using RST.Tray.Shell;
using Serilog;

namespace RST.Tray;

internal static class Program
{
    [STAThread]
    public static int Main(string[] argv)
    {
        var args = TrayArguments.Parse(argv);
        TrayLog.Configure();
        try
        {
            string sid;
            SingleInstance? instance;
            try
            {
                sid = SingleInstance.CurrentUserSid();
                instance = SingleInstance.TryClaim(sid, signalFirst: !args.StartHidden, log: m => Log.Warning("{Message}", m));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "single-instance check failed; not starting");
                return 1;
            }

            if (instance is null)
            {
                Log.Information("RST tray already running for this user (pid {Pid} exits){Signal}", Environment.ProcessId,
                    args.StartHidden ? "" : "; asked it to show its window");
                return 0;
            }

            using (instance)
            {
                foreach (var unknown in args.Unknown) Log.Warning("ignored argument {Argument}", unknown);
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.DispatcherUnhandledException += OnUnhandled;
                using var host = new TrayHost(args, instance);
                app.Startup += (_, _) => host.Start();
                var code = app.Run();
                Log.Information("RST tray exited ({Code})", code);
                return code;
            }
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "RST tray failed");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>A UI exception is logged and the tray keeps running: it must never vanish from under the user.</summary>
    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "unhandled UI exception");
        e.Handled = true;
    }
}
