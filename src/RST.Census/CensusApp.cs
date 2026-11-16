// rst-c's own (not vendored): the Ribbon Tool Census add-in (feature #22, spec #34). Test-seat tooling, never
// shipped (RST.Census.csproj). Ported from the WU1b–d probe (shared/evidence/search-it-family/wu1d/probe-addin,
// untracked): the ribbon walk, the CanExecute gate, bind + cancel, route A, the teardown and the idle proof.
//
// Driven by tools/census/run.sh through a file trigger: the host writes C:\RstC\census\go (one verb per line,
// fields separated by '|'); a 2 s DispatcherTimer on Revit's UI thread picks it up, deletes it and runs the lines.
// Verbs that need an API context run inside an ExternalEvent; the run itself lives on the UI thread, outside any
// API context (route A needs that), and awaits ExternalEvent round trips when it needs the API.
//
//   state|<tag>                   document, view, context key, build, element count, IsModified -> <year>\state\<tag>.txt
//   survey|<tag>                  every stock family template: category, placement type, views -> <year>\templates.tsv
//   newdoc|<tag>|project|<rte>    New Project dialog, driven in-process (Win32) to <rte>; never saved
//   newdoc|<tag>|family|<rft>     New Family dialog, driven in-process (Win32) to <rft>; never saved
//   view|<tag>|<ViewType>         activate (or create in the scratch document) a view of that type
//   run|<context>                 census of the active document and view; resumable (rows.tsv); progress.txt per tool
//   stop                          stop the run after the current tool
//   closedoc|<tag>                close the active document without saving (WM_COMMAND ID_REVIT_FILE_CLOSE + "No")
//
// Output under C:\RstC\census\<year>\: rows.tsv (the full table's columns, spec #34), progress.txt, detail.log,
// summary-<n>.txt, actions.log, dialogs.log, error.txt, current.txt (the tool being run, for the host watchdog).

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace RST.Census;

public sealed partial class CensusApp : IExternalApplication, IExternalEventHandler
{
    public const string Root = @"C:\RstC\census";

    System.Windows.Threading.DispatcherTimer _timer;
    ExternalEvent _ev;
    UIApplication _ui;
    UIControlledApplication _uic;
    string _year = "?";
    string _build = "?";
    string _censusBuild = "?";
    string YearDir => Path.Combine(Root, _year);

