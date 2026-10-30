// rst-c's own (not vendored): AC6 through the view-model (spec #16, WU3). The v2.2.0 fixture set
// (src/RST.Tests/Fixtures/V220: two profiles, the prefs and the active profile) loads into the
// Profile Loader view-model over the real backend; the writes the Loader makes (adopting presets,
// the opacity override, Load Profile) are compared byte for byte with the v2.2.0 bridge's writes
// (V220BridgeReference) on a second copy, and with the checked-in expected files.

using System.IO;
using FluentAssertions;
using RST.Core.Loader;
using RST.Tests.Loader;
using RST.UI.Loader;
using Xunit;

namespace RST.RuntimeTests.Loader;

public sealed class ProfileLoaderDataCompatibilityTests : IDisposable
{
    private static readonly string FixtureDir = Path.Combine(RepoRoot(), "src", "RST.Tests", "Fixtures", "V220");

    private static readonly string[] Ribbon = ["Architecture", "Analyze", "Collaborate", "Enscape", "Kinship", "Massing & Site", "Modify"];

    private readonly string _reference = V220BridgeReference.CopyFixtures(FixtureDir);
    private readonly string _ported = V220BridgeReference.CopyFixtures(FixtureDir);

    public void Dispose()
    {
        foreach (var dir in new[] { _reference, _ported })
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
        }
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "RST-C.sln"))) return dir.FullName;
        throw new InvalidOperationException("RST-C.sln not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public async Task The_v220_data_loads_into_the_view_model_and_its_writes_match_the_v220_bridge()
    {
        var dialogs = new LoaderTestDialogs();
        dialogs.ConfirmAnswers.Enqueue(true); // adopt Structure's presets
        var backend = new ProfileLoaderBackend("2026", new FakeScheduler(), Ribbon, LoaderPaths.Under(_ported),
            new FakeAddinSwitching(), LoaderAssets.LookupPath, () => V220BridgeReference.LoadedAt);
        var clock = new ManualClock();
        var vm = new ProfileLoaderViewModel(backend, dialogs, dialogs, clock.Schedule, _ => { });

        // Load: the two profiles, Architecture active and selected with its saved state.
        vm.Initialize();
        vm.Profiles.Select(c => c.Name).Should().BeEquivalentTo(["Architecture", "Structure"]);
        vm.SelectedCard!.Id.Should().Be(V220BridgeReference.ArchitectureId);
        vm.IsSelectionLoaded.Should().BeTrue();
        vm.OpacityOverride.Should().Be(70, "the saved override");
        vm.SelectedHiddenTabs.Should().Equal("Analyze");
        vm.DisableUnused.Should().BeFalse();
        vm.RequiredAddins.Select(r => r.TabName).Should().Equal("Architecture", "Kinship", "Enscape", "pyRevit");
        vm.RequiredAddins[0].StatusLabel.Should().Be("Native");
        vm.RequiredAddins[1].StatusLabel.Should().Be("Loaded", "Kinship is on the ribbon");
        dialogs.Confirms.Should().BeEmpty();

        // Structure has never been answered: the adopt prompt asks, and the answer is saved.
        vm.SelectedCard = vm.Profiles.Single(c => c.Name == "Structure");
        dialogs.Confirms.Should().ContainSingle().Which.Title.Should().Be("Adopt RSTify Presets?");
        vm.SelectedHiddenTabs.Should().Equal("Collaborate", "Massing & Site");
        vm.DisableUnused.Should().BeTrue();

        // Back to Architecture: override the opacity, hide Collaborate too, load.
        vm.SelectedCard = vm.Profiles.Single(c => c.Name == "Architecture");
        vm.OpacityOverride = 55;
        vm.CommitOpacityOverride();
        vm.TabToggles.Single(t => t.Tab == "Collaborate").IsHidden = true;
        await vm.LoadAsync();
        dialogs.AddinReports.Should().ContainSingle().Which.Title.Should().Be("Add-in Check");
        dialogs.AddinChanges.Should().BeEmpty("no add-in to change: no confirm");

        // The v2.2.0 bridge, the same steps.
        var r = LoaderPaths.Under(_reference);
        V220BridgeReference.RecordUserProfilePrefs(r.PrefsFile, V220BridgeReference.StructureId, ["Massing & Site", "Collaborate"], true, true);
        V220BridgeReference.RecordPanelOpacityOverride(r.PrefsFile, V220BridgeReference.ArchitectureId, 55);
        V220BridgeReference.LoadProfileWrites(r.ProfilesDir, r.ActiveProfileFile, r.PrefsFile, "Architecture", V220BridgeReference.ArchitectureId,
            false, ["Analyze", "Collaborate"], V220BridgeReference.LoadedAt.ToString("o"));

        foreach (var file in new[] { "user_profile_prefs.json", "active_profile.json" })
        {
            File.ReadAllBytes(Path.Combine(_ported, file)).Should().Equal(File.ReadAllBytes(Path.Combine(_reference, file)),
                $"{file} must be byte-identical to the v2.2.0 bridge's write");
        }
        Expected("user_profile_prefs.json", "user_profile_prefs.after-load.json");
        Expected("active_profile.json", "active_profile.after-load.json");

        // The profile files themselves are never rewritten by the Loader.
        foreach (var profile in Directory.GetFiles(Path.Combine(FixtureDir, "profiles")))
        {
            File.ReadAllBytes(Path.Combine(_ported, "profiles", Path.GetFileName(profile))).Should().Equal(File.ReadAllBytes(profile));
        }
    }

    private void Expected(string file, string expectedName)
    {
        var expected = File.ReadAllText(Path.Combine(FixtureDir, "expected", expectedName)).ReplaceLineEndings("\n").TrimEnd();
        File.ReadAllText(Path.Combine(_ported, file)).ReplaceLineEndings("\n").TrimEnd().Should().Be(expected, expectedName);
    }
}
