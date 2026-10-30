// rst-c's own (not vendored): spec #20 AC3, the CI half (F17 WU2). The tray's Add-ins page and the Profile Loader
// make the same plans and reports, and leave the same files, for the same inputs. One fake machine (2026 and 2027,
// per-user and machine-wide add-ins, an Autodesk add-in, RST itself, an add-in the user switched off, an admin lock,
// a v3.0 rename) is snapshotted, then run once through the Loader's path (what RST.UI's ProfileLoaderBackend calls:
// AddinMaintenanceService.PlanLoad, then ProfileLoadService Begin / RunAddinPasses / Finish with Revit's ribbon
// scheduler) and once, from the same snapshot, through TrayAddinService. Plans, reports, report lines and the disk
// must match. Then the page's own rules: the per-year status, the running-Revit note and the year of a Revit.exe.

using System.IO;
using System.Text.Json;
using FluentAssertions;
using RST.Core.Activation;
using RST.Core.Configuration;
using RST.Core.Loader;
using RST.Core.Profiles;
using RST.Core.Tray;
using RST.Tests.Activation;
using RST.Tests.Loader;
using Xunit;

namespace RST.Tests.Tray;

public sealed class TrayAddinsMatchTheLoaderTests : IDisposable
{
    private const string Probe = "d4c6e3e0-0000-4000-8000-525354500001";
    private const string DiRoots = "b4ad7489-2899-46b3-aca6-c0def7135528";
    private const string Align = "5a9e8c41-09d6-4cdf-8556-38f8119137a4";
    private const string Kinship = "0aa11111-2222-4333-8444-555566667777";
    private const string Legacy = "1bb22222-3333-4444-8555-666677778888";
    private const string Autodesk = "2cc33333-4444-4555-8666-777788889999";

    private readonly FakeAddinTree _tree = new();
    private readonly string _snapshot;
    private readonly LoaderPaths _paths;
    private readonly Profile _lean;
    private readonly Profile _needsProbe;

    public TrayAddinsMatchTheLoaderTests()
    {
        // The Loader's files live beside RST's record, as on a real machine (%AppData%\RST).
        _paths = LoaderPaths.Under(Path.Combine(_tree.Roots.AppData, "RST"));
        Directory.CreateDirectory(_paths.ProfilesDir);
        _lean = TempLoaderRoot.Profile("F16 Lean", "f1600000-0000-4000-8000-000000000001", "F16 Lean");
        _needsProbe = TempLoaderRoot.Profile("F16 Needs Probe", "f1600000-0000-4000-8000-000000000002", "F16 Probe");
        _needsProbe.RequiredAddins = [new RequiredAddin { TabName = "RSTProbe", AddinFile = "RstProbe.addin", AddinId = Probe }];
        ProfileStore.Save(_lean, _paths.ProfilesDir);
        ProfileStore.Save(_needsProbe, _paths.ProfilesDir);

        foreach (var year in new[] { 2026, 2027 })
        {
            _tree.Manifest(_tree.UserAddins(year), "RstProbe.addin", "RST-Probe", Probe);
            _tree.Manifest(_tree.UserAddins(year), "RST.addin", "RST", InventoryItem.RstClientId, vendor: "DSOS");
            _tree.Manifest(_tree.UserAddins(year), "Autodesk.addin", "Batch Print", Autodesk, vendor: "ADSK");
        }
        _tree.Manifest(_tree.MachineAddins(2026), "DiRoots.One.addin", "DiRoots.One", DiRoots);
        _tree.Manifest(_tree.MachineAddins(2027), "DiRoots.One.addin", "DiRoots.One", DiRoots);
        _tree.Manifest(_tree.UserAddins(2026), "Align.addin", "AlignTag", Align);
        _tree.Manifest(_tree.UserAddins(2027), "Kinship.addin", "Kinship", Kinship);
        _tree.Manifest(_tree.UserAddins(2026), "Legacy.addin", "Legacy", Legacy, renamed: true); // RST 3.0's rename
        // The user's own choice in Autodesk's Add-Ins Manager (2026), and an admin lock (2027).
        _tree.Settings(2026, """{"DisableAllAddIns":false,"AddInItemSettings":[{"Disabled":true,"GUID":"5a9e8c41-09d6-4cdf-8556-38f8119137a4","LoadTime":10,"Name":"AlignTag","Vendor":"BIM42"}]}""");
        _tree.Settings(2027, """{"DisableAllAddIns":false,"AddInItemSettings":[]}""");
        _tree.Lock(2027, Path.Combine(_tree.UserAddins(2027), "Kinship.addin"), Kinship, AdminLock.AlwaysEnabled);

        _snapshot = _tree.Root + ".snapshot";
        Copy(_tree.Root, _snapshot);
    }

