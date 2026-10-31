// rst-c's own (not vendored): the bootstrap's boot-log version (SC-054). The bootstrap cannot reference RST.Core, so
// it reads its informational version itself; it must agree with RST.Core's BuildVersion.

using System.Reflection;
using RST.Bootstrap;
using RST.Core.Logging;
using Xunit;

namespace RST.RuntimeTests.Bootstrap;

public class BootstrapVersionTests
{
    [Fact]
    public void The_boot_log_version_is_the_bootstraps_informational_version()
    {
        var bootstrap = typeof(RstBootstrap).Assembly;
        var informational = bootstrap.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.Equal(informational, RstBootstrap.BuildText(bootstrap));
        Assert.Equal(BuildVersion.Informational(bootstrap), RstBootstrap.BuildText(bootstrap));
        Assert.Equal(BuildVersion.Product(informational), RstBootstrap.ProductText(informational));
        Assert.Equal("3.2.0", RstBootstrap.ProductText("3.2.0+abc1234"));
        Assert.Equal("0.0.0-dev", RstBootstrap.ProductText("0.0.0-dev"));
    }

    [Fact]
    public void The_bootstrap_and_the_engines_libraries_carry_the_same_build()
    {
        // One Version for every RST assembly (Directory.Build.props): the boot log and the session log agree.
        Assert.Equal(BuildVersion.Informational(typeof(Log).Assembly), RstBootstrap.BuildText(typeof(RstBootstrap).Assembly));
    }
}
