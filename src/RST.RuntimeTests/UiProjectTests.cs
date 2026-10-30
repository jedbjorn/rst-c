// Adapted from splash-it tests/SplashIt.RuntimeTests/UiProjectTests.cs (splash-it commit ba86a71), MIT, same owner.
// Changes: namespaces (SplashIt.* to RST.*); RST.UI must not reference RevitAPI, RevitAPIUI, Autodesk.*, AdWindows or RST.Engine.

using System.Reflection;
using Xunit;

namespace RST.RuntimeTests;

/// <summary>
/// RST.UI (spec #16, Architecture: "RST.UI stays Revit-free") loads in a plain WPF test host
/// and references no Revit assembly and not RST.Engine, so its windows can be tested here
/// without Revit (AC2).
/// </summary>
public class UiProjectTests
{
    private static Assembly Ui() => Assembly.Load("RST.UI");

    [Fact]
    public void RstUi_Loads_WithoutRevit()
    {
        var ui = Ui();
        Assert.Equal("RST.UI", ui.GetName().Name);
        Assert.NotEmpty(ui.GetTypes());
    }

    [Fact]
    public void RstUi_ReferencesNoRevitAssemblyOrEngine()
    {
        var references = Ui().GetReferencedAssemblies().Select(a => a.Name ?? "").ToArray();
        Assert.DoesNotContain(references, n =>
            n.StartsWith("RevitAPI", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("Autodesk", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("AdWindows", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("RST.Engine", StringComparison.OrdinalIgnoreCase));
    }
}
