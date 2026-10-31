// rst-c's own (not vendored): RevitWatch's log lines say what the wait is for (SC-043 follow-up). The open window's
// watch (it refreshes the "… when Revit closes" buttons) is not an install, so it must not log "install: waiting";
// the install's wait (IRevitWatch, InstallCoordinator) still does. A short-lived ping.exe stands in for Revit.exe.

using System.Diagnostics;
using RST.Core.Updates;
using RST.Tray.Updates;
using Xunit;

namespace RST.RuntimeTests.Tray;

public class RevitWatchTests
{
    [Fact]
    public async Task The_window_watch_and_the_install_wait_log_their_own_purpose()
    {
        var window = await WaitLines(watch => watch.WaitUntilNoneAsync(RevitWaitPurpose.Window, CancellationToken.None));
        Assert.Contains(window, l => l.StartsWith("window: watching ", StringComparison.Ordinal)
                                     && l.Contains("Revit process(es) to refresh the Install buttons", StringComparison.Ordinal));
        Assert.DoesNotContain(window, l => l.StartsWith("install:", StringComparison.Ordinal));

        var install = await WaitLines(watch => ((IRevitWatch)watch).WaitUntilNoneAsync(CancellationToken.None));
        Assert.Contains(install, l => l.StartsWith("install: waiting for ", StringComparison.Ordinal)
                                      && l.Contains("Revit process(es) to exit", StringComparison.Ordinal));
        Assert.DoesNotContain(install, l => l.StartsWith("window:", StringComparison.Ordinal));
    }

    private static async Task<List<string>> WaitLines(Func<RevitWatch, Task> wait)
    {
        var lines = new List<string>();
        var watch = new RevitWatch("PING", m => { lock (lines) lines.Add(m); });
        using var standIn = Process.Start(new ProcessStartInfo("ping.exe", "-n 3 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!;
        Assert.True(watch.AnyRunning(), "the stand-in did not start");
        await wait(watch).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(standIn.HasExited);
        lock (lines) return lines.ToList();
    }
}
