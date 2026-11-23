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
//   - The catalogue and the open file's facts are read here, inside Execute, on Revit's UI thread, before
//     SearchItLauncher.Show returns. The ribbon's state is read just after Execute has returned, on the
//     dispatcher (spec #35: Revit greys more of its tools while an external command executes than its
//     ribbon shows, so the live CanExecute gate is asked outside the command; SearchItLauncher, "Deferred"). When the catalogue is already built this session
//     (RstApplication.HasCatalog) the window opens with its results. A cold build (first use in a session, a few seconds) happens under a window
//     that is already drawn, with its busy overlay. It is never built later from the modeless window,
//     which has no API context.
//   - A click on a result happens after Execute has returned, outside any API context. The tool
//     runs through SlotInvokeCommand, the dispatch every profile button uses: it calls
//     UIApplication.PostCommand from an AdWindows click, which is outside an API context too, and
//     carries the not-found and not-postable handling (SlotMessages). Nothing is forked here.
//
// Spec #35 (runs per file type): the list is cut down further to what runs here. Revit's live CanExecute is
// asked for places on visible tabs (SearchItCatalog), and the Ribbon Tool Census (RST.Core.Census, embedded per
// Revit year) is applied for the open file and view: its context key is built here from the active document and
// view (CensusDocument), and RST.Core.Search.SearchRunnable lists what runs here, badges NOT IN GUI the tools that
// run only from places Revit does not draw, badges Must Run In the tools that run only in another view of this
// file, and leaves out the rest. A click runs a postable id through SlotInvokeCommand (PostCommand), and any other
// through the ribbon's own command path ("route A", decision #92): after the window has closed, on the UI thread
// outside an API context, Autodesk.Windows.Autodesk.Windows.ComponentManager.CommandHandler.Execute(item) once CanExecute(item) is
// true again. A click that can't run says so (the window's amber message, or the same words in a dialog once the
// window is gone): no click is silent.
//
// Recently used (RST.Core.Search.SearchRecents): a tool is recorded only once Run has dispatched it (PostCommand
// accepted it, or the ribbon's command path ran it), never on a path that shows the can't-run message. The window
// reads the list when it reads the tools, and shows those it lists as chips.
//
// Guards (SC-045 / SC-046): the window is shown through WindowGuard, so a fault on the UI thread
// while it is open is logged and closes the window; everything here is caught and logged, and a
// failure returns Result.Failed with a plain message. Nothing can take Revit down.

