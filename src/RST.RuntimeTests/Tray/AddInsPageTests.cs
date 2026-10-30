// rst-c's own (not vendored): the RST tray's Add-ins page (spec #20 §3, AC3 and AC11; F17 WU2) on a real Windows
// runtime. The view-model runs over the real RST.Core TrayAddinService (the Profile Loader's own services) with the
// Loader tests' scripted switching, so what the page plans, confirms and reports is the Loader's code; the dialogs
// are the Loader's own, recorded here and then rendered. Every state is captured in light and dark (TestResults/
// screenshots, CI artifact RST-screenshots, R25 leg), and each capture also checks what it shows.

using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using RST.Core.Activation;
using RST.Core.Configuration;
using RST.Core.Loader;
using RST.Core.Profiles;
using RST.Core.Theming;
using RST.Core.Tray;
using RST.RuntimeTests.Support;
using RST.Tests.Loader;
using RST.Tray.AddIns;
using RST.Tray.Resources;
using RST.Tray.Windows;
using RST.UI.Loader;
using RST.UI.Theming;
using Xunit;

namespace RST.RuntimeTests.Tray;

/// <summary>The Loader's dialogs, recorded: Confirm answers <see cref="Confirms"/>.</summary>
internal sealed class RecordingLoaderDialogs : ILoaderDialogs
{
    public bool Confirms { get; set; } = true;

    public List<ConfirmAddinChangesModel> Confirmed { get; } = [];

    public List<AddinReportModel> Reports { get; } = [];

    public bool ConfirmAddinChanges(ConfirmAddinChangesModel model)
    {
        Confirmed.Add(model);
        return Confirms;
    }

    public void ShowAddinReport(AddinReportModel model) => Reports.Add(model);
}

/// <summary>The real backend over a test root, with the open Revit years and failures scripted.</summary>
internal sealed class ScriptedAddinsBackend(TrayAddinService service) : ITrayAddinsBackend
{
    private readonly TrayAddinsBackend _real = new(service);

    public IReadOnlyList<int> Running { get; set; } = [];

    public Exception? StatusThrows { get; set; }

    public bool RestoreFails { get; set; }

    public List<string> Calls { get; } = [];

    public List<(ProfileEntry Entry, bool Disable, IReadOnlyList<string> Hidden, ActivationPlan? Plan)> Applied { get; } = [];

    public TrayAddinStatus Status(IReadOnlyCollection<int> runningYears)
    {
        Calls.Add("Status");
        if (StatusThrows is not null) throw StatusThrows;
        return _real.Status(runningYears);
    }

    public IReadOnlyList<ProfileEntry> Profiles() => _real.Profiles();

    public UserProfilePrefs Prefs() => _real.Prefs();

    public IReadOnlyList<int> RunningYears() => Running;

    public LoadPlanOutcome PlanApply(ProfileEntry entry, bool disableUnused)
    {
        Calls.Add("Plan");
        return _real.PlanApply(entry, disableUnused);
    }

    public LoadResult Apply(ProfileEntry entry, bool disableUnused, IReadOnlyList<string> hiddenTabs, ActivationPlan? plan)
    {
        Calls.Add("Apply");
        Applied.Add((entry, disableUnused, hiddenTabs, plan));
        return _real.Apply(entry, disableUnused, hiddenTabs, plan);
    }

    public RestoreOutcome RestoreAll()
    {
        Calls.Add("RestoreAll");
        return RestoreFails ? new RestoreOutcome(false, "the record is locked", null) : _real.RestoreAll();
    }
}

/// <summary>A machine with Revit 2026 and 2027, two profiles, and some of RST's own switches.</summary>
internal sealed class AddInsFixture : IDisposable
{
    public const string LeanId = "f1600000-0000-4000-8000-000000000001";
    public const string ProbeId = "f1600000-0000-4000-8000-000000000002";

