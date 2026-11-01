// rst-c's own (not vendored): the RST tray's installs on a real Windows runtime (spec #20 §5, §6; AC6, AC7's CI
// analogue, AC8, AC11; F17 WU4):
//
//   - the Apps and Updates pages' new states in light and dark: the enabled buttons, close-Revit advice, an
//     install under way, done, a failed upgrade with "Reinstall previous version", a per-machine install, and every
//     failure message in plain words (one gallery);
//   - every InstallProblem and NetFailure has its plain wording, and none of it is exception text;
//   - AC6 (TrayInstallFixtureTests, run by installer/ci-tray-install.ps1): the real InstallCoordinator with the real
//     msi.dll (MsiDatabase, MsiProducts), System32's msiexec without a Revit.exe gate, against a fake GitHub
//     serving spec #15's fixture companion MSI: each of the eight refusals with its logged reason, the good fixture
//     installed, an install while a Revit.exe stand-in stays alive, a downgrade refused, a failing upgrade that leaves nothing
//     installed, and "Reinstall previous version" from the cache;
//   - AC8 (TraySelfUpdateFixtureTests, run by installer/ci-tray-selfupdate.ps1): RST's own verified download and the
//     real SelfUpdater hand-off to RST.TrayUpdater.exe against the INSTALLED tray; the script asserts the rest.

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RST.Core.Theming;
using RST.Core.Tray;
using RST.Core.Updates;
using RST.RuntimeTests.Support;
using RST.Tests.Updates;
using RST.Tray.Resources;
using RST.Tray.Shell;
using RST.Tray.Updates;
using RST.Tray.Windows;
using RST.UI.Theming;
using Xunit;
using Xunit.Abstractions;

namespace RST.RuntimeTests.Tray;

/// <summary>Install states for the pages.</summary>
internal static class InstallScenarios
{
    private static readonly ReleaseVersion V100 = new(1, 0, 0), V110 = new(1, 1, 0), V120 = new(1, 2, 0), V311 = new(3, 1, 1);

    private static ProductStatus S(string id, InstallKind kind, string? installed, string? latest, InstallContext context = InstallContext.User) =>
        Scenarios.S(id, kind, installed, latest, context: context);

    private static UpdateSnapshot Snapshot(params ProductStatus[] products) =>
        new(products, Scenarios.Today0914, Checking: false, AutomaticChecks: true, Detected: true);

    /// <summary>RST and Gantt-It have updates, Splash-It is not installed: the three buttons.</summary>
    public static UpdateSnapshot Updates => Snapshot(
        S("rst", InstallKind.Installer, "3.1.0", "3.1.1"),
        S("gantt-it", InstallKind.Installer, "1.1.0", "1.2.0"),
        S("splash-it", InstallKind.NotInstalled, null, "1.1.0"));

    public static FakeUpdatesHost Host(string scenario)
    {
        var host = new FakeUpdatesHost { Current = Updates };
        switch (scenario)
        {
            case "advice":
                break;
            case "progress":
                host.Installs = new Dictionary<string, InstallProgress>
                {
                    ["rst"] = new(InstallPhase.Downloading, V311),
                    ["gantt-it"] = new(InstallPhase.Queued, V120),
                    ["splash-it"] = new(InstallPhase.Installing, V110),
                };
                break;
            case "done":
                host.Current = Snapshot(
                    S("rst", InstallKind.Installer, "3.1.1", "3.1.1"),
                    S("gantt-it", InstallKind.Installer, "1.2.0", "1.2.0"),
                    S("splash-it", InstallKind.NotInstalled, null, "1.1.0"));
                host.Installs = new Dictionary<string, InstallProgress>
                {
                    ["rst"] = new(InstallPhase.Installed, V311, ExitCode: 3010, LogPath: Log("rst"), RestartNeeded: true),
                    ["gantt-it"] = new(InstallPhase.Installed, V120, ExitCode: 0, LogPath: Log("gantt-it")),
                    ["splash-it"] = new(InstallPhase.Failed, V110, InstallProblem.InstallFailed, ExitCode: 1603, LogPath: Log("splash-it"), Previous: V100),
                };
                break;
            case "permachine":
                host.Current = Snapshot(
                    S("rst", InstallKind.Installer, "3.1.0", "3.1.1", InstallContext.Machine),
                    S("gantt-it", InstallKind.Installer, "1.1.0", "1.2.0", InstallContext.Machine),
                    S("splash-it", InstallKind.Installer, "1.1.0", "1.1.0"));
                break;
            case "handoff":
                host.Installs = new Dictionary<string, InstallProgress> { ["rst"] = new(InstallPhase.HandingOff, V311) };
                break;
            case "queue":
                // Review B1: one install at a time; RST's self-update goes last.
                host.Installs = new Dictionary<string, InstallProgress>
                {
                    ["rst"] = new(InstallPhase.Queued, V311),
                    ["gantt-it"] = new(InstallPhase.Installing, V120),
                    ["splash-it"] = new(InstallPhase.Queued, V110),
                };
                break;
        }
        return host;
    }

    private static string Log(string id) => $@"C:\Users\modeller\AppData\Local\RST\Tray\logs\install-{id}-20261030-091500.log";