using System;
using System.Diagnostics;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RST.Core.Census;
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
            var open = new OpenFile(revitVersion);
            var opened = SearchItLauncher.Show(
                uiApp.MainWindowHandle,
                tools: () => ListForOpenFile(open),
                run: tool => Run(uiApp, revitVersion, tool),
                isReady: () => RstApplication.HasCatalog(revitVersion),
                canRunNow: tool => CanRunNow(revitVersion, tool),
                prepare: () => open.Read(uiApp),
                recents: () => SearchRecentsStore.Read());
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
    /// What Search-It needs from Revit's API for one open, read inside the command (<see cref="Read"/>): the catalogue
    /// (built cold on the first open of a session), the open file's kind and its census values. The tools are judged
    /// from it later, once the command has returned (<see cref="ListForOpenFile"/>): Revit greys more of its own tools
    /// while an external command executes than its ribbon shows (VM, F21-2), so the live CanExecute gate is asked
    /// outside the command, as the census and WU1c asked it.
    /// </summary>
    private sealed class OpenFile(string revitVersion)
    {
        public string RevitVersion { get; } = revitVersion;
        public SearchItCatalog Catalog { get; private set; } = SearchItCatalog.Empty;
        public bool IsFamily { get; private set; }
        public CensusDocument? Document { get; private set; }
        public Exception? DocumentError { get; private set; }

        public void Read(UIApplication uiApp)
        {
            Catalog = RstApplication.GetOrBuildSearchCatalog(RevitVersion);
            IsFamily = IsFamilyOpen(uiApp);
            try
            {
                Document = ReadDocument(uiApp, IsFamily);
            }
            catch (Exception ex)
            {
                // Reported once, as the census's failure, when the tools are judged.
                DocumentError = ex;
            }
        }
    }

    /// <summary>
    /// The tools to list now (spec #33's showing rule, spec #35's runnable rule), judged on the dispatcher after the
    /// command has returned, and the log line that says which list it is, which census context and index it used, how
    /// many tools it lists, badges and leaves out, how many catalogue tools are not showing, and what the rule cost.
    /// The time is the rule's alone: a cold catalogue build is logged by RstApplication.
    /// </summary>
    private static SearchList ListForOpenFile(OpenFile open)
    {
        var revitVersion = open.RevitVersion;
        var catalog = open.Catalog;
        var isFamily = open.IsFamily;
        var sw = Stopwatch.StartNew();
        var year = int.TryParse(revitVersion, out var y) ? y : 0;
        var hasIndex = year > 0 && !CensusIndex.For(year).IsEmpty;
        string? noKey = null;
        var list = catalog.ListShowing(isFamily, () =>
        {
            if (!hasIndex) return null;
            if (open.DocumentError is { } error) throw new InvalidOperationException("the open file and view could not be read", error);
            if (open.Document is not { } document) return null;
            var census = SearchCensus.For(year, document);
            if (census is null)
                noKey = string.IsNullOrEmpty(document.ViewType) ? "no active view"
                    : isFamily ? CensusContext.Family(document.Category ?? "", document.Placement ?? "", document.ViewType)
                    : CensusContext.Project(document.ViewType);
            return census;
        });
        var ms = sw.ElapsedMilliseconds;
        // list= is the kind of the open file; context= the census key used (with the view it stands in for after a
        // fallback); left-out= showing tools that run nowhere in this file; hidden= catalogue tools with no showing place.
        Log.Information("Search-It opened: revit={Version}, list={List}, context={Context}, index={Index}, tools={Tools}, "
                + "must-run-in={MustRunIn}, left-out={LeftOut}, not-in-gui={NotInGui}, hidden={Hidden}, rule={Ms}ms",
            revitVersion, isFamily ? "family" : "project", list.Census?.Key.ToString() ?? "none", hasIndex ? year.ToString(System.Globalization.CultureInfo.InvariantCulture) : "none",
            list.Tools.Count, list.MustRunInCount, list.LeftOut, list.NotInGuiCount, list.Hidden, ms);
        if (!hasIndex)
            Log.Information("Search-It: no census index for revit={Version}; the census removes, badges and labels nothing (Revit's live check only)", revitVersion);
        else if (noKey is not null)
            Log.Information("Search-It: the census has no context for {Asked}; it removes, badges and labels nothing (Revit's live check only)", noKey);
        return list;
    }

    /// <summary>
    /// The census's plain values for the open file and view (spec #35, "Context Key"), read as the census read them
    /// (src/RST.Census/Docs.cs): the family's category as its BuiltInCategory name and its FamilyPlacementType, and the
    /// active view's ViewType. A value that can't be read is "Invalid", which no census context has. Throws only when
    /// the active document or view itself can't be read; the caller then lists without the census, with one Warning.
    /// </summary>
    private static CensusDocument ReadDocument(UIApplication uiApp, bool isFamily)
    {
        var ui = uiApp.ActiveUIDocument;
        var view = ui?.ActiveView;
        var viewType = view?.ViewType.ToString();
        if (!isFamily || ui?.Document is not { } document) return new CensusDocument(false, null, null, viewType);
        var family = document.OwnerFamily;
        string category = "Invalid", placement = "Invalid";
        try
        {
            if (family?.FamilyCategory is { } c) category = ((BuiltInCategory)c.Id.Value).ToString();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Search-It: the family's category could not be read");
        }
        try
        {
            if (family is not null) placement = family.FamilyPlacementType.ToString();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Search-It: the family's placement type could not be read");
        }
        return new CensusDocument(true, category, placement, viewType);
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

    /// <summary>The click's decision, from <see cref="CanRunNow"/> to the queued <see cref="Run"/> of the same tool.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SearchTool, object> Decisions = new();

    /// <summary>
    /// Asked on a click on a runnable card, before the window closes (spec #35, Running a Tool, steps 4-5; decision
    /// #127): the place is chosen now from fresh visibility (the first run place whose tab and panel show, drawn
    /// first) and asked through <see cref="SearchItCatalog.AskLive"/>. False, a throw, or nothing showing keeps the
    /// window open with the amber message; nothing showing with every project place on a user-hidden tab posts a
    /// postable tool without asking (spec #33 AC12). The decision is kept for <see cref="Run"/>, which runs on that place.
    /// </summary>
    private static bool CanRunNow(string revitVersion, SearchTool tool)
    {
        var decision = RstApplication.GetOrBuildSearchCatalog(revitVersion).DecideClick(tool);
        Decisions.AddOrUpdate(tool, decision);
        if (decision.Route != ClickRoute.Refuse) return true;
        if (decision.Error is { } error) Log.Warning(error, "Search-It: CanExecute threw for {Name} ({CommandId})", tool.DisplayName, tool.Id);
        Log.Information("Search-It: {Name} ({CommandId}) can't run now ({Answer})", tool.DisplayName, tool.Id, decision.Answer);
        return false;
    }

    /// <summary>
    /// Runs the tool the user chose (a click, or Enter on the highlighted card), after the window has closed. Queued
    /// by the window on the dispatcher (Dispatcher.BeginInvoke), outside Execute and outside any API context; it never
    /// throws. It follows the click's decision (<see cref="CanRunNow"/>):
    /// <list type="number">
    /// <item>A postable id: the profile buttons' dispatch (<see cref="SlotInvokeCommand"/>: PostCommand, with its
    /// not-found and not-postable messages).</item>
    /// <item>Any other: route A (decision #92) through <see cref="SearchItCatalog.ExecuteLive"/> on the place the click
    /// chose: its tab and panel are read again, then CanExecute, and only when that is true,
    /// ComponentManager.CommandHandler.Execute on that item. Anything else, or a throw, shows the amber message's words
    /// in a dialog: the window is gone by now.</item>
    /// </list>
    /// A Must Run In tool never gets here (the window does not run it).
    /// </summary>
    private static void Run(UIApplication uiApp, string revitVersion, SearchTool tool)
    {
        try
        {
            if (!tool.CanRun)
            {
                Log.Warning("Search-It: {Name} ({CommandId}) must run in another view; not run", tool.DisplayName, tool.Id);
                return;
            }
            var catalog = RstApplication.GetOrBuildSearchCatalog(revitVersion);
            var decision = Decisions.TryGetValue(tool, out var kept) ? (ClickDecision)kept : catalog.DecideClick(tool);
            Decisions.Remove(tool);
            if (decision.Route == ClickRoute.Refuse)
            {
                Log.Warning(decision.Error, "Search-It: {Name} ({CommandId}) can't be run now ({Answer}); not run", tool.DisplayName, tool.Id, decision.Answer);
                ShowCantRun(tool);
                return;
            }
            if (tool.Postable)
            {
                Log.Information("Search-It: running {Name} ({CommandId}) from {Location}{How}", tool.DisplayName, tool.Id, tool.Location,
                    decision.Route == ClickRoute.PostWithoutAsking ? " (only on tabs the user hid: posted without asking)" : "");
                if (new SlotInvokeCommand(uiApp, new SlotTarget(SlotKind.Command, tool.Id, tool.DisplayName)).Invoke())
                    SearchRecentsStore.Record(tool);
                return;
            }
            Log.Information("Search-It: running {Name} ({CommandId}) from {Location} through the ribbon's command path (not postable)",
                tool.DisplayName, tool.Id, tool.Location);
            var sw = Stopwatch.StartNew();
            var answer = catalog.ExecuteLive(decision.Handle, item =>
            {
                if (Autodesk.Windows.ComponentManager.CommandHandler is not { } handler)
                    throw new InvalidOperationException("Revit's ribbon command handler is not available");
                handler.Execute(item);
            }, out var error);
            if (SearchGate.AllowsExecute(answer))
            {
                Log.Information("Search-It: {CommandId} returned from the ribbon's command path in {Ms}ms", tool.Id, sw.ElapsedMilliseconds);
                SearchRecentsStore.Record(tool);
                return;
            }
            Log.Warning(error, "Search-It: {Name} ({CommandId}) can't be run now ({Answer}); not run", tool.DisplayName, tool.Id, answer);
            ShowCantRun(tool);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Search-It: running {Name} ({CommandId}) failed", tool.DisplayName, tool.Id);
            ShowCantRun(tool);
        }
    }

    /// <summary>The amber message's words, in Revit's dialog: a route A click that failed after the window closed.</summary>
    private static void ShowCantRun(SearchTool tool) =>
        SlotInvokeCommand.Show(new SlotMessage(SearchViews.CantRunHere(tool.DisplayName, tool.Location), ""), "search-it-cant-run");
}