    public AddInsFixture(bool empty = false)
    {
        Root = new TempLoaderRoot();
        Switching = new FakeAddinSwitching();
        if (!empty)
        {
            var probe26 = FakeAddinSwitching.Item(2026, "RST-Probe", off: true);
            var di26 = FakeAddinSwitching.Item(2026, "DiRoots.One", AddinScope.Machine, off: true);
            var align26 = FakeAddinSwitching.Item(2026, "AlignTag", off: true); // the user's own choice: not RST's
            var enscape26 = FakeAddinSwitching.Item(2026, "Enscape");
            var probe27 = FakeAddinSwitching.Item(2027, "RST-Probe", off: true);
            var revizto27 = FakeAddinSwitching.Item(2027, "Revizto", off: true, adminLock: AdminLock.AlwaysDisabled);
            var kinship27 = FakeAddinSwitching.Item(2027, "Kinship");
            Switching.InventoryResult = FakeAddinSwitching.InventoryOf(probe26, di26, align26, enscape26, probe27, revizto27, kinship27);
            Switching.Ledger = new ActivationLedger(new[] { probe26, di26, probe27, revizto27 }.Select(Entry));
            // The machine follows what ran: a switch made is in the settings and RST's record afterwards, so the page
            // read after Apply or Restore All shows the result, as on a real machine.
            Switching.OnExecute = plan =>
            {
                var report = FakeAddinSwitching.AllDone(plan);
                var items = Switching.InventoryResult.Items.ToList();
                foreach (var done in report.Done)
                {
                    var i = items.FindIndex(x => x.Year == done.Year && x.ManifestPath == done.ManifestPath);
                    if (i < 0) continue;
                    var item = items[i];
                    items[i] = item with { DisabledIds = done.Action == PlanAction.Disable ? item.AddinIds : [] };
                    if (done.Action == PlanAction.Disable) Switching.Ledger.Add(Entry(item));
                    else Switching.Ledger.Remove(ActivationLedger.KeyOf(Entry(item)));
                }
                Switching.InventoryResult = new AddinInventory(Switching.InventoryResult.Years, items);
                return report;
            };

            var lean = TempLoaderRoot.Profile("F16 Lean", LeanId, "F16 Lean");
            var needsProbe = TempLoaderRoot.Profile("F16 Needs Probe", ProbeId, "F16 Probe");
            needsProbe.RequiredAddins = [new RequiredAddin { TabName = "Kinship", AddinFile = "Kinship.addin" }];
            Root.Add(lean);
            Root.Add(needsProbe);
            new UserProfilePrefs().Set(LeanId, ["Massing"], disableUnusedAddins: true, path: Root.Paths.PrefsFile);
            ActiveProfile.FromProfile(needsProbe, "F16 Needs Probe.json").Write(Root.Paths.ActiveProfileFile);
        }
        Service = new TrayAddinService(Root.Paths, Switching);
        Backend = new ScriptedAddinsBackend(Service);
    }

    private static LedgerEntry Entry(InventoryItem i) =>
        new(i.Year, i.AddinIds[0], i.Name, LedgerKind.Setting, i.ManifestPath, new DateTime(2026, 10, 30, 9, 0, 0, DateTimeKind.Utc));

    public TempLoaderRoot Root { get; }

    public FakeAddinSwitching Switching { get; }

    public TrayAddinService Service { get; }

    public ScriptedAddinsBackend Backend { get; }

    public RecordingLoaderDialogs Dialogs { get; } = new();

    public AddInsViewModel ViewModel() => new(Backend, Dialogs, runInline: true);

    public void Dispose() => Root.Dispose();
}

