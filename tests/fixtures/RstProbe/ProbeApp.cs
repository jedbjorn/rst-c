// rst-c's own (not vendored): RST-Probe, the test add-in for feature #16's VM checks (spec #18,
// AC5). It adds a ribbon tab so a capture shows whether Revit loaded it: "RSTProbe" for the
// per-user manifest, "RSTProbeBundle" when it loads from an ApplicationPlugins .bundle. Its one
// button says which variant and Revit year are running. Never shipped.

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RstProbe;

public sealed class ProbeApp : IExternalApplication
{
    public const string UserTab = "RSTProbe";
    public const string BundleTab = "RSTProbeBundle";

    /// <summary>The tab for the variant this assembly loaded as.</summary>
    public static string TabName =>
        typeof(ProbeApp).Assembly.Location.Contains(".bundle", StringComparison.OrdinalIgnoreCase) ? BundleTab : UserTab;

    public Result OnStartup(UIControlledApplication application)
    {
        var tab = TabName;
        application.CreateRibbonTab(tab);
        var panel = application.CreateRibbonPanel(tab, "Probe");
        panel.AddItem(new PushButtonData("RstProbeHello", "RST-Probe", typeof(ProbeApp).Assembly.Location, typeof(HelloCommand).FullName)
        {
            ToolTip = "RST-Probe: rst-c's test add-in (feature #16).",
        });
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;
}

[Transaction(TransactionMode.ReadOnly)]
public sealed class HelloCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        TaskDialog.Show("RST-Probe", "RST-Probe is loaded (" + ProbeApp.TabName + ", Revit " + commandData.Application.Application.VersionNumber + ").");
        return Result.Succeeded;
    }
}
