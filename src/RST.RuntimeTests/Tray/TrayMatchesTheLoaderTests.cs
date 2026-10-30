// rst-c's own (not vendored): spec #20 AC3 against the real Loader (F17 WU2, review N9). One fake machine (Revit
// 2026 and 2027; per-user, machine-wide and Autodesk add-ins; an add-in the user switched off; a profile with the
// admin's RSTify presets) is snapshotted, then:
//   - the Loader: the real ProfileLoaderViewModel over the real ProfileLoaderBackend (the code Revit runs), with
//     the Loader's own test dialogs: pick the profile, "Adopt RSTify Presets?" yes, Load Profile, Confirm;
//   - from the same snapshot, the tray: AddInsViewModel over the real TrayAddinsBackend and TrayAddinService: pick
//     the profile, Apply, the same question yes, Confirm.
// The question, the Confirm model, the report model and every file on disk must match.

using System.IO;
using RST.Core.Activation;
using RST.Core.Configuration;
using RST.Core.Loader;
using RST.Core.Profiles;
using RST.Core.Tray;
using RST.RuntimeTests.Loader;
using RST.Tests.Activation;
using RST.Tests.Loader;
using RST.Tray.AddIns;
using RST.Tray.Windows;
using RST.UI.Loader;
using Xunit;

namespace RST.RuntimeTests.Tray;

public sealed class TrayMatchesTheLoaderTests : IDisposable
{
    private const string OfficeId = "f1600000-0000-4000-8000-0000000000aa";
    private const string Probe = "d4c6e3e0-0000-4000-8000-525354500001";
    private const string DiRoots = "b4ad7489-2899-46b3-aca6-c0def7135528";
    private const string Align = "5a9e8c41-09d6-4cdf-8556-38f8119137a4";

    private static readonly string[] Ribbon = ["Architecture", "Massing", "Analyze", "Add-Ins", "Office"];

    private readonly FakeAddinTree _tree = new();
    private readonly string _snapshot;
    private readonly LoaderPaths _paths;