public class AddInsViewModelTests
{
    [Fact]
    public void The_page_reads_the_disk_only_when_it_is_first_shown()
    {
        using var f = new AddInsFixture();
        var tray = new TrayViewModel(new FakeTrayHost(), TrayPage.Updates, addIns: f.ViewModel());
        Assert.Empty(f.Backend.Calls);
        Assert.True(tray.AddIns.IsLoading);

        tray.Page = TrayPage.AddIns;

        Assert.Equal(new[] { "Status" }, f.Backend.Calls);
        Assert.True(tray.AddIns.IsLoaded);
        tray.Page = TrayPage.Settings;
        tray.Page = TrayPage.AddIns;
        Assert.Single(f.Backend.Calls); // shown again: no second read (Refresh reads again)
        tray.AddIns.RefreshCommand.Execute(null);
        Assert.Equal(2, f.Backend.Calls.Count);
    }

    [Fact]
    public async Task It_shows_the_active_profile_and_each_years_switches_in_the_reports_words()
    {
        using var f = new AddInsFixture();
        f.Backend.Running = [2027];
        var vm = f.ViewModel();

        await vm.RefreshAsync();

        Assert.Equal("F16 Needs Probe", vm.ActiveProfileText);
        Assert.Equal(new[] { "Revit 2026", "Revit 2027" }, vm.Years.Select(y => y.Label));
        var y26 = vm.Years[0];
        Assert.False(y26.IsRunning);
        Assert.Equal(new[] { "RST-Probe", "DiRoots.One" }, y26.SwitchedOff);
        Assert.DoesNotContain("AlignTag", y26.SwitchedOff); // the user's own choice
        var y27 = vm.Years[1];
        Assert.True(y27.IsRunning);
        Assert.Equal(new[] { "RST-Probe" }, y27.SwitchedOff);
        Assert.Equal(new[] { "Revizto (2027): locked off by your administrator" }, y27.CouldNotChange);
        Assert.Empty(y27.Notes);
        // The active profile is picked; its saved choice seeds the switch (none saved: off).
        Assert.Equal("F16 Needs Probe", vm.SelectedProfile!.Name);
        Assert.False(vm.DisableUnused);
        vm.SelectedProfile = vm.Profiles.Single(p => p.Name == "F16 Lean");
        Assert.True(vm.DisableUnused, "F16 Lean's saved Disable unused add-ins");
    }

    [Fact]
    public async Task Apply_shows_the_Loaders_Confirm_runs_exactly_that_plan_and_shows_the_Loaders_report()
    {
        using var f = new AddInsFixture();
        f.Backend.Running = [2027];
        var vm = f.ViewModel();
        await vm.RefreshAsync();
        vm.SelectedProfile = vm.Profiles.Single(p => p.Name == "F16 Lean");
        // The plan the Loader makes for the same inputs, before anything runs.
        var loaderPlan = new AddinMaintenanceService(f.Root.Paths, f.Switching).PlanLoad("F16 Lean", AddInsFixture.LeanId, true).Plan!;

        await vm.ApplyAsync();

        // The tray confirmed the Loader's plan, through the Loader's Confirm model of it.
        var confirm = Assert.Single(f.Dialogs.Confirmed);
        var expected = ConfirmAddinChangesModel.From(loaderPlan);
        Assert.Equal(expected.SwitchingOff, confirm.SwitchingOff);
        Assert.Equal(expected.CannotChange, confirm.CannotChange);
        Assert.Equal(expected.Subtitle, confirm.Subtitle);
        Assert.Equal(new[] { "Enscape (2026)", "Kinship (2027)" }, confirm.SwitchingOff);

        var applied = Assert.Single(f.Backend.Applied);
        Assert.True(applied.Disable);
        Assert.Equal(new[] { "Massing" }, applied.Hidden); // the profile's saved RSTify choice is kept
        Assert.Equal(loaderPlan.ToJson(), applied.Plan!.ToJson());
        Assert.Same(f.Switching.Executed.Single(), applied.Plan); // what was confirmed is what ran

        var report = Assert.Single(f.Dialogs.Reports);
        Assert.Equal("Add-in Check", report.Title);
        Assert.Equal(new[] { "Enscape (2026)", "Kinship (2027)" }, report.SwitchedOff);
        Assert.Contains("Revit 2027 is open: restart it to see the change.", report.Notes);
        Assert.Equal("Add-in changes apply the next time Revit starts.", report.NextStartNote);

        Assert.Equal(AddInsStrings.Applied("F16 Lean"), vm.ResultText);
        Assert.Equal("F16 Lean", vm.ActiveProfileText);
        Assert.Equal(new[] { "RST-Probe", "DiRoots.One", "Enscape" }, vm.Years[0].SwitchedOff);
        Assert.Equal(new[] { "RST-Probe", "Kinship" }, vm.Years[1].SwitchedOff);
        Assert.Equal("F16 Lean", ActiveProfile.Read(f.Root.Paths.ActiveProfileFile).ProfileName);
    }

