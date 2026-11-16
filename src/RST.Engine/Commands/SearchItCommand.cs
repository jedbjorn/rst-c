// SearchItCommand.cs — IExternalCommand wired to the "Search-It" ribbon button (v3.4).
//
// The user knows a tool exists but not where it is on the ribbon. Search-It lists the tools whose
// ribbon place is showing for the file that is open (spec #33), searched by name; a click on a result
// closes the window and runs the tool.
//
// The list comes from Search-It's own catalogue (RstApplication.GetOrBuildSearchCatalog: the Builder's
// scan, with the family-editor tabs kept), cut down when the window opens to the places Revit is
// showing now: a family lists family tools, a project lists project tools. The rule is
// RST.Core.Search.SearchShowing; the state it needs is read from the ribbon objects the scan kept
// (SearchItCatalog), never by walking the ribbon again. If that state cannot be read, an unfiltered
// list is shown (SearchShowing.Unfiltered: in a family the whole search catalogue, in a project the
// tools Search-It listed before spec #33) and a Warning logged: Search-It still opens.
//
// The window (RST.UI.SearchIt) is modeless, so the view behind it can be panned, zoomed and orbited.
// That shapes the threading:
//   - The catalogue and the ribbon's state are read here, inside Execute, on Revit's UI thread, before
//     SearchItLauncher.Show returns. When the catalogue is already built this session
//     (RstApplication.HasCatalog) the window opens with its results. A cold build (first use in a session, a few seconds) happens under a window
//     that is already drawn, with its busy overlay. It is never built later from the modeless window,
//     which has no API context.
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
using RST.Engine.Scanning;
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
                tools: () => ListForOpenFile(uiApp, revitVersion),
                run: tool => Run(uiApp, tool),
                isReady: () => RstApplication.HasCatalog(revitVersion));
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
    /// The tools to list now (spec #33, "Showing Rule"), and the log line that says which list it is, how
    /// many tools it has, how many catalogue tools it leaves out, and what the rule cost. The time is
    /// the rule's alone: a cold catalogue build is logged by RstApplication.
    /// </summary>
    private static SearchList ListForOpenFile(UIApplication uiApp, string revitVersion)
    {
        var catalog = RstApplication.GetOrBuildSearchCatalog(revitVersion);
        var sw = Stopwatch.StartNew();
        var isFamily = IsFamilyOpen(uiApp);
        var list = catalog.ListShowing(isFamily);
        // list= is the kind of the open file; hidden= counts catalogue tools not listed (not places).
        Log.Information("Search-It opened: revit={Version}, list={List}, tools={Tools}, hidden={Hidden}, rule={Ms}ms",
            revitVersion, isFamily ? "family" : "project", list.Tools.Count, catalog.Tools.Count - list.Tools.Count,
            sw.ElapsedMilliseconds);
        return list;
    }

    /// <summary>
    /// True when the active document is a family. A project template is a project. With no active
    /// document, or when its kind cannot be read, Search-It lists project tools and says so in the log.
    /// </summary>
    private static bool IsFamilyOpen(UIApplication uiApp)
    {
        try
        {
            if (uiApp.ActiveUIDocument?.Document is { } document) return document.IsFamilyDocument;
            Log.Warning("Search-It: no active document; listing project tools");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Search-It: the open file's kind could not be read; listing project tools");
        }
        return false;
    }

    /// <summary>
    /// Runs the tool the user chose (a click, or Enter on the highlighted card), after the window has
    /// closed: the profile buttons' dispatch (<see cref="SlotInvokeCommand"/>: PostCommand, with its
    /// not-found and not-postable messages). Queued by the window on the dispatcher, outside Execute;
    /// it never throws.
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
