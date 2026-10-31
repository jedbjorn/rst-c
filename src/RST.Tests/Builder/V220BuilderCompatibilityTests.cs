// rst-c's own (not vendored): AC6 for the Builder (spec #16, WU4). Each v2.2.0 fixture profile is
// loaded for editing and saved with no edits through the ported rules (loadIntoProfile,
// computeRequiredAddins, the save plan, save_profile), and the file is compared byte for byte
// with what v2.2.0 wrote (V220BuilderReference: the page's captured payload through v2.2.0's
// bridge write). Against the input file, exactly two fields change, as they did in v2.2.0:
// requiredAddins (recomputed from the placed tools at every save) and cleanupTargets (seeded with
// the defaults for a pre-RST-031 profile that has none).

using System.IO;
using System.Text.Json.Nodes;
using FluentAssertions;
using RST.Core.Builder;
using RST.Core.Profiles;
using RST.Tests.Loader;
using Xunit;

namespace RST.Tests.Builder;

public sealed class V220BuilderCompatibilityTests : IDisposable
{
    private readonly string _fixtures = V220BridgeReference.CopyFixtures(V220BuilderReference.FixtureDir);
    private readonly string _reference = Path.Combine(Path.GetTempPath(), "rst-v220-builder-ref-" + Guid.NewGuid().ToString("N"));
    private readonly string _ported = Path.Combine(Path.GetTempPath(), "rst-v220-builder-new-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (var dir in new[] { _fixtures, _reference, _ported })
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
        }
    }

    /// <summary>The ported path: pick the profile in the dropdown, change nothing, Save.</summary>
    private string SavePorted(string id)
    {
        var library = ProfileStore.List(Path.Combine(_fixtures, "profiles"));
        var entry = library.Single(e => e.Profile.Id == id);
        var editing = ProfileEditing.ForEditing(entry.Profile);
        ProfileEditing.PlanSave(editing, editMode: true, entry.Profile.ProfileName, library.Select(e => e.Profile))
            .Should().Be(SavePlanKind.InPlace);
        editing.RequiredAddins = RequiredAddinDetector.Compute(editing, V220BuilderReference.Catalog(), RequiredAddinDetectorTests.ShippedLookup());
        var result = new ProfileSaveService(_ported).Save(editing);
        result.Ok.Should().BeTrue(result.Error);
        return result.FileName!;
    }

    [Theory]
    [InlineData("Architecture", V220BridgeReference.ArchitectureId)]
    [InlineData("Structure", V220BridgeReference.StructureId)]
    public void A_v220_profile_saved_with_no_edits_is_byte_identical_to_v220s_write(string name, string id)
    {
        var referenceFile = V220BuilderReference.SaveProfile(V220BuilderReference.Payload(name), _reference);
        var portedFile = SavePorted(id);
        portedFile.Should().Be(referenceFile);
        File.ReadAllBytes(Path.Combine(_ported, portedFile)).Should().Equal(File.ReadAllBytes(Path.Combine(_reference, referenceFile)));
    }

    [Theory]
    [InlineData(V220BridgeReference.ArchitectureId)]
    [InlineData(V220BridgeReference.StructureId)]
    public void Against_the_input_only_requiredAddins_and_cleanupTargets_change(string id)
    {
        var input = Directory.GetFiles(Path.Combine(_fixtures, "profiles")).Single(f => f.Contains(id, StringComparison.Ordinal));
        var before = JsonNode.Parse(File.ReadAllText(input))!.AsObject();
        var after = JsonNode.Parse(File.ReadAllText(Path.Combine(_ported, SavePorted(id))))!.AsObject();

        after["cleanupTargets"]!.ToJsonString().Should().Be(new JsonArray(CleanupDefaults.Build().Select(t => JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(t))).ToArray()).ToJsonString());
        foreach (var changed in new[] { "requiredAddins", "cleanupTargets" })
        {
            before.Remove(changed);
            after.Remove(changed);
        }
        JsonNode.DeepEquals(before, after).Should().BeTrue("every other field round-trips unchanged");
    }

    [Fact]
    public void The_recomputed_required_addins_match_what_v220_computed()
    {
        var saved = ProfileSerializer.ReadString(File.ReadAllText(Path.Combine(_ported, SavePorted(V220BridgeReference.ArchitectureId))));
        saved.RequiredAddins.Select(r => (r.TabName, r.AddinFile, r.Url)).Should().Equal(
            ("Enscape", "Enscape.addin", "https://enscape3d.com"),
            ("Kinship", "Kinship.addin", ""));
    }
}