    [Fact]
    public async Task Cancel_at_Confirm_changes_nothing()
    {
        using var f = new AddInsFixture();
        f.Dialogs.Confirms = false;
        var vm = f.ViewModel();
        await vm.RefreshAsync();
        vm.SelectedProfile = vm.Profiles.Single(p => p.Name == "F16 Lean");

        await vm.ApplyAsync();

        Assert.Single(f.Dialogs.Confirmed);
        Assert.Empty(f.Backend.Applied);
        Assert.Empty(f.Dialogs.Reports);
        Assert.Equal("F16 Needs Probe", ActiveProfile.Read(f.Root.Paths.ActiveProfileFile).ProfileName);
        Assert.True(vm.IsIdle);
    }

    [Fact]
    public async Task Like_the_Loader_Confirm_is_skipped_when_nothing_would_change()
    {
        using var f = new AddInsFixture();
        f.Switching.Ledger = new ActivationLedger(); // nothing of RST's to switch back on
        var vm = f.ViewModel();
        await vm.RefreshAsync();
        vm.SelectedProfile = vm.Profiles.Single(p => p.Name == "F16 Needs Probe");
        Assert.False(vm.DisableUnused);

        await vm.ApplyAsync();

        Assert.Empty(f.Dialogs.Confirmed);
        Assert.Single(f.Backend.Applied);
        var report = Assert.Single(f.Dialogs.Reports);
        Assert.Equal("Add-in changes apply the next time Revit starts.", report.NextStartNote);
    }

    [Fact]
    public async Task Restore_All_reports_with_the_Loaders_report_and_the_running_Revit_note()
    {
        using var f = new AddInsFixture();
        f.Backend.Running = [2026, 2027];
        var vm = f.ViewModel();
        await vm.RefreshAsync();

        await vm.RestoreAllAsync();

        Assert.Empty(f.Dialogs.Confirmed); // as in the Loader: no Confirm before Restore All
        var report = Assert.Single(f.Dialogs.Reports);
        Assert.Equal("Restore All Add-ins", report.Title);
        Assert.Equal(new[] { "RST-Probe (2026, 2027)", "DiRoots.One (2026)" }, report.SwitchedOn);
        Assert.Equal(new[] { "Revizto (2027): locked off by your administrator" }, report.NotChanged);
        Assert.Equal(new[] { "Revit 2026 is open: restart it to see the change.", "Revit 2027 is open: restart it to see the change." },
                     report.Notes.Where(n => n.Contains("is open")));
        Assert.Equal(PlanAction.Enable, f.Switching.Executed.Single().Changes.Select(c => c.Action).Distinct().Single());
        Assert.Equal(AddInsStrings.Restored, vm.ResultText);
        Assert.True(vm.Years[0].IsClear);
        Assert.Empty(vm.Years[1].SwitchedOff);
        Assert.Equal(new[] { "Revizto (2027): locked off by your administrator" }, vm.Years[1].CouldNotChange);
    }

