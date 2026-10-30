// rst-c's own (not vendored): AC6 through the view-model (spec #16, WU4). The v2.2.0 fixture
// profiles (src/RST.Tests/Fixtures/V220) are picked in the Profile Builder view-model over the real
// backend and saved with no edits; the file is compared byte for byte with what v2.2.0 wrote
// (V220BuilderReference: the page's captured save payload through v2.2.0's bridge write). The
// fields v2.2.0 also rewrote, requiredAddins and cleanupTargets, are covered by the Core
// V220BuilderCompatibilityTests; here they match v2.2.0's values too, so the whole file is equal.

using System.IO;
using FluentAssertions;
using RST.Core.Builder;
using RST.Core.Loader;
using RST.Core.Scanning;
using RST.RuntimeTests.Loader;
using RST.RuntimeTests.Support;
using RST.Tests.Builder;
using RST.Tests.Loader;
using RST.UI.Builder;
using RST.UI.Loader;
using Xunit;

namespace RST.RuntimeTests.Builder;

public sealed class ProfileBuilderDataCompatibilityTests : IDisposable
{
    private readonly string _ported = V220BridgeReference.CopyFixtures(V220BuilderReference.FixtureDir);
    private readonly string _reference = Path.Combine(Path.GetTempPath(), "rst-v220-builder-ref-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (var dir in new[] { _ported, _reference })
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
        }
    }

    /// <summary>The scanned commands that get_catalog turns into the catalog.json fixture (host tab included).</summary>
    private static IReadOnlyList<ScannedCommand> Scanned() =>
        V220BuilderReference.Catalog().Select(c => new ScannedCommand(
            c.Id, c.DisplayName, Enum.Parse<CommandOrigin>(c.Origin), c.AddinFile, c.AssemblyPath,
            c.HostTab ?? c.SourceTab, c.HostTab is null ? c.SourcePanel : c.LiteralPanel)).ToList();

    [Theory]
    [InlineData("Architecture")]
    [InlineData("Structure")]
    public async Task A_v220_profile_picked_and_saved_with_no_edits_matches_v220s_write(string name)
    {
        File.Exists(LoaderAssets.LookupPath).Should().BeTrue("the shipped addin_lookup.json is next to RST.UI.dll");
        var scheduler = new FakeScheduler();
        var backend = new ProfileBuilderBackend("2026", Scanned(), scheduler, ["Architecture", "Enscape", "Kinship"],
            LoaderPaths.Under(_ported), new FakeAddinPasses(), LoaderAssets.LookupPath);
        backend.Catalog.Should().BeEquivalentTo(V220BuilderReference.Catalog(), "get_catalog's promotion gives the fixture's shape");

        var dialogs = new FakeDialogs();
        var clock = new ManualClock();
        var vm = new ProfileBuilderViewModel(backend, dialogs, clock.Schedule, a => a());
        vm.Initialize();
        vm.SelectedProfileOption = vm.ProfileOptions.Single(o => o.Entry?.Profile.ProfileName == name);
        vm.IsDirty.Should().BeFalse();
        (await vm.SaveAsync()).Should().BeTrue();

        var file = Directory.GetFiles(Path.Combine(_ported, "profiles")).Single(f => Path.GetFileName(f).StartsWith(name + "_", StringComparison.Ordinal));
        var referenceFile = V220BuilderReference.SaveProfile(V220BuilderReference.Payload(name), _reference);
        Path.GetFileName(file).Should().Be(referenceFile);
        File.ReadAllBytes(file).Should().Equal(File.ReadAllBytes(Path.Combine(_reference, referenceFile)));
        Directory.GetFiles(Path.Combine(_ported, "profiles")).Should().HaveCount(2, "saving in place adds no file");
    }
}
