// rst-c's own: the build's version in the startup log lines (SC-054, SC-015). The header is built from an
// assembly's informational version, never a constant; RST's assemblies carry the release version in FileVersion
// and InformationalVersion while AssemblyVersion stays at 0.1.0.0 for the bootstrap's resolver.

using System;
using System.Reflection;
using System.Reflection.Emit;
using FluentAssertions;
using RST.Core.Logging;
using Xunit;

namespace RST.Tests.Logging;

public class BuildVersionTests
{
    private static Assembly Fake(string? informational, Version version)
    {
        var attributes = informational is null
            ? Array.Empty<CustomAttributeBuilder>()
            : new[]
            {
                new CustomAttributeBuilder(
                    typeof(AssemblyInformationalVersionAttribute).GetConstructor(new[] { typeof(string) })!,
                    new object[] { informational }),
            };
        return AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("RstFake" + Guid.NewGuid().ToString("N")) { Version = version }, AssemblyBuilderAccess.Run, attributes);
    }

    [Fact]
    public void The_engine_header_names_the_release_its_commit_revit_and_the_runtime()
    {
        BuildVersion.EngineHeader("3.2.0+abc1234", "2026", "2026.4", "26.4.10.32", ".NET 8.0.11")
            .Should().Be("RST 3.2.0 (3.2.0+abc1234) starting in Revit 2026.4 (26.4.10.32), .NET 8.0.11");
    }

    [Fact]
    public void The_engine_header_falls_back_to_the_revit_year_and_leaves_out_a_missing_build()
    {
        BuildVersion.EngineHeader("0.0.0-dev", "2025", null, " ", ".NET 8.0.11")
            .Should().Be("RST 0.0.0-dev (0.0.0-dev) starting in Revit 2025, .NET 8.0.11");
    }

    [Fact]
    public void The_engine_header_is_read_from_the_assembly_informational_version_not_a_constant()
    {
        var header = BuildVersion.EngineHeader(Fake("9.8.7+feedbee", new Version(0, 1, 0, 0)), "2027", "2027.1", "27.1.0.5");
        header.Should().StartWith("RST 9.8.7 (9.8.7+feedbee) starting in Revit 2027.1 (27.1.0.5), ");
        header.Should().EndWith(BuildVersion.Runtime());
        BuildVersion.Runtime().Should().StartWith(".NET");

        BuildVersion.EngineHeader(Fake("1.2.3+0000000", new Version(0, 1, 0, 0)), "2027", "2027.1", "27.1.0.5")
            .Should().StartWith("RST 1.2.3 (1.2.3+0000000) starting");
    }

    [Fact]
    public void Without_an_informational_version_the_assembly_version_stands_in()
    {
        BuildVersion.Informational(Fake(null, new Version(4, 5, 6, 7))).Should().Be("4.5.6");
    }

    [Theory]
    [InlineData("3.2.0+abc1234", "3.2.0")]
    [InlineData("0.0.0-dev+abc1234", "0.0.0-dev")]
    [InlineData("3.2.0", "3.2.0")]
    [InlineData("", "0.0.0")]
    public void The_product_version_is_the_informational_version_without_its_commit(string informational, string product) =>
        BuildVersion.Product(informational).Should().Be(product);

    [Fact]
    public void RSTs_assemblies_carry_one_release_version_in_file_and_informational_version()
    {
        // Directory.Build.props: every RST assembly from one Version (RstDevVersion-dev here, the release's a.b.c in CI).
        var core = typeof(BuildVersion).Assembly;
        var informational = core.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        BuildVersion.Informational(core).Should().Be(informational);

        var numeric = BuildVersion.Product(informational).Split('-')[0];
        core.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version.Should().Be(numeric + ".0");
        // Above v3.1.1, the latest release, whose tray files are 3.1.1.0: Windows Installer would not install an
        // "older" file over it, and the MajorUpgrade would then leave none (installer/README.md, "Untagged builds").
        Version.Parse(numeric).Should().BeGreaterThan(new Version(3, 1, 1));
        var plus = informational.IndexOf('+');
        if (plus >= 0) informational[(plus + 1)..].Should().HaveLength(7, "the commit is the short hash (RstShortSourceRevision)");
    }

    [Fact]
    public void Untagged_binaries_msi_and_bundle_share_one_version_RstDevVersion()
    {
        // One source (installer/README.md, "Untagged builds"): the wixproj fallbacks and stage.sh read
        // Directory.Build.props' RstDevVersion and carry no number of their own, so they cannot drift apart.
        var root = Builder.RequiredAddinDetectorTests.RepoRoot();
        var props = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));
        var dev = System.Text.RegularExpressions.Regex.Match(props, @"<RstDevVersion>(\d+\.\d+\.\d+)</RstDevVersion>");
        dev.Success.Should().BeTrue("Directory.Build.props defines RstDevVersion as a.b.c");
        props.Should().Contain("<Version Condition=\"'$(Version)' == ''\">$(RstDevVersion)-dev</Version>");

        var msi = File.ReadAllText(Path.Combine(root, "installer", "RST.Installer.wixproj"));
        msi.Should().Contain("<RstMsiVersionEffective Condition=\"'$(RstMsiVersionEffective)' == ''\">$(RstDevVersion)</RstMsiVersionEffective>");
        var bundle = File.ReadAllText(Path.Combine(root, "installer-bundle", "RST.Setup.wixproj"));
        bundle.Should().Contain("<RstBundleVersionEffective Condition=\"'$(RstBundleVersionEffective)' == ''\">$(RstDevVersion)</RstBundleVersionEffective>");
        File.ReadAllText(Path.Combine(root, "build", "stage.sh")).Should().Contain("<RstDevVersion>");

        // This test build is untagged: its binaries carry RstDevVersion.
        var informational = typeof(BuildVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        if (Environment.GetEnvironmentVariable("RST_VERSION") is not { Length: > 0 })
            BuildVersion.Product(informational).Should().Be(dev.Groups[1].Value + "-dev");
    }

    [Fact]
    public void AssemblyVersion_stays_fixed_for_the_bootstraps_resolver()
    {
        // docs/DEVELOPING.md, "The bootstrap's assembly resolver": a per-release AssemblyVersion would let one RST
        // copy in a Revit process decline another (different major). Change it only with the resolver in mind.
        typeof(BuildVersion).Assembly.GetName().Version.Should().Be(new Version(0, 1, 0, 0));
    }
}