    [Fact]
    public async Task Failures_are_one_plain_banner()
    {
        using var f = new AddInsFixture();
        f.Backend.StatusThrows = new IOException("the disk is busy");
        var vm = f.ViewModel();
        await vm.RefreshAsync();
        Assert.Equal(AddInsStrings.ReadFailed("the disk is busy"), vm.ErrorText);
        Assert.False(vm.IsLoading);

        f.Backend.StatusThrows = null;
        await vm.RefreshAsync();
        Assert.Null(vm.ErrorText);
        f.Backend.RestoreFails = true;
        await vm.RestoreAllAsync();
        Assert.Equal("Failed to restore add-ins: the record is locked", vm.ErrorText);
        Assert.Empty(f.Dialogs.Reports);
    }

    [Fact]
    public async Task With_no_Revit_and_no_profiles_the_page_says_so()
    {
        using var f = new AddInsFixture(empty: true);
        var vm = f.ViewModel();
        await vm.RefreshAsync();

        Assert.True(vm.ShowNoRevit);
        Assert.True(vm.ShowNoProfiles);
        Assert.False(vm.CanApply);
        Assert.Equal(AddInsStrings.ActiveProfileNone, vm.ActiveProfileText);
    }

    [Fact]
    public void Every_AddInsStrings_accessor_resolves_to_a_resx_entry()
    {
        foreach (var p in typeof(AddInsStrings).GetProperties(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public))
        {
            var value = (string)p.GetValue(null)!;
            Assert.False(string.IsNullOrWhiteSpace(value), p.Name);
            Assert.NotEqual(p.Name, value);
        }
        Assert.Equal("Applying “F16 Lean”…", AddInsStrings.BusyApplying("F16 Lean"));
        Assert.StartsWith("“F16 Lean” is your profile now.", AddInsStrings.Applied("F16 Lean"));
        Assert.Equal("RST could not read your add-ins: x", AddInsStrings.ReadFailed("x"));
        Assert.Equal("The profile was not applied: x", AddInsStrings.ApplyFailed("x"));
    }
}

[Collection(WpfThemeCollection.Name)]
public class AddInsPageScreenshotTests
{
    private static readonly Size PageSize = new(720, 900);

    public static TheoryData<UiTheme> Themes => new() { UiTheme.Light, UiTheme.Dark };

    private static string Name(string what, UiTheme theme) => $"tray-addins-{what}-{theme.ToString().ToLowerInvariant()}";

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

    /// <summary>The tray window on its Add-ins page, laid out tall enough to show the whole page.</summary>
    private static TrayWindow Capture(AddInsViewModel vm, UiTheme theme, string what, Action<TrayWindow>? check = null)
    {
        var window = new TrayWindow(new TrayViewModel(new FakeTrayHost(), TrayPage.AddIns, addIns: vm));
        try
        {
            WindowCapture.ShowOffScreen(window);
            WindowCapture.DoEvents();
            var bitmap = WindowCapture.Content(window, PageSize);
            Screenshots.Save(bitmap, Name(what, theme));
            Assert.True(Screenshots.DistinctPixels(bitmap) > 40, $"{what} looks blank");
            Assert.Equal(Visibility.Visible, window.AddInsPage.Visibility);
            AssertColor(Palette(theme, UiPalette.Surface), Screenshots.PixelAt(bitmap, 2, 2), "header");
            AssertColor(Palette(theme, UiPalette.Window), Screenshots.PixelAt(bitmap, 4, bitmap.PixelHeight / 2), "page");
            check?.Invoke(window);
            return window;
        }
        finally
        {
            window.Close();
        }
    }

    private static IEnumerable<TextBlock> Visible(DependencyObject root) =>
        WindowCapture.Descendants<TextBlock>(root).Where(t => t.IsVisible);