    /// <summary>Every failure message the pages can show, each on its own Gantt-It (or RST) row.</summary>
    public static (UpdateSnapshot Snapshot, Dictionary<string, InstallProgress> Installs, List<string> Labels) Failures()
    {
        var gantt = ProductCatalog.Default.Find("gantt-it")!;
        var cases = new List<(string Label, InstallProgress Progress, bool Rst, InstallKind Kind, InstallContext Context)>();
        void Add(string label, InstallProgress p, bool rst = false, InstallKind kind = InstallKind.Installer, InstallContext context = InstallContext.User) =>
            cases.Add((label, p, rst, kind, context));

        foreach (var problem in Enum.GetValues<InstallProblem>())
        {
            if (problem is InstallProblem.None or InstallProblem.Network or InstallProblem.PerMachine or InstallProblem.InstallFailed
                or InstallProblem.InstallerBusy or InstallProblem.UpdaterFailed) continue;
            Add("Refused: " + problem, new InstallProgress(InstallPhase.Refused, V120, problem));
        }
        foreach (var net in Enum.GetValues<NetFailure>())
            Add("Refused: Network " + net, new InstallProgress(InstallPhase.Refused, V120, InstallProblem.Network, net,
                                                              net == NetFailure.HttpStatus ? 503 : null));
        Add("Failed: msiexec 1603", new InstallProgress(InstallPhase.Failed, V120, InstallProblem.InstallFailed, ExitCode: 1603, LogPath: Log("gantt-it")));
        Add("Failed: upgrade removed it", new InstallProgress(InstallPhase.Failed, V120, InstallProblem.InstallFailed, ExitCode: 1603,
                                                             LogPath: Log("gantt-it"), Previous: V110), kind: InstallKind.NotInstalled);
        Add("Failed: 1618 after three tries", new InstallProgress(InstallPhase.Failed, V120, InstallProblem.InstallerBusy, ExitCode: 1618));
        // The one RST row (the pages need exactly one); RST's per-machine wording is in tray-*-permachine-*.png.
        Add("Failed: the updater did not start", new InstallProgress(InstallPhase.Failed, V311, InstallProblem.UpdaterFailed), rst: true);
        Add("Per machine: a companion", InstallProgress.Idle, context: InstallContext.Machine);

        var products = new List<ProductStatus>();
        var installs = new Dictionary<string, InstallProgress>();
        var labels = new List<string>();
        for (var i = 0; i < cases.Count; i++)
        {
            var (label, progress, rst, kind, context) = cases[i];
            var id = rst ? ProductCatalog.RstId : "case-" + i.ToString(CultureInfo.InvariantCulture);
            var product = rst
                ? ProductCatalog.Default.Rst with { Id = id, Description = label }
                : gantt with { Id = id, Description = label };
            var installed = kind == InstallKind.Installer
                ? new ProductInstallation(kind, new InstalledProduct("{00000000-0000-0000-0000-" + i.ToString("D12", CultureInfo.InvariantCulture) + "}",
                                                                     rst ? "3.1.0" : "1.1.0", context))
                : ProductInstallation.None;
            products.Add(new ProductStatus(product, installed, rst ? V311 : V120, false));
            installs[id] = progress;
            labels.Add(label);
        }
        return (new UpdateSnapshot(products, Scenarios.Today0914, false, true, true), installs, labels);
    }
}

[Collection(WpfThemeCollection.Name)]
public class TrayInstallScreenshotTests
{
    private static readonly Size WindowSize = new(720, 540);