    public Result OnStartup(UIControlledApplication a)
    {
        _uic = a;
        _year = a.ControlledApplication.VersionNumber;
        _build = $"{a.ControlledApplication.SubVersionNumber} ({a.ControlledApplication.VersionBuild})";
        _censusBuild = CensusBuild();
        Directory.CreateDirectory(YearDir);
        _ev = ExternalEvent.Create(this);
        a.DialogBoxShowing += OnDialog;
        a.ControlledApplication.DocumentChanged += OnDocChanged;
        a.Idling += (s, e) => { _idle++; if (_fast) { try { e.SetRaiseWithoutDelay(); } catch { } } };
        try { _ui = (UIApplication)typeof(UIControlledApplication).GetField("m_uiapplication", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(a); } catch { }
        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (s, e) => { try { Tick(); } catch (Exception ex) { Err("tick", ex); } };
        _timer.Start();
        Act($"RST.Census {_censusBuild} started in Revit {_year} build {_build}");
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication a) { _timer?.Stop(); return Result.Succeeded; }
    public string GetName() => "RST.Census";

    static string CensusBuild()
    {
        var asm = typeof(CensusApp).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        try
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(asm.Location))).Substring(0, 12).ToLowerInvariant();
            return string.IsNullOrEmpty(info) ? hash : info + "/" + hash;
        }
        catch { return info; }
    }

    // ---------------- logging ----------------
    readonly object _logLock = new();
    void Append(string name, string text) { lock (_logLock) { try { File.AppendAllText(Path.Combine(YearDir, name), text, new UTF8Encoding(false)); } catch { } } }
    void Act(string s) => Append("actions.log", $"{DateTime.Now:HH:mm:ss.fff} {s}\r\n");
    void Err(string what, Exception ex) => Append("error.txt", $"{DateTime.Now:HH:mm:ss.fff} {what}: {ex}\r\n");
    static string S(string v) => (v ?? "").Replace("\t", " ").Replace("\r", " ").Replace("\n", " ").Trim();
    static string Trunc(string s, int n) => s == null ? "" : s.Length <= n ? s : s.Substring(0, n) + "…";

    // ---------------- trigger ----------------
    readonly ConcurrentQueue<string> _q = new();

    void Tick()
    {
        var go = Path.Combine(Root, "go");
        if (!File.Exists(go)) return;
        string[] lines;
        try { lines = File.ReadAllLines(go); File.Delete(go); } catch { return; }
        bool raise = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line == "" || line.StartsWith("#")) continue;
            var f = line.Split('|');
            try
            {
                switch (f[0])
                {
                    // UI thread, outside any API context
                    case "run": if (_running) throw new InvalidOperationException("a run is active"); _ = RunContext(f); Act($"OK {line}"); continue;
                    case "stop": _stop = true; Act($"OK {line}"); continue;
                    case "newdoc": _ = NewDoc(f); Act($"OK {line}"); continue;
                    case "closedoc": _ = CloseDoc(f); Act($"OK {line}"); continue;
                    case "view": _ = SetView(f); Act($"OK {line}"); continue;
                    case "drawn": _ = DrawnVerb(f); Act($"OK {line}"); continue;
                }
            }
            catch (Exception ex) { Act($"FAIL {line}: {ex.GetType().Name}: {S(ex.Message)}"); Err(line, ex); continue; }
            _q.Enqueue(line); raise = true; Act($"queued {line}");
        }
        if (raise) _ev.Raise();
    }

    public void Execute(UIApplication app)
    {
        _ui = app;
        DrainApi(app);
        while (_q.TryDequeue(out var line))
        {
            try { RunApiVerb(app, line); Act($"OK {line}"); }
            catch (Exception ex) { Act($"FAIL {line}: {ex.GetType().Name}: {S(ex.Message)}"); Err(line, ex); }
        }
    }

    void RunApiVerb(UIApplication app, string line)
    {
        var f = line.Split('|');
        var tag = f.Length > 1 ? f[1] : DateTime.Now.ToString("HHmmss");
        switch (f[0])
        {
            case "state": WriteFile(Path.Combine("state", tag + ".txt"), State(app)); break;
            case "survey": WriteFile("templates.tsv", Survey(app, tag)); break;
            default: throw new ArgumentException("unknown verb " + f[0]);
        }
    }

    void WriteFile(string rel, string text)
    {
        var p = Path.Combine(YearDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p));
        File.WriteAllText(p, text, new UTF8Encoding(false));
    }

    // ---------------- ExternalEvent round trips ----------------
    sealed class Box { public volatile bool V; }
    readonly ConcurrentQueue<(Action<UIApplication> a, TaskCompletionSource<bool> t, Box c)> _apiQ = new();

    void DrainApi(UIApplication app)
    {
        while (_apiQ.TryDequeue(out var x))
        {
            if (x.c.V) { x.t.TrySetCanceled(); continue; }
            try { x.a(app); x.t.TrySetResult(true); } catch (Exception ex) { x.t.TrySetException(ex); }
        }
    }

    /// <summary>Runs an action in an ExternalEvent; ran=false if it did not run within ms (then it never runs).</summary>
    async Task<(bool ran, Exception ex)> Api(Action<UIApplication> a, int ms)
    {
        var t = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var c = new Box();
        _apiQ.Enqueue((a, t, c)); _ev.Raise();
        var done = await Task.WhenAny(t.Task, Task.Delay(ms));
        if (done != t.Task) { c.V = true; return (false, null); }
        return t.Task.IsFaulted ? (true, t.Task.Exception?.InnerException) : (true, null);
    }

    volatile int _idle; volatile bool _fast;

    // ---------------- dialogs ----------------
    volatile bool _arm; readonly List<string> _dlg = new();

    void OnDialog(object s, DialogBoxShowingEventArgs e)
    {
        var id = e.DialogId ?? "";
        var msg = e is TaskDialogShowingEventArgs td ? td.Message : e is MessageBoxShowingEventArgs mb ? mb.Message : "";
        var note = "";
        lock (_dlg) _dlg.Add(id + (msg != "" ? " msg=" + S(Trunc(msg, 120)) : ""));
        if (_arm)
        {
            int[] order = id.Contains("Serious_Error") ? new[] { 7, 2, 8 } : new[] { 2, 8, 7, 1 };
            note = " [armed: override refused]";
            foreach (var r in order) { try { if (e.OverrideResult(r)) { note = $" [armed: OverrideResult({r})]"; lock (_dlg) _dlg.Add($"  -> OverrideResult({r}) accepted"); break; } } catch { } }
        }
        else if (_closeNo)
        {
            // closing a scratch document: "Save changes?" -> No (IDNO = 7); never save
            foreach (var r in new[] { 7, 1002, 2 }) { try { if (e.OverrideResult(r)) { note = $" [close: OverrideResult({r})]"; break; } } catch { } }
        }
        else if (_acceptNext != null && id.Contains(_acceptNext, StringComparison.OrdinalIgnoreCase))
        {
            try { if (e.OverrideResult(1)) note = " [accepted: OverrideResult(IDOK)]"; } catch { }
        }
        else if (msg.Contains("not saved", StringComparison.OrdinalIgnoreCase) || id.Contains("Reminder", StringComparison.OrdinalIgnoreCase))
        {
            // the "You have not saved your project recently" reminder: never save
            foreach (var r in new[] { 2, 7, 8 }) { try { if (e.OverrideResult(r)) { note = $" [nuisance: OverrideResult({r})]"; break; } } catch { } }
        }
        Append("dialogs.log", $"{DateTime.Now:HH:mm:ss.fff} {e.GetType().Name} id='{id}' msg='{S(Trunc(msg, 200))}'{note}\r\n");
    }
    volatile string _acceptNext;

    // ---------------- document changes ----------------
    readonly List<string> _docEv = new();

    void OnDocChanged(object s, DocumentChangedEventArgs e)
    {
        try
        {
            var names = string.Join("+", e.GetTransactionNames());
            lock (_docEv) _docEv.Add($"{e.Operation}:{names}");
        }
        catch { }
    }

    // ---------------- Win32 ----------------
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, string l);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern int GetDlgCtrlID(IntPtr h);

    static string Text(IntPtr h) { var t = new StringBuilder(512); GetWindowText(h, t, 512); return t.ToString(); }
    static string Cls(IntPtr h) { var c = new StringBuilder(256); GetClassName(h, c, 256); return c.ToString(); }

    /// <summary>Visible top-level windows of every process on this desktop.</summary>
    static List<(IntPtr h, string title, string cls, int pid)> AllWins()
    {
        var o = new List<(IntPtr, string, string, int)>();
        EnumWindows((h, l) =>
        {
            if (IsWindowVisible(h)) { GetWindowThreadProcessId(h, out var pid); o.Add((h, Text(h), Cls(h), (int)pid)); }
            return true;
        }, IntPtr.Zero);
        return o;
    }

    static List<IntPtr> Children(IntPtr parent)
    {
        var o = new List<IntPtr>();
        EnumChildWindows(parent, (h, l) => { o.Add(h); return true; }, IntPtr.Zero);
        return o;
    }

    void WmCmd(int id) { if (id != 0 && _ui != null) PostMessage(_ui.MainWindowHandle, 0x0111, (IntPtr)id, IntPtr.Zero); }
}
