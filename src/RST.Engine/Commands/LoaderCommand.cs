// LoaderCommand.cs — IExternalCommand wired to the "Loader" ribbon button.
//
// Opens the native WPF Profile Loader (spec #16, WU3) modally on the Revit UI
// thread, owned by Revit's main window and in Revit's theme. The hand-off
// lives in RST.UI.Loader.ProfileLoaderLauncher, so RST.UI stays Revit-free:
// Revit is reached only through the switch scheduler and the captured data.

using System.Diagnostics;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RST.Engine.Ribbon;
using RST.Engine.Scanning;
using RST.Engine.Theming;
using RST.UI.Loader;
using RST.Core.Logging;

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
            RevitThemeDetector.ApplyToWindows();
            ProfileLoaderLauncher.Show(commandData.Application.MainWindowHandle, revitVersion, catalog, scheduler, allTabs);
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