    private static void Sta(UiTheme theme, Action action) => Screenshots.RunSta(() =>
    {
        var culture = CultureInfo.GetCultureInfo("en-US");
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        SynchronizationContext.SetSynchronizationContext(null);
        RstTheme.Apply(theme);
        try
        {
            action();
        }
        finally
        {
            RstTheme.Apply(UiTheme.Light);
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    });

    public static TheoryData<string, UiTheme> AppsScenarios()
    {
        var data = new TheoryData<string, UiTheme>();
        foreach (var s in new[] { "advice", "progress", "queue", "done", "permachine" })
            foreach (var t in new[] { UiTheme.Light, UiTheme.Dark })
                data.Add(s, t);
        return data;
    }

    public static TheoryData<string, UiTheme> UpdatesScenarios()
    {
        var data = new TheoryData<string, UiTheme>();
        foreach (var s in new[] { "handoff", "permachine" })
            foreach (var t in new[] { UiTheme.Light, UiTheme.Dark })
                data.Add(s, t);
        return data;
    }

    public static TheoryData<UiTheme> Themes() => new() { UiTheme.Light, UiTheme.Dark };

    private static string Name(string page, string scenario, UiTheme theme) => $"tray-{page}-{scenario}-{theme.ToString().ToLowerInvariant()}";

    private static void Capture(Window window, string name, Size? size = null)
    {
        WindowCapture.ShowOffScreen(window);
        WindowCapture.DoEvents();
        var bitmap = WindowCapture.Content(window, size ?? WindowSize);
        Screenshots.Save(bitmap, name);
        Assert.True(Screenshots.DistinctPixels(bitmap) > 40, $"{name} looks blank");
    }

    private static List<string> Texts(DependencyObject root) =>
        WindowCapture.Descendants<TextBlock>(root).Where(t => t.IsVisible)
            .Select(t => new System.Windows.Documents.TextRange(t.ContentStart, t.ContentEnd).Text)
            .ToList();

    private static FrameworkElement? ById(DependencyObject root, string id) =>
        WindowCapture.Descendants<FrameworkElement>(root).FirstOrDefault(e => System.Windows.Automation.AutomationProperties.GetAutomationId(e) == id);

    private static Button Button(DependencyObject root, string id) => Assert.IsType<Button>(ById(root, id));

    [Theory]
    [MemberData(nameof(AppsScenarios))]
    public void The_Apps_page_install_states(string scenario, UiTheme theme) => Sta(theme, () =>
    {
        var host = InstallScenarios.Host(scenario);
        var window = new TrayWindow(new TrayViewModel(new FakeTrayHost(), TrayPage.Apps, host));
        try
        {
            // The whole page, not the first 540 px: every row's note must be in the picture.
            WindowCapture.ShowOffScreen(window);
            var root = (FrameworkElement)window.Content;
            root.Measure(new Size(WindowSize.Width, double.PositiveInfinity));
            Capture(window, Name("apps", scenario, theme),
                    new Size(WindowSize.Width, Math.Max(WindowSize.Height, Math.Ceiling(root.DesiredSize.Height) + 40)));
            var texts = Texts(window.AppsPage);
            Assert.Contains(TrayStrings.Install_CloseRevitAdvice, texts);
            Assert.DoesNotContain(texts, t => t.Contains("Exception", StringComparison.Ordinal) || t.Contains("Coming soon", StringComparison.Ordinal));
            switch (scenario)
            {
                case "advice":
                    // Decision #73: advice is visible, and the normal one-click actions stay enabled.
                    Assert.Equal("Update", Button(window, "Tray.App.rst.Action").Content);
                    Assert.Equal("Update", Button(window, "Tray.App.gantt-it.Action").Content);
                    Assert.Equal("Install", Button(window, "Tray.App.splash-it.Action").Content);
                    Assert.All(new[] { "rst", "gantt-it", "splash-it" }, id => Assert.True(Button(window, $"Tray.App.{id}.Action").IsEnabled));
                    Button(window, "Tray.App.splash-it.Action").Command.Execute(window.ViewModel.Updates.Rows[2]);
                    Assert.Equal("install:splash-it", host.Calls[^1]);
                    break;
                case "progress":
                    Assert.Contains("Downloading and checking RST 3.1.1…", texts);
                    Assert.Contains("Waiting its turn: Gantt-It 1.2.0 installs when the installation before it has finished.", texts);
                    Assert.Contains("Installing Splash-It 1.1.0…", texts);
                    Assert.True(Button(window, "Tray.App.gantt-it.Cancel").IsVisible);
                    Assert.True(Button(window, "Tray.App.rst.Cancel").IsVisible);
                    Assert.False(Button(window, "Tray.App.splash-it.Cancel").IsVisible, "msiexec is not cancelled once it runs");
                    Assert.All(new[] { "rst", "gantt-it", "splash-it" }, id => Assert.False(Button(window, $"Tray.App.{id}.Action").IsVisible));
                    Button(window, "Tray.App.gantt-it.Cancel").Command.Execute(window.ViewModel.Updates.Rows[1]);
                    Assert.Equal("cancel:gantt-it", host.Calls[^1]);
                    break;
                case "done":
                    Assert.Contains("RST 3.1.1 is installed. Restart Windows to finish; RST never restarts it for you.", texts);
                    Assert.Contains("Gantt-It 1.2.0 is installed.", texts);
                    // Review N5: the wording offers only what the page offers (Reinstall), nothing else.
                    Assert.Contains("Couldn't update Splash-It to 1.1.0: Windows Installer stopped with error 1603, and Splash-It is no longer installed. Reinstall the previous version to get it back; the log has the details.", texts);
                    var reinstall = Button(window, "Tray.App.splash-it.Action");
                    Assert.Equal("Reinstall previous version (1.0.0)", reinstall.Content);
                    reinstall.Command.Execute(window.ViewModel.Updates.Rows[2]);
                    Assert.Equal("reinstall:splash-it", host.Calls[^1]);
                    Assert.Equal(3, texts.Count(t => t == "Open log")); // each finished install links its msiexec log
                    break;
                case "queue":
                    Assert.Contains("RST 3.1.1 updates once the other installs have finished, so none of them is lost when RST restarts.", texts);
                    Assert.Contains("Installing Gantt-It 1.2.0…", texts);
                    Assert.Contains("Waiting its turn: Splash-It 1.1.0 installs when the installation before it has finished.", texts);
                    Assert.True(Button(window, "Tray.App.splash-it.Cancel").IsVisible, "a queued install can be cancelled");
                    Assert.True(Button(window, "Tray.App.rst.Cancel").IsVisible);
                    Assert.False(Button(window, "Tray.App.gantt-it.Cancel").IsVisible);
                    break;
                case "permachine":
                    Assert.Contains("RST 3.1.1 is available. It's installed for everyone on this PC, so run RST-Setup.exe as an administrator or ask whoever manages software here.", texts);
                    Assert.Contains("Gantt-It 1.2.0 is available. It's installed for everyone on this PC, so ask whoever manages software here to update it.", texts);
                    Assert.False(Button(window, "Tray.App.rst.Action").IsVisible, "a per-user tray never installs over a per-machine install");
                    Assert.False(Button(window, "Tray.App.gantt-it.Action").IsVisible);
                    Assert.Contains("Release page", texts);
                    break;
            }
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [MemberData(nameof(UpdatesScenarios))]
    public void The_Updates_page_install_states(string scenario, UiTheme theme) => Sta(theme, () =>
    {
        var host = InstallScenarios.Host(scenario);
        var window = new TrayWindow(new TrayViewModel(new FakeTrayHost(), TrayPage.Updates, host));
        try
        {
            Capture(window, Name("updates", scenario, theme));
            var texts = Texts(window.UpdatesPage);
            Assert.Contains(TrayStrings.Install_CloseRevitAdvice, texts);
            if (scenario == "handoff")
            {
                Assert.Contains("Updating RST to 3.1.1. RST closes now and opens again when it is done.", texts);
                Assert.False(window.RstUpdateButton.IsVisible);
            }
            else
            {
                Assert.Equal("RST 3.1.1 is available", window.RstHeadline.Text);
                Assert.Contains("RST 3.1.1 is available. It's installed for everyone on this PC, so run RST-Setup.exe as an administrator or ask whoever manages software here.", texts);
                Assert.False(window.RstUpdateButton.IsVisible);
                Assert.Contains("Release page", texts);
            }
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void Every_failure_message_in_plain_words(UiTheme theme) => Sta(theme, () =>
    {
        var (snapshot, installs, labels) = InstallScenarios.Failures();
        var host = new FakeUpdatesHost { Current = snapshot, Installs = installs };
        var window = new TrayWindow(new TrayViewModel(new FakeTrayHost(), TrayPage.Apps, host));
        try
        {
            WindowCapture.ShowOffScreen(window);
            var root = (FrameworkElement)window.Content;
            root.Measure(new Size(WindowSize.Width, double.PositiveInfinity));
            var height = Math.Ceiling(root.DesiredSize.Height) + 40;
            Capture(window, $"tray-install-failures-{theme.ToString().ToLowerInvariant()}", new Size(WindowSize.Width, height));
            var rows = window.ViewModel.Updates.Rows;
            Assert.Equal(labels.Count, rows.Count);
            foreach (var row in rows)
            {
                Assert.True(row.HasNote, row.Description + " shows a message");
                var note = row.NoteText!;
                Assert.DoesNotContain("Exception", note);
                Assert.DoesNotContain("Problem_", note);
                Assert.DoesNotContain("Net_", note);
                Assert.DoesNotContain("://", note); // no URL from a detail
            }
            Assert.Contains(rows, r => r.NoteText == "RST didn't install Gantt-It 1.2.0: the download doesn't match its checksum. Nothing was changed.");
            Assert.Contains(rows, r => r.NoteText == "RST didn't install Gantt-It 1.2.0: couldn't reach GitHub. Nothing was changed.");
            Assert.Contains(rows, r => r.NoteText == "RST didn't install Gantt-It 1.2.0: GitHub answered HTTP 503. Nothing was changed.");
        }
        finally
        {
            window.Close();
        }
    });
}

/// <summary>Review N1 on Windows, where FileShare is enforced: a held package can be read (msiexec, the bundle) but not written.</summary>
public class HeldPackageTests
{
    [Fact]
    public void A_held_package_can_be_read_but_not_written_replaced_or_deleted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "RST-Held-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "GanttIt-1.2.0.msi");
            File.WriteAllText(path, "the verified bytes");
            var sha = InstallCache.Sha256Of(path);
            Assert.Null(InstallCache.HoldVerified(path, new string('0', 64))); // changed since: refused
            using (var hold = InstallCache.HoldVerified(path, sha))
            {
                Assert.NotNull(hold);
                using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Assert.Equal((int)'t', reader.ReadByte()); // a reader such as msiexec opens it
                Assert.ThrowsAny<IOException>(() => File.AppendAllText(path, "swapped"));
                Assert.ThrowsAny<Exception>(() => File.Delete(path));
                Assert.ThrowsAny<Exception>(() => File.Move(path, path + ".old"));
            }
            File.AppendAllText(path, " released"); // writable again once the install has finished
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

/// <summary>
/// The updater holds the tray's single-instance mutex while it replaces RST (Planner, from #149's review): a tray
/// started meanwhile (Revit, WU2) finds "a tray runs" and exits; once released, the next tray takes it.
/// </summary>
public class TrayInstanceClaimTests
{
    [Fact]
    public void While_the_updater_holds_the_mutex_a_tray_cannot_start_and_after_the_hand_over_one_can()
    {
        const string sid = "S-1-5-21-0-0-0-424242"; // a test-only name, never a real user's tray
        var claim = TrayInstanceClaim.TryClaim(sid, TimeSpan.FromSeconds(5));
        Assert.True(claim.Held);
        try
        {
            Assert.Null(SingleInstance.TryClaim(sid, signalFirst: false, log: _ => { })); // a tray started now exits
            using var second = TrayInstanceClaim.TryClaim(sid, TimeSpan.FromMilliseconds(200)); // nor can a second updater take it
            Assert.False(second.Held);
            Assert.Equal(ClaimRelease.NotHeld, second.Release());
        }
        finally
        {
            Assert.Equal(ClaimRelease.Released, claim.Release());
        }
        Assert.False(claim.Held);
        Assert.Equal(ClaimRelease.Released, claim.Release()); // once confirmed, the same answer again (Dispose too)
        using var tray = SingleInstance.TryClaim(sid, signalFirst: false, log: _ => { });
        Assert.NotNull(tray); // the new tray takes it after the hand-over
    }

    /// <summary>
    /// SC-022: the tray was still running when the updater asked (it was never told to quit), so the claim was not held
    /// in time; it keeps waiting and takes the mutex the moment the tray lets it go (Restart Manager closing it for
    /// msiexec), so a tray started after that exits instead of running from the folder being replaced.
    /// </summary>
    [Fact]
    public void A_claim_not_held_in_time_takes_the_mutex_the_moment_the_tray_lets_it_go()
    {
        var sid = "S-1-5-21-0-0-1-" + Random.Shared.Next(100000, 999999);
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>(); // written on the claim's own thread
        using var trayExits = new ManualResetEventSlim();
        using var trayRuns = new ManualResetEventSlim();
        var trayOwned = false;
        var tray = new Thread(() =>
        {
            using var running = SingleInstance.TryClaim(sid, signalFirst: false, log: _ => { });
            trayOwned = running is not null;
            trayRuns.Set();
            trayExits.Wait();
        });
        tray.Start();
        Assert.True(trayRuns.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(trayOwned);

        using var claim = TrayInstanceClaim.TryClaim(sid, TimeSpan.FromMilliseconds(300), log.Enqueue);
        Assert.False(claim.Held);
        trayExits.Set(); // the old tray quits (Restart Manager)
        tray.Join();
        Assert.True(SpinWait.SpinUntil(() => claim.Held, TimeSpan.FromSeconds(10)), "the pending claim did not take the freed mutex");
        Assert.Contains(log, l => l.Contains("took the tray's single-instance mutex", StringComparison.Ordinal));
        Assert.Null(SingleInstance.TryClaim(sid, signalFirst: false, log: _ => { })); // Restart Manager's restart exits
        Assert.Equal(ClaimRelease.Released, claim.Release());
        using var next = SingleInstance.TryClaim(sid, signalFirst: false, log: _ => { });
        Assert.NotNull(next); // the updater's tray, after the hand-over
    }

    /// <summary>A killed tray leaves its mutex abandoned: the waiting claim gets it (exactly one owner) and hands it over.</summary>
    [Fact]
    public void A_claim_waiting_on_a_killed_tray_takes_the_abandoned_mutex()
    {
        var sid = "S-1-5-21-0-0-2-" + Random.Shared.Next(100000, 999999);
        using var trayDies = new ManualResetEventSlim();
        using var trayRuns = new ManualResetEventSlim();
        var trayOwned = false;
        var tray = new Thread(() =>
        {
            var mutex = new Mutex(false, TrayPaths.MutexName(sid));
            trayOwned = mutex.WaitOne(0);
            trayRuns.Set();
            trayDies.Wait();
            // The thread ends owning it, never released: Windows marks it abandoned (a killed tray).
            GC.KeepAlive(mutex);
        });
        tray.Start();
        Assert.True(trayRuns.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(trayOwned);

        using var claim = TrayInstanceClaim.TryClaim(sid, TimeSpan.FromMilliseconds(300));
        Assert.False(claim.Held);
        trayDies.Set();
        tray.Join();
        Assert.True(SpinWait.SpinUntil(() => claim.Held, TimeSpan.FromSeconds(10)), "the pending claim did not take the abandoned mutex");
        Assert.Null(SingleInstance.TryClaim(sid, signalFirst: false, log: _ => { }));
        Assert.Equal(ClaimRelease.Released, claim.Release());
        using var next = SingleInstance.TryClaim(sid, signalFirst: false, log: _ => { });
        Assert.NotNull(next);
    }
}

public class TrayInstallWordingTests
{
    [Fact]
    public void The_reinstall_button_is_an_immediate_action()
    {
        var host = InstallScenarios.Host("done");
        var vm = new UpdatesViewModel(host, () => new UpdateChecksState(true, false));
        Assert.Equal("Reinstall previous version (1.0.0)", vm.Rows[2].ActionText);
    }

    [Fact]
    public void Every_problem_and_network_failure_has_plain_wording()
    {
        foreach (var problem in Enum.GetValues<InstallProblem>())
        {
            var text = TrayStrings.Problem(problem);
            Assert.False(string.IsNullOrWhiteSpace(text), problem.ToString());
            Assert.DoesNotContain("_", text);
        }
        foreach (var net in Enum.GetValues<NetFailure>())
        {
            var text = TrayStrings.Net(net, 503);
            Assert.DoesNotContain("_", text);
        }
        Assert.Equal("GitHub answered HTTP 503", TrayStrings.Net(NetFailure.HttpStatus, 503));
        Assert.Equal("the proxy refused the connection (HTTP 407)", TrayStrings.Net(NetFailure.ProxyRefused, 407));
    }

    [Fact]
    public void The_row_offers_one_button_for_each_state()
    {
        var host = InstallScenarios.Host("advice");
        var vm = new UpdatesViewModel(host, () => new UpdateChecksState(true, false));
        Assert.Equal(new[] { "Update", "Update", "Install" }, vm.Rows.Select(r => r.ActionText));
        Assert.True(vm.ShowRstInstallNote);

        var machine = new UpdatesViewModel(InstallScenarios.Host("permachine"), () => new UpdateChecksState(true, false));
        Assert.Null(machine.Rst.ActionText);
        Assert.False(machine.ShowRstInstallNote);
        Assert.True(machine.Rst.ShowNoteReleasePage);

        // A per-machine TRAY (Program Files) never installs RST, even when Windows Installer reports a per-user one.
        var tray = InstallScenarios.Host("advice");
        tray.PerMachineTray = true;
        var fromProgramFiles = new UpdatesViewModel(tray, () => new UpdateChecksState(true, false));
        Assert.Null(fromProgramFiles.Rst.ActionText);
        Assert.Equal("Update", fromProgramFiles.Rows[1].ActionText);
    }
}

/// <summary>Runs only when installer/ci-tray-install.ps1 has set RST_TRAY_INSTALL_FIXTURES (Windows CI, the tray-install job).</summary>
public sealed class TrayInstallFixtureFactAttribute : FactAttribute
{
    public const string Variable = "RST_TRAY_INSTALL_FIXTURES";

    public TrayInstallFixtureFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"{Variable} is not set: run by installer/ci-tray-install.ps1 with the fixture companion MSIs";
    }
}

/// <summary>
/// AC6 (and AC7's CI analogue): the tray's real install path against spec #15's fixture companion MSI, served by a
/// fake GitHub. One test, in order, because each step builds on what Windows Installer now has installed.
/// </summary>
public sealed class TrayInstallFixtureTests : IDisposable
{
    private const string FixtureUpgradeCode = "A0E85F7A-BF17-4052-AB6A-010781D8CF6D"; // installer-bundle/fixture/Product.wxs
    private const string Repo = "jedbjorn/rst-c-companion-fixture";
    private static readonly ReleaseVersion V100 = new(1, 0, 0), V110 = new(1, 1, 0), V200 = new(2, 0, 0);
    private readonly ITestOutputHelper _output;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RST-TrayInstall-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = new();
    private readonly List<object> _steps = new();

    public TrayInstallFixtureTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort; the msiexec logs were copied out */ }
    }

    private static readonly ProductInfo Fixture = new(
        "companion-fixture", "RST Companion Fixture", "test only", Repo, new[] { FixtureUpgradeCode }, "7e57f1c7-a5e5-4c3d-8f0e-000000000ae5",
        "RST-CompanionFixture-{0}.msi", @"^RST-CompanionFixture-[0-9]{1,9}\.[0-9]{1,9}\.[0-9]{1,9}\.msi\z");

    private void Log(string line)
    {
        lock (_log) _log.Add(line);
        _output.WriteLine(line);
    }

    private string Fixtures => Environment.GetEnvironmentVariable(TrayInstallFixtureFactAttribute.Variable)!;

    private byte[] FixtureBytes(ReleaseVersion v) => File.ReadAllBytes(Path.Combine(Fixtures, $"RST-CompanionFixture-{v}.msi"));

    /// <summary>A copy of the 2.0.0 fixture changed with SQL (MsiDatabase.Execute), for the wrong-identity cases.</summary>
    private byte[] Variant(string name, params string[] sql)
    {
        var path = Path.Combine(_root, "variants", name + ".msi");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(Path.Combine(Fixtures, "RST-CompanionFixture-2.0.0.msi"), path, overwrite: true);
        MsiDatabase.Execute(path, sql);
        return File.ReadAllBytes(path);
    }

    private static void Serve(FakeReleaseData release, byte[] bytes)
    {
        release.Bytes = bytes;
        release.Sums = $"{FakeReleaseData.Sha256(bytes)}  {release.Asset}\n";
        release.Digest = "sha256:" + FakeReleaseData.Sha256(bytes);
        release.RedirectHost = "release-assets.githubusercontent.com";
    }

    private IReadOnlyList<InstalledProduct> Installed() => new MsiProducts(Log).Related(FixtureUpgradeCode);

    private void RemoveFixture()
    {
        foreach (var p in Installed())
        {
            using var x = Process.Start(new ProcessStartInfo(Msiexec.ExePath, $"/x {p.ProductCode} /qn /norestart") { UseShellExecute = false })!;
            x.WaitForExit(300_000);
            Log($"cleanup: msiexec /x {p.ProductCode} exited {x.ExitCode}");
        }
    }

    private void Step(string name, InstallProgress progress, int msiexecRuns, string? reason = null)
    {
        var installed = Installed();
        _steps.Add(new
        {
            step = name,
            phase = progress.Phase.ToString(),
            problem = progress.Problem.ToString(),
            exitCode = progress.ExitCode,
            previous = progress.Previous?.ToString(),
            msiexecRuns,
            installed = installed.Select(p => $"{p.VersionText} {p.Context}").ToList(),
            reason,
        });
        _output.WriteLine($"[{name}] {progress.Phase} {progress.Problem} exit {progress.ExitCode}; installed: {string.Join(", ", installed.Select(p => p.VersionText))}");
    }

    private sealed class CountingMsiexec(IMsiexec inner) : IMsiexec
    {
        public int Runs;

        public Task<int> InstallAsync(string msiPath, string logPath, CancellationToken cancel)
        {
            Interlocked.Increment(ref Runs);
            return inner.InstallAsync(msiPath, logPath, cancel);
        }

        public Task<int> RunBundleAsync(string exePath, string logPath, CancellationToken cancel)
        {
            Interlocked.Increment(ref Runs);
            return inner.RunBundleAsync(exePath, logPath, cancel);
        }
    }

    [TrayInstallFixtureFact]
    public async Task AC6_refuses_everything_unverified_and_installs_upgrades_and_reinstalls_the_fixture()
    {
        RemoveFixture();
        Assert.Empty(Installed());

        var github = new FakeGitHub();
        var r100 = github.Add(Repo, "v1.0.0", "RST-CompanionFixture-1.0.0.msi", FixtureBytes(V100));
        var r110 = github.Add(Repo, "v1.1.0", "RST-CompanionFixture-1.1.0.msi", FixtureBytes(V110));
        var r200 = github.Add(Repo, "v2.0.0", "RST-CompanionFixture-2.0.0.msi", FixtureBytes(V200));
        var msiexec = new CountingMsiexec(new Msiexec());
        var paths = new InstallPaths(Path.Combine(_root, "cache"), Path.Combine(_root, "logs"), Path.Combine(_root, "selfupdate.json"));
        using var http = TrayHttp.CreateClient(github);
        using var installs = new InstallCoordinator(new[] { Fixture }, http, new MsiProducts(Log), new MsiDatabase(), new BundleReader(), msiexec,
            new FakeSelfUpdater(), paths, TimeProvider.System, a => a(), Log, () => { }, () => { });

        async Task<InstallProgress> Refused(string name, InstallProblem problem, Action arrange, ProductInfo? product = null)
        {
            Serve(r200, FixtureBytes(V200));
            arrange();
            var before = msiexec.Runs;
            var logBefore = _log.Count;
            await installs.InstallAsync(product ?? Fixture, V200);
            var p = installs.For(Fixture.Id);
            var reason = _log.Skip(logBefore).FirstOrDefault(l => l.StartsWith("refused ", StringComparison.Ordinal));
            Step(name, p, msiexec.Runs - before, reason);
            Assert.Equal(InstallPhase.Refused, p.Phase);
            Assert.Equal(problem, p.Problem);
            Assert.Equal(before, msiexec.Runs);
            Assert.NotNull(reason); // the logged reason
            Assert.Contains(problem.ToString(), reason);
            Assert.Empty(Installed());
            return p;
        }

        // The eight refusals (AC6), before anything is installed.
        var wrongCode = Variant("wrong-code", "UPDATE `Property` SET `Value` = '{0B1CC712-E936-4DA1-9680-2D9A95F88271}' WHERE `Property` = 'UpgradeCode'");
        var perMachine = Variant("per-machine", "INSERT INTO `Property` (`Property`, `Value`) VALUES ('ALLUSERS', '1')");
        await Refused("wrong hash", InstallProblem.HashMismatch, () => r200.Bytes = FixtureBytes(V110));
        await Refused("hash missing from SHA256SUMS.txt", InstallProblem.NotInSums, () => r200.Sums = $"{FakeReleaseData.Sha256(r200.Bytes)}  RST-CompanionFixture-1.9.9.msi\n");
        await Refused("digest mismatch", InstallProblem.DigestMismatch, () => r200.Digest = "sha256:" + FakeReleaseData.Sha256(FixtureBytes(V110)));
        await Refused("wrong UpgradeCode", InstallProblem.WrongUpgradeCode, () => Serve(r200, wrongCode));
        await Refused("wrong scope (ALLUSERS=1)", InstallProblem.WrongScope, () => Serve(r200, perMachine));
        await Refused("off-allowlist redirect", InstallProblem.RedirectNotAllowed, () => r200.RedirectHost = "objects.example.com");
        await Refused("bad asset name", InstallProblem.AssetNameBad, () => { }, Fixture with { AssetTemplate = "RST-CompanionFixture_{0}.msi" });
        Assert.DoesNotContain(github.Requests, u => u.Host == "objects.example.com");

        // The good fixture installs.
        await installs.InstallAsync(Fixture, V100);
        Step("good fixture 1.0.0", installs.For(Fixture.Id), 1);
        Assert.Equal(InstallPhase.Installed, installs.For(Fixture.Id).Phase);
        Assert.Equal("1.0.0", Assert.Single(Installed()).VersionText);
        Assert.Equal(InstallContext.User, Installed()[0].Context);

        // Decision #73 / #201: a background Revit.exe must neither hold the queue nor be terminated.
        using (var stub = StartRevitStub())
        {
            try
            {
                await installs.InstallAsync(Fixture, V110).WaitAsync(TimeSpan.FromMinutes(5));
                Assert.False(stub.HasExited, "the installer must not terminate the background process");
                Step("Revit running: upgrade to 1.1.0 proceeds", installs.For(Fixture.Id), 1);
                Assert.Contains("MSIRESTARTMANAGERCONTROL = Disable", File.ReadAllText(installs.For(Fixture.Id).LogPath!));
            }
            finally
            {
                if (!stub.HasExited) stub.Kill(entireProcessTree: true); // test-owned stand-in only
            }
        }
        Assert.Equal(InstallPhase.Installed, installs.For(Fixture.Id).Phase);
        Assert.Equal("1.1.0", Assert.Single(Installed()).VersionText);

        // A downgrade is refused.
        var downgradeRuns = msiexec.Runs;
        await installs.InstallAsync(Fixture, V100);
        Step("downgrade to 1.0.0", installs.For(Fixture.Id), msiexec.Runs - downgradeRuns,
             _log.LastOrDefault(l => l.StartsWith("refused ", StringComparison.Ordinal)));
        Assert.Equal(InstallProblem.NotNewer, installs.For(Fixture.Id).Problem);
        Assert.Equal(downgradeRuns, msiexec.Runs);

        // An upgrade that fails on purpose (a type 19 custom action after the files) once the old version was
        // removed (the default afterInstallValidate MajorUpgrade): nothing is left installed.
        Serve(r200, FailingUpgrade());
        await installs.InstallAsync(Fixture, V200);
        var failed = installs.For(Fixture.Id);
        Step("upgrade to 2.0.0 fails on purpose", failed, 1);
        Assert.Equal(InstallPhase.Failed, failed.Phase);
        Assert.Equal(1603, failed.ExitCode);
        Assert.Empty(Installed());
        Assert.Equal(V110, failed.Previous);
        Assert.True(File.Exists(failed.LogPath), "the msiexec log is kept");

        // Reinstall previous version: from the cache, no download.
        var requests = github.Count;
        await installs.ReinstallPreviousAsync(Fixture);
        Step("reinstall previous version 1.1.0", installs.For(Fixture.Id), 1);
        Assert.Equal(InstallPhase.Installed, installs.For(Fixture.Id).Phase);
        Assert.Equal("1.1.0", Assert.Single(Installed()).VersionText);
        Assert.Equal(requests, github.Count);

        Assert.All(github.Requests, u => Assert.True(UpdateHosts.All.Contains(u.Host) && u.Scheme == "https", u.ToString()));
        WriteResult(github);
        RemoveFixture();
    }

    /// <summary>
    /// The 2.0.0 fixture with a type 19 custom action ("display an error and fail") late in InstallExecuteSequence.
    /// It runs after RemoveExistingProducts (afterInstallValidate) has already removed the old version, so the
    /// failed upgrade leaves nothing installed: the companions' real failure mode (spec #20 Current Posture).
    /// </summary>
    private byte[] FailingUpgrade()
    {
        var path = Path.Combine(_root, "variants", "fails.msi");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(Path.Combine(Fixtures, "RST-CompanionFixture-2.0.0.msi"), path, overwrite: true);
        try
        {
            MsiDatabase.Execute(path, "CREATE TABLE `CustomAction` (`Action` CHAR(72) NOT NULL, `Type` SHORT NOT NULL, `Source` CHAR(72), `Target` CHAR(255) PRIMARY KEY `Action`)");
        }
        catch (InvalidOperationException ex)
        {
            Log("fixture variant: CustomAction table not created (" + ex.Message + "); it already exists");
        }
        MsiDatabase.Execute(path,
            "INSERT INTO `CustomAction` (`Action`, `Type`, `Source`, `Target`) VALUES ('RstFixtureFailOnPurpose', 19, '', 'RST CI: this fixture upgrade fails on purpose')",
            "INSERT INTO `InstallExecuteSequence` (`Action`, `Condition`, `Sequence`) VALUES ('RstFixtureFailOnPurpose', 'NOT REMOVE', 6590)");
        return File.ReadAllBytes(path);
    }

    /// <summary>A Revit.exe in this session: a copy of cmd.exe under that name, waiting on ping.</summary>
    private Process StartRevitStub()
    {
        var dir = Path.Combine(_root, "revit-stub");
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "Revit.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), exe, overwrite: true);
        var p = Process.Start(new ProcessStartInfo(exe, "/c ping -n 600 127.0.0.1 > nul") { UseShellExecute = false, CreateNoWindow = true })!;
        Log($"started a Revit.exe stand-in, pid {p.Id}");
        return p;
    }

    private void WriteResult(FakeGitHub github)
    {
        var path = Environment.GetEnvironmentVariable("RST_TRAY_INSTALL_RESULT");
        if (string.IsNullOrEmpty(path)) return;
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            steps = _steps,
            requests = github.Requests.Select(u => u.GetLeftPart(UriPartial.Path)).ToList(),
            log = _log,
        }, new JsonSerializerOptions { WriteIndented = true }));
        var logs = Path.Combine(Path.GetDirectoryName(path)!, "msiexec-logs");
        Directory.CreateDirectory(logs);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(_root, "logs")))
            File.Copy(file, Path.Combine(logs, Path.GetFileName(file)), overwrite: true);
    }

    private static async Task WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(100);
        Assert.True(condition(), "timed out");
    }
}

/// <summary>Runs only when installer/ci-tray-selfupdate.ps1 sets RST_SELFUPDATE_NEW_MSI (Windows CI, the tray-selfupdate job).</summary>
public sealed class TraySelfUpdateFactAttribute : FactAttribute
{
    public const string Variable = "RST_SELFUPDATE_NEW_MSI";

