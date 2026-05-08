// BuilderCommand.cs — IExternalCommand wired to the "Builder" ribbon button.
//
// Same WebView2 host as the Loader, but lands directly on profile_builder.html
// instead of profile_loader.html. Lets the user edit/create profiles without
// going through the loader picker first — matches the upstream pyRevit RST
// pattern where Builder and Loader are dedicated entry points.

using System.Diagnostics;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RST.Engine.Scanning;
using RST.UI.Loader;
using Serilog;

namespace RST.Engine.Commands;

[Transaction(TransactionMode.ReadOnly)]
[Regeneration(RegenerationOption.Manual)]
public sealed class BuilderCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var sw = Stopwatch.StartNew();
        var app = commandData.Application.Application;
        var revitVersion = app.VersionNumber;
        var user = string.IsNullOrEmpty(app.Username) ? "(unknown)" : app.Username;
        Log.Information("=== Builder session opened: revit={Version}, user={User} ===", revitVersion, user);
        try
        {
            var catalog = RstApplication.GetOrBuildCatalog(revitVersion);
            var scheduler = RstApplication.GetSwitchScheduler();
            var allTabs = RibbonTabEnumerator.Enumerate();
            LoaderHost.ShowModalToBuilder(revitVersion, catalog, scheduler, allTabs);
            Log.Information("=== Builder session closed: duration={Ms}ms ===", sw.ElapsedMilliseconds);
            return Result.Succeeded;
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "Builder session FAILED after {Ms}ms", sw.ElapsedMilliseconds);
            message = ex.Message;
            return Result.Failed;
        }
    }
}
