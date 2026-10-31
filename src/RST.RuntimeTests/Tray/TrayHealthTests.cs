// rst-c's own (not vendored): the tray's Health page (spec #25 Scope 1, Device, Look and Feel; F19 WU2). Device
// mirrors the add-in's Health tab (decisions #53, #59): the same sections from the same report, with the installed
// Revit years in the Revit section, saved to tray_health_scan.json; Clean Junk Files lists and cleans each
// year-specific target once per Revit year found (decision #56), over a fake user-folder tree (CleanupFolders), so
// nothing here touches the runner's own folders. The screenshots are tray-health-device-*, tray-health-junk-* and
// (from TrayWindowScreenshotTests, the page opening on Models) tray-health-*; AC 6 and 7 are checked on the VM.

using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using RST.Core.Health;
using RST.Core.Profiles;
using RST.Core.Theming;
using RST.RuntimeTests.Health;
using RST.RuntimeTests.Support;
using RST.Tray.Windows;
using RST.UI.Health;
using RST.UI.Theming;
using Xunit;

namespace RST.RuntimeTests.Tray;

/// <summary>A user's folders in a temp tree: <c>%LocalAppData%</c>, <c>%AppData%</c> and <c>%UserProfile%</c>.</summary>
internal sealed class FakeUserFolders : IDisposable
{
    private readonly TempFolder _root = new();

    public FakeUserFolders()
    {
        Folders = new CleanupFolders(
            System.IO.Path.Combine(_root.Path, "Local"),
            System.IO.Path.Combine(_root.Path, "Roaming"),
            System.IO.Path.Combine(_root.Path, "User"));
        Directory.CreateDirectory(Folders.LocalAppData);
        Directory.CreateDirectory(Folders.AppData);
        Directory.CreateDirectory(Folders.UserProfile);
    }

    public CleanupFolders Folders { get; }

    public string Journals(int year) => Dir(Folders.LocalAppData, "Autodesk", "Revit", CleanupYears.FolderName(year), "Journals");

    public string CollabCache(int year) => Dir(Folders.LocalAppData, "Autodesk", "Revit", CleanupYears.FolderName(year), "CollaborationCache");

    public string RevitIni(int year) => System.IO.Path.Combine(Dir(Folders.AppData, "Autodesk", "Revit", CleanupYears.FolderName(year)), "Revit.ini");

    public string Temp => Dir(Folders.LocalAppData, "Temp");

    /// <summary>Two Revit years with journals, a cache and a recent-file list each, and a temp file.</summary>
    public FakeUserFolders WithTwoYears()
    {
        foreach (var year in new[] { 2026, 2027 })
        {
            for (var i = 0; i < 3; i++) File.WriteAllText(System.IO.Path.Combine(Journals(year), $"journal.{i:0000}.txt"), "journal");
            File.WriteAllText(System.IO.Path.Combine(CollabCache(year), "cache.bin"), "cache");
            File.WriteAllText(RevitIni(year), "[Recent File List]\r\nFile1=C:\\a.rvt\r\n", System.Text.Encoding.Unicode);
        }
        File.WriteAllText(System.IO.Path.Combine(Temp, "tmp.txt"), "temp");
        return this;
    }

    public static string Dir(params string[] parts)
    {
        var path = System.IO.Path.Combine(parts);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose() => _root.Dispose();
}

internal static class TrayHealthSamples
{
    public static readonly IReadOnlyList<RevitInstallInfo> Installs =
    [
        new() { Year = 2026, Build = "26.0.4.409", InstallDir = @"C:\Program Files\Autodesk\Revit 2026" },
        new() { Year = 2027, Build = "27.0.10.28", InstallDir = @"C:\Program Files\Autodesk\Revit 2027" },
    ];

    /// <summary>What the tray's scan records: the add-in's sample machine, no running Revit, two installed years.</summary>
    public static HealthSnapshot Snapshot()
    {
        var s = HealthSamples.Snapshot();
        s.Revit = new RevitInfo();
        s.RevitInstalls = [.. Installs];
        return s;
    }

