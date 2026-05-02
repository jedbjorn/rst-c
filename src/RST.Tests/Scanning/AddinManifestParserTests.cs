using System.IO;
using System.Linq;
using FluentAssertions;
using RST.Core.Scanning;
using Xunit;

namespace RST.Tests.Scanning;

public sealed class AddinManifestParserTests
{
    private static string FixtureDir =>
        Path.Combine(Path.GetDirectoryName(typeof(AddinManifestParserTests).Assembly.Location)!,
                     "Fixtures", "Addins");

    [Fact]
    public void ParseFile_SingleEntry_Roundtrips()
    {
        var m = AddinManifestParser.ParseFile(Path.Combine(FixtureDir, "Acme.addin"));

        m.Should().NotBeNull();
        m!.FileName.Should().Be("Acme.addin");
        m.IsDisabled.Should().BeFalse();
        m.Entries.Should().HaveCount(1);

        var e = m.Entries[0];
        e.Type.Should().Be("Application");
        e.AssemblyPath.Should().Be(@"C:\Program Files\Acme\Revit 2024\AcmeTools.dll");
        e.AddinId.Should().Be("11111111-1111-1111-1111-111111111111");
        e.Name.Should().Be("Acme Tools");
        e.VendorId.Should().Be("ACME");
    }

    [Fact]
    public void ParseFile_MultipleEntries_AllReturned()
    {
        var m = AddinManifestParser.ParseFile(Path.Combine(FixtureDir, "MultiEntry.addin"));

        m!.Entries.Should().HaveCount(2);
        m.Entries[0].Type.Should().Be("Application");
        m.Entries[1].Type.Should().Be("Command");
        m.Entries[1].AssemblyPath.Should().Be(@"C:\Vendor\MultiEntry\Cmd.dll",
            because: "surrounding quotes should be stripped");
    }

    [Fact]
    public void ParseFile_RstDisabledSuffix_StrippedFromCanonicalName()
    {
        var path = Path.Combine(FixtureDir, "Disabled.addin.RSTdisabled");
        var m = AddinManifestParser.ParseFile(path);

        m!.IsDisabled.Should().BeTrue();
        m.FileName.Should().Be("Disabled.addin");
        m.FilePath.Should().Be(path);
    }

    [Fact]
    public void ParseDirectory_SkipsMalformed_AndReportsViaCallback()
    {
        var skipped = new System.Collections.Generic.List<string>();
        var manifests = AddinManifestParser
            .ParseDirectory(FixtureDir, onSkip: (p, _) => skipped.Add(Path.GetFileName(p)))
            .ToList();

        manifests.Select(m => m.FileName).Should().BeEquivalentTo(new[]
        {
            "Acme.addin", "MultiEntry.addin", "Disabled.addin",
        });
        skipped.Should().Contain("Malformed.addin");
    }

    [Fact]
    public void ParseDirectory_NonExistent_ReturnsEmpty()
    {
        AddinManifestParser.ParseDirectory("/no/such/path/here").Should().BeEmpty();
    }
}
