// LoaderCommand.cs — IExternalCommand wired to the "Loader" ribbon button.
//
// Opens the WebView2-hosted profile selector modally on the Revit UI
// thread. Hand-off lives in RST.UI.Loader.LoaderHost so the Engine
// project doesn't take a hard dependency on WebView2 types.

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RST.UI.Loader;
using Serilog;

namespace RST.Engine.Commands;

[Transaction(TransactionMode.ReadOnly)]
[Regeneration(RegenerationOption.Manual)]
public sealed class LoaderCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var revitVersion = commandData.Application.Application.VersionNumber;
        Log.Information("Loader opened (Revit {Version})", revitVersion);
        try
        {
            var catalog = RstApplication.GetOrBuildCatalog(revitVersion);
            LoaderHost.ShowModal(revitVersion, catalog);
            return Result.Succeeded;
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "Loader window failed to open.");
            message = ex.Message;
            return Result.Failed;
        }
    }
}