    public static HealthViewModelOptions Options() => new()
    {
        RunInBackground = work =>
        {
            work();
            return Task.CompletedTask;
        },
        Culture = CultureInfo.GetCultureInfo("en-US"),
        Zone = TimeZoneInfo.Utc,
        ToastSchedule = (_, _) => new NoTimer(),
    };

    private sealed class NoTimer : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

/// <summary>The Revit section in the tray's scope, and the add-in's left as it was.</summary>
public class TrayHealthReportTests
{
    private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");

    private static HealthSection Revit(HealthReport report) => report.Sections.Single(s => s.Title == "Revit");

    [Fact]
    public void The_tray_lists_each_installed_Revit_year_with_its_build_in_place_of_the_running_Revits_rows()
    {
        var report = HealthReport.Build(TrayHealthSamples.Snapshot(), EnUs, TimeZoneInfo.Utc, HealthScope.Tray);
        var rows = Revit(report).Rows.Select(r => r.ToString()).ToList();
        Assert.Equal(new[]
        {
            @"Revit 2026 | 26.0.4.409 | C:\Program Files\Autodesk\Revit 2026",
            @"Revit 2027 | 27.0.10.28 | C:\Program Files\Autodesk\Revit 2027",
        }, rows);
        Assert.Equal(HealthSeverity.Info, Revit(report).Dot);

        // Everything else is the add-in's, row for row.
        var addIn = HealthReport.Build(TrayHealthSamples.Snapshot(), EnUs, TimeZoneInfo.Utc);
        Assert.Equal(new[] { "Hardware", "Display & Network", "Revit", "Operating System" }, report.Sections.Select(s => s.Title));
        foreach (var title in new[] { "Hardware", "Display & Network", "Operating System" })
            Assert.Equal(addIn.Sections.Single(s => s.Title == title).Rows.Select(r => r.ToString()),
                         report.Sections.Single(s => s.Title == title).Rows.Select(r => r.ToString()));
        Assert.Equal(addIn.Device, report.Device);
        Assert.Equal(addIn.Captured, report.Captured);
    }

    [Fact]
    public void Before_a_scan_the_tray_reads_a_dash_and_a_scan_with_no_Revit_says_so()
    {
        var empty = Revit(HealthReport.Build(null, EnUs, TimeZoneInfo.Utc, HealthScope.Tray));
        var row = Assert.Single(empty.Rows);
        Assert.Equal("Installed", row.Label);
        Assert.True(row.IsMissing);

        var none = TrayHealthSamples.Snapshot();
        none.RevitInstalls = [];
        Assert.Equal("Installed | No Revit 2025 or later found",
            Assert.Single(Revit(HealthReport.Build(none, EnUs, TimeZoneInfo.Utc, HealthScope.Tray)).Rows).ToString());

        var unreadable = TrayHealthSamples.Snapshot();
        unreadable.RevitInstalls = [new RevitInstallInfo { Year = 2026 }];
        Assert.Equal("Revit 2026 | —", Assert.Single(Revit(HealthReport.Build(unreadable, EnUs, TimeZoneInfo.Utc, HealthScope.Tray)).Rows).ToString());
    }

    [Fact]
    public void The_add_ins_Revit_section_is_unchanged_by_a_snapshot_carrying_installs()
    {
        var snap = HealthSamples.Snapshot();
        snap.RevitInstalls = [.. TrayHealthSamples.Installs];
        var labels = Revit(HealthReport.Build(snap, EnUs, TimeZoneInfo.Utc)).Rows.Select(r => r.Label);
        Assert.Equal(new[] { "Version", "Username", "Hardware Accel", "Model", "Warnings" }, labels);
    }

