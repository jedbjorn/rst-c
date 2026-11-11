// SearchItCommand.cs — IExternalCommand wired to the "Search-It" ribbon button (v3.4).
//
// The user knows a tool exists but not where it is on the ribbon. Search-It lists every tool the
// Profile Builder's catalogue offers (the same RstApplication.GetOrBuildCatalog, so the two can never
// disagree: what RST knows won't run is already left out by CommandCatalog.Build), searched by name;
// a click on a result closes the window and runs the tool.
//
// The window (RST.UI.SearchIt) is modeless, so the view behind it can be panned, zoomed and orbited.
// That shapes the threading:
//   - The catalogue is read here, inside Execute, on Revit's UI thread: SearchItLauncher.Show shows
//     the window with its busy overlay, draws it, and then calls back for the catalogue before it
//     returns. A cold build (first use in a session, a few seconds) happens under a window that says
//     so. It is never built later from the modeless window, which has no API context.
//   - A click on a result happens after Execute has returned, outside any API context. The tool
//     runs through SlotInvokeCommand, the dispatch every profile button uses: it calls
//     UIApplication.PostCommand from an AdWindows click, which is outside an API context too, and
//     carries the not-found and not-postable handling (SlotMessages). Nothing is forked here.
//
// Guards (SC-045 / SC-046): the window is shown through WindowGuard, so a fault on the UI thread
// while it is open is logged and closes the window; everything here is caught and logged, and a
// failure returns Result.Failed with a plain message. Nothing can take Revit down.

using System;
using System.Diagnostics;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RST.Core.Logging;
using RST.Core.Ribbon;
using RST.Core.Search;
using RST.Engine.Ribbon;
using RST.Engine.Theming;
using RST.UI.Resources;
using RST.UI.SearchIt;

namespace RST.Engine.Commands;

[Transaction(TransactionMode.ReadOnly)]
[Regeneration(RegenerationOption.Manual)]
public sealed class SearchItCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var uiApp = commandData.Application;
            var revitVersion = uiApp.Application.VersionNumber;
            Log.Information("=== Search-It opened: revit={Version} ===", revitVersion);
            RevitThemeDetector.ApplyToWindows();
            var opened = SearchItLauncher.Show(
                uiApp.MainWindowHandle,
                catalog: () => RstApplication.GetOrBuildCatalog(revitVersion),
                run: tool => Run(uiApp, tool));
            Log.Information(opened
                    ? "Search-It window shown in {Ms}ms; it stays open until the user closes it or picks a tool"
                    : "Search-It was already open; focused it ({Ms}ms)",
                sw.ElapsedMilliseconds);
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Search-It FAILED to open after {Ms}ms", sw.ElapsedMilliseconds);
            message = UiStrings.SearchIt_Failed;
            return Result.Failed;
        }
    }

    /// <summary>
    /// Runs the tool the user clicked, after the window has closed: the profile buttons' dispatch
    /// (<see cref="SlotInvokeCommand"/>: PostCommand, with its not-found and not-postable messages).
    /// Called from the window's click, outside Execute; it never throws.
    /// </summary>
    private static void Run(UIApplication uiApp, SearchTool tool)
    {
        try
        {
            Log.Information("Search-It: running {Name} ({CommandId}) from {Location}", tool.DisplayName, tool.Id, tool.Location);
            new SlotInvokeCommand(uiApp, new SlotTarget(SlotKind.Command, tool.Id, tool.DisplayName)).Execute(null);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Search-It: running {Name} ({CommandId}) failed", tool.DisplayName, tool.Id);
        }
    }
}
