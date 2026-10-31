// rst-c's own (not vendored): the bootstrap's boot-log version (SC-054). The bootstrap cannot reference RST.Core, so
// it reads its informational version itself (BootVersion); it must agree with RST.Core's BuildVersion. The tests
// take the bootstrap assembly through EngineLocation: RstBootstrap implements a Revit interface, and RevitAPIUI is
// not in this test process.

using System.Reflection;
using RST.Bootstrap;
using RST.Core.Logging;
using Xunit;

namespace RST.RuntimeTests.Bootstrap;

public class BootstrapVersionTests
{
    private static readonly Assembly BootstrapAssembly = typeof(EngineLocation).Assembly;

    [Fact]
    public void The_boot_log_version_is_the_bootstraps_informational_version()
    {
        var informational = BootstrapAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.Equal(informational, BootVersion.BuildText(BootstrapAssembly));
        Assert.Equal(BuildVersion.Informational(BootstrapAssembly), BootVersion.BuildText(BootstrapAssembly));
        Assert.Equal(BuildVersion.Product(informational), BootVersion.ProductText(informational));
        Assert.Equal("3.2.0", BootVersion.ProductText("3.2.0+abc1234"));
        Assert.Equal("0.0.0-dev", BootVersion.ProductText("0.0.0-dev"));
    }

    [Fact]
    public void The_bootstrap_and_the_engines_libraries_carry_the_same_build()
    {
        // One Version for every RST assembly (Directory.Build.props): the boot log and the session log agree.
        Assert.Equal(BuildVersion.Informational(typeof(Log).Assembly), BootVersion.BuildText(BootstrapAssembly));
    }
}
