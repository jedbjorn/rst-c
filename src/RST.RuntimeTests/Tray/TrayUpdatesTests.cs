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

    /// <summary>The last check failed: RST keeps its last known latest, the companions were never reached.</summary>
    public static UpdateSnapshot Failed => new(new[]
    {
        S("rst", InstallKind.Installer, "3.1.0", "3.1.0", failed: true),
        S("gantt-it", InstallKind.Installer, "1.1.0", null, failed: true),
        S("splash-it", InstallKind.NotInstalled, null, null, failed: true),
    }, new DateTimeOffset(2026, 10, 28, 16, 5, 0, TimeSpan.Zero), Checking: false, AutomaticChecks: true, Detected: true);
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
        foreach (var s in new[] { "mixed", "current" })
            foreach (var t in new[] { UiTheme.Light, UiTheme.Dark })
                data.Add(s, t);
        return data;
    }

    private static UpdateSnapshot For(string scenario) => scenario switch
    {
        "available" or "mixed" => Scenarios.Mixed,
        "current" => Scenarios.Current,
        "off" => Scenarios.Off,
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
                    Assert.False(ById(window, "Tray.RstUpdate").IsEnabled, "installs are WU4's");
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
                    Assert.True(failed.IsVisible);
                    Assert.Equal("Last checked 28 Oct 2026 at 16:05", window.LastChecked.Text);
                    Assert.Contains(texts, t => t.Contains("Couldn't check"));
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
            Assert.Contains(TrayStrings.Apps_InstallsComingSoon, texts);
            Assert.False(ById(window, "Tray.AppsDetecting").IsVisible);
            foreach (var row in rows.Where(r => r.HasAction))
                Assert.False(ById(window, $"Tray.App.{row.Id}.Action").IsEnabled, "Install and Update are WU4's");

            if (scenario == "mixed")
            {
                Assert.Equal(new[] { ProductState.UpdateAvailable, ProductState.InstalledManually, ProductState.NotInstalled },
                             rows.Select(r => r.State));
                Assert.Contains(TrayStrings.App_ManualNote, texts);
                Assert.Equal("Update", rows[0].ActionText);
                Assert.Null(rows[1].ActionText);
                Assert.Equal("Install", rows[2].ActionText);
                Assert.Contains("Coming soon", texts);
            }
            else
            {
                Assert.Equal(new[] { ProductState.UpToDate, ProductState.UpdateAvailable, ProductState.UpToDate },
                             rows.Select(r => r.State));
                Assert.DoesNotContain(TrayStrings.App_ManualNote, texts);
                Assert.Contains(texts, t => t.Contains("1.1.0, for everyone on this PC"));
            }
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void The_menu_enables_Check_for_updates() => Sta(UiTheme.Light, () =>
    {
        var checks = 0;
        var menu = TrayMenu.Build(new TrayMenuActions(() => { }, () => checks++, () => { }, () => { }));
        var item = menu.Items.OfType<MenuItem>().Single(i => (string)i.Header == TrayStrings.Menu_CheckForUpdates);
        Assert.True(item.IsEnabled);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(1, checks);
    });
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
        Assert.All(vm.Rows, r => Assert.False(r.CanAct));

        var off = new UpdatesViewModel(new FakeUpdatesHost { Current = Scenarios.Off }, () => new UpdateChecksState(false, false));
        Assert.Equal("Not checked yet", off.Rows[0].LatestText);
        Assert.Equal("Installed", off.Rows[0].StateText);
        Assert.Null(off.Rows[0].ActionText);
        var failed = new UpdatesViewModel(new FakeUpdatesHost { Current = Scenarios.Failed }, () => new UpdateChecksState(true, false));
        Assert.Equal("3.1.0", failed.Rows[0].LatestText); // the last known latest is kept
        Assert.Equal("Couldn't check", failed.Rows[1].LatestText);
        Assert.True(failed.ShowCheckFailed);
    }

    [Fact]
    public void An_RST_that_Windows_Installer_does_not_know_gets_no_Install_button()
    {
        var snapshot = UpdateSnapshot.Empty(ProductCatalog.Default, true);
        var vm = new UpdatesViewModel(new FakeUpdatesHost { Current = snapshot }, () => new UpdateChecksState(true, false));
        Assert.Null(vm.Rst.ActionText);
        Assert.Equal("RST", vm.RstHeadline);
        Assert.True(vm.IsDetecting);
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

    private Harness Make(CountingHandler? handler = null, FakeInstalledProducts? installed = null, Func<bool>? policy = null)
    {
        handler ??= new CountingHandler(CountingHandler.Redirect(new Dictionary<string, string>
        {
            ["jedbjorn/rst-c"] = "v3.1.1", ["jedbjorn/gantt-it"] = "v1.1.0", ["jedbjorn/splash-it"] = "v1.1.0",
        }));
        installed ??= new FakeInstalledProducts().With(ProductCatalog.RstUpgradeCode, "3.1.0");
        var time = new ManualTimeProvider(Start);
        var log = new List<string>();
        var announced = new List<IReadOnlyList<ProductStatus>>();
        var updates = new TrayUpdates(
            ProductCatalog.Default,
            TrayHttp.CreateClient(handler),
            installed,
            () => new Dictionary<string, string>(),
            policy ?? TrayUpdates.SettingsAndPresetPolicy(SettingsPath, WindowsState.UpdateChecksPresets),
            time,
            StatePath,
            action => action(),
            m => { lock (log) log.Add(m); },
            () => TimeSpan.FromMinutes(5));
        updates.NewVersionsFound += (_, found) => announced.Add(found);
        updates.Start();
        return new Harness(updates, time, handler, log, announced);
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

    [Fact]
    public void Windows_Installer_answers_without_admin_for_unknown_and_real_codes()
    {
        var msi = new MsiProducts(m => _output.WriteLine(m));
        Assert.Empty(msi.Related("00000000-0000-0000-0000-000000000001"));
        Assert.Empty(msi.Related("not a guid"));
        foreach (var product in ProductCatalog.Default.Products)
        {
            var found = ProductDetection.Query(msi, product);
            _output.WriteLine($"{product.Name}: {(found.Count == 0 ? "none" : string.Join(", ", found))}");
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
        var manifests = ProductDetection.ManifestClientIds(ProductDetection.ManifestRoots());
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