    public TrayMatchesTheLoaderTests()
    {
        _paths = LoaderPaths.Under(Path.Combine(_tree.Roots.AppData, "RST"));
        Directory.CreateDirectory(_paths.ProfilesDir);
        var office = TempLoaderRoot.Profile("Office", OfficeId, "Office");
        office.Presets = new ProfilePresets { HiddenTabs = ["Massing", "Analyze"], DisableUnusedAddins = true };
        ProfileStore.Save(office, _paths.ProfilesDir);
        foreach (var year in new[] { 2026, 2027 })
        {
            _tree.Manifest(_tree.UserAddins(year), "RstProbe.addin", "RST-Probe", Probe);
            _tree.Manifest(_tree.MachineAddins(year), "DiRoots.One.addin", "DiRoots.One", DiRoots);
            _tree.Manifest(_tree.UserAddins(year), "Autodesk.addin", "Batch Print", "2cc33333-4444-4555-8666-777788889999", vendor: "ADSK");
        }
        _tree.Manifest(_tree.UserAddins(2026), "Align.addin", "AlignTag", Align);
        _tree.Settings(2026, """{"DisableAllAddIns":false,"AddInItemSettings":[{"Disabled":true,"GUID":"5a9e8c41-09d6-4cdf-8556-38f8119137a4","LoadTime":10,"Name":"AlignTag","Vendor":"BIM42"}]}""");
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
        _tree.Roots, () => [FakeAddinTree.Year(2026), FakeAddinTree.Year(2027)], Path.Combine(_paths.Root, ActivationLedger.FileName));

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
    }

    private Dictionary<string, string> Disk()
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(_tree.Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            var text = File.ReadAllText(file);
            if (name == ActivationLedger.FileName)
                text = string.Join("\n", ActivationLedger.Load(file).Entries.Select(e => (e with { At = default }).ToString()));
            else if (name == "active_profile.json")
            {
                var ap = ActiveProfile.Read(file);
                ap.LoadedAt = "";
                text = System.Text.Json.JsonSerializer.Serialize(ap);
            }
            files[Path.GetRelativePath(_tree.Root, file)] = text;
        }
        return files;
    }

    private static void SameConfirm(ConfirmAddinChangesModel tray, ConfirmAddinChangesModel loader)
    {
        Assert.Equal(loader.Subtitle, tray.Subtitle);
        Assert.Equal(loader.SwitchingOff, tray.SwitchingOff);
        Assert.Equal(loader.SwitchingOn, tray.SwitchingOn);
        Assert.Equal(loader.Staying, tray.Staying);
        Assert.Equal(loader.CannotChange, tray.CannotChange);
    }

    private static void SameReport(AddinReportModel tray, AddinReportModel loader)
    {
        Assert.Equal(loader.Title, tray.Title);
        Assert.Equal(loader.Subtitle, tray.Subtitle);
        Assert.Equal(loader.SwitchedOff, tray.SwitchedOff);
        Assert.Equal(loader.SwitchedOn, tray.SwitchedOn);
        Assert.Equal(loader.Moved, tray.Moved);
        Assert.Equal(loader.NotChanged, tray.NotChanged);
        Assert.Equal(loader.Notes, tray.Notes);
        Assert.Equal(loader.RequiredOff, tray.RequiredOff);
        Assert.Equal(loader.NextStartNote, tray.NextStartNote);
    }

    [Fact]
    public async Task Apply_with_the_adopted_presets_matches_the_real_Loader_question_plan_report_and_files()
    {
        // The Loader, as Revit runs it.
        var clock = new ManualClock();
        var loaderDialogs = new LoaderTestDialogs();
        loaderDialogs.ConfirmAnswers.Enqueue(true); // Adopt RSTify Presets? Yes
        var backend = new ProfileLoaderBackend("2026", new FakeScheduler(), Ribbon, _paths, Switching(),
            lookupPath: Path.Combine(_tree.Root, "no-lookup.json"));
        var loader = new ProfileLoaderViewModel(backend, loaderDialogs, loaderDialogs, clock.Schedule, _ => { });
        loader.Initialize();
        loader.SelectedCard = loader.Profiles.Single(c => c.Id == OfficeId);
        await loader.LoadAsync();
        var loaderDisk = Disk();

        // The tray, from the same snapshot.
        Directory.Delete(_tree.Root, recursive: true);
        Copy(_snapshot, _tree.Root);
        var trayDialogs = new LoaderTestDialogs();
        trayDialogs.ConfirmAnswers.Enqueue(true);
        var tray = new AddInsViewModel(new TrayAddinsBackend(new TrayAddinService(_paths, Switching())), trayDialogs, trayDialogs, runInline: true);
        await tray.RefreshAsync();
        tray.SelectedProfile = tray.Profiles.Single(p => p.Id == OfficeId);
        await tray.ApplyAsync();
        var trayDisk = Disk();

        // The same question, asked once each.
        var asked = Assert.Single(trayDialogs.Confirms);
        Assert.Equal(Assert.Single(loaderDialogs.Confirms), asked);
        Assert.Equal(ProfileLoaderViewModel.AdoptPresetsDialogId, asked.AutomationId);

        // The same plan shown, the same report, the same files.
        SameConfirm(Assert.Single(trayDialogs.AddinChanges), Assert.Single(loaderDialogs.AddinChanges));
        SameReport(Assert.Single(trayDialogs.AddinReports), Assert.Single(loaderDialogs.AddinReports));
        Assert.Equal(loaderDisk, trayDisk);

        // And it did something: both years switched off, the presets taken, the user's own AlignTag untouched.
        Assert.Equal(new[] { "RST-Probe (2026, 2027)", "DiRoots.One (2026, 2027)" }, trayDialogs.AddinReports[0].SwitchedOff);
        var prefs = UserProfilePrefs.Read(_paths.PrefsFile).Get(OfficeId)!;
        Assert.Equal(new[] { "Massing", "Analyze" }, prefs.HiddenTabs);
        Assert.True(prefs.DisableUnusedAddins);
        Assert.True(prefs.PresetsAdopted);
        Assert.Equal(new[] { "Massing", "Analyze" }, ActiveProfile.Read(_paths.ActiveProfileFile).HiddenTabs);
    }
}
