// CompanionRegistryTests.cs — validates the checked-in companion registry.
//
// Feature #13 (Companion Add-ins: Registry & Chained Install), spec #15, AC1.
// companions/companions.json is edited by hand in a PR whenever a companion
// ships or a pin moves, and Phase 2's setup bundle is generated from it. The
// rules (spec #15 "Registry"; contract in companions/README.md):
//   - the schema parses: known fields only, each with its type;
//   - ids are unique kebab-case;
//   - every `shippable` entry has a complete `release` (tag, .msi asset,
//     64-hex sha256, GUID upgradeCode, version), a non-null addinClientId
//     and VendorId DSOS (C6);
//   - no upgradeCode or addinClientId is rst-c's own, and no two companions
//     share one.
// The validator lives here, not in RST.Core: rst-c has no runtime awareness
// of companions (spec #15 Out of Scope).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace RST.Tests.Companions;

/// <summary>rst-c's own product identities, which no companion may reuse.</summary>
internal sealed record RstIdentities(IReadOnlySet<Guid> UpgradeCodes, IReadOnlySet<Guid> ClientIds);

/// <summary>Rule check over the registry JSON. Returns every violation found (empty = valid).</summary>
internal static class CompanionRegistryValidator
{
    public static readonly string[] Statuses = { "in_development", "shippable", "retired" };

    private static readonly string[] EntryFields =
        { "id", "name", "description", "repo", "status", "revitYears",
          "addinClientId", "vendorId", "defaultSelected", "release" };

    private static readonly string[] ReleaseFields =
        { "tag", "asset", "sha256", "upgradeCode", "version" };

    private static readonly Regex KebabId = new(@"^[a-z0-9]+(-[a-z0-9]+)*$");
    private static readonly Regex Repo = new(@"^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$");
    private static readonly Regex Sha256 = new(@"^[0-9a-fA-F]{64}$");

