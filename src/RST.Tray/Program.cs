// rst-c's own (not vendored): RST.Tray.exe's entry point (spec #20 §1; F17 WU1).
//
//   RST.Tray.exe              open the window (or, if a tray already runs for this user and session, ask it to
//                             show its window, and exit)
//   RST.Tray.exe --startup    start hidden in the notification area (the HKMU Run value RST.msi writes); if a tray
//                             already runs, exit quietly
//   RST.TrayUpdater.exe --apply <msi> --wait-pid <pid>
//                             the updater copy (F17 WU4): wait for that tray to exit, install RST.msi, start the
//                             new tray; refused unless run as the copy in %LocalAppData%\RST\Tray\updater\
//
// Exit code 0 in every normal case, including a second launch.
//
// SC-022: the single-instance mutex is the first thing a tray takes, before its log, settings, theme or WPF. Two
// launches that meet (Restart Manager's restart and the updater's start, Revit's start and a sign-in) decide which
// one is the tray before either does anything else, and the other exits at once; the time a launch spends as a
// second RST.Tray.exe process is only the runtime's own start.

using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;
using RST.Core.Tray;
using RST.Tray.Shell;
using RST.Core.Logging;

namespace RST.Tray;

internal static class Program
{
    [STAThread]
    public static int Main(string[] argv)
    {
        var args = TrayArguments.Parse(argv);

        // The updater copy (F17 WU4): installs RST.msi and exits. It takes the tray's mutex its own way (TrayInstanceClaim,
        // while files are replaced), shows no window and never registers with Restart Manager.
        if (args.IsApply) return RunUpdater(args);

        // The single instance, before any other work (SC-022). A launch that finds a tray exits right after logging so.
        var notes = new List<string>();
        SingleInstance? instance = null;
        Exception? claimFailure = null;
        try
        {
            instance = SingleInstance.TryClaim(SingleInstance.CurrentUserSid(), signalFirst: !args.StartHidden, log: notes.Add);
        }
        catch (Exception ex)
        {
            claimFailure = ex;
        }

        using (instance)
        {
            TrayLog.Configure();
            try
            {
                foreach (var note in notes) Log.Warning("{Message}", note);
                if (claimFailure is not null)
                {
                    Log.Error(claimFailure, "single-instance check failed; not starting");
                    return 1;
                }
                if (instance is null)
                {
                    Log.Information("RST tray already running for this user (pid {Pid} exits){Signal}", Environment.ProcessId,
                        args.StartHidden ? "" : "; asked it to show its window");
                    return 0;
                }

                foreach (var unknown in args.Unknown) Log.Warning("ignored argument {Argument}", unknown);
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.DispatcherUnhandledException += OnUnhandled;
                using var host = new TrayHost(args, instance);
                app.Startup += (_, _) => host.Start();
                var code = app.Run();
                Log.Information("RST tray exited ({Code}; {Reason})", code, host.ExitReason);
                return code;
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
    }

    private static int RunUpdater(TrayArguments args)
    {
        TrayLog.Configure();
        try
        {
            return RST.Tray.Updates.Updater.Run(args);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "RST updater failed");
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
