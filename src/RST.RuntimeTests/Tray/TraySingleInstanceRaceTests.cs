// rst-c's own (not vendored): SC-022, the tray's single instance when launches meet (spec #20 §1; F17 WU1, WU4).
// Under any interleaving at most one RST tray stays running for a user and session:
//
//   - SingleInstanceRaceTests (every test-runtime run): many threads race SingleInstance.TryClaim for one fresh SID,
//     round after round; each round's owner either releases the mutex or ends owning it (a killed tray: abandoned).
//     Exactly one owner every round, and an abandoned mutex goes to exactly one of the next round's launches.
//   - TraySingleInstanceRaceTests (run by installer/ci-tray-selfupdate.ps1 against the INSTALLED tray): two real
//     RST.Tray.exe started at the same instant, round after round, in every pairing of --startup (Restart Manager's
//     restart, the Run value) and no switch (the updater's start, a double-click). Exactly one survives each round,
//     the other exits 0 having found it, and the survivor holds the mutex. When a launch with no switch (the
//     updater's) loses to a --startup tray (Restart Manager's), that tray shows its window. Every fifth survivor is
//     killed while this host keeps a handle to the mutex open (review B1: without one the mutex is simply gone), so
//     the next round meets an abandoned mutex: exactly its survivor logs "taking over", and no other round's does.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using RST.Core.Tray;
using RST.Tray.Shell;
using Xunit;
using Xunit.Abstractions;

namespace RST.RuntimeTests.Tray;

public class SingleInstanceRaceTests
{
    [Fact]
    public void Of_many_launches_racing_for_the_mutex_exactly_one_owns_it_every_round_released_or_abandoned()
    {
        const int Rounds = 200, Racers = 8;
        var sid = "S-1-5-21-0-0-3-" + Random.Shared.Next(100000, 999999); // a test-only name, never a real user's tray
        var abandoned = new List<SingleInstance>();
        var previousAbandoned = false;
        try
        {
            for (var round = 0; round < Rounds; round++)
            {
                var abandon = round % 3 == 2;
                var owners = new ConcurrentBag<int>();
                var notes = new ConcurrentBag<string>();
                SingleInstance? kept = null;
                using var start = new Barrier(Racers);
                using var tried = new CountdownEvent(Racers);
                using var done = new ManualResetEventSlim();
                var threads = Enumerable.Range(0, Racers).Select(i => new Thread(() =>
                {
                    start.SignalAndWait();
                    var instance = SingleInstance.TryClaim(sid, signalFirst: false, log: notes.Add);
                    if (instance is not null) owners.Add(i);
                    tried.Signal();
                    if (instance is null) return;
                    done.Wait(); // the owner holds it until every launch has tried
                    if (abandon) kept = instance; // the thread ends owning it: abandoned, as a killed tray leaves it
                    else instance.Dispose();
                }) { IsBackground = true }).ToList();
                threads.ForEach(t => t.Start());
                Assert.True(tried.Wait(TimeSpan.FromSeconds(30)), $"round {round}: the launches did not all try");
                Assert.True(owners.Count == 1, $"round {round}: {owners.Count} owners");
                var takeOvers = notes.Count(n => n.Contains("taking over", StringComparison.Ordinal));
                Assert.True(takeOvers == (previousAbandoned ? 1 : 0),
                            $"round {round}: {takeOvers} launches took over an abandoned mutex (the previous round {(previousAbandoned ? "abandoned" : "released")} it)");
                done.Set();
                threads.ForEach(t => t.Join());
                if (kept is not null) abandoned.Add(kept);
                previousAbandoned = abandon;
            }
        }
        finally
        {
            foreach (var instance in abandoned) instance.Dispose(); // the handles (the owning threads are gone)
        }
        using var last = SingleInstance.TryClaim(sid, signalFirst: false, log: _ => { });
        Assert.NotNull(last);
    }
}

/// <summary>Runs only when installer/ci-tray-selfupdate.ps1 sets RST_TRAY_RACE_EXE (Windows CI, the tray-selfupdate job).</summary>
public sealed class TraySingleInstanceRaceFactAttribute : FactAttribute
{
    public const string Variable = "RST_TRAY_RACE_EXE";

    public TraySingleInstanceRaceFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"{Variable} is not set: run by installer/ci-tray-selfupdate.ps1 against the installed RST.Tray.exe";
    }
}

public sealed class TraySingleInstanceRaceTests
{
    /// <summary>The pairings: Restart Manager's restart and the Run value pass --startup, the updater and a double-click none.</summary>
    private static readonly (string A, string B)[] Pairings =
    {
        (TrayPaths.StartupArgument, ""),
        (TrayPaths.StartupArgument, TrayPaths.StartupArgument),
        ("", TrayPaths.StartupArgument),
        ("", ""),
    };