    public void Dispose()
    {
        _tree.Dispose();
        try { Directory.Delete(_snapshot, recursive: true); }
        catch (IOException) { }
    }

    private AddinSwitching Switching() => new(
        _tree.Roots,
        () => [FakeAddinTree.Year(2026), FakeAddinTree.Year(2027)],
        Path.Combine(_paths.Root, ActivationLedger.FileName));

    private void Reset()
    {
        Directory.Delete(_tree.Root, recursive: true);
        Copy(_snapshot, _tree.Root);
    }

    private static void Copy(string from, string to)
    {
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
    }

    /// <summary>Every file under the fake machine, with the time stamps RST writes taken out.</summary>
    private Dictionary<string, string> Disk()
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(_tree.Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);
            if (name == ActivationLedger.FileName)
                text = string.Join("\n", ActivationLedger.Load(file).Entries.Select(e => (e with { At = default }).ToString()));
            else if (name == "active_profile.json")
            {
                var ap = ActiveProfile.Read(file);
                ap.LoadedAt = "";
                text = JsonSerializer.Serialize(ap);
            }
            files[Path.GetRelativePath(_tree.Root, file)] = text;
        }
        return files;
    }

    /// <summary>The Loader's Load Profile, as ProfileLoaderBackend runs it (Revit's ribbon scheduler included).</summary>
    private (ActivationPlan Plan, LoadResult Result) LoaderLoad(Profile profile, bool disableUnused)
    {
        var switching = Switching();
        var addins = new AddinMaintenanceService(_paths, switching);
        var load = new ProfileLoadService(_paths, new FakeScheduler(), switching);
        var plan = addins.PlanLoad(profile.ProfileName, profile.Id, disableUnused).Plan!;
        var start = load.Begin(new LoadRequest(profile.ProfileName, profile.Id, disableUnused, [], plan));
        return (plan, load.Finish(start, load.RunAddinPasses(start)));
    }

    private (ActivationPlan Plan, LoadResult Result) TrayApply(Profile profile, bool disableUnused)
    {
        var tray = new TrayAddinService(_paths, Switching());
        var entry = tray.Profiles().Single(p => p.Profile.Id == profile.Id);
        var plan = tray.PlanApply(entry, disableUnused).Plan!;
        return (plan, tray.Apply(entry, disableUnused, [], plan));
    }

    private static IReadOnlyList<string> Lines(ActivationReport r) =>
    [
        .. ActivationWording.GroupByName(r.Done.Where(o => o.Action == PlanAction.Disable), o => o.Name, o => o.Year).Select(l => "off: " + l),
        .. ActivationWording.GroupByName(r.Done.Where(o => o.Action == PlanAction.Enable), o => o.Name, o => o.Year).Select(l => "on: " + l),
        .. r.NotChanged.Select(ActivationWording.NotChangedLine).Select(l => "not: " + l),
        .. r.Notes.Select(n => "note: " + n),
    ];

    private static void Same(ActivationReport tray, ActivationReport loader)
    {
        tray.Intent.Should().Be(loader.Intent);
        tray.Outcomes.Should().Equal(loader.Outcomes);
        tray.Notes.Should().Equal(loader.Notes);
        Lines(tray).Should().Equal(Lines(loader));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Apply_profile_makes_the_Loaders_plan_and_report_and_writes_the_same_files(bool disableUnused)
    {
        // A first load on both paths, so the second one also has RST's own switches to work with.
        var (loaderPlan1, loader1) = LoaderLoad(_lean, disableUnused: true);
        var loaderPlan2 = LoaderLoad(_needsProbe, disableUnused);
        var loaderDisk = Disk();

        Reset();
        var (trayPlan1, tray1) = TrayApply(_lean, disableUnused: true);
        var trayPlan2 = TrayApply(_needsProbe, disableUnused);
        var trayDisk = Disk();

        trayPlan1.ToJson().Should().Be(loaderPlan1.ToJson(), "the Confirm dialog shows the same plan");
        trayPlan2.Plan.ToJson().Should().Be(loaderPlan2.Plan.ToJson());
        Same(tray1.Report, loader1.Report);
        Same(trayPlan2.Result.Report, loaderPlan2.Result.Report);
        tray1.Ok.Should().BeTrue();
        tray1.RestartNeeded.Should().BeTrue("outside Revit the profile and the add-ins apply at Revit's next start");
        trayDisk.Should().Equal(loaderDisk);

        // The first plan really switches things off in both years, and keeps the user's, Autodesk's and RST's own.
        Lines(tray1.Report).Should().Contain(["off: RST-Probe (2026, 2027)", "off: DiRoots.One (2026, 2027)"])
            .And.Contain(l => l.StartsWith("not: Kinship (2027): locked on by your administrator", StringComparison.Ordinal))
            .And.NotContain(l => l.Contains("AlignTag") || l.Contains("Batch Print") || l.Contains(": RST ("));
    }

    [Fact]
    public void Restore_All_matches_the_Loader_and_switches_back_on_only_RSTs_own()
    {
        LoaderLoad(_lean, disableUnused: true);
        var loader = new AddinMaintenanceService(_paths, Switching()).RestoreAll();
        var loaderDisk = Disk();

        Reset();
        TrayApply(_lean, disableUnused: true);
        var tray = new TrayAddinService(_paths, Switching()).RestoreAll();
        var trayDisk = Disk();

        tray.Ok.Should().BeTrue();
        Same(tray.Addins!, loader.Addins!);
        trayDisk.Should().Equal(loaderDisk);

        Lines(tray.Addins!).Should().Contain(l => l.StartsWith("on: ", StringComparison.Ordinal) && l.Contains("RST-Probe (2026, 2027)"));
        Lines(tray.Addins!).Should().Contain(l => l.Contains("Legacy (2026)"), "RST 3.0's rename is RST's own too");
        var align = AddinsSettingsDocument.TryParse(_tree.ReadSettings(2026)!, out _)!;
        align.IsDisabled(Align).Should().BeTrue("the user's own choice in Autodesk's Add-Ins Manager stays");
        _tree.Ledger().Entries.Should().BeEmpty();
    }

    [Fact]
    public void The_status_lists_RSTs_switches_per_year_and_what_it_cannot_change()
    {
        TrayApply(_lean, disableUnused: true);
        var service = new TrayAddinService(_paths, Switching());

        var status = service.Status([2027]);

        status.ActiveProfile.Should().Be("F16 Lean");
        status.Error.Should().BeNull();
        status.Years.Select(y => y.Year).Should().Equal(2026, 2027);
        var y26 = status.Years[0];
        y26.Running.Should().BeFalse();
        y26.SwitchedOff.Should().BeEquivalentTo(["RST-Probe", "DiRoots.One", "Legacy"]);
        y26.SwitchedOff.Should().NotContain("AlignTag", "the user switched it off, not RST");
        var y27 = status.Years[1];
        y27.Running.Should().BeTrue();
        y27.SwitchedOff.Should().BeEquivalentTo(["RST-Probe", "DiRoots.One"]);
        y27.CouldNotChange.Should().BeEmpty();

        // Restore All empties it, and an admin lock that now keeps one of RST's switches off is "could not change".
        service.RestoreAll();
        service.Status().Years.Should().OnlyContain(y => y.IsClear);
    }

    [Fact]
    public void A_switch_the_administrator_now_locks_off_is_listed_as_could_not_change_in_the_reports_words()
    {
        TrayApply(_lean, disableUnused: true);
        _tree.Lock(2026, Path.Combine(_tree.UserAddins(2026), "RstProbe.addin"), Probe, AdminLock.AlwaysDisabled);

        var y26 = new TrayAddinService(_paths, Switching()).Status().Years.Single(y => y.Year == 2026);

        y26.CouldNotChange.Should().Equal("RST-Probe (2026): locked off by your administrator");
        y26.SwitchedOff.Should().NotContain("RST-Probe");
    }

    [Fact]
    public void A_v30_rename_RST_could_not_undo_is_listed_with_the_admin_rights_reason()
    {
        // RST 3.0 renamed an add-in installed for all users; the one-time move could not rename it back.
        const string shared = "3dd44444-5555-4666-8777-88889999aaaa";
        var path = _tree.Manifest(_tree.MachineAddins(2026), "Shared.addin", "Shared", shared, renamed: true);
        ActivationLedger.SaveMerged(Path.Combine(_paths.Root, ActivationLedger.FileName),
            [l => l.Add(new LedgerEntry(2026, null, "Shared", LedgerKind.V30Rename, path, DateTime.UtcNow))]);

        var y26 = new TrayAddinService(_paths, Switching()).Status().Years.Single(y => y.Year == 2026);

        y26.CouldNotChange.Should().Equal("Shared (2026): installed for all users; renaming its file back needs administrator rights");
        y26.SwitchedOff.Should().Contain("Legacy", "RST 3.0's rename of a per-user add-in is still RST's own, and undoable");
    }

    [Fact]
    public void A_per_user_v30_rename_not_undone_last_time_predicts_nothing()
    {
        // Review N4: the record keeps a V30Rename for any failed undo (a file in use, a settings file it could not
        // write); for an add-in that is not installed for all users, the cause may have cleared.
        var path = Path.Combine(_tree.UserAddins(2026), "Legacy.addin");
        ActivationLedger.SaveMerged(Path.Combine(_paths.Root, ActivationLedger.FileName),
            [l => l.Add(new LedgerEntry(2026, null, "Legacy", LedgerKind.V30Rename, path, DateTime.UtcNow))]);

        var y26 = new TrayAddinService(_paths, Switching()).Status().Years.Single(y => y.Year == 2026);

        y26.CouldNotChange.Should().Equal("Legacy (2026): RST could not rename its file back last time; Restore All tries again");
        y26.SwitchedOff.Should().NotContain("Legacy");
    }

    [Fact]
    public void The_adopt_presets_rule_and_record_are_the_Loaders()
    {
        var office = TempLoaderRoot.Profile("Office", "f1600000-0000-4000-8000-0000000000aa", "Office");
        office.Presets = new ProfilePresets { HiddenTabs = ["Massing"], DisableUnusedAddins = true };
        ProfileStore.Save(office, _paths.ProfilesDir);
        var tray = new TrayAddinService(_paths, Switching());
        var entry = tray.Profiles().Single(p => p.Profile.Id == office.Id);
        var lean = tray.Profiles().Single(p => p.Profile.Id == _lean.Id);

        TrayAddinService.AsksToAdoptPresets(entry, tray.Prefs()).Should().BeTrue();
        TrayAddinService.AsksToAdoptPresets(lean, tray.Prefs()).Should().BeFalse("no presets");

        var (disable, hidden) = tray.AdoptPresets(entry, adopt: true);
        disable.Should().BeTrue();
        hidden.Should().Equal("Massing");
        var saved = tray.Prefs().Get(office.Id!)!;
        saved.HiddenTabs.Should().Equal("Massing");
        saved.DisableUnusedAddins.Should().BeTrue();
        saved.PresetsAdopted.Should().BeTrue();
        TrayAddinService.AsksToAdoptPresets(entry, tray.Prefs()).Should().BeFalse("asked once per profile per machine");
    }

    [Fact]
    public void The_saved_choices_seed_Apply_as_the_Loader_seeds_them()
    {
        var prefs = new UserProfilePrefs();
        prefs.Profiles[_needsProbe.Id!] = new UserProfilePrefsEntry { HiddenTabs = ["Massing"], DisableUnusedAddins = true };
        var tray = new TrayAddinService(_paths, Switching());
        var entries = tray.Profiles();

        var probe = TrayAddinService.Choices(entries.Single(e => e.Profile.Id == _needsProbe.Id), prefs);
        probe.DisableUnused.Should().BeTrue("the profile's prefs entry");
        probe.HiddenTabs.Should().Equal("Massing");
        var lean = TrayAddinService.Choices(entries.Single(e => e.Profile.Id == _lean.Id), prefs);
        lean.DisableUnused.Should().BeFalse();
        lean.HiddenTabs.Should().BeEmpty();
    }
}