    public static IReadOnlyList<string> Validate(string json, RstIdentities rst)
    {
        var errors = new List<string>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return new[] { $"registry is not valid JSON: {ex.Message}" };
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new[] { "registry root must be an object" };

            UnknownFields(root, new[] { "schemaVersion", "companions" }, "registry", errors);

            if (!root.TryGetProperty("schemaVersion", out var sv)
                || sv.ValueKind != JsonValueKind.Number || !sv.TryGetInt32(out var v) || v != 1)
                errors.Add("schemaVersion must be the number 1");

            if (!root.TryGetProperty("companions", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                errors.Add("companions must be an array");
                return errors;
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var upgradeCodes = new Dictionary<Guid, string>();
            var clientIds = new Dictionary<Guid, string>();
            var index = 0;
            foreach (var entry in list.EnumerateArray())
            {
                ValidateEntry(entry, index++, rst, ids, names, upgradeCodes, clientIds, errors);
            }
        }
        return errors;
    }

    private static void ValidateEntry(
        JsonElement e, int index, RstIdentities rst,
        HashSet<string> ids, HashSet<string> names,
        Dictionary<Guid, string> upgradeCodes, Dictionary<Guid, string> clientIds,
        List<string> errors)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"companions[{index}] must be an object");
            return;
        }

        var id = e.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString()!
            : null;
        var at = id is null ? $"companions[{index}]" : $"companion '{id}'";

        UnknownFields(e, EntryFields, at, errors);
        foreach (var f in EntryFields)
            if (!e.TryGetProperty(f, out _))
                errors.Add($"{at}: missing field '{f}' (use null where a value is not known yet)");

        if (id is null)
            errors.Add($"{at}: id must be a string");
        else if (!KebabId.IsMatch(id))
            errors.Add($"{at}: id must be kebab-case");
        else if (!ids.Add(id))
            errors.Add($"{at}: duplicate id");

        var name = NonEmptyString(e, "name", at, errors);
        if (name is not null && !names.Add(name))
            errors.Add($"{at}: duplicate name '{name}'");
        NonEmptyString(e, "description", at, errors);

        var repo = NonEmptyString(e, "repo", at, errors);
        if (repo is not null && !Repo.IsMatch(repo))
            errors.Add($"{at}: repo must be 'owner/name'");

        var status = NonEmptyString(e, "status", at, errors);
        if (status is not null && !Statuses.Contains(status))
            errors.Add($"{at}: status must be one of {string.Join(", ", Statuses)}");
        var shippable = status == "shippable";

        if (e.TryGetProperty("revitYears", out var years))
        {
            if (years.ValueKind != JsonValueKind.Array || years.GetArrayLength() == 0)
                errors.Add($"{at}: revitYears must be a non-empty array");
            else
            {
                var seen = new HashSet<int>();
                foreach (var y in years.EnumerateArray())
                {
                    if (y.ValueKind != JsonValueKind.Number || !y.TryGetInt32(out var year) || year < 2000 || year > 2099)
                        errors.Add($"{at}: revitYears holds a value that is not a Revit year");
                    else if (!seen.Add(year))
                        errors.Add($"{at}: revitYears lists {year} twice");
                }
            }
        }

        Guid? clientId = null;
        if (e.TryGetProperty("addinClientId", out var cid) && cid.ValueKind != JsonValueKind.Null)
        {
            clientId = ParseGuid(cid);
            if (clientId is null)
                errors.Add($"{at}: addinClientId must be a GUID or null");
        }
        if (shippable && clientId is null)
            errors.Add($"{at}: shippable requires addinClientId");
        if (clientId is Guid c)
        {
            if (rst.ClientIds.Contains(c))
                errors.Add($"{at}: addinClientId is rst-c's own ClientId");
            else if (clientIds.TryGetValue(c, out var other))
                errors.Add($"{at}: addinClientId is already used by '{other}'");
            else
                clientIds[c] = id ?? at;
        }

        var vendorId = NonEmptyString(e, "vendorId", at, errors);
        if (shippable && vendorId is not null && vendorId != "DSOS")
            errors.Add($"{at}: shippable requires vendorId DSOS (C6)");

        if (e.TryGetProperty("defaultSelected", out var ds)
            && ds.ValueKind != JsonValueKind.True && ds.ValueKind != JsonValueKind.False)
            errors.Add($"{at}: defaultSelected must be true or false");

        if (!e.TryGetProperty("release", out var release) || release.ValueKind == JsonValueKind.Null)
        {
            if (shippable)
                errors.Add($"{at}: shippable requires a complete release");
            return;
        }
        if (release.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{at}: release must be an object or null");
            return;
        }

        // A non-null release is always complete, whatever the status: a
        // half-filled pin is never valid input to the bundle.
        var rat = $"{at} release";
        UnknownFields(release, ReleaseFields, rat, errors);
        NonEmptyString(release, "tag", rat, errors);
        NonEmptyString(release, "version", rat, errors);

        var asset = NonEmptyString(release, "asset", rat, errors);
        if (asset is not null && !asset.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            errors.Add($"{rat}: asset must be an .msi");

        var sha = NonEmptyString(release, "sha256", rat, errors);
        if (sha is not null && !Sha256.IsMatch(sha))
            errors.Add($"{rat}: sha256 must be 64 hex characters");

        if (!release.TryGetProperty("upgradeCode", out var uc) || ParseGuid(uc) is not Guid upgrade)
        {
            errors.Add($"{rat}: upgradeCode must be a GUID");
            return;
        }
        if (rst.UpgradeCodes.Contains(upgrade))
            errors.Add($"{rat}: upgradeCode is rst-c's own UpgradeCode");
        else if (upgradeCodes.TryGetValue(upgrade, out var owner))
            errors.Add($"{rat}: upgradeCode is already used by '{owner}'");
        else
            upgradeCodes[upgrade] = id ?? at;
    }

    private static void UnknownFields(JsonElement obj, string[] known, string at, List<string> errors)
    {
        foreach (var p in obj.EnumerateObject())
            if (!known.Contains(p.Name, StringComparer.Ordinal))
                errors.Add($"{at}: unknown field '{p.Name}'");
    }

    private static string? NonEmptyString(JsonElement obj, string field, string at, List<string> errors)
    {
        if (!obj.TryGetProperty(field, out var el))
        {
            if (Array.IndexOf(ReleaseFields, field) >= 0)
                errors.Add($"{at}: missing field '{field}'");
            return null; // entry-level missing fields are reported once, above
        }
        if (el.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(el.GetString()))
        {
            errors.Add($"{at}: {field} must be a non-empty string");
            return null;
        }
        return el.GetString();
    }

    // Registry form: the 36-character "D" format, as written in .addin and .wxs files.
    private static Guid? ParseGuid(JsonElement el) =>
        el.ValueKind == JsonValueKind.String && Guid.TryParseExact(el.GetString(), "D", out var g) ? g : null;
}

