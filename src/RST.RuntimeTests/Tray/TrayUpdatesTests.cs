// rst-c's own (not vendored): the RST tray's update checks on a real Windows runtime (spec #20 §4, §6; AC4, AC5,
// AC11; F17 WU3):
//
//   - the Updates and Apps pages in light and dark, covering every state (Up to date, Update available, Not
//     installed, Installed manually, Installed with the latest unknown), checks off, and a failed check;
//   - AC4 through the tray's own checker (TrayUpdates): a counting HttpMessageHandler over 48 simulated hours
//     makes ZERO requests with the setting off, and zero with the real UpdateChecks registry preset (HKCU and
//     HKLM, read by the tray's own WindowsState), until Check now; with checks on, two checks of three HEADs;
//   - one notification per newly found version, not repeated a day later or after a restart; a failure is
//     logged once;
//   - the HTTP handler: no redirects, the system proxy, default credentials for the proxy only;
//   - msi.dll detection (MsiProducts) answers without admin, and, when ci-tray-detect.ps1 has installed the
//     fixture companion MSI, matches what Windows Installer reports (AC5's fixture half; skipped otherwise).

using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
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

/// <summary>A scripted update checker for the pages.</summary>
internal sealed class FakeUpdatesHost : ITrayUpdatesHost
{
    public UpdateSnapshot Current { get; set; } = UpdateSnapshot.Empty(ProductCatalog.Default, automaticChecks: true);

    public event EventHandler? Changed;

    public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    public TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.Utc;

    public List<string> Calls { get; } = new();

    public int Subscribers => Changed?.GetInvocationList().Length ?? 0;

    public void CheckNow() => Calls.Add("check");

    public void OpenReleasePage(ProductInfo product) => Calls.Add("release:" + product.Id);

    // F17 WU4.

    public IReadOnlyDictionary<string, InstallProgress> Installs { get; set; } = new Dictionary<string, InstallProgress>();

    public bool RevitRunning { get; set; }

    public bool PerMachineTray { get; set; }

    public void Install(ProductInfo product) => Calls.Add("install:" + product.Id);

    public void ReinstallPrevious(ProductInfo product) => Calls.Add("reinstall:" + product.Id);

    public void CancelInstall(ProductInfo product) => Calls.Add("cancel:" + product.Id);

    public void OpenLog(string path) => Calls.Add("log:" + path);

