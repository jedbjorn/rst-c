// rst-c's own (not vendored): one tool — gate, start, observe, teardown, idle proof (spec #34, "Per-tool
// classification" and "Teardown"). Ported from WU1d's Probe1d.cs (shared/evidence/search-it-family/wu1d/teardown/).
//
//   gate      effective CanExecute (own handler for add-in buttons, AND IsEnabled). False -> greyed.
//   start     bindable (CanHaveBinding, no other binding): AddInCommandBinding with only BeforeExecuted setting Cancel,
//             then PostCommand if postable, else route A (ComponentManager.CommandHandler.Execute(item)) on the UI
//             thread outside an API context. Otherwise a real start with the teardown armed.
//   observe   BeforeExecuted, DialogBoxShowing, new windows (any process started during the tool), mode / active
//             command, document changes, the journal.
//   teardown  armed DialogBoxShowing -> Cancel; new windows -> Window.Close() / WM_CLOSE (kill a hung WebView2 child
//             only as a last resort); modes -> ID_CANCEL_EDITOR, route A on Cancel Edit Mode; pick prompts (an
//             ExternalEvent that will not run) -> ID_CANCEL_EDITOR; commits -> ID_EDIT_UNDO per transaction; panes ->
//             restored; a late watch for slow windows of other processes.
//   idle      no extra window, mode = baseline, active command idle, ExternalEvent round trip, no WebView2 left.

using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Windows;
using RibbonItem = Autodesk.Windows.RibbonItem;

namespace RST.Census;

public sealed partial class CensusApp
{
    // BeforeExecuted state
    volatile bool _fired; string _firedAs = ""; string _cancellable = ""; volatile bool _cancelMode;