    private static void CaptureDialog(Window dialog, UiTheme theme, string what)
    {
        WindowCapture.ShowOffScreen(dialog);
        try
        {
            var bitmap = WindowCapture.Client(dialog);
            Screenshots.Save(bitmap, Name(what, theme));
            AssertColor(Palette(theme, UiPalette.Window), Screenshots.PixelAt(bitmap, 1, 1), $"{what} corner");
            Assert.True(Screenshots.DistinctPixels(bitmap) > 30, $"{what} looks blank");
        }
        finally
        {
            dialog.Close();
        }
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_status_per_Revit_year(UiTheme theme) => Sta(theme, () =>
    {
        using var f = new AddInsFixture();
        f.Backend.Running = [2027];
        var vm = f.ViewModel();
        vm.RefreshAsync().GetAwaiter().GetResult();
        Capture(vm, theme, "status", window =>
        {
            var texts = Visible(window.AddInsView).Select(t => t.Text).ToList();
            Assert.Contains("F16 Needs Probe", texts);
            Assert.Contains("Revit 2026", texts);
            Assert.Contains("Revit 2027", texts);
            Assert.Contains("Revizto (2027): locked off by your administrator", texts);
            Assert.Contains(AddInsStrings.YearSwitchedOff, texts);
            Assert.True(window.AddInsView.ApplyButton.IsEnabled);
            Assert.Equal(2, window.AddInsView.YearCards.Items.Count);
        });
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void After_Apply_and_its_Confirm_and_report(UiTheme theme) => Sta(theme, () =>
    {
        using var f = new AddInsFixture();
        f.Backend.Running = [2027];
        var vm = f.ViewModel();
        vm.RefreshAsync().GetAwaiter().GetResult();
        vm.SelectedProfile = vm.Profiles.Single(p => p.Name == "F16 Lean");
        vm.ApplyAsync().GetAwaiter().GetResult();

        Capture(vm, theme, "applied", window =>
        {
            Assert.Contains(AddInsStrings.Applied("F16 Lean"), Visible(window.AddInsView).Select(t => t.Text));
            Assert.Equal("F16 Lean", window.AddInsView.ActiveProfile.Text);
        });
        CaptureDialog(new ConfirmAddinChangesWindow(f.Dialogs.Confirmed.Single()), theme, "confirm");
        CaptureDialog(new AddinReportWindow(f.Dialogs.Reports.Single()), theme, "report-apply");
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void After_Restore_All_and_its_report(UiTheme theme) => Sta(theme, () =>
    {
        using var f = new AddInsFixture();
        f.Backend.Running = [2026, 2027];
        var vm = f.ViewModel();
        vm.RefreshAsync().GetAwaiter().GetResult();
        vm.RestoreAllAsync().GetAwaiter().GetResult();

        CaptureDialog(new AddinReportWindow(f.Dialogs.Reports.Single()), theme, "report-restore");
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void No_Revit_and_no_profiles(UiTheme theme) => Sta(theme, () =>
    {
        using var f = new AddInsFixture(empty: true);
        var vm = f.ViewModel();
        vm.RefreshAsync().GetAwaiter().GetResult();
        Capture(vm, theme, "empty", window =>
        {
            var texts = Visible(window.AddInsView).Select(t => t.Text).ToList();
            Assert.Contains(AddInsStrings.NoRevit, texts);
            Assert.Contains(AddInsStrings.NoProfiles, texts);
            Assert.Contains(AddInsStrings.ActiveProfileNone, texts);
            Assert.False(window.AddInsView.ApplyButton.IsVisible);
        });
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_add_ins_could_not_be_read(UiTheme theme) => Sta(theme, () =>
    {
        using var f = new AddInsFixture();
        f.Backend.StatusThrows = new IOException("Access to the path 'AddInsSettings.json' is denied.");
        var vm = f.ViewModel();
        vm.RefreshAsync().GetAwaiter().GetResult();
        Capture(vm, theme, "error", window =>
        {
            var texts = Visible(window.AddInsView).Select(t => t.Text).ToList();
            Assert.Contains(AddInsStrings.ReadFailed("Access to the path 'AddInsSettings.json' is denied."), texts);
            Assert.DoesNotContain(AddInsStrings.ActiveProfileNone, texts); // nothing is known: nothing is claimed
            Assert.False(window.AddInsView.RestoreAllButton.IsVisible);
        });
    });
}