public class CompanionRegistryTests
{
    // rst-c's identities, pinned so a deleted or rewritten source file cannot
    // silently drop one: the unified RST.msi, the R27 preview RST-R27.msi
    // (folded by feature #11, but its code stays rst-c's), and RST.addin.
    private static readonly Guid RstMsiUpgradeCode = Guid.Parse("AF0331FC-5280-492A-94BD-95EDF108BE74");
    private static readonly Guid RstR27UpgradeCode = Guid.Parse("02BE4399-A563-4B15-BD12-ECF5D8486FB3");
    private static readonly Guid RstAddinClientId = Guid.Parse("4f8ef7a0-0001-4000-8000-525354000001");

    private const string GanttItClientId = "9920fb42-542b-4ce1-9659-a078489af6b0";
    private const string SplashItClientId = "0b1cc712-e936-4da1-9680-2d9a95f88271";

    private static readonly Regex WxsUpgradeCode = new(@"UpgradeCode=""\{?([0-9A-Fa-f-]{36})\}?""");
    private static readonly Regex AddinClientId = new(@"<ClientId>\s*\{?([0-9A-Fa-f-]{36})\}?\s*</ClientId>");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RST-C.sln")))
            dir = dir.Parent;
        dir.Should().NotBeNull($"repo root (RST-C.sln) must be discoverable from {AppContext.BaseDirectory}");
        return dir!.FullName;
    }

    private static string RegistryPath() => Path.Combine(RepoRoot(), "companions", "companions.json");

    private static string RegistryJson() => File.ReadAllText(RegistryPath());

    /// <summary>
    /// rst-c's identities read from source (every UpgradeCode in an
    /// installer*/ .wxs, the ClientId in RST.Bootstrap's .addin), plus the
    /// pinned ones. A later installer (e.g. Phase 2's bundle) is covered
    /// without editing this test.
    /// </summary>
    private static RstIdentities Rst()
    {
        var root = RepoRoot();
        var upgradeCodes = new HashSet<Guid> { RstMsiUpgradeCode, RstR27UpgradeCode };
        foreach (var dir in Directory.GetDirectories(root, "installer*"))
            foreach (var wxs in Directory.GetFiles(dir, "*.wxs", SearchOption.AllDirectories))
                foreach (Match m in WxsUpgradeCode.Matches(File.ReadAllText(wxs)))
                    upgradeCodes.Add(Guid.Parse(m.Groups[1].Value));

        var clientIds = new HashSet<Guid> { RstAddinClientId };
        foreach (var addin in Directory.GetFiles(Path.Combine(root, "src", "RST.Bootstrap"), "*.addin"))
            foreach (Match m in AddinClientId.Matches(File.ReadAllText(addin)))
                clientIds.Add(Guid.Parse(m.Groups[1].Value));

        return new RstIdentities(upgradeCodes, clientIds);
    }

    private static IReadOnlyList<string> Validate(string json) => CompanionRegistryValidator.Validate(json, Rst());

    // ---- the checked-in registry -------------------------------------------

    [Fact]
    public void Checked_in_registry_validates()
    {
        File.Exists(RegistryPath()).Should().BeTrue("the registry is checked in at companions/companions.json");
        Validate(RegistryJson()).Should().BeEmpty();
    }

    [Fact]
    public void Checked_in_registry_tracks_both_companions_with_their_verified_identities()
    {
        var companions = JsonNode.Parse(RegistryJson())!["companions"]!.AsArray();

        var gantt = companions.Single(c => (string?)c!["id"] == "gantt-it")!;
        ((string?)gantt["repo"]).Should().Be("jedbjorn/gantt-it");
        ((string?)gantt["addinClientId"]).Should().Be(GanttItClientId);
        ((string?)gantt["vendorId"]).Should().Be("DSOS");

        var splash = companions.Single(c => (string?)c!["id"] == "splash-it")!;
        ((string?)splash["repo"]).Should().Be("jedbjorn/splash-it");
        ((string?)splash["addinClientId"]).Should().Be(SplashItClientId);
        ((string?)splash["vendorId"]).Should().Be("DSOS");
    }

    [Fact]
    public void Rst_identities_are_discovered_from_source()
    {
        // Guards the regexes: if the source files moved or changed shape,
        // discovery would quietly find nothing and only the pins would remain.
        var root = RepoRoot();
        var wxs = File.ReadAllText(Path.Combine(root, "installer", "Product.wxs"));
        WxsUpgradeCode.Matches(wxs).Select(m => Guid.Parse(m.Groups[1].Value))
            .Should().Contain(RstMsiUpgradeCode, "installer/Product.wxs carries RST.msi's UpgradeCode");

        var addin = File.ReadAllText(Path.Combine(root, "src", "RST.Bootstrap", "RST.addin"));
        AddinClientId.Matches(addin).Select(m => Guid.Parse(m.Groups[1].Value))
            .Should().Contain(RstAddinClientId, "src/RST.Bootstrap/RST.addin carries rst-c's ClientId");
    }

    // ---- the validator ----------------------------------------------------

    /// <summary>
    /// A registry in Phase 2's shape: gantt-it shippable with its v1.1.0 pin
    /// (verified 2026-10-30 with gh release view -R jedbjorn/gantt-it).
    /// </summary>
    private static JsonNode ShippableRegistry()
    {
        var node = JsonNode.Parse(RegistryJson())!;
        var gantt = Companion(node, "gantt-it");
        gantt["status"] = "shippable";
        gantt["release"] = new JsonObject
        {
            ["tag"] = "v1.1.0",
            ["asset"] = "GanttIt-1.1.0.msi",
            ["sha256"] = "06f403468ae484526dc7e8ddc9251bf2b34715ddcb14d668049b63c74d1eb18b",
            ["upgradeCode"] = "644FCAD5-E631-4441-9A11-75F175565136",
            ["version"] = "1.1.0",
        };
        return node;
    }

    private static JsonObject Companion(JsonNode registry, string id) =>
        registry["companions"]!.AsArray().Single(c => (string?)c!["id"] == id)!.AsObject();

    [Fact]
    public void A_complete_shippable_entry_validates()
    {
        Validate(ShippableRegistry().ToJsonString()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("duplicate-id", "duplicate id")]
    [InlineData("id-not-kebab", "id must be kebab-case")]
    [InlineData("shippable-without-release", "shippable requires a complete release")]
    [InlineData("shippable-release-missing-sha256", "missing field 'sha256'")]
    [InlineData("shippable-sha256-not-64-hex", "sha256 must be 64 hex characters")]
    [InlineData("shippable-upgradecode-not-guid", "upgradeCode must be a GUID")]
    [InlineData("shippable-asset-not-msi", "asset must be an .msi")]
    [InlineData("shippable-without-clientid", "shippable requires addinClientId")]
    [InlineData("shippable-vendor-not-dsos", "shippable requires vendorId DSOS")]
    [InlineData("reuses-rst-msi-upgradecode", "upgradeCode is rst-c's own UpgradeCode")]
    [InlineData("reuses-rst-r27-upgradecode", "upgradeCode is rst-c's own UpgradeCode")]
    [InlineData("reuses-rst-clientid", "addinClientId is rst-c's own ClientId")]
    [InlineData("reuses-rst-clientid-other-case", "addinClientId is rst-c's own ClientId")]
    [InlineData("shares-upgradecode-between-companions", "upgradeCode is already used by 'gantt-it'")]
    [InlineData("shares-clientid-between-companions", "addinClientId is already used by 'gantt-it'")]
    [InlineData("unknown-status", "status must be one of")]
    [InlineData("unknown-field", "unknown field 'relase'")]
    [InlineData("missing-field", "missing field 'defaultSelected'")]
    [InlineData("schema-version-2", "schemaVersion must be the number 1")]
    [InlineData("revit-year-not-a-year", "not a Revit year")]
    [InlineData("partial-release-on-in-development", "sha256 must be 64 hex characters")]
    public void A_broken_registry_fails_validation(string breakage, string expectedError)
    {
        var registry = ShippableRegistry();
        var gantt = Companion(registry, "gantt-it");
        var splash = Companion(registry, "splash-it");
        var release = gantt["release"]!.AsObject();

        switch (breakage)
        {
            case "duplicate-id": splash["id"] = "gantt-it"; break;
            case "id-not-kebab": splash["id"] = "Splash_It"; break;
            case "shippable-without-release": gantt["release"] = null; break;
            case "shippable-release-missing-sha256": release.Remove("sha256"); break;
            case "shippable-sha256-not-64-hex": release["sha256"] = "06f40346"; break;
            case "shippable-upgradecode-not-guid": release["upgradeCode"] = "not-a-guid"; break;
            case "shippable-asset-not-msi": release["asset"] = "GanttIt-1.1.0-Revit2027.zip"; break;
            case "shippable-without-clientid": gantt["addinClientId"] = null; break;
            case "shippable-vendor-not-dsos": gantt["vendorId"] = "RSTC"; break;
            case "reuses-rst-msi-upgradecode": release["upgradeCode"] = "AF0331FC-5280-492A-94BD-95EDF108BE74"; break;
            case "reuses-rst-r27-upgradecode": release["upgradeCode"] = "02be4399-a563-4b15-bd12-ecf5d8486fb3"; break;
            case "reuses-rst-clientid": splash["addinClientId"] = "4f8ef7a0-0001-4000-8000-525354000001"; break;
            case "reuses-rst-clientid-other-case": gantt["addinClientId"] = "4F8EF7A0-0001-4000-8000-525354000001"; break;
            case "shares-upgradecode-between-companions":
                splash["status"] = "shippable";
                splash["release"] = release.DeepClone();
                splash["release"]!["asset"] = "SplashIt-1.1.0.msi";
                break;
            case "shares-clientid-between-companions": splash["addinClientId"] = GanttItClientId; break;
            case "unknown-status": splash["status"] = "beta"; break;
            case "unknown-field": splash["relase"] = null; break;
            case "missing-field": splash.Remove("defaultSelected"); break;
            case "schema-version-2": registry["schemaVersion"] = 2; break;
            case "revit-year-not-a-year": splash["revitYears"] = new JsonArray(2026, 27); break;
            case "partial-release-on-in-development":
                splash["release"] = new JsonObject
                {
                    ["tag"] = "v1.1.0", ["asset"] = "SplashIt-1.1.0.msi", ["sha256"] = "TODO",
                    ["upgradeCode"] = "080648F4-EEC7-4DB6-A793-683A1C008BFC", ["version"] = "1.1.0",
                };
                break;
            default: throw new ArgumentOutOfRangeException(nameof(breakage), breakage, null);
        }

        Validate(registry.ToJsonString()).Should().Contain(e => e.Contains(expectedError),
            $"breakage '{breakage}' must be reported");
    }

    [Fact]
    public void Malformed_json_fails_validation()
    {
        Validate("{ \"schemaVersion\": 1, \"companions\": [ ").Should()
            .ContainSingle(e => e.StartsWith("registry is not valid JSON"));
    }

    [Fact]
    public void Each_breakage_is_reported_against_the_entry_it_is_in()
    {
        var registry = ShippableRegistry();
        Companion(registry, "splash-it")["status"] = "shippable"; // release still null

        Validate(registry.ToJsonString()).Should().Equal(
            "companion 'splash-it': shippable requires a complete release");
    }
}