    private static readonly TimeSpan Decide = TimeSpan.FromSeconds(90), Settle = TimeSpan.FromSeconds(1.5);
    private readonly ITestOutputHelper _output;

    public TraySingleInstanceRaceTests(ITestOutputHelper output) => _output = output;

    [TraySingleInstanceRaceFact]
    public void Two_trays_started_together_leave_exactly_one_running_every_round()
    {
        var exe = Path.GetFullPath(Environment.GetEnvironmentVariable(TraySingleInstanceRaceFactAttribute.Variable)!);
        Assert.True(File.Exists(exe), exe);
        var rounds = int.TryParse(Environment.GetEnvironmentVariable("RST_TRAY_RACE_ROUNDS"), NumberStyles.None, CultureInfo.InvariantCulture, out var r) && r > 0
            ? r : 40;
        var mutex = TrayPaths.MutexName(SingleInstance.CurrentUserSid());

        foreach (var running in Trays()) End(running, kill: false);
        Assert.True(SpinWait.SpinUntil(() => Trays().Length == 0, TimeSpan.FromSeconds(30)), "a tray still runs before the race");

        var record = new List<object>();
        // Review B1: a named mutex is gone once its last handle closes, so a killed tray alone leaves nothing abandoned
        // for the next launches. This host keeps a handle open across each kill (as the updater's or another launch's
        // handle would be), until the next round has decided: then the mutex outlives its owner, abandoned.
        Mutex? keptOpen = null;
        var abandonedBefore = false;
        try
        {
            for (var round = 0; round < rounds; round++)
            {
                var (argsA, argsB) = Pairings[round % Pairings.Length];
                var kill = round % 5 == 4;
                var roundStart = DateTime.Now;
                var (a, b) = StartTogether(exe, argsA, argsB);
                try
                {
                    var clock = Stopwatch.StartNew();
                    while (!a.HasExited && !b.HasExited && clock.Elapsed < Decide) Thread.Sleep(20);
                    var decided = clock.Elapsed;
                    Thread.Sleep(Settle); // a second survivor (or a survivor that also ends) shows here
                    var alive = new[] { a, b }.Where(p => !p.HasExited).ToList();
                    var line = $"round {round}: [{Name(argsA)}] pid {a.Id} {State(a)}, [{Name(argsB)}] pid {b.Id} {State(b)}; decided in {decided.TotalMilliseconds:0} ms";
                    _output.WriteLine(line);
                    Assert.True(alive.Count == 1, $"exactly one tray must survive: {line}");
                    var survivor = alive[0];
                    var loser = survivor == a ? b : a;
                    Assert.True(loser.ExitCode == 0, $"the second launch exits 0 having found the first: {line}");
                    Assert.True(HeldByAnother(mutex), $"the surviving tray holds {mutex}: {line}");
                    var all = Trays();
                    Assert.True(all.Length == 1 && all[0].Id == survivor.Id, $"one RST.Tray.exe in all (got {all.Length}): {line}");
                    // The updater's start (no switch) losing to Restart Manager's --startup tray: it asks that tray to show its
                    // window (the update's result) before it exits, so the user still sees it.
                    var survivorArgs = survivor == a ? argsA : argsB;
                    var loserArgs = survivor == a ? argsB : argsA;
                    var shown = false;
                    if (loserArgs.Length == 0 && survivorArgs.Length > 0)
                    {
                        shown = SpinWait.SpinUntil(() =>
                        {
                            survivor.Refresh();
                            return survivor.HasExited || survivor.MainWindowHandle != IntPtr.Zero;
                        }, TimeSpan.FromSeconds(30)) && !survivor.HasExited;
                        Assert.True(shown, $"the --startup tray shows its window when a launch with no switch finds it: {line}");
                    }
                    // Exactly the survivor took over an abandoned mutex, and only after a killed one: its log says so before
                    // its "starting" line, which it writes once it is up.
                    Assert.True(SpinWait.SpinUntil(() => survivor.HasExited || TrayLogHas(survivor.Id, $"starting (pid {survivor.Id},", roundStart), TimeSpan.FromSeconds(60))
                                && !survivor.HasExited, $"the surviving tray logs its start: {line}");
                    var tookOver = TrayLogHas(survivor.Id, "taking over", roundStart);
                    Assert.True(tookOver == abandonedBefore,
                                $"the survivor {(tookOver ? "took over" : "did not take over")} an abandoned mutex, after a round that {(abandonedBefore ? "killed" : "closed")} its tray: {line}");
                    Assert.False(TrayLogHas(loser.Id, "taking over", roundStart), $"the second launch never takes over: {line}");
                    keptOpen?.Dispose(); // this round has decided
                    keptOpen = null;
                    record.Add(new
                    {
                        round,
                        a = Name(argsA),
                        b = Name(argsB),
                        survivor = survivor == a ? "a" : "b",
                        survivorPid = survivor.Id,
                        loserExit = loser.ExitCode,
                        decidedMs = (int)decided.TotalMilliseconds,
                        windowShownOnSignal = shown,
                        tookOverAbandoned = tookOver,
                        ended = kill ? "killed (next round meets an abandoned mutex)" : "closed",
                    });
                    if (kill) keptOpen = Mutex.OpenExisting(mutex);
                    End(survivor, kill);
                    abandonedBefore = kill;
                }
                finally
                {
                    foreach (var p in new[] { a, b })
                    {
                        if (!p.HasExited) End(p, kill: true);
                        p.Dispose();
                    }
                }
            }
        }
        finally
        {
            keptOpen?.Dispose();
        }

        var result = Environment.GetEnvironmentVariable("RST_TRAY_RACE_RESULT");
        if (!string.IsNullOrEmpty(result))
            File.WriteAllText(result, JsonSerializer.Serialize(new { exe, rounds, record }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Name(string args) => args.Length == 0 ? "no switch" : args;

    /// <summary>
    /// A line of the tray log (%AppData%\RST\logs\tray_*.log, shared) written by <paramref name="pid"/> since
    /// <paramref name="since"/> (a pid can recur in a day's log) that holds <paramref name="text"/>.
    /// </summary>
    private static bool TrayLogHas(int pid, string text, DateTime since)
    {
        var tag = $" [{pid}] "; // after the level: "… 00:25:37.832 INF [884] …"
        var from = since.AddSeconds(-1);
        foreach (var file in Directory.EnumerateFiles(TrayPaths.LogsDir, "tray_*.log"))
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } l)
                {
                    // "yyyy-MM-dd HH:mm:ss.fff LVL [pid] message" (TrayLog's template), local time.
                    if (l.Length < 23 || !l.Contains(text, StringComparison.Ordinal)) continue;
                    if (!DateTime.TryParseExact(l[..23], "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                        || at < from) continue;
                    if (l.IndexOf(tag, 23, StringComparison.Ordinal) is >= 23 and <= 30) return true;
                }
            }
            catch (IOException)
            {
                // being rolled: the next look reads it
            }
        }
        return false;
    }

    private static string State(Process p) => p.HasExited ? $"exited {p.ExitCode}" : "running";

    private static Process[] Trays() => Process.GetProcessesByName(Path.GetFileNameWithoutExtension(TrayPaths.ExeName));

    /// <summary>Both launches released at one instant from two threads. ShellExecute, as the updater and Revit start it:
    /// the trays inherit no handle of the test host.</summary>
    private static (Process A, Process B) StartTogether(string exe, string argsA, string argsB)
    {
        using var go = new Barrier(2);
        Process? a = null, b = null;
        Exception? failure = null;
        Thread Launch(string args, Action<Process> set) => new(() =>
        {
            try
            {
                var info = new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)!, Arguments = args };
                go.SignalAndWait();
                set(Process.Start(info) ?? throw new InvalidOperationException("the tray did not start"));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        var ta = Launch(argsA, p => a = p);
        var tb = Launch(argsB, p => b = p);
        ta.Start();
        tb.Start();
        ta.Join();
        tb.Join();
        if (failure is not null || a is null || b is null)
        {
            a?.Kill();
            b?.Kill();
            throw new InvalidOperationException("could not start both trays", failure);
        }
        return (a, b);
    }

    /// <summary>The mutex exists and this process cannot take it: a tray owns it.</summary>
    private static bool HeldByAnother(string name)
    {
        if (!Mutex.TryOpenExisting(name, out var m)) return false;
        using (m)
        {
            bool taken;
            try
            {
                taken = m.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                taken = true;
            }
            if (taken) m.ReleaseMutex();
            return !taken;
        }
    }

    /// <summary>Kills the tray, or closes it the way util:CloseApplication does (WM_CLOSE), killing it if that fails.</summary>
    private void End(Process tray, bool kill)
    {
        if (!kill)
        {
            // The hidden window exists once the tray has started its icon: until then, keep asking.
            var asked = Stopwatch.StartNew();
            while (!tray.HasExited && asked.Elapsed < TimeSpan.FromSeconds(30))
            {
                if (CloseWindows(tray.Id) > 0 && tray.WaitForExit(15000)) break;
                Thread.Sleep(200);
            }
            if (tray.HasExited) return;
            _output.WriteLine($"pid {tray.Id} did not close on WM_CLOSE; killing it");
        }
        try
        {
            tray.Kill();
        }
        catch (InvalidOperationException)
        {
            // already gone
        }
        Assert.True(tray.WaitForExit(15000), $"pid {tray.Id} did not end");
    }

    private const uint WmClose = 0x0010;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    private static int CloseWindows(int pid)
    {
        var count = 0;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner == pid && PostMessageW(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero)) count++;
            return true;
        }, IntPtr.Zero);
        return count;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