    public TraySelfUpdateFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"{Variable} is not set: run by installer/ci-tray-selfupdate.ps1 with two RST.msi builds";
    }
}

/// <summary>
/// AC8's tray half: the RST.msi built at a newer version, served by a fake GitHub, passes RST's own four checks against
/// the INSTALLED older RST (real msi.dll), lands in the real tray cache, and the real SelfUpdater copies the INSTALLED
/// tray to the updater folder and starts RST.TrayUpdater.exe --apply … --wait-pid. In "close" mode the old tray is then
/// closed the way it quits after the hand-off (WM_CLOSE to its window); in "race" mode it is left running and the
/// updater waits for nothing, so Windows Installer's Restart Manager and the updater both start a tray.
/// installer/ci-tray-selfupdate.ps1 asserts the rest: one tray, the new version, one product.
/// </summary>
public sealed class TraySelfUpdateFixtureTests
{
    private readonly ITestOutputHelper _output;

    public TraySelfUpdateFixtureTests(ITestOutputHelper output) => _output = output;

    [TraySelfUpdateFact]
    public async Task The_installed_tray_hands_over_to_its_updater_copy()
    {
        var msi = Environment.GetEnvironmentVariable(TraySelfUpdateFactAttribute.Variable)!;
        var version = ReleaseVersion.TryParseTag("v" + Environment.GetEnvironmentVariable("RST_SELFUPDATE_VERSION"))!.Value;
        var trayPid = int.Parse(Environment.GetEnvironmentVariable("RST_SELFUPDATE_TRAY_PID")!, CultureInfo.InvariantCulture);
        var race = Environment.GetEnvironmentVariable("RST_SELFUPDATE_MODE") == "race";
        var log = new List<string>();
        void Log(string m)
        {
            log.Add(m);
            _output.WriteLine(m);
        }

        // Decision #34: how RST was installed decides the package. The script says which install it made; the tray's
        // own detection (the bundle's per-user registration) must agree.
        var expectBundle = Environment.GetEnvironmentVariable("RST_SELFUPDATE_EXPECT_BUNDLE") == "1";
        using var tray = Process.GetProcessById(trayPid);
        Assert.Equal("RST.Tray", tray.ProcessName);
        var waitFor = race ? Environment.ProcessId : trayPid; // race: nothing to wait for, msiexec meets the running tray
        var self = new SelfUpdater(TrayPaths.PerUserInstallDir, TrayPaths.UpdaterDir, waitFor, Log);
        Assert.False(self.PerMachineTray);
        Assert.Equal(expectBundle, self.InstalledByBundle);
        var kind = self.InstalledByBundle ? PackageKind.Bundle : PackageKind.Msi;

        var github = new FakeGitHub();
        github.Add(ProductCatalog.RstRepo, version.Tag, kind == PackageKind.Bundle ? ProductCatalog.RstBundleAsset : ProductCatalog.RstAsset,
                   File.ReadAllBytes(msi));
        var installed = ProductDetection.Query(new MsiProducts(Log), ProductCatalog.RstProduct, Log);
        var current = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<InstalledProduct>>(installed));
        Assert.Equal(InstallContext.User, current.Context);
        Assert.True(version > current.Version!.Value, $"{version} must be newer than the installed {current.VersionText}");

