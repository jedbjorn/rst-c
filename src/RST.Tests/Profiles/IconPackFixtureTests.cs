// IconPackFixtureTests.cs — spec #9 compatibility-fixture gate: the
// full.json fixture carries BOTH icon value forms (legacy bare
// pack:<name> and explicit pack:<name>_<color>), and load / save /
// export round trips preserve every value exactly — the Builder and
// the serializer must never rewrite a legacy bare value into the
// explicit form (or mangle an explicit one).

using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using RST.Core.Profiles;
using RST.Core.Ribbon;
using Xunit;

namespace RST.Tests.Profiles;

public class IconPackFixtureTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Profiles", name);

    private static List<string?> IconFiles(Profile p) =>
        p.Panels.SelectMany(panel => panel.Slots)
                .Select(slot => slot?.IconFile)
                .ToList();

    [Fact]
    public void Full_fixture_carries_both_icon_value_forms()
    {
        using var stream = File.OpenRead(FixturePath("full.json"));
        var profile = ProfileSerializer.Read(stream);
        var values = IconFiles(profile).Where(v => v is not null).ToList();

        // Legacy bare forms stay on file (compatibility anchors).
        values.Should().Contain(new[] { "pack:move", "pack:link_external" },
            "the fixture keeps legacy bare pack:<name> values");

        // At least one explicit form, valid per the shared parser.
        var explicitValues = values.Where(v =>
            IconPack.TryParseValue(v, out var parsed) && parsed!.IsExplicit).ToList();
        explicitValues.Should().NotBeEmpty("the fixture must pin the explicit pack:<name>_<color> form");

        // Every pack value in the fixture parses under the shared contract.
        foreach (var v in values)
            IconPack.TryParseValue(v, out _).Should().BeTrue($"{v} must parse under the IconPack contract");
    }

    [Fact]
    public void Load_save_round_trip_preserves_icon_strings_exactly()
    {
        using var stream = File.OpenRead(FixturePath("full.json"));
        var first = ProfileSerializer.Read(stream);
        var before = IconFiles(first);

        var roundTrip = ProfileSerializer.WriteString(first);
        var second = ProfileSerializer.ReadString(roundTrip);

        IconFiles(second).Should().Equal(before,
            "save must not rewrite bare values or mangle explicit ones");
    }

    [Fact]
    public void Export_zip_round_trip_preserves_icon_strings_exactly()
    {
        using var stream = File.OpenRead(FixturePath("full.json"));
        var first = ProfileSerializer.Read(stream);
        var before = IconFiles(first);

        using var zip = new MemoryStream();
        ProfileZip.Pack(first, resolvedLogoPath: null, resolvedUrl: null, destination: zip);
        zip.Position = 0;
        var package = ProfileZip.Unpack(zip);

        IconFiles(package.Profile).Should().Equal(before,
            "export must not rewrite bare values or mangle explicit ones");
    }
}
