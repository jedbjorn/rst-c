// rst-c's own (not vendored): one context's census run (spec #34, "Per-tool classification", "Runner and recovery").
//
// run|<context> classifies every command id on the visible, non-contextual tabs of the active scratch document:
//   third-party owner -> no row (counted in the summary)  ->  skip list -> skipped  ->  gate false -> greyed
//   -> bind + cancel, else a real start with the teardown armed  ->  idle proof.
// Each place gets a row; a place the not-drawn sweep never saw drawn is `not-drawn` (whatever the id does elsewhere).
// Rows are appended to rows.tsv as soon as a tool is done, so a run resumes after a crash, a stop or a new VM session:
// ids that already have a non-error row for this context are not run again. current.txt names the tool being run,
// so the host watchdog can put a tool that crashed or hung Revit on the skip list (tools/census/run.sh).

using System.Diagnostics;
using System.IO;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Windows;
using RibbonItem = Autodesk.Windows.RibbonItem;

namespace RST.Census;

public sealed partial class CensusApp
{
    // rows.tsv = the full table's columns (spec #34) + two internal ones the assembler uses for exceptions.tsv's reason
    public const string RowsHeader = "year\tbuild\tcontext\tid\ttab\tpanel\tpath\tlabel\tresult\ttested\topened\tteardown\tleft\tgate\tms\trun_date\tcensus_build\tnote\tjournal";

    volatile bool _stop, _running;
    Dictionary<string, string> _skip = new(StringComparer.Ordinal);

    void LoadSkip()
    {
        _skip = new Dictionary<string, string>(StringComparer.Ordinal);
        var p = Path.Combine(Root, "skip.tsv");
        if (!File.Exists(p)) return;
        foreach (var l in File.ReadAllLines(p))
        {
            var f = l.Split('\t');
            if (f.Length < 2 || l.StartsWith("#") || f[0] == "year") continue;
            if (f[0].Trim() != "*" && f[0].Trim() != _year) continue;
            _skip[f[1].Trim()] = f.Length > 2 && f[2].Trim() != "" ? f[2].Trim() : "skip list";
        }
    }

    static string Safe(string ctx) => ctx.Replace('/', '_');

    void Progress(string line) => Append("progress.txt", $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}\r\n");