    // ---- processes ----
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint f, uint pid);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PE32 { public uint dwSize, cntUsage, th32ProcessID; public IntPtr th32DefaultHeapID; public uint th32ModuleID, cntThreads, th32ParentProcessID; public int pcPriClassBase; public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr h, ref PE32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr h, ref PE32 e);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    static List<(int pid, int ppid, string exe)> Descendants()
    {
        var all = new List<(int, int, string)>();
        var h = CreateToolhelp32Snapshot(2, 0);
        try
        {
            var e = new PE32 { dwSize = (uint)Marshal.SizeOf<PE32>() };
            if (Process32FirstW(h, ref e)) do all.Add(((int)e.th32ProcessID, (int)e.th32ParentProcessID, e.szExeFile)); while (Process32NextW(h, ref e));
        }
        finally { CloseHandle(h); }
        var me = Environment.ProcessId; var set = new HashSet<int> { me }; var o = new List<(int, int, string)>();
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var p in all) if (!set.Contains(p.Item1) && set.Contains(p.Item2)) { set.Add(p.Item1); o.Add(p); grew = true; }
        }
        return o;
    }

    static HashSet<int> AllPids() { try { return new HashSet<int>(Process.GetProcesses().Select(p => p.Id)); } catch { return new HashSet<int>(); } }
    static bool Alive(int pid) { try { return !Process.GetProcessById(pid).HasExited; } catch { return false; } }
    static string ProcName(int pid) { try { return Process.GetProcessById(pid).ProcessName; } catch { return "?"; } }
    static bool BgExe(string exe) => exe.Equals("msedgewebview2.exe", StringComparison.OrdinalIgnoreCase) || exe.Equals("AdskLicensingAgent.exe", StringComparison.OrdinalIgnoreCase)
        || exe.Equals("RevitWorker.exe", StringComparison.OrdinalIgnoreCase) || exe.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase) || exe.StartsWith("WerFault", StringComparison.OrdinalIgnoreCase);
    static readonly string[] Browsers = { "brave", "chrome", "msedge", "firefox", "iexplore", "opera" };

    // ---- active command and mode ----
    static readonly Func<string> _activeCmd = MakeActive();
    static Func<string> MakeActive()
    {
        try
        {
            var m = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "UIFrameworkServices")?.GetType("UIFrameworkServices.CommandHandlerService")?.GetMethod("getActiveCommandId");
            return m == null ? () => "?" : (Func<string>)Delegate.CreateDelegate(typeof(Func<string>), m);
        }
        catch { return () => "?"; }
    }
    static string ActiveCmd() { try { return _activeCmd() ?? ""; } catch { return "?"; } }
    static bool CmdIdle(string c) => c == "" || c == "ID_BUTTON_SELECT" || c == "?";

    static string ModeSig()
    {
        var rib = ComponentManager.Ribbon; if (rib == null) return "";
        var ctx = rib.Tabs.Where(t => t != null && t.IsVisible && t.IsContextualTab).Select(t => t.Id + ":" + t.Title).OrderBy(x => x);
        var mod = rib.Tabs.FirstOrDefault(t => t?.Id == "Modify")?.Title;
        return string.Join(",", ctx) + "|" + mod;
    }

    // WU1d's list, plus Windows' own text-input host (a false window signal in WU1d, deviation 6)
    static bool IgnoredWindow((IntPtr h, string title, string cls, int pid) w) =>
        w.cls == "tooltips_class32" || w.title.EndsWith("Monitor", StringComparison.Ordinal) || w.cls.StartsWith("#32768") || w.cls == "Shell_TrayWnd" || w.cls == "Progman"
        || (w.cls == "Windows.UI.Core.CoreWindow" && w.title.Contains("Text Input", StringComparison.OrdinalIgnoreCase));

    sealed class Snap { public HashSet<IntPtr> Wins; public HashSet<int> Pids; public string Mode; public HashSet<string> CtxTabs; public string ModifyTitle; public HashSet<int> Kids; public int DocEv; public Dictionary<string, bool> Panes; }

    static HashSet<string> CtxTabs() => new(ComponentManager.Ribbon?.Tabs.Where(t => t != null && t.IsVisible && t.IsContextualTab).Select(t => t.Id + ":" + t.Title) ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
    static string ModifyTitle() => ComponentManager.Ribbon?.Tabs.FirstOrDefault(t => t?.Id == "Modify")?.Title ?? "";

    /// <summary>
    /// A mode is a contextual tab that was not there at the baseline, or a renamed Modify tab. A baseline contextual
    /// tab that went away (a schedule's tab, when a pane takes the focus) is not a mode; the teardown restores it.
    /// </summary>
    static bool ModeChanged(Snap b) => CtxTabs().Any(t => !b.CtxTabs.Contains(t)) || ModifyTitle() != b.ModifyTitle;

    Snap Baseline()
    {
        int de; lock (_docEv) de = _docEv.Count;
        return new Snap { Wins = new HashSet<IntPtr>(AllWins().Select(w => w.h)), Pids = AllPids(), Mode = ModeSig(), CtxTabs = CtxTabs(), ModifyTitle = ModifyTitle(), Kids = new HashSet<int>(Descendants().Select(p => p.pid)), DocEv = de };
    }

    /// <summary>A new window counts if it belongs to Revit or to a process that did not exist at the baseline.</summary>
    List<(IntPtr h, string title, string cls, int pid)> NewWins(Snap b)
    {
        var me = Environment.ProcessId;
        return AllWins().Where(w => !IgnoredWindow(w) && !b.Wins.Contains(w.h) && (w.pid == me || (!b.Pids.Contains(w.pid) && w.title != ""))).ToList();
    }
    List<(int pid, int ppid, string exe)> NewKids(Snap b) => Descendants().Where(p => !b.Kids.Contains(p.pid)).ToList();

    // ---- command ids used by the teardown, and the journal ----
    int _cidCancelEditor, _cidUndo, _cidSelect, _cidCancelSketch;
    string _journal; long _jpos;

    async Task InitRun()
    {
        if (_cidUndo != 0) return;
        await Api(app =>
        {
            int L(string id) { try { return (int)(RevitCommandId.LookupCommandId(id)?.Id ?? 0); } catch { return 0; } }
            _cidCancelEditor = L("ID_CANCEL_EDITOR"); _cidUndo = L("ID_EDIT_UNDO"); _cidSelect = L("ID_BUTTON_SELECT"); _cidCancelSketch = L("ID_CANCEL_SKETCH");
            try { _journal = app.Application.RecordingJournalFilename; } catch { }
        }, 10000);
    }

    string JournalSince()
    {
        try
        {
            if (string.IsNullOrEmpty(_journal) || !File.Exists(_journal)) return "";
            using var fs = new FileStream(_journal, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (_jpos > fs.Length || _jpos == 0) { _jpos = fs.Length; return ""; }
            fs.Seek(_jpos, SeekOrigin.Begin); var buf = new byte[fs.Length - _jpos]; int n = fs.Read(buf, 0, buf.Length); _jpos += n;
            return Encoding.Default.GetString(buf, 0, n);
        }
        catch { return ""; }
    }

    // ---- the per-tool record ----
    sealed class ToolRes
    {
        public string Id, Result = "", Tested = "", How = "", Gate = "", Bindable = "", Postable = "", Signals = "", Teardown = "", Left = "", Note = "", Journal = "";
        public readonly SortedSet<string> Opened = new(StringComparer.Ordinal);
        public long Ms;
        public bool IdleOk = true;
    }

    /// <summary>Destructive if really started (a binding + cancel is fine).</summary>
    static readonly Dictionary<string, string> RealStartDeny = new(StringComparer.Ordinal)
    {
        ["ID_LOAD_INTO_PROJECTS_CLOSE"] = "destructive if really started: closes the family",
        ["ID_LOAD_INTO_PROJECTS_CLOSE_REBAR_SHAPE"] = "destructive if really started: closes the family",
        ["ID_REVIT_FILE_CLOSE"] = "destructive if really started: closes the scratch document",
        ["ID_APP_EXIT"] = "destructive if really started: exits Revit",
    };

    Snap _sess;

    async Task<ToolRes> RunTool(RibbonItem it, string gate, StringBuilder log, bool forceReal = false)
    {
        var r = new ToolRes { Id = it.Id, Gate = gate };
        var sw = Stopwatch.StartNew();
        AddInCommandBinding bind = null; RevitCommandId cid = null;
        try
        {
            try { cid = RevitCommandId.LookupCommandId(r.Id); } catch { }
            if (cid == null) { r.Result = "not-a-command"; r.Note = IsContainerType(it) ? "container" : "unresolved id"; return r; }
            if (gate != "True") { r.Result = gate.StartsWith("ERR") ? "error" : "greyed"; r.Tested = "gate"; r.Note = gate; return r; }
            bool bindable = false, postable = false;
            await Api(app => { bindable = cid.CanHaveBinding && !cid.HasBinding; postable = app.CanPostCommand(cid); }, 5000);
            if (_sess != null) { var pre = await Teardown(_sess, () => true, false, null); if (pre != "none") r.Note += " pre-sweep (late leftovers of an earlier tool): " + pre; }
            r.Bindable = bindable.ToString(); r.Postable = postable.ToString();
            bool cancel = bindable && !forceReal;
            if (!cancel && RealStartDeny.TryGetValue(r.Id, out var dw)) { r.Result = "skipped"; r.Note = dw; return r; }
            r.Tested = cancel ? "bind+cancel" : "real";
            r.How = postable ? "PostCommand" : "routeA";

            // baseline
            lock (_docEv) _docEv.Clear();
            var b = Baseline(); lock (_dlg) _dlg.Clear();
            if (!cancel) b.Panes = await Panes();
            JournalSince(); _fired = false; _firedAs = ""; _cancellable = ""; _cancelMode = cancel; _arm = true; _fast = true;
            bool raReturned = true; string raErr = ""; Exception postEx = null;
            if (cancel)
            {
                var (ran, ex) = await Api(app =>
                {
                    bind = app.CreateAddInCommandBinding(cid);
                    bind.BeforeExecuted += (s, e) =>
                    {
                        _fired = true; _firedAs = e.CommandId?.Name ?? ""; _cancellable = e.Cancellable.ToString();
                        if (_cancelMode) { try { e.Cancel = true; } catch { } }
                    };
                    if (postable) app.PostCommand(cid);
                }, 5000);
                if (!ran || ex != null) { r.Result = "error"; r.Note = "bind/post " + (ran ? ex?.GetType().Name + ": " + S(ex?.Message) : "ExternalEvent did not run"); }
            }
            else if (postable)
            {
                var (ran, ex) = await Api(app => app.PostCommand(cid), 5000);
                if (!ran) { r.Result = "error"; r.Note = "ExternalEvent did not run"; } else postEx = ex;
            }
            int idle0 = _idle;
            if (r.Result == "" && !postable)
            {
                raReturned = false;
                var h = ComponentManager.CommandHandler;
                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
                {
                    try { h.Execute(it); } catch (Exception ex) { raErr = ex.GetType().Name + ": " + S(ex.Message); }
                    raReturned = true;
                }));
            }
            if (postEx != null) { r.Result = postEx is Autodesk.Revit.Exceptions.ArgumentException ? "refused" : "error"; r.Note = postEx.GetType().Name + ": " + S(Trunc(postEx.Message, 150)); }

            // observe
            var t0 = sw.ElapsedMilliseconds;
            if (r.Result == "")
            {
                int min = cancel ? 350 : 1200, max = cancel ? 3000 : 7000;
                while (true)
                {
                    await Task.Delay(50);
                    var el = sw.ElapsedMilliseconds - t0;
                    var nw = NewWins(b); var mode = ModeSig(); var ac = ActiveCmd(); int dl; lock (_dlg) dl = _dlg.Count; int dc; lock (_docEv) dc = _docEv.Count;
                    bool signal = nw.Count > 0 || ModeChanged(b) || !CmdIdle(ac) || dl > 0 || dc > 0;
                    if (cancel && _fired && el > 150) break;
                    if (signal && el > 400) break;
                    if (el >= min && raReturned && (_idle - idle0 >= 3 || !postable)) break;
                    if (el >= max) break;
                }
            }
            // signals
            var me = Environment.ProcessId;
            {
                var nw = NewWins(b); var kids = NewKids(b); var ac = ActiveCmd(); var mode = ModeSig();
                var parts = new List<string>();
                if (_fired) parts.Add($"BeforeExecuted(as {_firedAs}, cancellable {_cancellable})");
                lock (_dlg) if (_dlg.Count > 0) { parts.Add("dialogs[" + string.Join("; ", _dlg) + "]"); r.Opened.Add("revit-dialog"); }
                if (nw.Count > 0) parts.Add("windows[" + string.Join("; ", nw.Select(w => $"'{S(w.title)}' {w.cls}{(w.pid == me ? "" : " pid " + w.pid + " " + ProcName(w.pid))}")) + "]");
                foreach (var w in nw) r.Opened.Add(OpenedKind(w, kids));
                if (kids.Count > 0) parts.Add("childProcs[" + string.Join("; ", kids.Select(k => k.exe + ":" + k.pid)) + "]");
                if (ModeChanged(b)) { parts.Add("mode[" + mode + "]"); r.Opened.Add(mode.Contains("Sketch", StringComparison.OrdinalIgnoreCase) || mode.Contains("Edit Profile") || mode.Contains("Edit Boundary") ? "sketch" : "placement"); }
                else if (!CmdIdle(ac)) r.Opened.Add("placement");
                if (!CmdIdle(ac)) parts.Add("activeCommand[" + ac + "]");
                lock (_docEv) if (_docEv.Count > 0) { parts.Add("doc[" + string.Join("; ", _docEv) + "]"); if (_docEv.Any(x => x.StartsWith("TransactionCommitted"))) r.Opened.Add("doc-change"); }
                if (!raReturned) parts.Add("routeA blocked");
                if (raErr != "") parts.Add("routeA threw " + raErr);
                r.Signals = string.Join(" ", parts);
            }

            // teardown (+ late watch after real starts: some tools open their window seconds later)
            r.Teardown = await Teardown(b, () => raReturned, true, r, !cancel);
            if (!cancel)
            {
                var lw = Stopwatch.StartNew(); var late = new List<string>(); var waited = new HashSet<string>();
                while (true)
                {
                    await Task.Delay(500);
                    var t2 = await Teardown(b, () => raReturned, true, r, lw.ElapsedMilliseconds < 600); if (t2 != "none") late.Add(t2);
                    var wins = AllWins();
                    var pending = NewKids(b).Where(k => !BgExe(k.exe) && Alive(k.pid) && !wins.Any(w => w.pid == k.pid)).ToList();
                    // a window-less process the tool started outside Revit's tree (GenerativeDesign.exe for Dynamo Player,
                    // slow on its first start) is waited for too
                    foreach (var pid in AllPids().Where(p => !b.Pids.Contains(p) && !pending.Any(k => k.pid == p)))
                    {
                        var name = ProcName(pid);
                        if ((name.StartsWith("GenerativeDesign", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Dynamo", StringComparison.OrdinalIgnoreCase)) && !wins.Any(w => w.pid == pid))
                            pending.Add((pid, 0, name + ".exe"));
                    }
                    foreach (var k in pending) waited.Add(k.exe);
                    if (lw.ElapsedMilliseconds >= 1500 && pending.Count == 0) break;
                    if (lw.ElapsedMilliseconds >= 25000) { r.Left += " windowless child process still running: " + string.Join(",", pending.Select(k => k.exe + ":" + k.pid)); break; }
                }
                // a WebView2 child whose window is gone should exit on its own; one that hangs is killed (spec #34 "Teardown")
                for (int i = 0; i < 20 && NewKids(b).Any(k => k.exe.Equals("msedgewebview2.exe", StringComparison.OrdinalIgnoreCase) && Alive(k.pid)) && NewWins(b).Count == 0; i++) await Task.Delay(300);
                // only after a WebView2 *window* was closed; a dockable pane (Forma, Issues) keeps its WebView2 alive, and
                // that process belongs to Revit: never killed
                var hung = NewKids(b).Where(k => k.exe.Equals("msedgewebview2.exe", StringComparison.OrdinalIgnoreCase) && Alive(k.pid)).ToList();
                if (hung.Count > 0 && NewWins(b).Count == 0 && r.Opened.Contains("webview2"))
                {
                    foreach (var k in hung) { try { Process.GetProcessById(k.pid).Kill(); } catch { } }
                    late.Add($"killed {hung.Count} hung msedgewebview2 child(ren) after its window closed");
                }
                else if (hung.Count > 0) late.Add($"{hung.Count} WebView2 process(es) kept by Revit (a pane or embedded view, no window)");
                if (late.Count > 0) r.Teardown += " | late: " + string.Join("; ", late);
                if (waited.Count > 0) r.Note += " late watch waited for " + string.Join(",", waited);
                var pn = await Panes(); var changed = b.Panes == null ? new List<string>() : pn.Where(kv => b.Panes.TryGetValue(kv.Key, out var was) && was != kv.Value).Select(kv => kv.Key).ToList();
                if (changed.Count > 0)
                {
                    r.Signals += " panes[" + string.Join(",", changed.Select(c => c + "=" + (pn[c] ? "shown" : "hidden"))) + "]";
                    r.Opened.Add("pane");
                    await Api(app => { foreach (var c in changed) { var dp = app.GetDockablePane(_paneIds[c]); if (b.Panes[c]) dp.Show(); else dp.Hide(); } }, 5000);
                    var pn2 = await Panes(); r.Teardown += " | pane restore: " + string.Join(",", changed.Select(c => c + (pn2[c] == b.Panes[c] ? " ok" : " FAILED")));
                }
            }
            if (bind != null) { var (ran, ex) = await Api(app => app.RemoveAddInCommandBinding(cid), 8000); if (!ran || ex != null) r.Left += $" unbind:{(ran ? ex?.GetType().Name : "event did not run")}"; bind = null; }
            _arm = false; _fast = false;
            var j = JournalSince();
            var jl = j.Split('\n').Where(l => (l.Contains(r.Id) || l.Contains("cannot be invoked") || l.Contains("Jrn.Command") || l.Contains("external command")) && !l.Contains("registering command")).Select(l => S(l.Trim())).Take(4).ToList();
            r.Journal = string.Join(" || ", jl);
            bool jDispatched = j.Contains(r.Id) && (j.Contains("Jrn.Command") || j.Contains("Jrn.RibbonEvent")) && !j.Contains("cannot be invoked");
            bool jRefusedCtx = j.Contains("cannot be invoked in this context");

            // idle proof
            var idle = await IdleProof(b, () => raReturned, r.Opened.Contains("webview2"));
            if (idle != "") { r.Left = (r.Left + " " + idle).Trim(); }
            r.IdleOk = r.Left.Trim() == "";

            // classify
            if (r.Result == "")
            {
                bool otherSignal = r.Signals.Replace($"BeforeExecuted(as {_firedAs}, cancellable {_cancellable})", "").Trim() != "";
                if (cancel) r.Result = _fired || otherSignal ? "executes" : "accepted-nothing";
                else r.Result = (otherSignal || jDispatched) && !(jRefusedCtx && !otherSignal) ? "executes" : "accepted-nothing";
                if (cancel && !_fired && otherSignal) r.Note += " binding bypassed: signals without BeforeExecuted";
                if (cancel && _fired && _cancellable == "False") r.Note += " not cancellable: really started";
                if (_fired && _firedAs != "" && _firedAs != r.Id) r.Note += $" event reported alias {_firedAs}";
                if (r.Result == "accepted-nothing") r.Note += jRefusedCtx ? " journal: cannot be invoked in this context" : " accepted, nothing started";
            }
        }
        catch (Exception ex) { r.Result = "error"; r.Note += " EXC " + ex.GetType().Name + ": " + S(ex.Message); _arm = false; _fast = false; }
        finally
        {
            if (bind != null) { try { await Api(app => app.RemoveAddInCommandBinding(cid), 8000); } catch { } }
            _arm = false; _fast = false;
            r.Ms = sw.ElapsedMilliseconds;
            log.AppendLine($"{DateTime.Now:HH:mm:ss.fff} {r.Id} -> {r.Result} [{r.Tested} {r.How}] gate={r.Gate} opened={string.Join(",", r.Opened)} sig={r.Signals} teardown={r.Teardown} left={r.Left} note={r.Note} journal={r.Journal} ({r.Ms} ms)");
        }
        return r;
    }

    string OpenedKind((IntPtr h, string title, string cls, int pid) w, List<(int pid, int ppid, string exe)> kids)
    {
        if (w.pid != Environment.ProcessId)
        {
            var exe = ProcName(w.pid);
            return Browsers.Any(bw => exe.StartsWith(bw, StringComparison.OrdinalIgnoreCase)) ? "browser" : "external-process:" + exe.ToLowerInvariant() + ".exe";
        }
        if (w.cls == "#32770") return "win32";
        bool wpf = false;
        foreach (System.Windows.PresentationSource ps in System.Windows.PresentationSource.CurrentSources)
            if (ps is System.Windows.Interop.HwndSource hs && hs.Handle == w.h) wpf = true;
        if (wpf || w.cls.StartsWith("HwndWrapper", StringComparison.Ordinal))
            return kids.Any(k => k.exe.Equals("msedgewebview2.exe", StringComparison.OrdinalIgnoreCase)) ? "webview2" : "wpf";
        return "win32";
    }

    async Task<string> Teardown(Snap b, Func<bool> raReturned, bool undo, ToolRes r, bool deep = false)
    {
        var acts = new List<string>();
        for (int round = 0; round < 8; round++)
        {
            var nw = NewWins(b); var mode = ModeSig(); var ac = ActiveCmd();
            bool modeOn = ModeChanged(b) || !CmdIdle(ac);
            // an API pick prompt (Selection.PickObject) shows no tab and no active command: only an ExternalEvent that will not run tells
            if (deep && nw.Count == 0 && !modeOn && raReturned()) { var (ran, _) = await Api(app => { }, 1500); if (!ran) { modeOn = true; acts.Add("ExternalEvent blocked (pick prompt?)"); r?.Opened.Add("pick"); } }
            if (nw.Count == 0 && !modeOn && raReturned()) break;
            if (nw.Count > 0)
            {
                var kids = NewKids(b);
                foreach (var w in nw)
                {
                    r?.Opened.Add(OpenedKind(w, kids));
                    System.Windows.Window wpf = null;
                    foreach (System.Windows.PresentationSource ps in System.Windows.PresentationSource.CurrentSources)
                        if (ps is System.Windows.Interop.HwndSource hs && hs.Handle == w.h && hs.RootVisual is System.Windows.Window ww) wpf = ww;
                    if (round < 2)
                    {
                        if (wpf != null) { try { wpf.Close(); acts.Add($"Window.Close '{S(w.title)}'"); } catch (Exception ex) { acts.Add("Window.Close threw " + ex.GetType().Name); } }
                        else { PostMessage(w.h, 0x0010, IntPtr.Zero, IntPtr.Zero); acts.Add($"WM_CLOSE '{S(w.title)}' {w.cls}{(w.pid == Environment.ProcessId ? "" : " (" + ProcName(w.pid) + ")")}"); }
                    }
                    else if (round < 4) { PostMessage(w.h, 0x0111, (IntPtr)2, IntPtr.Zero); PostMessage(w.h, 0x0100, (IntPtr)0x1B, IntPtr.Zero); PostMessage(w.h, 0x0101, (IntPtr)0x1B, IntPtr.Zero); acts.Add($"IDCANCEL+Esc '{S(w.title)}'"); }
                    else
                    {
                        foreach (var k in kids.Where(k => k.exe.Equals("msedgewebview2.exe", StringComparison.OrdinalIgnoreCase))) { try { Process.GetProcessById(k.pid).Kill(); acts.Add($"kill msedgewebview2 {k.pid}"); } catch { } }
                        PostMessage(w.h, 0x0010, IntPtr.Zero, IntPtr.Zero); acts.Add($"WM_CLOSE again '{S(w.title)}'");
                    }
                }
                await Task.Delay(600);
                continue;
            }
            if (modeOn)
            {
                var sketchCancel = Walk().Where(x => x.Tab.IsVisible && x.Panel.IsVisible && x.Tab.IsContextualTab).Select(x => x.Item).FirstOrDefault(i => i.IsVisible && (i.Id == "ID_CANCEL_SKETCH" || i.Id == "ID_QUIT_INPLACE_FAMILY" || i.Id == "ID_QUIT_INPLACE_MASS"));
                if (sketchCancel != null) r?.Opened.Add("sketch");
                if (round < 3 && sketchCancel == null) { WmCmd(_cidCancelEditor); await Task.Delay(150); WmCmd(_cidCancelEditor); acts.Add("ID_CANCEL_EDITOR x2 (WM_COMMAND)"); }
                else if (round < 5)
                {
                    var cancelItem = Walk().Where(x => x.Tab.IsVisible && x.Panel.IsVisible).Select(x => x.Item).FirstOrDefault(i => i.IsVisible && (i.Id == "ID_CANCEL_SKETCH" || i.Id == "ID_QUIT_INPLACE_FAMILY" || i.Id == "ID_QUIT_INPLACE_MASS" || i.Id == "ID_CANCEL_SKETCH_MODE" || (i.Id ?? "").StartsWith("ID_CANCEL")) && ComponentManager.CommandHandler.CanExecute(i));
                    if (cancelItem != null) { var h = ComponentManager.CommandHandler; System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => { try { h.Execute(cancelItem); } catch { } })); acts.Add("routeA " + cancelItem.Id); }
                    else { WmCmd(_cidCancelSketch); acts.Add("ID_CANCEL_SKETCH (WM_COMMAND)"); }
                }
                else { WmCmd(_cidSelect); acts.Add("ID_BUTTON_SELECT (WM_COMMAND)"); }
                await Task.Delay(700);
                continue;
            }
            await Task.Delay(300); // route A still blocked but nothing visible: give it time
        }
        // a baseline contextual tab that went away (the view lost the focus to a pane or window): give the view its focus back
        if (b.CtxTabs.Count > 0 && b.CtxTabs.Any(t => !CtxTabs().Contains(t)))
        {
            Autodesk.Revit.DB.ElementId cur = null;
            await Api(app =>
            {
                var ui = app.ActiveUIDocument; cur = ui.ActiveView.Id;
                var other = new Autodesk.Revit.DB.FilteredElementCollector(ui.Document).OfClass(typeof(Autodesk.Revit.DB.View)).Cast<Autodesk.Revit.DB.View>()
                    .FirstOrDefault(v => !v.IsTemplate && v.Id != cur && v.ViewType == Autodesk.Revit.DB.ViewType.FloorPlan);
                if (other != null) ui.ActiveView = other;
            }, 5000);
            await Task.Delay(800);
            await Api(app => { var ui = app.ActiveUIDocument; if (cur != null) ui.ActiveView = (Autodesk.Revit.DB.View)ui.Document.GetElement(cur); }, 5000);
            await Task.Delay(1200);
            acts.Add(b.CtxTabs.All(t => CtxTabs().Contains(t)) ? "view focus restored (its contextual tab was gone)" : "view focus NOT restored");
        }
        // undo transactions committed during the tool that a later rollback or undo did not already remove
        int commits = 0;
        if (undo) lock (_docEv)
        {
            var evs = _docEv.Skip(b.DocEv).ToList();
            int last = Math.Max(evs.FindLastIndex(x => x.StartsWith("TransactionGroupRolledBack") || x.StartsWith("TransactionUndone") || x.StartsWith("TransactionRolledBack")), -1);
            commits = evs.Skip(last + 1).Count(x => x.StartsWith("TransactionCommitted") && !x.Contains(":census"));
        }
        if (commits > 0)
        {
            r?.Opened.Add("doc-change");
            int before; lock (_docEv) before = _docEv.Count(x => x.StartsWith("TransactionUndone"));
            for (int i = 0; i < commits; i++) { WmCmd(_cidUndo); await Task.Delay(500); }
            int after; lock (_docEv) after = _docEv.Count(x => x.StartsWith("TransactionUndone"));
            acts.Add($"undo x{commits} (WM_COMMAND ID_EDIT_UNDO) -> undone {after - before}");
            if (after - before < commits) acts.Add($"LEFT {commits - (after - before)} committed transaction(s)");
        }
        lock (_dlg) if (_dlg.Any(d => d.Contains("OverrideResult"))) acts.Insert(0, "DialogBoxShowing override");
        return acts.Count == 0 ? "none" : string.Join("; ", acts);
    }

    async Task<string> IdleProof(Snap b, Func<bool> raReturned, bool webview2Window = false)
    {
        var left = new List<string>();
        for (int i = 0; i < 20 && !raReturned(); i++) await Task.Delay(100);
        if (!raReturned()) left.Add("routeA call still blocked");
        var nw = NewWins(b); if (nw.Count > 0) left.Add("windows[" + string.Join("; ", nw.Select(w => $"'{S(w.title)}' {w.cls}")) + "]");
        var mode = ModeSig(); if (ModeChanged(b)) left.Add("mode[" + mode + "]");
        var ac = ActiveCmd(); if (!CmdIdle(ac)) left.Add("activeCommand[" + ac + "]");
        var (ran, _) = await Api(app => { }, 4000); if (!ran) left.Add("ExternalEvent did not run within 4 s (not idle)");
        // after a WebView2 window was closed, its process must be gone too (a pane's WebView2 is Revit's to keep)
        if (webview2Window)
        {
            for (int i = 0; i < 10 && NewKids(b).Any(k => k.exe.Equals("msedgewebview2.exe", StringComparison.OrdinalIgnoreCase)); i++) await Task.Delay(300);
            var kids = NewKids(b).Where(k => k.exe.Equals("msedgewebview2.exe", StringComparison.OrdinalIgnoreCase)).ToList();
            if (kids.Count > 0) left.Add("webview2 procs still running: " + kids.Count);
        }
        return string.Join(" ", left);
    }

    // ---- dockable panes (built-in ids): shown state ----
    Dictionary<string, DockablePaneId> _paneIds;
    async Task<Dictionary<string, bool>> Panes()
    {
        var o = new Dictionary<string, bool>();
        await Api(app =>
        {
            if (_paneIds == null)
            {
                _paneIds = new Dictionary<string, DockablePaneId>();
                foreach (var pr in typeof(DockablePanes.BuiltInDockablePanes).GetProperties(BindingFlags.Public | BindingFlags.Static).Where(p => p.PropertyType == typeof(DockablePaneId)))
                    try { _paneIds[pr.Name] = (DockablePaneId)pr.GetValue(null); } catch { }
                foreach (var fi in typeof(DockablePanes.BuiltInDockablePanes).GetFields(BindingFlags.Public | BindingFlags.Static).Where(p => p.FieldType == typeof(DockablePaneId)))
                    try { _paneIds[fi.Name] = (DockablePaneId)fi.GetValue(null); } catch { }
            }
            foreach (var kv in _paneIds) { try { if (DockablePane.PaneExists(kv.Value)) o[kv.Key] = app.GetDockablePane(kv.Value).IsShown(); } catch { } }
        }, 5000);
        return o;
    }
}
