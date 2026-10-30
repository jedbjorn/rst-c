// rst-c's own (not vendored): AC6's reference for the Builder (spec #16, WU4).
//
// What v2.2.0 wrote when a v2.2.0 profile was picked in the Builder's dropdown and saved with no
// edits. Two halves:
//   - the page's side is captured, not re-implemented: Fixtures/V220/builder/<name>.save-payload.json
//     is the JSON v2.2.0's profile_builder.html sent to save_profile, produced by running the page's
//     own loadIntoProfile (on get_profiles' DTO, which has no cleanupTargets) and
//     computeRequiredAddins (over Fixtures/V220/builder/catalog.json and the shipped
//     addin_lookup.json) in Node, from `git show v2.2.0:src/RST.UI/Assets/profile_builder.html`;
//   - the bridge's side is v2.2.0 LoaderBridge.SaveProfile's write, copied below (the only change:
//     the profiles folder is a parameter, not %AppData%\RST\profiles).
// RST.RuntimeTests compiles this file too.

using System.IO;
using System.Text.Json;
using RST.Core.Builder;
using RST.Core.Profiles;

namespace RST.Tests.Builder;

public static class V220BuilderReference
{
    /// <summary>src/RST.Tests/Fixtures/V220 in the repo (found from the test output, so RST.RuntimeTests can read it too).</summary>
    public static string FixtureDir
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "RST-C.sln"))) return Path.Combine(dir.FullName, "src", "RST.Tests", "Fixtures", "V220");
            throw new InvalidOperationException("RST-C.sln not found above " + AppContext.BaseDirectory);
        }
    }

    /// <summary>The page's save_profile payload for a fixture profile ("Architecture" or "Structure").</summary>
    public static string Payload(string name) => File.ReadAllText(Path.Combine(FixtureDir, "builder", name + ".save-payload.json"));

    /// <summary>The get_catalog fixture the payloads were computed with.</summary>
    public static IReadOnlyList<CatalogCommand> Catalog() =>
        JsonSerializer.Deserialize<List<CatalogCommand>>(
            File.ReadAllText(Path.Combine(FixtureDir, "builder", "catalog.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    /// <summary>v2.2.0 SaveProfile (LoaderBridge.cs:509-560): deserialize, id and date when missing, validate, save.</summary>
    public static string SaveProfile(string profileJson, string profilesDir)
    {
        var profile = JsonSerializer.Deserialize<Profile>(profileJson)
                      ?? throw new InvalidOperationException("empty profile");
        if (string.IsNullOrEmpty(profile.Id)) profile.Id = Guid.NewGuid().ToString();
        if (string.IsNullOrEmpty(profile.ExportDate)) profile.ExportDate = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var errors = ProfileSerializer.Validate(profile);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("; ", errors));
        return ProfileStore.Save(profile, profilesDir);
    }
}
