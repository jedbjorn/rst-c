// rst-c's own (not vendored): SC-048 item 5. The two user-facing Revit message sites use the
// same resource title and opt out of Revit's context-dependent prefix. Revit itself cannot run here.

using System.Xml.Linq;
using RST.Tests.Loader;
using Xunit;

namespace RST.Tests.Ribbon;

public class RstDialogTitleTests
{
    [Fact]
    public void User_messages_share_the_RST_title_without_the_add_in_prefix()
    {
        var root = AddinLookupServiceTests.RepoRoot();
        var strings = XDocument.Load(Path.Combine(root, "src/RST.UI/Resources/UiStrings.resx"));
        Assert.Equal("RST", strings.Root!.Elements("data")
            .Single(e => (string?)e.Attribute("name") == "Common_RstTitle").Element("value")!.Value);

        foreach (var file in new[] { "Ribbon/SlotInvokeCommand.cs", "Commands/RstifyCommand.cs" })
        {
            var source = File.ReadAllText(Path.Combine(root, "src/RST.Engine", file));
            Assert.Contains("new TaskDialog(UiStrings.Common_RstTitle)", source);
            Assert.Contains("TitleAutoPrefix = false", source);
        }
    }
}