        using var http = TrayHttp.CreateClient(github);
        var verified = await new VerifiedDownload(http, new MsiDatabase(), TrayPaths.CacheDir, Log, bundles: new BundleReader())
            .RunAsync(ProductCatalog.RstProduct, version, current.Version, CancellationToken.None, kind);
        Assert.True(verified.Ok, verified.Problem + ": " + verified.Detail);
        Assert.Equal(Path.Combine(TrayPaths.CacheDir, "rst", version.ToString(),
                                  kind == PackageKind.Bundle ? ProductCatalog.RstBundleAsset : ProductCatalog.RstAsset), verified.Msi!.Path);
        if (kind == PackageKind.Bundle)
        {
            var identity = new BundleReader().Read(verified.Msi.Path);
            Log($"RST-Setup.exe version resource: FileVersion {identity.FileVersion}, ProductName {identity.ProductName}");
        }
        Assert.Null(self.Start(verified.Msi.Path));
        Assert.True(File.Exists(Path.Combine(TrayPaths.UpdaterDir, TrayPaths.UpdaterExeName)));
        Assert.False(File.Exists(Path.Combine(TrayPaths.UpdaterDir, TrayPaths.ExeName)), "the copy never runs as RST.Tray.exe");

        var closed = false;
        if (!race)
        {
            // The tray quits once the updater has started; WM_CLOSE to its window is that same Quit path.
            closed = CloseWindows(trayPid) > 0;
            Assert.True(closed, "the tray has a window to close");
        }
        var result = Environment.GetEnvironmentVariable("RST_SELFUPDATE_RESULT");
        if (!string.IsNullOrEmpty(result))
            File.WriteAllText(result, JsonSerializer.Serialize(new
            {
                mode = race ? "race" : "close",
                kind = kind.ToString(),
                installedBefore = current.VersionText,
                version = version.ToString(),
                verified.Msi.Path,
                verified.Msi.Sha256,
                trayPid,
                waitPid = waitFor,
                closed,
                requests = github.Requests.Select(u => u.GetLeftPart(UriPartial.Path)),
                log,
            }, new JsonSerializerOptions { WriteIndented = true }));
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
