// rst-c's own (not vendored): RST-Probe, the test add-in for feature #16's VM checks (spec #18,
// AC5). It adds a ribbon tab so a capture shows whether Revit loaded it. Each manifest names its
// own application class with its own tab: "RSTProbe" (RstProbe.addin, ProbeApp) for the per-user
// variant and "RSTProbeBundle" (RstProbeBundle.addin, BundleProbeApp) for the ApplicationPlugins
// bundle. Both variants can be installed at once: Revit reuses the first-loaded RstProbe.dll for
// the second manifest, so the tab must come from the class, not from the assembly's path (the VM
// run's "The tab with the input name exists already"). Never shipped.

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RstProbe;

/// <summary>Adds one tab with one button; the tab name is the variant's.</summary>
public abstract class ProbeAppBase : IExternalApplication
{
    protected abstract string Tab { get; }

    protected abstract Type Command { get; }

    public Result OnStartup(UIControlledApplication application)
    {
        application.CreateRibbonTab(Tab);
        var panel = application.CreateRibbonPanel(Tab, "Probe");
        panel.AddItem(new PushButtonData(Tab + "Hello", "RST-Probe", typeof(ProbeAppBase).Assembly.Location, Command.FullName)
        {
            ToolTip = "RST-Probe: rst-c's test add-in (feature #16), tab " + Tab + ".",
        });
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;
}

/// <summary>The per-user variant (RstProbe.addin): tab "RSTProbe".</summary>
public sealed class ProbeApp : ProbeAppBase
{
    public const string TabName = "RSTProbe";

    protected override string Tab => TabName;

    protected override Type Command => typeof(HelloCommand);
}

/// <summary>The bundle variant (RstProbeBundle.addin): tab "RSTProbeBundle".</summary>
public sealed class BundleProbeApp : ProbeAppBase
{
    public const string TabName = "RSTProbeBundle";

    protected override string Tab => TabName;

    protected override Type Command => typeof(BundleHelloCommand);
}

[Transaction(TransactionMode.ReadOnly)]
public sealed class HelloCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
        Hello.Show(ProbeApp.TabName, commandData);
}

[Transaction(TransactionMode.ReadOnly)]
public sealed class BundleHelloCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
        Hello.Show(BundleProbeApp.TabName, commandData);
}

internal static class Hello
{
    public static Result Show(string tab, ExternalCommandData commandData)
    {
        TaskDialog.Show("RST-Probe", "RST-Probe is loaded (" + tab + ", Revit " + commandData.Application.Application.VersionNumber + ").");
        return Result.Succeeded;
    }
}