    async Task RunContext(string[] f)
    {
        _running = true; _stop = false;
        var ctx = f[1];
        var rowsPath = Path.Combine(YearDir, "rows.tsv");
        int n = 0;
        try
        {
            await InitRun(); LoadSkip();
            string key = "", docStart = ""; int elems0 = -1; bool mod0 = false; string title = "";
            await Api(app =>
            {
                key = ContextKey(app); var d = app.ActiveUIDocument.Document; title = d.Title;
                elems0 = ElementCount(d); mod0 = d.IsModified; docStart = $"'{d.Title}' view '{app.ActiveUIDocument.ActiveView.Name}'";
            }, 10000);
            if (key != ctx) { Progress($"CONTEXT MISMATCH want {ctx} have {key}: run not started"); return; }

            var done = new HashSet<string>(StringComparer.Ordinal);
            if (File.Exists(rowsPath))
                foreach (var l in File.ReadLines(rowsPath).Skip(1)) { var c = l.Split('\t'); if (c.Length > 8 && c[2] == ctx && c[8] != "error") done.Add(c[3]); }
            else File.WriteAllText(rowsPath, RowsHeader + "\r\n", new UTF8Encoding(false));

            // the walk: visible, non-contextual tabs only (never a hidden tab in a family: WU1c)
            var places = Walk().Where(p => !string.IsNullOrEmpty(p.Item.Id) && p.Tab.IsVisible && !p.Tab.IsContextualTab).ToList();
            var ids = places.Select(p => p.Item.Id).Distinct(StringComparer.Ordinal).ToList();

            // owners (decision #110)
            Dictionary<RibbonItem, string> apiAsm = null; var hasBinding = new Dictionary<string, bool>(StringComparer.Ordinal);
            await Api(app =>
            {
                apiAsm = ApiButtonAssemblies(app, places.Select(p => p.Tab.Title).Where(t => !string.IsNullOrEmpty(t)));
                foreach (var id in ids) { try { var c = RevitCommandId.LookupCommandId(id); if (c != null) hasBinding[id] = c.HasBinding; } catch { } }
            }, 30000);
            var bindings = JournalBindings();
            var ownerSkip = new Dictionary<string, Owner>(StringComparer.Ordinal); var unknown = new Dictionary<string, Owner>(StringComparer.Ordinal); var stockBound = new Dictionary<string, Owner>(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                var it = places.First(p => p.Item.Id == id).Item;
                Owner o = null;
                bool addinButton = id.StartsWith("CustomCtrl_", StringComparison.Ordinal) || (it is RibbonCommandItem rc && rc.CommandHandler != null);
                if (addinButton)
                {
                    string asm = null;
                    foreach (var p in places.Where(p => p.Item.Id == id)) if (apiAsm != null && apiAsm.TryGetValue(p.Item, out asm)) break;
                    if (asm != null) o = OwnerOfAssembly(asm, "api button");
                    else { string hasm = null; try { hasm = (it as RibbonCommandItem)?.CommandHandler?.GetType().Assembly.Location; } catch { } o = OwnerOfAssembly(hasm, "handler"); }
                }
                else if (hasBinding.TryGetValue(id, out var hb) && hb)
                {
                    o = bindings.TryGetValue(id, out var appName) ? OwnerOfApp(appName) : new Owner { How = "HasBinding; binder not in the journal" };
                    if (o.Known && o.Stock) stockBound[id] = o;
                }
                if (o == null) continue;
                if (o.Known && !o.Stock) ownerSkip[id] = o;
                else if (!o.Known) unknown[id] = o;
            }

            Progress($"START {ctx} doc {docStart} elements={elems0} modified={mod0} places={places.Count} placeKeys={places.Select(p => (p.Item.Id, p.TabId, p.PanelTitle, p.Path)).Distinct().Count()} ids={ids.Count} alreadyDone={done.Count} skipList={_skip.Count} ownerSkipped={ownerSkip.Count} ownerUnknown={unknown.Count} build={_build} census={_censusBuild}");

            // not-drawn sweep
            var dlog = new StringBuilder();
            var drawn = await DrawnSweep(places, dlog);
            var dsb = new StringBuilder("tab\tpanel\tpath\tid\tlabel\titemVisible\tparentsVisible\tpanelVisible\tdrawn\r\n");
            foreach (var p in places) dsb.Append(p.TabId).Append('\t').Append(p.PanelTitle).Append('\t').Append(p.Path).Append('\t').Append(p.Item.Id).Append('\t').Append(p.Label).Append('\t').Append(p.Item.IsVisible).Append('\t').Append(p.ParentsVisible).Append('\t').Append(p.Panel.IsVisible).Append('\t').Append(drawn.Contains(p.Item)).Append("\r\n");
            WriteFile(Path.Combine("drawn", Safe(ctx) + ".tsv"), dsb.ToString());
            WriteFile(Path.Combine("drawn", Safe(ctx) + ".log"), dlog.ToString());
            Progress($"DRAWN {ctx} drawn={places.Count(p => drawn.Contains(p.Item))} notDrawn={places.Count(p => !drawn.Contains(p.Item))}");

            bool aborted = false;
            _sess = Baseline();
            var swAll = Stopwatch.StartNew();
            var date = DateTime.Now.ToString("yyyy-MM-dd");
            foreach (var id in ids)
            {
                if (_stop) { Progress($"STOPPED {ctx} by stop"); break; }
                if (done.Contains(id) || ownerSkip.ContainsKey(id)) continue;
                var pl = places.Where(p => p.Item.Id == id).ToList();
                var via = pl.OrderByDescending(p => drawn.Contains(p.Item)).ThenByDescending(p => p.Panel.IsVisible).ThenByDescending(p => p.Item.IsVisible).First();
                File.WriteAllText(Path.Combine(Root, "current.txt"), $"{DateTime.Now:o}\t{_year}\t{ctx}\t{id}\r\n");
                var log = new StringBuilder();
                ToolRes r;
                var b0 = Baseline();
                if (_skip.TryGetValue(id, out var why)) r = new ToolRes { Id = id, Result = "skipped", Note = why };
                else if (!via.Tab.IsVisible) r = new ToolRes { Id = id, Result = "error", Note = "tab hidden at test time; not asked (WU1c hazard)" };
                else r = await RunTool(via.Item, Gate(via.Item), log);
                if (r.Tested == "" || r.Tested == "gate")
                {
                    var idle = await IdleProof(b0, () => true);
                    if (idle != "") { r.Left = (r.Left + " " + idle).Trim(); r.IdleOk = false; }
                }
                var sb = new StringBuilder();
                // Revit keeps hidden copies of some containers (a second Paint or Dimension split button, fold-panel
                // variants): copies with the same tab, panel and path are one place, drawn if any copy is drawn
                foreach (var g in pl.GroupBy(p => (p.TabId, p.PanelTitle, p.Path)))
                {
                    var p = g.OrderByDescending(x => drawn.Contains(x.Item)).ThenByDescending(x => x.Panel.IsVisible).First();
                    string result = r.Result, note = r.Note;
                    if (result is not ("skipped" or "not-a-command" or "error") && !drawn.Contains(p.Item))
                    {
                        result = "not-drawn";
                        note = (!p.Panel.IsVisible ? "not drawn: its panel is hidden here" : !p.Item.IsVisible || !p.ParentsVisible ? "not drawn: hidden in the ribbon model at this place" : "not drawn: no visible control at this place")
                               + $" (the id: {r.Result})";
                    }
                    var teardown = r.Result == "skipped" ? "skip: " + r.Note : r.Teardown;
                    sb.Append(string.Join("\t", _year, _build, ctx, S(id), S(p.TabId), p.PanelTitle, S(p.Path), p.Label, result, r.Tested,
                        string.Join(",", r.Opened), S(Trunc(teardown, 300)), S(Trunc(r.Left, 300)), r.Gate, r.Ms, date, _censusBuild, S(Trunc(note, 300)), S(Trunc(r.Journal, 300)))).Append("\r\n");
                }
                Append("rows.tsv", sb.ToString());
                Append("detail.log", log.Length > 0 ? log.ToString() : $"{DateTime.Now:HH:mm:ss.fff} {id} -> {r.Result} note={r.Note}\r\n");
                n++;
                var keys = pl.GroupBy(p => (p.TabId, p.PanelTitle, p.Path)).ToList(); int nd = keys.Count(g => !g.Any(x => drawn.Contains(x.Item)));
                Progress($"{ctx} {id} {r.Result}{(nd > 0 ? " (not-drawn places " + nd + "/" + keys.Count + ")" : "")} {r.Tested} {r.Ms}ms idle={(r.IdleOk ? "ok" : "FAIL " + S(Trunc(r.Left, 150)))}");
                if (!r.IdleOk && (r.Left.Contains("not idle") || r.Left.Contains("blocked") || r.Left.Contains("mode[") || r.Left.Contains("windows[")))
                {
                    Progress($"STUCK after {id}: waiting up to 90 s for idle");
                    var b2 = Baseline(); bool ok = false;
                    for (int i = 0; i < 90 && !ok; i++)
                    {
                        await Task.Delay(1000);
                        var (ran, _) = await Api(app => { }, 900);
                        ok = ran && NewWins(_sess).Count == 0 && CmdIdle(ActiveCmd()) && ModeSig() == _sess.Mode;
                        if (!ok && i % 10 == 5) { WmCmd(_cidCancelEditor); foreach (var w in NewWins(_sess)) PostMessage(w.h, 0x0010, IntPtr.Zero, IntPtr.Zero); }
                    }
                    Progress(ok ? "recovered" : "STILL STUCK: stopping the run");
                    if (!ok) { aborted = true; break; }
                }
            }
            int elems1 = -1; bool mod1 = false;
            await Api(app => { var d = app.ActiveUIDocument.Document; elems1 = ElementCount(d); mod1 = d.IsModified; }, 10000);
            var sum = new StringBuilder();
            sum.AppendLine($"context\t{ctx}\nbuild\t{_build}\ncensus_build\t{_censusBuild}\ndocument\t{docStart}\nelements_start\t{elems0}\nelements_end\t{elems1}\nmodified_start\t{mod0}\nmodified_end\t{mod1}");
            sum.AppendLine($"ids\t{ids.Count}\nplaces\t{places.Count}\nrun_this_session\t{n}\nseconds\t{swAll.Elapsed.TotalSeconds:F0}");
            sum.AppendLine($"owner_skipped\t{ownerSkip.Count}");
            foreach (var kv in ownerSkip) sum.AppendLine($"  skipped(owner)\t{kv.Key}\t{kv.Value.Who}\t{kv.Value.Vendor}\t{kv.Value.How}");
            sum.AppendLine($"owner_unknown\t{unknown.Count} (treated as stock)");
            foreach (var kv in unknown) sum.AppendLine($"  unknown\t{kv.Key}\t{kv.Value.Who}\t{kv.Value.How}");
            sum.AppendLine($"bound_by_stock_addin\t{stockBound.Count} (started for real)");
            foreach (var kv in stockBound) sum.AppendLine($"  bound\t{kv.Key}\t{kv.Value.Who}\t{kv.Value.Vendor}");
            WriteFile(Path.Combine("summary", Safe(ctx) + ".txt"), sum.ToString());
            Progress($"END {ctx} tools={n} {swAll.Elapsed.TotalSeconds:F0}s elements {elems0}->{elems1} modified {mod0}->{mod1}{(_stop ? " (stopped)" : aborted ? " (incomplete)" : "")}");
            try { File.Delete(Path.Combine(Root, "current.txt")); } catch { }
        }
        catch (Exception ex) { Progress($"RUN EXC {ctx} {ex.GetType().Name}: {S(ex.Message)}"); Err("run " + ctx, ex); }
        finally { _running = false; _arm = false; _fast = false; _sess = null; }
    }
}
