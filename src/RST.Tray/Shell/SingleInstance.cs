// rst-c's own (not vendored): one RST tray per user and session (spec #20 §1; F17 WU1). The first tray owns the
// mutex Local\RST.Tray.<UserSid>; a later launch sets the named event Local\RST.Tray.Show.<UserSid>, which the
// first tray waits on (a thread-pool wait, no polling) to show its window, and exits. Decision #29: no pipe.
// "Local\" keeps both names in the signed-in session's namespace; the SID keeps two users of one session
// (runas) apart.

using System.Runtime.InteropServices;
using System.Security.Principal;
using RST.Core.Tray;

namespace RST.Tray.Shell;

internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _show;
    private RegisteredWaitHandle? _wait;

    private SingleInstance(Mutex mutex, EventWaitHandle show)
    {
        _mutex = mutex;
        _show = show;
    }

    /// <summary>The current user's SID (S-1-5-21-…).</summary>
    public static string CurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new InvalidOperationException("The current user has no SID.");
    }

    /// <summary>
    /// Claims the instance for <paramref name="userSid"/>. Null when another tray of this user and session holds
    /// it; then, unless <paramref name="signalFirst"/> is false, that tray is told to show its window.
    /// </summary>
    public static SingleInstance? TryClaim(string userSid, bool signalFirst, Action<string> log)
    {
        // The event first: both launches open the same one, so a launch that loses the mutex race can always
        // signal, and a signal sent before the first tray starts waiting stays set until it does.
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, TrayPaths.ShowEventName(userSid));
        var mutex = new Mutex(false, TrayPaths.MutexName(userSid));
        // WaitOne(0) is one kernel operation: of any number of launches racing here (in any state of their own start),
        // exactly one gets the mutex. Program.Main calls this before any other work (SC-022), so the others exit at once.
        bool owned;
        try
        {
            owned = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // A tray that was killed (or ended without releasing it): Windows hands the abandoned mutex to exactly ONE
            // waiter, which owns it from then on, so this launch is the tray and every later one sees it held.
            log("single instance: the previous tray ended without releasing its mutex; taking over");
            owned = true;
        }

        if (owned) return new SingleInstance(mutex, show);

        if (signalFirst)
        {
            // Let the running tray bring its window to the front: this launch holds the foreground right.
            _ = AllowSetForegroundWindow(-1 /* ASFW_ANY */);
            show.Set();
        }
        mutex.Dispose();
        show.Dispose();
        return null;
    }

    /// <summary>Calls <paramref name="onShow"/> (on a thread-pool thread) each time a later launch signals.</summary>
    public void OnShowRequested(Action onShow)
    {
        ArgumentNullException.ThrowIfNull(onShow);
        _wait?.Unregister(null);
        _wait = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => onShow(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        _wait = null;
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned by this thread (disposed off the UI thread at exit): the process exit releases it.
        }
        _mutex.Dispose();
        _show.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