    [Fact]
    public void The_tray_snapshot_is_its_own_file_beside_Revits()
    {
        Assert.Equal("tray_health_scan.json", System.IO.Path.GetFileName(DeviceHealthService.DefaultSnapshotPath));
        Assert.Equal(System.IO.Path.GetDirectoryName(HealthService.DefaultSnapshotPath), System.IO.Path.GetDirectoryName(DeviceHealthService.DefaultSnapshotPath));
        Assert.NotEqual(HealthService.DefaultSnapshotPath, DeviceHealthService.DefaultSnapshotPath);
    }

    [Fact]
    public void A_builds_version_is_read_from_the_exe_and_a_missing_exe_has_none()
    {
        var notepad = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "notepad.exe");
        var build = DeviceHealthService.ReadBuild(notepad);
        Assert.Matches(@"^\d+\.\d+\.\d+\.\d+$", build);
        Assert.Equal("", DeviceHealthService.ReadBuild(@"C:\nowhere\Revit.exe"));
    }
}

/// <summary>Clean Junk Files in the tray: one row per year found, a row cleans only its year. Uses the process-wide work flag.</summary>
[Collection(HealthWorkTests.Name)]
public sealed class TrayJunkCleanupTests : IDisposable
{
    private readonly FakeUserFolders _user = new();
    private readonly TempFolder _snapshots = new();

    public void Dispose()
    {
        _user.Dispose();
        _snapshots.Dispose();
    }

    private DeviceHealthService Service(Func<IReadOnlyList<CleanupTarget>>? targets = null) => new(
        _snapshots.File("tray_health_scan.json"),
        () => TrayHealthSamples.Installs,
        targets ?? CleanupDefaults.Build,
        _user.Folders);

    /// <summary>The handle of the listed row named <paramref name="name"/>.</summary>
    private static string IdOf(IReadOnlyList<CleanupTarget> rows, string name) => rows.Single(r => r.Name == name).Id;