public sealed class TrayAddinRulesTests
{
    [Fact]
    public void Every_running_year_adds_one_restart_note_after_the_reports_own()
    {
        var report = ActivationReport.Empty(ActivationIntent.RestoreAll, "Tidied up: …");

        var withNotes = TrayAddinService.WithRunningNotes(report, [2027, 2026, 2027]);

        withNotes.Notes.Should().Equal(
            "Tidied up: …",
            "Revit 2026 is open: restart it to see the change.",
            "Revit 2027 is open: restart it to see the change.");
        TrayAddinService.WithRunningNotes(report, []).Should().BeSameAs(report);
    }

    [Theory]
    [InlineData(@"C:\Program Files\Autodesk\Revit 2026\Revit.exe", 26, 2026)]
    [InlineData(@"D:\Apps\Revit 2027\Revit.exe", 27, 2027)] // its uninstall entry's folder wins
    [InlineData(@"E:\Elsewhere\Revit.exe", 27, 2027)]       // else the file version (27.x is 2027)
    [InlineData(@"E:\Elsewhere\Revit.exe", 24, null)]       // 2024 is not RST's
    [InlineData(null, null, null)]
    public void A_running_Revit_is_matched_to_its_year(string? exe, int? major, int? expected)
    {
        IReadOnlyList<RevitYear> installed =
        [
            new(2026, @"C:\Program Files\Autodesk\Revit 2026\"),
            new(2027, @"D:\Apps\Revit 2027"),
        ];
        if (!OperatingSystem.IsWindows() && exe is not null) exe = exe.Replace('\\', '/');
        if (!OperatingSystem.IsWindows())
            installed = installed.Select(y => y with { InstallDir = y.InstallDir!.Replace('\\', '/') }).ToList();

        TrayAddinService.RevitYearOf(exe, major, installed).Should().Be(expected);
    }
}
