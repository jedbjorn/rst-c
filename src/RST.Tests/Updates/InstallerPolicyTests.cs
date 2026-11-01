using System.IO;
using System.Linq;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace RST.Tests.Updates;

public class InstallerPolicyTests
{
    [Fact]
    public void The_bundle_forwards_the_tray_override_to_RST_and_keeps_the_standalone_default()
    {
        var root = Builder.RequiredAddinDetectorTests.RepoRoot();
        var xml = XDocument.Load(Path.Combine(root, "installer-bundle", "Bundle.wxs"));
        XNamespace wix = "http://wixtoolset.org/schemas/v4/wxs";
        XNamespace bal = "http://wixtoolset.org/schemas/v4/wxs/bal";
        var variable = xml.Descendants(wix + "Variable").Single(e => (string?)e.Attribute("Name") == "RstRestartManagerControl");
        ((string?)variable.Attribute("Value")).Should().Be("0");
        ((string?)variable.Attribute(bal + "Overridable")).Should().Be("yes");
        var rst = xml.Descendants(wix + "MsiPackage").Single(e => (string?)e.Attribute("Id") == "RST");
        var property = rst.Elements(wix + "MsiProperty").Single(e => (string?)e.Attribute("Name") == "MSIRESTARTMANAGERCONTROL");
        ((string?)property.Attribute("Value")).Should().Be("[RstRestartManagerControl]");
    }
}