    [Fact]
    public void Each_year_specific_target_is_listed_once_per_Revit_year_found()
    {
        _user.WithTwoYears();
        var rows = Service().GetCleanupTargets();
        Assert.Equal(new[]
        {
            "Temp", "PacCache",
            "Journals — 2026", "Journals — 2027",
            "Collaboration Cache — 2026", "Collaboration Cache — 2027",
            "Recent File List — 2026", "Recent File List — 2027",
            "RST Logs",
        }, rows.Select(r => r.Name));
        var journals2027 = rows.Single(r => r.Name == "Journals — 2027");
        Assert.Equal(@"%LocalAppData%\Autodesk\Revit\Autodesk Revit 2027\Journals", journals2027.Path);
        Assert.Equal(CleanupTarget.KindIniRecentFiles, rows.Single(r => r.Name == "Recent File List — 2026").Kind);
        // Every row has its own handle, and none is a profile id or built from one (review F1).
        Assert.Equal(rows.Count, rows.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(rows, r => Assert.DoesNotContain("rst.cleanup", r.Id, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_profiles_enabled_targets_set_the_rows_and_a_year_specific_target_with_no_year_is_listed_once()
    {
        // No Revit folder at all: Journals has no year, so it is listed once, as in the add-in.
        var profile = CleanupDefaults.Build();
        profile.Single(t => t.Id == CleanupDefaults.IdCollabCache).Enabled = false;
        var rows = Service(() => profile).GetCleanupTargets();
        Assert.Equal(new[] { "Temp", "PacCache", "Journals", "Recent File List", "RST Logs" }, rows.Select(r => r.Name));
    }

    [Fact]
    public void A_profile_path_that_is_not_a_preset_is_never_fanned_out_or_cleaned()
    {
        _user.WithTwoYears();
        var custom = new CleanupTarget
        {
            Id = "custom", Name = "Custom", Kind = CleanupTarget.KindDirectory, Enabled = true,
            Path = @"%LocalAppData%\Autodesk\Revit\{revit-version}",
        };
        var service = Service(() => [custom]);
        var rows = service.GetCleanupTargets();
        Assert.Equal("Custom", Assert.Single(rows).Name);

        // The cleaner refuses it, so nothing under the Revit folders goes.
        Assert.Empty(service.Clean([rows[0].Id]).Deleted);
        Assert.Equal(3, Directory.GetFiles(_user.Journals(2026)).Length);
    }

    [Fact]
    public void One_run_cleans_every_chosen_year_and_counts_a_locked_file_as_skipped()
    {
        _user.WithTwoYears();
        var service = Service();
        var rows = service.GetCleanupTargets();
        string j2026 = IdOf(rows, "Journals — 2026"), j2027 = IdOf(rows, "Journals — 2027");
        var locked = System.IO.Path.Combine(_user.Journals(2027), "journal.0001.txt");
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = service.Clean([j2026, j2027]);
            Assert.Equal(3, result.Deleted[j2026]);
            Assert.Equal(0, result.Skipped[j2026]);
            Assert.Equal(2, result.Deleted[j2027]);
            Assert.Equal(1, result.Skipped[j2027]);
        }
        Assert.Empty(Directory.GetFiles(_user.Journals(2026)));
        Assert.Equal(new[] { "journal.0001.txt" }, Directory.GetFiles(_user.Journals(2027)).Select(System.IO.Path.GetFileName));

        // Nothing else was touched: the caches, the recent-file lists and Temp are still there.
        Assert.True(File.Exists(System.IO.Path.Combine(_user.CollabCache(2026), "cache.bin")));
        Assert.Contains("File1=", File.ReadAllText(_user.RevitIni(2027)));
        Assert.True(File.Exists(System.IO.Path.Combine(_user.Temp, "tmp.txt")));
    }

    [Fact]
    public void A_row_cleans_only_its_own_year_and_a_handle_the_dialog_did_not_list_cleans_nothing()
    {
        _user.WithTwoYears();
        var service = Service();
        var rows = service.GetCleanupTargets();
        var recent2027 = IdOf(rows, "Recent File List — 2027");
        var result = service.Clean([recent2027, CleanupDefaults.IdTemp, "rst.cleanup.journals@2026", "row-99", ""]);
        Assert.Equal(new[] { recent2027 }, result.Deleted.Keys);
        Assert.Equal(1, result.Deleted[recent2027]);
        Assert.DoesNotContain("File1=", File.ReadAllText(_user.RevitIni(2027)));
        Assert.Contains("File1=", File.ReadAllText(_user.RevitIni(2026)));
        Assert.True(File.Exists(System.IO.Path.Combine(_user.Temp, "tmp.txt")), "a profile id is not a row handle");
        Assert.Equal(3, Directory.GetFiles(_user.Journals(2026)).Length);

        // A service that has listed nothing cleans nothing.
        Assert.Empty(Service().Clean([recent2027]).Deleted);
    }

    [Fact]
    public void A_row_listed_before_the_profile_changed_cleans_nothing()
    {
        _user.WithTwoYears();
        var profile = CleanupDefaults.Build();
        var service = Service(() => profile);
        var rows = service.GetCleanupTargets();
        var j2026 = IdOf(rows, "Journals — 2026");

        // Switched off since the dialog opened.
        profile.Single(t => t.Id == CleanupDefaults.IdJournals).Enabled = false;
        Assert.Empty(service.Clean([j2026]).Deleted);

        // Or another target now sits where Journals was.
        profile.Single(t => t.Id == CleanupDefaults.IdJournals).Enabled = true;
        (profile[0], profile[2]) = (profile[2], profile[0]);
        Assert.Empty(service.Clean([j2026]).Deleted);
        Assert.Equal(3, Directory.GetFiles(_user.Journals(2026)).Length);
        Assert.True(File.Exists(System.IO.Path.Combine(_user.Temp, "tmp.txt")));
    }

    /// <summary>
    /// PR #176 review F1: a profile may give Temp the id "rst.cleanup.journals@2026", the string the first build used
    /// for the Journals — 2026 row. With Temp unticked and only that Journals row ticked, Temp's files must survive.
    /// </summary>
    [Fact]
    public void A_profile_id_that_looks_like_a_year_row_never_cleans_another_target()
    {
        _user.WithTwoYears();
        var profile = CleanupDefaults.Build();
        profile.Single(t => t.Id == CleanupDefaults.IdTemp).Id = "rst.cleanup.journals@2026";
        profile.Single(t => t.Id == CleanupDefaults.IdJournals).Id = "rst.cleanup.journals";
        var service = Service(() => profile);
        var rows = service.GetCleanupTargets();
        Assert.Equal(rows.Count, rows.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var result = service.Clean([IdOf(rows, "Journals — 2026")]);

        Assert.Equal(3, result.Deleted.Values.Single());
        Assert.Empty(Directory.GetFiles(_user.Journals(2026)));
        Assert.True(File.Exists(System.IO.Path.Combine(_user.Temp, "tmp.txt")), "the unticked Temp was cleaned");
        Assert.Equal(3, Directory.GetFiles(_user.Journals(2027)).Length);

        // And through the dialog: Temp off, only Journals — 2026 on.
        _user.WithTwoYears();
        Screenshots.RunSta(() =>
        {
            SynchronizationContext.SetSynchronizationContext(null);
            var health = new TrayHealthViewModel(service, new FakeDialogs { ConfirmResult = true }, TrayHealthSamples.Options());
            health.SubPage = HealthSubPage.Device;
            health.Device.OpenJunk();
            foreach (var t in health.Device.JunkTargets) t.IsSelected = t.Name == "Journals — 2026";
            health.Device.RunJunkAsync().GetAwaiter().GetResult();
            Assert.Equal("Journals — 2026: 3 deleted", health.Device.Toast.Text);
            health.Dispose();
        });
        Assert.Empty(Directory.GetFiles(_user.Journals(2026)));
        Assert.True(File.Exists(System.IO.Path.Combine(_user.Temp, "tmp.txt")), "the unticked Temp was cleaned through the dialog");
    }

    [Fact]
    public void The_Junk_modal_flow_on_the_Device_page_cleans_the_chosen_years_and_reports_each_row() => Screenshots.RunSta(() =>
    {
        // No dispatcher context: the awaited clean's continuation runs inline (as HealthViewModelTests drive it).
        SynchronizationContext.SetSynchronizationContext(null);
        _user.WithTwoYears();
        var dialogs = new FakeDialogs { ConfirmResult = true };
        var health = new TrayHealthViewModel(Service(), dialogs, TrayHealthSamples.Options());
        health.SubPage = HealthSubPage.Device;
        var vm = health.Device;
        vm.OpenJunk();
        Assert.Equal(9, vm.JunkTargets.Count);
        Assert.All(vm.JunkTargets, t => Assert.True(t.IsSelected));
        foreach (var t in vm.JunkTargets) t.IsSelected = t.Name.StartsWith("Journals", StringComparison.Ordinal);

        vm.RunJunkAsync().GetAwaiter().GetResult();

        Assert.Equal(HealthViewModel.ConfirmCleanId, Assert.Single(dialogs.ConfirmCalls).AutomationId);
        Assert.True(Assert.Single(dialogs.ConfirmCalls).Danger);
        Assert.Equal("Journals — 2026: 3 deleted\nJournals — 2027: 3 deleted", vm.Toast.Text);
        Assert.Empty(Directory.GetFiles(_user.Journals(2026)));
        Assert.Empty(Directory.GetFiles(_user.Journals(2027)));
        Assert.True(File.Exists(System.IO.Path.Combine(_user.CollabCache(2027), "cache.bin")));
        health.Dispose();
    });

    [Fact]
    public void A_scan_saves_the_installed_Revit_years_to_the_trays_own_file()
    {
        var service = Service(() => []);
        var snap = service.RunScan();
        Assert.Equal(new[] { 2026, 2027 }, snap.RevitInstalls!.Select(r => r.Year));
        Assert.Equal("", snap.Revit.Username);
        Assert.Null(snap.Revit.HardwareAcceleration);

        var path = _snapshots.File("tray_health_scan.json");
        Assert.True(File.Exists(path));
        Assert.Contains("\"revitInstalls\"", File.ReadAllText(path));
        var back = service.LoadSnapshot()!;
        Assert.Equal(new[] { "26.0.4.409", "27.0.10.28" }, back.RevitInstalls!.Select(r => r.Build));
        Assert.False(HealthWork.IsRunning);
    }
}

/// <summary>The page's sub-tabs: it opens on Models; Device reads its snapshot the first time it is shown.</summary>
public class TrayHealthViewModelTests
{
    private sealed class CountingService : IHealthService
    {
        public int Loads { get; private set; }

        public HealthSnapshot? LoadSnapshot()
        {
            Loads++;
            return TrayHealthSamples.Snapshot();
        }

        public HealthSnapshot RunScan() => TrayHealthSamples.Snapshot();

        public IReadOnlyList<CleanupTarget> GetCleanupTargets() => [];

        public CleanResult Clean(IReadOnlyCollection<string> targetIds) => new();

        public ActivityData GetActivity() => throw new NotSupportedException();

        public TelemetryToggleResult SetTelemetryEnabled(bool enabled) => new(false, null, null);

        public bool IsWorkRunning => false;

        public event EventHandler? WorkFinished
        {
            add { }
            remove { }
        }
    }

    [Fact]
    public void The_page_opens_on_Models_and_Device_loads_once_when_first_shown()
    {
        var service = new CountingService();
        var health = new TrayHealthViewModel(service, new FakeDialogs(), TrayHealthSamples.Options());
        var tray = new TrayViewModel(new FakeTrayHost(), TrayPage.Updates, health: health);
        Assert.Equal(HealthSubPage.Models, health.SubPage);
        Assert.True(health.IsModelsPage);
        Assert.Equal(1, health.SubPageIndex);
        Assert.Equal(0, health.Models.TimesShown);

        tray.Page = TrayPage.Health;
        Assert.True(tray.IsHealthPage);
        Assert.Equal(1, health.Models.TimesShown);
        Assert.Equal(0, service.Loads);

        health.SubPageIndex = 0;
        Assert.Equal(HealthSubPage.Device, health.SubPage);
        Assert.True(health.IsDevicePage);
        Assert.Equal(1, service.Loads);
        Assert.Equal("WS-ARCH-042", health.Device.Device);
        Assert.Equal(new[] { "Revit 2026", "Revit 2027" }, health.Device.Sections.Single(s => s.Title == "Revit").Rows.Select(r => r.Label));

        health.SubPage = HealthSubPage.Models;
        health.SubPage = HealthSubPage.Device;
        tray.Page = TrayPage.Settings;
        tray.Page = TrayPage.Health;
        Assert.Equal(1, service.Loads);
        Assert.Equal(2, health.Models.TimesShown);
        health.SubPageIndex = 7;
        Assert.Equal(HealthSubPage.Device, health.SubPage);
    }

    [Fact]
    public void A_tray_opened_on_Health_tells_Models_and_a_default_page_has_nothing_to_clean()
    {
        var tray = new TrayViewModel(new FakeTrayHost(), TrayPage.Health);
        Assert.Equal(1, tray.Health.Models.TimesShown);
        tray.Health.SubPage = HealthSubPage.Device;
        Assert.Equal(HealthFormat.Missing, tray.Health.Device.Captured);
        tray.Health.Device.OpenJunk();
        Assert.False(tray.Health.Device.HasJunkTargets);
    }
}

[Collection(WpfThemeCollection.Name)]
public class TrayHealthScreenshotTests
{
    private static readonly Size WindowSize = new(720, 540);

    public static TheoryData<UiTheme> Themes => new() { UiTheme.Light, UiTheme.Dark };

    private static string Name(string what, UiTheme theme) => $"tray-{what}-{theme.ToString().ToLowerInvariant()}";

    private static Color Palette(UiTheme theme, string key)
    {
        var c = UiPalette.For(theme)[key];
        return Color.FromRgb(c.R, c.G, c.B);
    }

    private static void AssertColor(Color expected, Color actual, string what)
    {
        var distance = Math.Max(Math.Abs(expected.R - actual.R), Math.Max(Math.Abs(expected.G - actual.G), Math.Abs(expected.B - actual.B)));
        Assert.True(distance <= 3, $"{what}: expected {expected}, rendered {actual}");
    }

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

    private static TrayWindow Window(FakeHealthService service, out TrayHealthViewModel health)
    {
        health = new TrayHealthViewModel(service, new FakeDialogs(), TrayHealthSamples.Options());
        return new TrayWindow(new TrayViewModel(new FakeTrayHost(), TrayPage.Health, health: health));
    }

    /// <summary>720 wide and, with <paramref name="fullHeight"/>, as tall as the page's content (the real window scrolls).</summary>
    private static BitmapSource Capture(TrayWindow window, string name, bool fullHeight)
    {
        WindowCapture.ShowOffScreen(window);
        WindowCapture.DoEvents();
        var size = WindowSize;
        if (fullHeight)
        {
            // The header, the Device page at its content's height and the footer (as the Health window's captures).
            var root = (FrameworkElement)window.Content;
            root.Measure(new Size(WindowSize.Width, double.PositiveInfinity));
            size = new Size(WindowSize.Width, Math.Max(WindowSize.Height, Math.Ceiling(root.DesiredSize.Height)));
        }
        var bitmap = WindowCapture.Content(window, size);
        Screenshots.Save(bitmap, name);
        Assert.True(Screenshots.DistinctPixels(bitmap) > 40, $"{name} looks blank");
        return bitmap;
    }

    private static Color At(BitmapSource bitmap, Visual element, Visual root, Point p)
    {
        var q = element.TransformToAncestor(root).Transform(p);
        return Screenshots.PixelAt(bitmap, (int)Math.Floor(q.X), (int)Math.Floor(q.Y));
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Device_mirrors_the_add_ins_Health_tab_with_the_installed_Revit_years(UiTheme theme) => Sta(theme, () =>
    {
        var window = Window(new FakeHealthService { Snapshot = TrayHealthSamples.Snapshot() }, out var health);
        try
        {
            health.SubPage = HealthSubPage.Device;
            foreach (var s in health.Device.Sections) s.IsExpanded = true;
            var bitmap = Capture(window, Name("health-device", theme), fullHeight: true);

            Assert.Equal(Visibility.Visible, window.HealthPage.Visibility);
            Assert.Equal(0, window.HealthPage.SubTabs.SelectedIndex);
            Assert.Equal(3, window.Tabs.SelectedIndex);
            Assert.True(window.HealthPage.DevicePage.IsVisible);
            Assert.False(window.HealthPage.ModelsPage.IsVisible);
            Assert.True(window.HealthPage.ScanButton.IsEnabled);
            Assert.True(window.HealthPage.CleanButton.IsEnabled);

            // The add-in's sections and dots: Hardware danger (RAM 88 %), Display & Network info, Revit info, OS none.
            var expanders = WindowCapture.Descendants<Expander>(window.HealthPage).ToList();
            Assert.Equal(4, expanders.Count(e => e.IsExpanded));
            var dots = expanders.ToDictionary(e => ((HealthSection)e.DataContext).Title, e => WindowCapture.Descendants<Ellipse>(e).First());
            var root = (Visual)window.Content;
            AssertColor(Palette(theme, UiPalette.Error), At(bitmap, dots["Hardware"], root, new Point(4, 4)), "Hardware dot");
            AssertColor(Palette(theme, UiPalette.AccentForeground), At(bitmap, dots["Revit"], root, new Point(4, 4)), "Revit dot");
            Assert.Equal(Visibility.Collapsed, dots["Operating System"].Visibility);

            // The Revit section lists the installed years and builds, and none of the rows that need a running Revit.
            var revit = expanders.Single(e => ((HealthSection)e.DataContext).Title == "Revit");
            var texts = WindowCapture.Descendants<TextBlock>(revit).Select(t => t.Text).ToList();
            Assert.Contains("Revit 2026", texts);
            Assert.Contains("27.0.10.28", texts);
            Assert.DoesNotContain("Username", texts);
            Assert.DoesNotContain("Hardware Accel", texts);
            Assert.DoesNotContain("Warnings", texts);

            // The tray's header and footer in the surface color, the page in the window color.
            AssertColor(Palette(theme, UiPalette.Surface), Screenshots.PixelAt(bitmap, 2, 2), "header");
            AssertColor(Palette(theme, UiPalette.Surface), Screenshots.PixelAt(bitmap, 2, bitmap.PixelHeight - 3), "footer");
            AssertColor(Palette(theme, UiPalette.Window), Screenshots.PixelAt(bitmap, 4, bitmap.PixelHeight / 2), "page");
            Assert.All(new[] { "Tray.Health.Scan", "Tray.Health.Clean", "Tray.Health.Captured", "Tray.Health.DeviceName", "Tray.Health.Tabs" },
                id => Assert.Contains(WindowCapture.Descendants<FrameworkElement>(window), e => AutomationProperties.GetAutomationId(e) == id));
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void Device_before_the_first_scan_reads_a_dash(UiTheme theme) => Sta(theme, () =>
    {
        var window = Window(new FakeHealthService(), out var health);
        try
        {
            health.SubPage = HealthSubPage.Device;
            health.Device.Sections.Single(s => s.Title == "Revit").IsExpanded = true;
            Capture(window, Name("health-device-empty", theme), fullHeight: false);
            Assert.Equal("—", health.Device.Captured);
            var revit = WindowCapture.Descendants<Expander>(window.HealthPage).Single(e => ((HealthSection)e.DataContext).Title == "Revit");
            Assert.Contains(WindowCapture.Descendants<TextBlock>(revit), t => t.Text == "Installed");
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_Junk_modal_lists_each_year_and_fits_the_tray_window(UiTheme theme) => Sta(theme, () =>
    {
        using var user = new FakeUserFolders().WithTwoYears();
        var device = new DeviceHealthService(System.IO.Path.Combine(user.Folders.UserProfile, "tray_health_scan.json"),
            () => TrayHealthSamples.Installs, CleanupDefaults.Build, user.Folders);
        var window = Window(new FakeHealthService { Snapshot = TrayHealthSamples.Snapshot(), Targets = [.. device.GetCleanupTargets()] }, out var health);
        try
        {
            health.SubPage = HealthSubPage.Device;
            health.Device.OpenJunk();
            var bitmap = Capture(window, Name("health-junk", theme), fullHeight: false);

            Assert.Equal(Visibility.Visible, window.HealthPage.JunkOverlay.Visibility);
            Assert.False(window.HealthPage.DevicePage.IsEnabled);
            Assert.False(window.HealthPage.SubTabs.IsEnabled);
            var toggles = WindowCapture.Descendants<CheckBox>(window.HealthPage.JunkOverlay).ToList();
            Assert.Equal(9, toggles.Count);
            Assert.All(toggles, t => Assert.True(t.IsChecked));
            var names = WindowCapture.Descendants<TextBlock>(window.HealthPage.JunkOverlay).Select(t => t.Text).ToList();
            Assert.Contains("Journals — 2026", names);
            Assert.Contains("Journals — 2027", names);

            // Cancel and Clean Selected stay inside the window: the list scrolls instead.
            var run = WindowCapture.Descendants<Button>(window.HealthPage.JunkOverlay)
                .Single(b => AutomationProperties.GetAutomationId(b) == "Health.JunkRun");
            var bottom = run.TransformToAncestor((Visual)window.Content).Transform(new Point(0, run.ActualHeight)).Y;
            Assert.True(bottom < WindowSize.Height, $"Clean Selected is cut off at {bottom}");
            Assert.True(Screenshots.PixelAt(bitmap, 4, bitmap.PixelHeight / 2).R < Palette(theme, UiPalette.Window).R, "no scrim");
        }
        finally
        {
            window.Close();
        }
    });
}