    public void Publish(UpdateSnapshot snapshot)
    {
        Current = snapshot;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Snapshots for the pages: every state appears in at least one.</summary>
internal static class Scenarios
{
    public static readonly DateTimeOffset Today0914 = new(2026, 10, 30, 9, 14, 0, TimeSpan.Zero);

    private static ReleaseVersion V(string s) => ReleaseVersion.TryParseInstalled(s)!.Value;

    public static ProductStatus S(string id, InstallKind kind, string? installed, string? latest, bool failed = false,
                                  InstallContext context = InstallContext.User)
    {
        var product = ProductCatalog.Default.Find(id)!;
        var install = kind switch
        {
            InstallKind.Installer => new ProductInstallation(kind, new InstalledProduct("{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}", installed!, context)),
            InstallKind.Unknown => ProductInstallation.CouldNotRead,
            InstallKind.Manual => new ProductInstallation(kind, ManifestPath: @"C:\Users\modeller\AppData\Roaming\Autodesk\Revit\Addins\2026\GanttIt.addin"),
            _ => ProductInstallation.None,
        };
        return new ProductStatus(product, install, latest is null ? null : V(latest), failed);
    }

    /// <summary>Update available (RST), Installed manually (Gantt-It), Not installed (Splash-It); checked today.</summary>
    public static UpdateSnapshot Mixed => new(new[]
    {
        S("rst", InstallKind.Installer, "3.1.0", "3.1.1"),
        S("gantt-it", InstallKind.Manual, null, "1.2.0"),
        S("splash-it", InstallKind.NotInstalled, null, "1.1.0"),
    }, Today0914, Checking: false, AutomaticChecks: true, Detected: true);

    /// <summary>Up to date (RST, Splash-It), Update available for everyone on this PC (Gantt-It); checked yesterday.</summary>
    public static UpdateSnapshot Current => new(new[]
    {
        S("rst", InstallKind.Installer, "3.1.1", "3.1.1"),
        S("gantt-it", InstallKind.Installer, "1.1.0", "1.2.0", context: InstallContext.Machine),
        S("splash-it", InstallKind.Installer, "1.1.0", "1.1.0"),
    }, Today0914.AddDays(-1), Checking: false, AutomaticChecks: true, Detected: true);

    /// <summary>Checks off, never checked: Installed with the latest unknown (RST, Gantt-It), Not installed (Splash-It).</summary>
    public static UpdateSnapshot Off => new(new[]
    {
        S("rst", InstallKind.Installer, "3.1.0", null),
        S("gantt-it", InstallKind.Installer, "1.1.0", null),
        S("splash-it", InstallKind.NotInstalled, null, null),
    }, null, Checking: false, AutomaticChecks: false, Detected: true);

    /// <summary>
    /// What TrayUpdates produces when today's check fails after a good one on 28 Oct (TrayUpdateChecksTests
    /// .A_failed_check_keeps_the_last_success_and_claims_nothing_stale proves the real code gives these states):
    /// "last checked" stays the last success, RST's last known latest is kept but not called "up to date",
    /// Gantt-It's known newer 1.2.0 stays "update available", Splash-It was never reached.
    /// </summary>
    public static UpdateSnapshot Failed => new(new[]
    {
        S("rst", InstallKind.Installer, "3.1.0", "3.1.0", failed: true),
        S("gantt-it", InstallKind.Installer, "1.1.0", "1.2.0", failed: true),
        S("splash-it", InstallKind.NotInstalled, null, null, failed: true),
    }, new DateTimeOffset(2026, 10, 28, 16, 5, 0, TimeSpan.Zero), Checking: false, AutomaticChecks: true, Detected: true,
       FailureReason: "no answer in 30 s", Failure: NetFailure.TimedOut);

    /// <summary>Windows Installer could not be read (msi.dll failed): every product Unknown; the latest versions are known.</summary>
    public static UpdateSnapshot Unknown => new(new[]
    {
        S("rst", InstallKind.Unknown, null, "3.1.1"),
        S("gantt-it", InstallKind.Unknown, null, "1.2.0"),
        S("splash-it", InstallKind.Unknown, null, "1.1.0"),
    }, Today0914, Checking: false, AutomaticChecks: true, Detected: true);
}

[Collection(WpfThemeCollection.Name)]
public class TrayUpdatesScreenshotTests
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

    public static TheoryData<string, UiTheme> UpdatesScenarios()
    {
        var data = new TheoryData<string, UiTheme>();
        foreach (var s in new[] { "available", "current", "off", "failed" })
            foreach (var t in new[] { UiTheme.Light, UiTheme.Dark })
                data.Add(s, t);
        return data;
    }

    public static TheoryData<string, UiTheme> AppsScenarios()
    {
        var data = new TheoryData<string, UiTheme>();
        foreach (var s in new[] { "mixed", "current", "unknown" })
            foreach (var t in new[] { UiTheme.Light, UiTheme.Dark })
                data.Add(s, t);
        return data;
    }

    private static UpdateSnapshot For(string scenario) => scenario switch
    {
        "available" or "mixed" => Scenarios.Mixed,
        "current" => Scenarios.Current,
        "off" => Scenarios.Off,
        "unknown" => Scenarios.Unknown,
        _ => Scenarios.Failed,
    };

    private static BitmapSource Capture(Window window, string name)
    {
        WindowCapture.ShowOffScreen(window);
        WindowCapture.DoEvents();
        var bitmap = WindowCapture.Content(window, WindowSize);
        Screenshots.Save(bitmap, name);
        Assert.True(Screenshots.DistinctPixels(bitmap) > 40, $"{name} looks blank");
        return bitmap;
    }

    private static string Name(string page, string scenario, UiTheme theme) =>
        $"tray-{page}-{scenario}-{theme.ToString().ToLowerInvariant()}";

    /// <summary>The rendered text of every visible TextBlock, its Runs and Hyperlinks included (TextBlock.Text omits bound Runs).</summary>
    private static List<string> Texts(DependencyObject root) =>
        WindowCapture.Descendants<TextBlock>(root).Where(t => t.IsVisible)
            .Select(t => new System.Windows.Documents.TextRange(t.ContentStart, t.ContentEnd).Text)
            .ToList();

    private static FrameworkElement ById(DependencyObject root, string id) =>
        WindowCapture.Descendants<FrameworkElement>(root).Single(e => System.Windows.Automation.AutomationProperties.GetAutomationId(e) == id);

    [Theory]
    [MemberData(nameof(UpdatesScenarios))]
    public void The_Updates_page(string scenario, UiTheme theme) => Sta(theme, () =>
    {
        var host = new FakeUpdatesHost { Current = For(scenario) };
        var settings = new FakeTrayHost { UpdateChecks = new UpdateChecksState(host.Current.AutomaticChecks, false) };
        var window = new TrayWindow(new TrayViewModel(settings, TrayPage.Updates, host));
        try
        {
            Capture(window, Name("updates", scenario, theme));
            Assert.Equal(Visibility.Visible, window.UpdatesPage.Visibility);
            var texts = Texts(window.UpdatesPage);
            var failed = ById(window, "Tray.CheckFailed");
            var off = ById(window, "Tray.ChecksOff");
            var note = ById(window, "Tray.RstInstallNote");
            switch (scenario)
            {
                case "available":
                    Assert.Equal("RST 3.1.1 is available", window.RstHeadline.Text);
                    Assert.Equal("Last checked today at 09:14", window.LastChecked.Text);
                    Assert.True(note.IsVisible);
                    Assert.True(ById(window, "Tray.RstUpdate").IsEnabled, "Update installs on click (WU4)");
                    Assert.Equal("Update", ((Button)ById(window, "Tray.RstUpdate")).Content);
                    Assert.Contains("Update available", texts);
                    Assert.Contains("Installed manually", texts);
                    break;
                case "current":
                    Assert.Equal("RST is up to date", window.RstHeadline.Text);
                    Assert.Equal("Last checked yesterday at 09:14", window.LastChecked.Text);
                    Assert.False(note.IsVisible);
                    Assert.Contains(texts, t => t.Contains("1.1.0, for everyone on this PC"));
                    break;
                case "off":
                    Assert.Equal("RST 3.1.0", window.RstHeadline.Text);
                    Assert.Equal("Not checked yet", window.LastChecked.Text);
                    Assert.True(off.IsVisible);
                    Assert.Contains(TrayStrings.Updates_ChecksOff, texts);
                    Assert.Contains("Installed", texts);
                    break;
                default:
                    // A failed check: the last SUCCESSFUL check, the reason, and nothing stale ("up to date", "today").
                    Assert.True(failed.IsVisible);
                    Assert.Equal("Last successful check 28 Oct 2026 at 16:05", window.LastChecked.Text);
                    Assert.Contains(texts, t => t.StartsWith("Couldn't check: GitHub didn't answer in time.", StringComparison.Ordinal));
                    Assert.Equal("RST 3.1.0", window.RstHeadline.Text);
                    Assert.Contains("3.1.0 (couldn't check)", texts);
                    Assert.DoesNotContain("Up to date", texts);
                    Assert.DoesNotContain(texts, t => t.Contains("today"));
                    Assert.Contains("Update available", texts); // Gantt-It's known 1.2.0 is still newer
                    break;
            }
            Assert.Equal(scenario == "failed", failed.IsVisible);
            Assert.Equal(scenario == "off", off.IsVisible);
            Assert.True(window.CheckNowButton.IsEnabled);
            Assert.Equal("Check now", window.CheckNowButton.Content);
            Assert.Equal(2, window.CompanionLines.Items.Count);
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [MemberData(nameof(AppsScenarios))]
    public void The_Apps_page(string scenario, UiTheme theme) => Sta(theme, () =>
    {
        var host = new FakeUpdatesHost { Current = For(scenario) };
        var window = new TrayWindow(new TrayViewModel(new FakeTrayHost(), TrayPage.Apps, host));
        try
        {
            Capture(window, Name("apps", scenario, theme));
            Assert.Equal(Visibility.Visible, window.AppsPage.Visibility);
            Assert.Equal(3, window.AppRows.Items.Count);
            var rows = window.ViewModel.Updates.Rows;
            Assert.Equal(new[] { "RST", "Gantt-It", "Splash-It" }, rows.Select(r => r.Name));
            var texts = Texts(window.AppsPage);
            Assert.Contains(TrayStrings.Apps_InstallsNote, texts);
            Assert.False(ById(window, "Tray.AppsDetecting").IsVisible);
            foreach (var row in rows.Where(r => r.HasAction))
                Assert.True(ById(window, $"Tray.App.{row.Id}.Action").IsEnabled, "Install and Update act on click (WU4)");

            if (scenario == "mixed")
            {
                Assert.Equal(new[] { ProductState.UpdateAvailable, ProductState.InstalledManually, ProductState.NotInstalled },
                             rows.Select(r => r.State));
                Assert.Contains(TrayStrings.App_ManualNote, texts);
                Assert.Equal("Update", rows[0].ActionText);
                Assert.Null(rows[1].ActionText);
                Assert.Equal("Install", rows[2].ActionText);
                Assert.DoesNotContain("Coming soon", texts);
                // Not installed and Installed manually are said once, by the pill, not again as an "Installed" value.
                Assert.DoesNotContain(texts, t => t.Contains("Installed Not installed") || t.Contains("Installed Installed manually"));
                Assert.False(rows[1].ShowInstalledVersion);
                Assert.True(rows[0].ShowInstalledVersion);
            }
            else if (scenario == "unknown")
            {
                // msi.dll failed: Unknown everywhere, never "Not installed" or "Installed manually", no button.
                Assert.All(rows, r => Assert.Equal(ProductState.Unknown, r.State));
                Assert.All(rows, r => Assert.Null(r.ActionText));
                Assert.True(ById(window, "Tray.DetectionFailed").IsVisible);
                Assert.Contains(TrayStrings.State_Unknown, texts);
                Assert.DoesNotContain("Not installed", texts);
                Assert.DoesNotContain("Installed manually", texts);
                Assert.DoesNotContain(TrayStrings.App_ManualNote, texts);
            }
            else
            {
                Assert.Equal(new[] { ProductState.UpToDate, ProductState.UpdateAvailable, ProductState.UpToDate },
                             rows.Select(r => r.State));
                Assert.DoesNotContain(TrayStrings.App_ManualNote, texts);
                Assert.Contains(texts, t => t.Contains("1.1.0, for everyone on this PC"));
            }
            Assert.Equal(scenario == "unknown", ById(window, "Tray.DetectionFailed").IsVisible);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void The_menu_enables_Check_for_updates()
    {
        var checks = 0;
        var actions = new TrayMenuActions(() => { }, () => checks++, () => { }, () => { });
        var item = TrayMenu.Entries(actions).Single(e => e.Command == TrayMenuCommand.CheckForUpdates);
        Assert.Equal(TrayStrings.Menu_CheckForUpdates, item.Text);
        Assert.True(item.Enabled);
        TrayMenu.Run(actions, TrayMenuCommand.CheckForUpdates);
        Assert.Equal(1, checks);
    }
}

public class UpdatesViewModelTests
{
    [Fact]
    public void Rows_say_what_is_installed_where_and_what_is_latest()
    {
        var host = new FakeUpdatesHost { Current = Scenarios.Mixed };
        var vm = new UpdatesViewModel(host, () => new UpdateChecksState(true, false));
        var (rst, gantt, splash) = (vm.Rows[0], vm.Rows[1], vm.Rows[2]);
        Assert.Equal("3.1.0, for you", rst.InstalledText);
        Assert.Equal("3.1.1", rst.LatestText);
        Assert.Equal("Update available", rst.StateText);
        Assert.True(rst.IsUpdateAvailable);
        Assert.Equal("Installed manually", gantt.InstalledText);
        Assert.True(gantt.ShowManualNote);
        Assert.True(gantt.IsManual);
        Assert.Equal("Not installed", splash.InstalledText);
        Assert.Equal("Install", splash.ActionText);
        Assert.True(splash.IsNeutral);
        Assert.Equal(new[] { true, false, true }, vm.Rows.Select(r => r.CanAct)); // Update RST, nothing for a manual copy, Install Splash-It

        var off = new UpdatesViewModel(new FakeUpdatesHost { Current = Scenarios.Off }, () => new UpdateChecksState(false, false));
        Assert.Equal("Not checked yet", off.Rows[0].LatestText);
        Assert.Equal("Installed", off.Rows[0].StateText);
        Assert.Null(off.Rows[0].ActionText);
        var failed = new UpdatesViewModel(new FakeUpdatesHost { Current = Scenarios.Failed }, () => new UpdateChecksState(true, false));
        Assert.Equal("3.1.0 (couldn't check)", failed.Rows[0].LatestText); // the last known latest, marked as such
        Assert.Equal("1.2.0 (couldn't check)", failed.Rows[1].LatestText);
        Assert.Equal("Couldn't check", failed.Rows[2].LatestText);
        Assert.Equal(ProductState.Installed, failed.Rows[0].State); // not "up to date": that would be stale
        Assert.Equal("RST 3.1.0", failed.RstHeadline);
        Assert.True(failed.ShowCheckFailed);
        // WU3 review D3: the plain wording for a timeout; the raw "no answer in 30 s" stays in the log.
        Assert.Equal("Couldn't check: GitHub didn't answer in time. RST tries again at the next daily check. Details are in the log.",
                     failed.CheckFailedText);
        Assert.Equal("Last successful check 28 Oct 2026 at 16:05", failed.LastCheckedText);
    }

    [Fact]
    public void An_RST_that_Windows_Installer_does_not_know_gets_no_Install_button()
    {
        var snapshot = UpdateSnapshot.Empty(ProductCatalog.Default, true);
        var host = new FakeUpdatesHost { Current = snapshot };
        var vm = new UpdatesViewModel(host, () => new UpdateChecksState(true, false));
        Assert.True(vm.IsDetecting);
        Assert.All(vm.Rows, r => Assert.Null(r.ActionText)); // before detection a row claims nothing
        Assert.All(vm.Rows, r => Assert.False(r.IsNeutral || r.IsUpToDate || r.IsUpdateAvailable || r.IsManual));

        host.Publish(snapshot with { Detected = true });
        Assert.False(vm.IsDetecting);
        Assert.Null(vm.Rst.ActionText);
        Assert.Equal("RST", vm.RstHeadline);
        Assert.Null(vm.Rows[1].ActionText); // Install needs a latest version to install (F17 WU4)
        Assert.True(vm.Rows[1].IsNeutral);

        var checkedOnce = snapshot with
        {
            Detected = true,
            Products = snapshot.Products.Select(p => p with { Latest = new ReleaseVersion(1, 2, 0) }).ToList(),
        };
        host.Publish(checkedOnce);
        Assert.Null(vm.Rst.ActionText); // RST is never "installed" from the tray, only updated
        Assert.Equal("Install", vm.Rows[1].ActionText);
    }

    [Fact]
    public void Check_now_calls_the_host_and_is_disabled_while_checking()
    {
        var host = new FakeUpdatesHost { Current = Scenarios.Mixed };
        var vm = new UpdatesViewModel(host, () => new UpdateChecksState(true, false));
        Assert.True(vm.CheckNowCommand.CanExecute(null));
        vm.CheckNowCommand.Execute(null);
        Assert.Equal(new[] { "check" }, host.Calls);

        host.Publish(Scenarios.Mixed with { Checking = true });
        Assert.True(vm.IsChecking);
        Assert.Equal("Checking…", vm.CheckNowText);
        Assert.False(vm.CheckNowCommand.CanExecute(null));
        vm.CheckNowCommand.Execute(null);
        Assert.Single(host.Calls);

        vm.Rows[2].ReleasePageCommand.Execute(vm.Rows[2]);
        Assert.Equal("release:splash-it", host.Calls[^1]);
    }

    [Fact]
    public void Checks_off_wording_follows_the_preset_and_detach_stops_following()
    {
        var host = new FakeUpdatesHost { Current = Scenarios.Off };
        var vm = new UpdatesViewModel(host, () => new UpdateChecksState(false, LockedOff: true));
        Assert.True(vm.ShowChecksOff);
        Assert.Equal(TrayStrings.Updates_ChecksOffLocked, vm.ChecksOffText);
        Assert.Equal(1, host.Subscribers);
        vm.Detach();
        Assert.Equal(0, host.Subscribers);
        host.Publish(Scenarios.Mixed);
        Assert.True(vm.ShowChecksOff, "a detached page no longer changes");
    }

    [Theory]
    [InlineData("2026-10-30T09:14:00Z", "Last checked today at 09:14")]
    [InlineData("2026-10-29T23:59:00Z", "Last checked yesterday at 23:59")]
    [InlineData("2026-10-28T16:05:00Z", "Last checked 28 Oct 2026 at 16:05")]
    public void Last_checked_reads_as_today_yesterday_or_a_date(string at, string expected) =>
        Assert.Equal(expected, UpdatesViewModel.FormatLastChecked(
            DateTimeOffset.Parse(at, CultureInfo.InvariantCulture), new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc));

    [Fact]
    public void Last_checked_uses_the_PCs_time_zone_and_never_checked_says_so()
    {
        var tokyo = TimeZoneInfo.CreateCustomTimeZone("UTC+9", TimeSpan.FromHours(9), "UTC+9", "UTC+9");
        Assert.Equal("Last checked today at 08:30", UpdatesViewModel.FormatLastChecked(
            new DateTimeOffset(2026, 10, 29, 23, 30, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 30, 3, 0, 0, TimeSpan.Zero), tokyo));
        Assert.Equal("Not checked yet", UpdatesViewModel.FormatLastChecked(null, DateTimeOffset.UtcNow, TimeZoneInfo.Utc));
    }
}

/// <summary>
/// AC4 through the tray's own checker, with the tray's own policy (settings file + WindowsState presets). The
/// registry preset tests write HKCU and HKLM <c>Software\Designs-OS\RST\Tray\UpdateChecks</c> = 0 and remove it.
/// One class: its tests run one at a time, so nothing else sees the preset while it is set.
/// </summary>
public sealed class TrayUpdateChecksTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 30, 8, 0, 0, TimeSpan.Zero);
    private static readonly ProductInfo Rst = ProductCatalog.Default.Rst;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RST-TrayUpdates-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public TrayUpdateChecksTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }

    private string SettingsPath => Path.Combine(_root, "tray_settings.json");

    private string StatePath => Path.Combine(_root, "tray_updates.json");

    private void WriteSettings(bool checks) => File.WriteAllText(SettingsPath, "{\"theme\":\"system\",\"checkForUpdates\":" + (checks ? "true" : "false") + "}");

    private sealed record Harness(TrayUpdates Updates, ManualTimeProvider Time, CountingHandler Handler, List<string> Log,
                                  List<IReadOnlyList<ProductStatus>> Announced) : IDisposable
    {
        public void Advance(TimeSpan by) => Time.Advance(by, () => Updates.Loop.Running?.GetAwaiter().GetResult());

        public void Dispose() => Updates.Dispose();
    }

    /// <param name="notificationShown">What the tray's notification does: shown (the tray then calls MarkAnnounced) or not.</param>
    private Harness Make(CountingHandler? handler = null, FakeInstalledProducts? installed = null, Func<bool>? policy = null,
                         bool notificationShown = true, IReadOnlyDictionary<string, string>? manifests = null)
    {
        handler ??= new CountingHandler(CountingHandler.Redirect(new Dictionary<string, string>
        {
            ["jedbjorn/rst-c"] = "v3.1.1", ["jedbjorn/gantt-it"] = "v1.1.0", ["jedbjorn/splash-it"] = "v1.1.0",
        }));
        installed ??= new FakeInstalledProducts().With(ProductCatalog.RstUpgradeCode, "3.1.0");
        policy ??= TrayUpdates.SettingsAndPresetPolicy(SettingsPath, WindowsState.UpdateChecksPresets);
        var time = new ManualTimeProvider(Start);
        var log = new List<string>();
        var announced = new List<IReadOnlyList<ProductStatus>>();
        TrayUpdates? updates = null;
        updates = new TrayUpdates(
            ProductCatalog.Default,
            TrayHttp.CreateClient(handler),
            installed,
            () => manifests ?? new Dictionary<string, string>(),
            policy,
            policy(), // the tray passes the value it read at start
            time,
            StatePath,
            action => action(),
            m => { lock (log) log.Add(m); },
            () => TimeSpan.FromMinutes(5));
        updates.NewVersionsFound += (_, found) =>
        {
            announced.Add(found);
            if (notificationShown) updates!.MarkAnnounced(found); // as TrayHost does once ShowNotification succeeds
        };
        updates.Start();
        return new Harness(updates, time, handler, log, announced);
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> Answer(Func<string, string?> tagFor, Func<bool>? fail = null) => request =>
    {
        if (fail?.Invoke() == true) throw new HttpRequestException("No such host is known. (github.com:443)");
        var path = request.RequestUri!.AbsolutePath;
        var repo = path.Substring(1, path.Length - 1 - "/releases/latest".Length);
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = tagFor(repo) is { } tag
            ? new Uri($"https://github.com/{repo}/releases/tag/{tag}")
            : new Uri($"https://github.com/{repo}/releases");
        return response;
    };

    [Fact]
    public void A_failed_check_keeps_the_last_success_and_claims_nothing_stale()
    {
        WriteSettings(checks: true);
        var offline = false;
        var handler = new CountingHandler(Answer(repo => repo == "jedbjorn/gantt-it" ? "v1.2.0" : repo == "jedbjorn/rst-c" ? "v3.1.0" : null,
                                                 () => offline));
        var installed = new FakeInstalledProducts()
            .With(ProductCatalog.RstUpgradeCode, "3.1.0")
            .With(ProductCatalog.Default.Find("gantt-it")!.UpgradeCodes[0], "1.1.0");
        using var h = Make(handler, installed);
        h.Advance(TimeSpan.FromMinutes(5));
        var good = h.Updates.Current;
        Assert.Null(good.FailureReason);
        Assert.Equal(Start.AddMinutes(5), good.LastCheckedUtc);
        Assert.Equal(ProductState.UpToDate, good.Rst!.State);

        offline = true;
        h.Advance(TimeSpan.FromHours(24));
        var failed = h.Updates.Current;
        Assert.Equal(Start.AddMinutes(5), failed.LastCheckedUtc); // the last SUCCESS, not the failed attempt
        Assert.StartsWith("HttpRequestException: No such host", failed.FailureReason);
        Assert.True(failed.LastCheckFailed);
        Assert.Equal(ProductState.Installed, failed.Rst!.State); // never "up to date" after a failure
        Assert.Equal("3.1.0", failed.Rst.Latest.ToString());
        Assert.Equal(new[] { ProductState.UpdateAvailable, ProductState.NotInstalled }, failed.Companions.Select(c => c.State));

        // Exactly what the screenshot fixture shows: "RST 3.1.0", "3.1.0 (couldn't check)", "Last successful check …".
        var vm = new UpdatesViewModel(new FakeUpdatesHost { Current = failed, UtcNow = Start.AddHours(24).AddMinutes(5) },
                                      () => new UpdateChecksState(true, false));
        Assert.Equal("RST 3.1.0", vm.RstHeadline);
        Assert.Equal("3.1.0 (couldn't check)", vm.Rst.LatestText);
        Assert.Equal("Last successful check yesterday at 08:05", vm.LastCheckedText);
        // WU3 review D3: the page says it plainly; the raw reason stays in the snapshot and the log.
        Assert.Equal(NetFailure.Offline, failed.Failure);
        Assert.Equal("Couldn't check: couldn't reach GitHub. RST tries again at the next daily check. Details are in the log.", vm.CheckFailedText);
        Assert.DoesNotContain("Exception", vm.CheckFailedText);

        offline = false;
        h.Advance(TimeSpan.FromHours(24));
        Assert.Null(h.Updates.Current.FailureReason);
        Assert.Equal(Start.AddMinutes(5).AddHours(48), h.Updates.Current.LastCheckedUtc);
    }

    [Fact]
    public void An_unreadable_Windows_Installer_shows_Unknown_never_manual_and_announces_nothing()
    {
        WriteSettings(checks: true);
        var gantt = ProductCatalog.Default.Find("gantt-it")!;
        var installed = new FakeInstalledProducts { Unreadable = true };
        using var h = Make(installed: installed, manifests: new Dictionary<string, string> { [gantt.AddinClientId!] = @"C:\x\GanttIt.addin" });
        h.Advance(TimeSpan.FromMinutes(5));
        var snapshot = h.Updates.Current;
        Assert.All(snapshot.Products, p => Assert.Equal(ProductState.Unknown, p.State));
        Assert.True(snapshot.DetectionFailed);
        Assert.Empty(h.Announced);
        var vm = new UpdatesViewModel(new FakeUpdatesHost { Current = snapshot }, () => new UpdateChecksState(true, false));
        Assert.True(vm.ShowDetectionFailed);
        Assert.All(vm.Rows, r => Assert.Equal(TrayStrings.State_Unknown, r.StateText));
        Assert.All(vm.Rows, r => Assert.Null(r.ActionText));
    }

    [Fact]
    public void A_version_is_announced_again_until_its_notification_is_shown()
    {
        WriteSettings(checks: true);
        using (var h = Make(notificationShown: false))
        {
            h.Advance(TimeSpan.FromMinutes(5));
            h.Advance(TimeSpan.FromHours(24));
            Assert.Equal(2, h.Announced.Count); // not shown: offered again at the next check
        }
        using (var h = Make(notificationShown: true))
        {
            h.Advance(TimeSpan.FromMinutes(5));
            h.Advance(TimeSpan.FromHours(24));
            Assert.Single(h.Announced); // shown and recorded: once
        }
        using var restarted = Make(notificationShown: true);
        restarted.Advance(TimeSpan.FromHours(48));
        Assert.Empty(restarted.Announced);
    }

    [Fact]
    public void A_repo_with_no_full_release_says_No_release_yet()
    {
        WriteSettings(checks: true);
        var handler = new CountingHandler(Answer(repo => repo == "jedbjorn/rst-c" ? "v3.1.1" : null));
        using var h = Make(handler);
        h.Advance(TimeSpan.FromMinutes(5));
        var vm = new UpdatesViewModel(new FakeUpdatesHost { Current = h.Updates.Current }, () => new UpdateChecksState(true, false));
        Assert.Equal("3.1.1", vm.Rows[0].LatestText);
        Assert.Equal("No release yet", vm.Rows[1].LatestText);
        Assert.Equal("No release yet", vm.Rows[2].LatestText);
        Assert.False(h.Updates.Current.LastCheckFailed);
        using var restarted = Make(handler);
        Assert.True(restarted.Updates.Current.Companions.All(c => c.NoRelease), "remembered across a restart");
    }

    [Fact]
    public async Task Check_now_within_30_seconds_shows_the_last_result_without_asking_again()
    {
        WriteSettings(checks: false);
        using var h = Make();
        await h.Updates.CheckNowAsync();
        Assert.Equal(3, h.Handler.Count);
        h.Time.Advance(TimeSpan.FromSeconds(10));
        await h.Updates.CheckNowAsync();
        await h.Updates.CheckNowAsync();
        Assert.Equal(3, h.Handler.Count);
        Assert.Contains(h.Log, l => l.Contains("showing its result, no request made"));
        h.Time.Advance(TimeSpan.FromSeconds(25));
        await h.Updates.CheckNowAsync();
        Assert.Equal(6, h.Handler.Count);
    }

    [Fact]
    public void The_pages_snapshot_reads_no_settings_file_or_registry()
    {
        WriteSettings(checks: true);
        var reads = 0;
        using var h = Make(policy: () => { Interlocked.Increment(ref reads); return true; });
        var before = reads;
        for (var i = 0; i < 20; i++) _ = h.Updates.Current;
        Assert.Equal(before, reads);
        Assert.True(h.Updates.Current.AutomaticChecks);
        h.Updates.AutomaticChecksChanged(false);
        Assert.False(h.Updates.Current.AutomaticChecks);
        Assert.Equal(before, reads);
    }

    private static async Task WithPreset(RegistryKey hive, Func<Task> body)
    {
        using var key = hive.CreateSubKey(TrayPaths.PresetKey, writable: true);
        key.SetValue(TrayPaths.UpdateChecksValueName, 0, RegistryValueKind.DWord);
        try
        {
            await body();
        }
        finally
        {
            key.DeleteValue(TrayPaths.UpdateChecksValueName, throwOnMissingValue: false);
        }
    }

    [Fact]
    public async Task Setting_off_makes_zero_requests_in_48_hours_until_Check_now()
    {
        WriteSettings(checks: false);
        using var h = Make();
        h.Advance(TimeSpan.FromHours(48));
        Assert.Equal(0, h.Handler.Count);
        Assert.False(h.Updates.Current.AutomaticChecks);
        Assert.Contains(h.Log, l => l.Contains("automatic checks are off"));

        await h.Updates.CheckNowAsync();
        Assert.Equal(3, h.Handler.Count);
        Assert.All(h.Handler.Requests, r => Assert.Equal(HttpMethod.Head, r.Method));
        _output.WriteLine("setting off: 0 requests in 48 h; Check now: " + string.Join(", ", h.Handler.Requests.Select(r => r.Uri)));
    }

    [Fact]
    public async Task The_HKCU_preset_makes_zero_requests_in_48_hours_until_Check_now()
    {
        WriteSettings(checks: true);
        await WithPreset(Registry.CurrentUser, async () =>
        {
            using var h = Make();
            h.Advance(TimeSpan.FromHours(48));
            Assert.Equal(0, h.Handler.Count);
            Assert.False(h.Updates.Current.AutomaticChecks);
            await h.Updates.CheckNowAsync();
            Assert.Equal(3, h.Handler.Count);
        });
        Assert.True(TrayUpdates.SettingsAndPresetPolicy(SettingsPath, WindowsState.UpdateChecksPresets)(), "the preset is removed again");
        _output.WriteLine("HKCU UpdateChecks=0: 0 requests in 48 h with the setting on; Check now: 3");
    }

    [Fact]
    public async Task The_HKLM_preset_makes_zero_requests_in_48_hours_until_Check_now()
    {
        WriteSettings(checks: true);
        await WithPreset(Registry.LocalMachine, async () =>
        {
            using var h = Make();
            h.Advance(TimeSpan.FromHours(48));
            Assert.Equal(0, h.Handler.Count);
            Assert.False(h.Updates.Current.AutomaticChecks);
            await h.Updates.CheckNowAsync();
            Assert.Equal(3, h.Handler.Count);
        });
        Assert.True(TrayUpdates.SettingsAndPresetPolicy(SettingsPath, WindowsState.UpdateChecksPresets)(), "the preset is removed again");
        _output.WriteLine("HKLM UpdateChecks=0: 0 requests in 48 h with the setting on; Check now: 3");
    }

    [Fact]
    public void Checks_on_ask_twice_in_48_hours_and_announce_a_version_once()
    {
        WriteSettings(checks: true);
        using (var h = Make())
        {
            Assert.Equal(0, h.Handler.Count);
            h.Advance(TimeSpan.FromMinutes(4));
            Assert.Equal(0, h.Handler.Count); // the start delay
            h.Advance(TimeSpan.FromHours(48) - TimeSpan.FromMinutes(4));
            Assert.Equal(6, h.Handler.Count);
            Assert.All(h.Handler.Requests, r => Assert.True(UpdateHosts.IsAllowed(r.Uri, UpdateHostUse.Feed)));

            var announced = Assert.Single(h.Announced);
            Assert.Equal("rst", Assert.Single(announced).Product.Id);
            Assert.Equal("3.1.1", announced[0].Latest.ToString());
            var snapshot = h.Updates.Current;
            Assert.Equal(ProductState.UpdateAvailable, snapshot.Rst!.State);
            Assert.Equal(new[] { ProductState.NotInstalled, ProductState.NotInstalled }, snapshot.Companions.Select(c => c.State));
            Assert.NotNull(snapshot.LastCheckedUtc);
        }

        // After a restart (the same state file), the same version is not announced again.
        using var restarted = Make();
        Assert.Equal("3.1.1", restarted.Updates.Current.Rst!.Latest.ToString());
        restarted.Advance(TimeSpan.FromHours(48));
        Assert.Empty(restarted.Announced);
    }

    [Fact]
    public void A_failing_network_is_logged_once_and_retried_only_at_the_next_slot()
    {
        WriteSettings(checks: true);
        var handler = new CountingHandler(_ => throw new HttpRequestException("No such host is known. (github.com:443)"));
        using var h = Make(handler);
        h.Advance(TimeSpan.FromHours(48));
        Assert.Equal(6, handler.Count);
        var lines = h.Log.Where(l => l.StartsWith("update check: couldn't check", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, lines.Count); // one per product, not one per try
        Assert.True(h.Updates.Current.LastCheckFailed);
        Assert.Empty(h.Announced);
        _output.WriteLine(string.Join(Environment.NewLine, lines));
    }

    [Fact]
    public void The_handler_follows_no_redirects_and_uses_the_system_proxy()
    {
        using var handler = TrayHttp.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.True(handler.UseProxy);
        Assert.Null(handler.Proxy); // null with UseProxy: HttpClient.DefaultProxy, the Windows proxy settings
        Assert.Same(CredentialCache.DefaultCredentials, handler.DefaultProxyCredentials);
        Assert.Null(handler.Credentials);
        Assert.False(handler.UseCookies);
        using var client = TrayHttp.CreateClient(new CountingHandler());
        Assert.Equal("RST-Tray", client.DefaultRequestHeaders.UserAgent.ToString());
    }

    /// <summary>
    /// msi.dll answers (from System32) for an unknown code and the real ones. The CI runner is elevated, so this
    /// does not show that no admin is needed; that is AC5's VM half (an unelevated user).
    /// </summary>
    [Fact]
    public void Windows_Installer_answers_for_unknown_and_real_codes()
    {
        var msi = new MsiProducts(m => _output.WriteLine(m));
        Assert.Empty(msi.Related("00000000-0000-0000-0000-000000000001"));
        Assert.Empty(msi.Related("not a guid"));
        foreach (var product in ProductCatalog.Default.Products)
        {
            var found = ProductDetection.Query(msi, product);
            Assert.NotNull(found); // readable on a healthy Windows: never Unknown here
            _output.WriteLine($"{product.Name}: {(found!.Count == 0 ? "none" : string.Join(", ", found))}");
        }
    }
}

/// <summary>Runs only when ci-tray-detect.ps1 has set RST_MSI_FIXTURE_EXPECT (Windows CI, the tray-detect job).</summary>
public sealed class MsiFixtureFactAttribute : FactAttribute
{
    public const string Variable = "RST_MSI_FIXTURE_EXPECT";

    public MsiFixtureFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"{Variable} is not set: run by installer/ci-tray-detect.ps1 after it installs the fixture MSI";
    }
}

/// <summary>
/// AC5's fixture half: the tray's detection (MsiProducts, then ProductDetection with the real manifest scan)
/// against spec #15's fixture companion MSI, installed by ci-tray-detect.ps1 with msiexec. The script writes what
/// Windows Installer reports (its COM automation interface and the MSI's own Property table) to the expectation
/// file; this test must find exactly that.
/// </summary>
public sealed class MsiFixtureDetectionTests
{
    private readonly ITestOutputHelper _output;

    public MsiFixtureDetectionTests(ITestOutputHelper output) => _output = output;

    private sealed record ExpectedProduct(string ProductCode, string Version, int Context);

    private sealed record Expectation(string Scenario, string UpgradeCode, string ClientId, string Kind, List<ExpectedProduct> Products);

    [MsiFixtureFact]
    public void Detection_matches_what_msiexec_installed()
    {
        var path = Environment.GetEnvironmentVariable(MsiFixtureFactAttribute.Variable)!;
        var expect = JsonSerializer.Deserialize<Expectation>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var fixture = new ProductInfo("companion-fixture", "RST Companion Fixture", "test only", "jedbjorn/rst-c",
                                      new[] { ProductCatalog.NormalizeGuid(expect.UpgradeCode)! },
                                      ProductCatalog.NormalizeGuid(expect.ClientId)!.ToLowerInvariant());

        var found = new MsiProducts(m => _output.WriteLine(m)).Related(expect.UpgradeCode);
        var manifests = ProductDetection.InstalledClientIds(m => _output.WriteLine(m));
        var install = ProductDetection.Decide(fixture, found, manifests);
        _output.WriteLine($"[{expect.Scenario}] detected {install.Kind}: {string.Join(", ", found)}");

        static InstallContext Context(int native) => native switch
        {
            1 => InstallContext.UserManaged,
            2 => InstallContext.User,
            4 => InstallContext.Machine,
            _ => throw new ArgumentOutOfRangeException(nameof(native), native, "not an MSIINSTALLCONTEXT"),
        };
        var expected = expect.Products
            .Select(p => new InstalledProduct(p.ProductCode.ToUpperInvariant(), p.Version, Context(p.Context)))
            .OrderBy(p => p.ProductCode).ToList();
        var actual = found.Select(p => p with { ProductCode = p.ProductCode.ToUpperInvariant() }).OrderBy(p => p.ProductCode).ToList();
        Assert.Equal(expected, actual);
        Assert.Equal(Enum.Parse<InstallKind>(expect.Kind), install.Kind);
        if (install.Kind == InstallKind.Installer)
            Assert.Equal(expected.OrderByDescending(p => p.Version).First().VersionText, install.Installed!.VersionText);

        var result = Environment.GetEnvironmentVariable("RST_MSI_FIXTURE_RESULT");
        if (!string.IsNullOrEmpty(result))
            File.WriteAllText(result, JsonSerializer.Serialize(new
            {
                expect.Scenario,
                Kind = install.Kind.ToString(),
                Products = actual.Select(p => new { p.ProductCode, p.VersionText, Context = p.Context.ToString() }),
                install.ManifestPath,
            }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
