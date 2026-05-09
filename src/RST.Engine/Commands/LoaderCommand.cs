// LoaderCommand.cs — IExternalCommand wired to the "Loader" ribbon button.
//
// Opens the WebView2-hosted profile selector modally on the Revit UI
// thread. Hand-off lives in RST.UI.Loader.LoaderHost so the Engine
// project doesn't take a hard dependency on WebView2 types.

using System.Diagnostics;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RST.Engine.Ribbon;
using RST.Engine.Scanning;
using RST.UI.Loader;
using Serilog;

namespace RST.Engine.Commands;

[Transaction(TransactionMode.ReadOnly)]
[Regeneration(RegenerationOption.Manual)]
public sealed class LoaderCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var sw = Stopwatch.StartNew();
        var app = commandData.Application.Application;
        var revitVersion = app.VersionNumber;
        var user = string.IsNullOrEmpty(app.Username) ? "(unknown)" : app.Username;
        Log.Information("=== Loader session opened: revit={Version}, user={User} ===", revitVersion, user);
        try
        {
            var catalog = RstApplication.GetOrBuildCatalog(revitVersion);
            var scheduler = RstApplication.GetSwitchScheduler();
            var allTabs = RibbonTabEnumerator.Enumerate();
            LoaderHost.ShowModal(revitVersion, catalog, scheduler, allTabs);
            Log.Information("=== Loader session closed: duration={Ms}ms ===", sw.ElapsedMilliseconds);
            return Result.Succeeded;
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "Loader session FAILED after {Ms}ms", sw.ElapsedMilliseconds);
            message = ex.Message;
            return Result.Failed;
        }
    }
}
