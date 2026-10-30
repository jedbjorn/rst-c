// rst-c's own (not vendored): AC6 for the Loader (spec #16, WU3). A v2.2.0 fixture set (two
// profiles, the prefs and the active profile, Fixtures/V220) is read by the new services, and
// every write the Loader makes (the adopt answer, the opacity override, Load Profile, Unload,
// Delete, the prefs migration) is compared byte for byte with the v2.2.0 bridge's writes
// (V220BridgeReference) run on a second copy, and with the checked-in expected files.

using System.IO;
using FluentAssertions;
using RST.Core.Loader;
using RST.Core.Profiles;
using Xunit;

namespace RST.Tests.Loader;

public sealed class V220CompatibilityTests : IDisposable
{
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "V220");

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

    private static LoaderPaths Paths(string root) => LoaderPaths.Under(root);

    private static void AssertSameBytes(string referenceRoot, string portedRoot, string file)
    {
        var expected = File.ReadAllBytes(Path.Combine(referenceRoot, file));
        var actual = File.ReadAllBytes(Path.Combine(portedRoot, file));
        actual.Should().Equal(expected, $"{file} must be byte-identical to the v2.2.0 bridge's write");
    }

    /// <summary>The checked-in expected file, newlines as this machine writes them (System.Text.Json uses Environment.NewLine).</summary>
    private static void AssertMatchesExpected(string root, string file, string expectedName)
    {
        var expected = File.ReadAllText(Path.Combine(FixtureDir, "expected", expectedName)).ReplaceLineEndings("\n");
        var actual = File.ReadAllText(Path.Combine(root, file)).ReplaceLineEndings("\n");
        actual.TrimEnd().Should().Be(expected.TrimEnd(), expectedName);
    }

    [Fact]
    public void The_v220_fixtures_load_through_the_new_services()
    {
        var paths = Paths(_ported);
        var library = new ProfileLibraryService(paths);
        var profiles = library.List();
        profiles.Select(p => p.Profile.ProfileName).Should().BeEquivalentTo(["Architecture", "Structure"]);
        var arch = profiles.Single(p => p.Profile.Id == V220BridgeReference.ArchitectureId).Profile;
        arch.Panels.Should().HaveCount(4);
        arch.Panels[0].Slots[0].IconFile.Should().Be("pack:building_office_blue");
        arch.Presets!.HiddenTabs.Should().Equal("Analyze");

        var active = library.GetActive();
        active.ProfileId.Should().Be(V220BridgeReference.ArchitectureId);
        active.HiddenTabs.Should().Equal("Analyze");

        var prefs = new LoaderPrefsService(paths).Read();
        prefs.Get(V220BridgeReference.ArchitectureId)!.PanelOpacityOverride.Should().Be(70);
        prefs.Get(V220BridgeReference.StructureId).Should().BeNull("Structure has never been answered: the adopt prompt will ask");
    }

    [Fact]
    public void Adopt_opacity_and_load_write_what_the_v220_bridge_wrote()
    {
        // v2.2.0 bridge.
        var r = Paths(_reference);
        V220BridgeReference.RecordUserProfilePrefs(r.PrefsFile, V220BridgeReference.StructureId, ["Massing & Site", "Collaborate"], true, true);
        V220BridgeReference.RecordPanelOpacityOverride(r.PrefsFile, V220BridgeReference.ArchitectureId, 55);
        V220BridgeReference.LoadProfileWrites(r.ProfilesDir, r.ActiveProfileFile, r.PrefsFile, "Architecture", V220BridgeReference.ArchitectureId,
            false, ["Analyze", "Collaborate"], V220BridgeReference.LoadedAt.ToString("o"));

        // The services the WPF Loader (and the bridge) now call.
        var p = Paths(_ported);
        var prefs = new LoaderPrefsService(p);
        prefs.Record(V220BridgeReference.StructureId, ["Massing & Site", "Collaborate"], true, true).Ok.Should().BeTrue();
        prefs.RecordOpacityOverride(V220BridgeReference.ArchitectureId, 55).Ok.Should().BeTrue();
        var load = new ProfileLoadService(p, [], new FakeScheduler(), new FakeAddinPasses(), clock: () => V220BridgeReference.LoadedAt);
        load.Load(new LoadRequest("Architecture", V220BridgeReference.ArchitectureId, false, ["Analyze", "Collaborate"])).Ok.Should().BeTrue();

        AssertSameBytes(_reference, _ported, "user_profile_prefs.json");
        AssertSameBytes(_reference, _ported, "active_profile.json");
        AssertMatchesExpected(_ported, "user_profile_prefs.json", "user_profile_prefs.after-load.json");
        AssertMatchesExpected(_ported, "active_profile.json", "active_profile.after-load.json");
    }

    [Fact]
    public void Unload_writes_the_v220_blank_stub()
    {
        V220BridgeReference.UnloadProfile(Paths(_reference).ActiveProfileFile);
        new ProfileLoadService(Paths(_ported), [], null, new FakeAddinPasses()).Unload();
        AssertSameBytes(_reference, _ported, "active_profile.json");
        AssertMatchesExpected(_ported, "active_profile.json", "active_profile.blank.json");
    }

    [Fact]
    public void Delete_of_the_loaded_profile_matches_the_v220_bridge()
    {
        var r = Paths(_reference);
        V220BridgeReference.UnloadProfile(r.ActiveProfileFile);
        V220BridgeReference.RemoveProfile(r.ProfilesDir, r.ActiveProfileFile, "Architecture", V220BridgeReference.ArchitectureId);

        var p = Paths(_ported);
        new ProfileLoadService(p, [], null, new FakeAddinPasses()).Unload();
        new ProfileLibraryService(p).Delete("Architecture", V220BridgeReference.ArchitectureId).Ok.Should().BeTrue();

        Directory.GetFiles(p.ProfilesDir).Select(Path.GetFileName).Should().Equal(Directory.GetFiles(r.ProfilesDir).Select(Path.GetFileName));
        AssertSameBytes(_reference, _ported, "active_profile.json");
        AssertSameBytes(_reference, _ported, "user_profile_prefs.json");
    }

    [Fact]
    public void The_prefs_migration_writes_what_the_v220_bridge_wrote()
    {
        // A pre-RST-026 install: hidden tabs in active_profile.json, no prefs file.
        foreach (var root in new[] { _reference, _ported }) File.Delete(Path.Combine(root, "user_profile_prefs.json"));
        V220BridgeReference.GetUserProfilePrefs(Paths(_reference).PrefsFile, Paths(_reference).ActiveProfileFile);
        new LoaderPrefsService(Paths(_ported)).Read();
        AssertSameBytes(_reference, _ported, "user_profile_prefs.json");
        AssertMatchesExpected(_ported, "user_profile_prefs.json", "user_profile_prefs.migrated.json");
    }

    [Fact]
    public void A_v220_profile_round_trips_unchanged_through_the_store()
    {
        // The Loader never rewrites a profile file; reading and saving one keeps its bytes (schema v1).
        var file = Path.Combine(FixtureDir, "profiles", "Architecture_" + V220BridgeReference.ArchitectureId + ".json");
        using var fs = File.OpenRead(file);
        var profile = ProfileSerializer.Read(fs);
        ProfileSerializer.WriteString(profile).ReplaceLineEndings("\n").TrimEnd()
            .Should().Be(File.ReadAllText(file).ReplaceLineEndings("\n").TrimEnd());
    }
}
