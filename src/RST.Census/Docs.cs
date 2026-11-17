// rst-c's own (not vendored): scratch documents and views for the census (spec #34, "Contexts").
//
// Every context runs in a new, unsaved scratch document: a project from default.rte, or a family from a stock
// template. The New Project / New Family dialogs are driven in-process through Win32 (a background thread sets the
// template path in the file dialog and presses its buttons), so no screen coordinates are involved. Nothing is
// ever saved: a document is closed with "No" to the save prompt.

using System.IO;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RST.Core.Census;

namespace RST.Census;

public sealed partial class CensusApp
{
    const uint WM_SETTEXT = 0x000C, WM_COMMAND = 0x0111, BM_CLICK = 0x00F5, BM_GETCHECK = 0x00F0, WM_CLOSE = 0x0010;

    // ---------------- context key and state ----------------
    static string ContextKey(UIApplication app)
    {
        var ui = app.ActiveUIDocument; var d = ui?.Document; var v = ui?.ActiveView;
        if (d == null || v == null) return "";
        if (!d.IsFamilyDocument) return CensusContext.Project(v.ViewType.ToString());
        var fam = d.OwnerFamily;
        string cat = "Invalid", place = "Invalid";
        try { var c = fam?.FamilyCategory; if (c != null) cat = ((BuiltInCategory)c.Id.Value).ToString(); } catch { }
        try { place = fam?.FamilyPlacementType.ToString() ?? "Invalid"; } catch { }
        return CensusContext.Family(cat, place, v.ViewType.ToString());
    }

    static int ElementCount(Document d)
    {
        try { return new FilteredElementCollector(d).WhereElementIsNotElementType().GetElementCount() + new FilteredElementCollector(d).WhereElementIsElementType().GetElementCount(); }
        catch { return -1; }
    }

    string State(UIApplication app)
    {
        var sb = new StringBuilder();
        var ui = app.ActiveUIDocument; var d = ui?.Document;
        sb.AppendLine($"time\t{DateTime.Now:o}");
        sb.AppendLine($"revit\t{app.Application.VersionNumber}\t{app.Application.VersionName}\tbuild {app.Application.VersionBuild}\tsubversion {app.Application.SubVersionNumber}\t{app.Application.Language}");
        sb.AppendLine($"census_build\t{_censusBuild}");
        sb.AppendLine($"active={(d == null ? "(none)" : d.Title)}\tfamily={d?.IsFamilyDocument}\tview={ui?.ActiveView?.Name}\tviewType={ui?.ActiveView?.ViewType}\tcontext={ContextKey(app)}\tIsModified={d?.IsModified}\telements={(d == null ? -1 : ElementCount(d))}\tpath='{d?.PathName}'");
        foreach (Document od in app.Application.Documents) sb.AppendLine($"open\t'{od.Title}'\tfamily={od.IsFamilyDocument}\tlinked={od.IsLinked}\tmodified={od.IsModified}\tpath='{od.PathName}'");
        return sb.ToString();
    }

    // ---------------- family templates ----------------
    IEnumerable<string> TemplateRoots()
    {
        var baseDir = $@"C:\ProgramData\Autodesk\RVT {_year}\Family Templates";
        foreach (var lang in new[] { "English-Imperial", "English" })
        {
            var p = Path.Combine(baseDir, lang);
            if (Directory.Exists(p)) yield return p;
        }
    }

    string Survey(UIApplication app, string tag)
    {
        var sb = new StringBuilder("template\tcategory\tplacement\tview_types\tviews\tms\terror\r\n");
        foreach (var root in TemplateRoots())
            foreach (var rft in Directory.EnumerateFiles(root, "*.rft", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string cat = "", place = "", types = "", views = "", err = "";
                Document d = null;
                try
                {
                    d = app.Application.NewFamilyDocument(rft);
                    var fam = d.OwnerFamily;
                    try { var c = fam?.FamilyCategory; cat = c == null ? "Invalid" : ((BuiltInCategory)c.Id.Value).ToString(); } catch (Exception ex) { cat = "ERR " + ex.GetType().Name; }
                    try { place = fam?.FamilyPlacementType.ToString(); } catch (Exception ex) { place = "ERR " + ex.GetType().Name; }
                    var vs = new FilteredElementCollector(d).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate && v.CanBePrinted).ToList();
                    types = string.Join(",", vs.Select(v => v.ViewType.ToString()).Distinct().OrderBy(x => x));
                    views = string.Join(";", vs.Select(v => v.ViewType + ":" + S(v.Name)));
                }
                catch (Exception ex) { err = ex.GetType().Name + ": " + S(ex.Message); }
                finally { try { d?.Close(false); } catch (Exception ex) { err += " close: " + ex.GetType().Name; } }
                sb.Append(rft).Append('\t').Append(cat).Append('\t').Append(place).Append('\t').Append(types).Append('\t').Append(views).Append('\t').Append(sw.ElapsedMilliseconds).Append('\t').Append(err).Append("\r\n");
            }
        return sb.ToString();
    }

    // ---------------- new scratch document (dialogs driven through Win32) ----------------
    async Task NewDoc(string[] f)
    {
        var tag = f[1]; var kind = f[2]; var template = f[3];
        var log = new StringBuilder($"{DateTime.Now:HH:mm:ss.fff} newdoc {kind} {template}\r\n");
        void L(string s) { lock (log) log.Append($"{DateTime.Now:HH:mm:ss.fff} {s}\r\n"); }
        string before = "";
        try
        {
            if (!File.Exists(template)) throw new FileNotFoundException(template);
            await Api(app => before = DocSig(app), 5000);
            var me = Environment.ProcessId;
            var oldWins = new HashSet<IntPtr>(AllWins().Select(w => w.h));
            var cmd = kind == "project" ? "ID_FILE_NEW_CHOOSE_TEMPLATE" : "ID_FAMILY_NEW";
            var (ran, ex) = await Api(app => app.PostCommand(RevitCommandId.LookupCommandId(cmd)), 5000);
            L($"post {cmd}: ran={ran} ex={ex?.GetType().Name}: {ex?.Message}");
            var driver = Task.Run(() => DriveNewDocDialogs(kind, template, oldWins, me, L));
            var ok = await driver;
            L("dialog driver: " + ok);
            string after = "";
            for (int i = 0; i < 60; i++)
            {
                await Task.Delay(1000);
                var (r2, _) = await Api(app => after = DocSig(app), 3000);
                if (r2 && after != before && after != "") break;
            }
            L($"active before='{before}' after='{after}'");
        }
        catch (Exception ex) { L("EXC " + ex); }
        Append("newdoc.log", log.ToString());
        Act($"newdoc done {tag}");
    }

    static string DocSig(UIApplication app)
    {
        var d = app.ActiveUIDocument?.Document;
        return d == null ? "" : $"{d.Title}|{d.IsFamilyDocument}|{app.Application.Documents.Size}";
    }

    static bool WaitFor(Func<bool> cond, int ms) { var sw = System.Diagnostics.Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; Thread.Sleep(200); } return cond(); }

    static (IntPtr h, string title) NewDialog(HashSet<IntPtr> old, int pid, Func<string, bool> titleOk)
    {
        foreach (var w in AllWins())
            if (w.pid == pid && w.cls == "#32770" && !old.Contains(w.h) && titleOk(w.title)) return (w.h, w.title);
        return (IntPtr.Zero, "");
    }

    static string Dump(IntPtr dlg) => string.Join(" | ", Children(dlg).Select(c => $"{Cls(c)}#{GetDlgCtrlID(c)}'{Text(c)}'"));

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, StringBuilder l);
    const uint WM_GETTEXT = 0x000D;
    static string GetText(IntPtr h) { var sb = new StringBuilder(1024); SendMessage(h, WM_GETTEXT, (IntPtr)1024, sb); return sb.ToString(); }

    /// <summary>
    /// Sets the file name box of a common file dialog and presses Open (control id 1). The dialog fills the box with
    /// its own default while it initialises, so wait, set, read back, and only then press Open.
    /// </summary>
    static bool FillFileDialog(IntPtr dlg, string path, Action<string> L)
    {
        Thread.Sleep(1500);
        for (int attempt = 0; attempt < 4; attempt++)
        {
            var kids = Children(dlg);
            // the file name box: the Edit inside Autodesk's file dialog's name combo (id 13006), or inside the common
            // dialog's ComboBoxEx32 (cmb13, id 0x47C); the Edit's own id is 1001
            var combo = kids.FirstOrDefault(c => Cls(c) == "ComboBox" && GetDlgCtrlID(c) == 13006);
            if (combo == IntPtr.Zero) combo = kids.FirstOrDefault(c => Cls(c) == "ComboBoxEx32" && GetDlgCtrlID(c) == 0x47C);
            var edit = combo != IntPtr.Zero ? Children(combo).FirstOrDefault(c => Cls(c) == "Edit") : IntPtr.Zero;
            if (edit == IntPtr.Zero) edit = kids.FirstOrDefault(c => Cls(c) == "Edit" && GetDlgCtrlID(c) == 0x480);
            if (edit == IntPtr.Zero) { L("file dialog: no file name box; children: " + Dump(dlg)); return false; }
            // typed, not set: Autodesk's dialog only takes the name from user edits (CBN_EDITCHANGE), so clear the box
            // and send one WM_CHAR per character, as the keyboard would
            SendMessage(edit, WM_SETTEXT, IntPtr.Zero, "");
            SendMessage(edit, 0x00B1 /* EM_SETSEL */, IntPtr.Zero, (IntPtr)(-1));
            foreach (var ch in path) SendMessage(edit, 0x0102 /* WM_CHAR */, (IntPtr)ch, (IntPtr)1);
            Thread.Sleep(400);
            var back = GetText(edit);
            L($"file dialog: set file name (attempt {attempt + 1}), reads back '{back}'");
            if (!string.Equals(back, path, StringComparison.OrdinalIgnoreCase)) { Thread.Sleep(800); continue; }
            var open = kids.FirstOrDefault(c => Cls(c) == "Button" && GetDlgCtrlID(c) == 1);
            if (open != IntPtr.Zero) PostMessage(open, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
            else PostMessage(dlg, WM_COMMAND, (IntPtr)1, IntPtr.Zero);
            L("file dialog: Open pressed");
            return true;
        }
        return false;
    }

    bool DriveNewDocDialogs(string kind, string template, HashSet<IntPtr> old, int pid, Action<string> L)
    {
        var ok = DriveNewDocDialogsOnce(kind, template, old, pid, L);
        if (!ok)
        {
            // leave nothing open: cancel every new dialog of ours, newest first
            for (int i = 0; i < 3; i++)
                foreach (var w in AllWins().Where(w => w.pid == pid && w.cls == "#32770" && !old.Contains(w.h)))
                {
                    var cancel = Children(w.h).FirstOrDefault(c => Cls(c) == "Button" && GetDlgCtrlID(c) == 2);
                    if (cancel != IntPtr.Zero) PostMessage(cancel, BM_CLICK, IntPtr.Zero, IntPtr.Zero); else PostMessage(w.h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    L($"cancelled '{w.title}'");
                    Thread.Sleep(700);
                }
        }
        return ok;
    }

    bool DriveNewDocDialogsOnce(string kind, string template, HashSet<IntPtr> old, int pid, Action<string> L)
    {
        try
        {
            if (kind == "family")
            {
                (IntPtr h, string t) fd = default;
                if (!WaitFor(() => (fd = NewDialog(old, pid, t => true)).h != IntPtr.Zero, 20000)) { L("no family template file dialog"); return false; }
                L($"family dialog '{fd.t}': {Dump(fd.h)}");
                return FillFileDialog(fd.h, template, L);
            }
            (IntPtr h, string t) np = default;
            if (!WaitFor(() => (np = NewDialog(old, pid, t => t.Contains("New Project", StringComparison.OrdinalIgnoreCase))).h != IntPtr.Zero, 20000)) { L("no New Project dialog"); return false; }
            L($"New Project dialog: {Dump(np.h)}");
            var browse = Children(np.h).FirstOrDefault(c => Cls(c) == "Button" && Text(c).Replace("&", "").StartsWith("Browse", StringComparison.OrdinalIgnoreCase));
            if (browse == IntPtr.Zero) { L("no Browse button"); return false; }
            var old2 = new HashSet<IntPtr>(AllWins().Select(w => w.h));
            PostMessage(browse, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
            (IntPtr h, string t) fd2 = default;
            if (!WaitFor(() => (fd2 = NewDialog(old2, pid, t => true)).h != IntPtr.Zero, 15000)) { L("no template file dialog after Browse"); return false; }
            L($"template file dialog '{fd2.t}'");
            if (!FillFileDialog(fd2.h, template, L)) return false;
            if (!WaitFor(() => !IsWindowVisible(fd2.h), 15000)) { L("template file dialog still open"); return false; }
            Thread.Sleep(800);
            var kids = Children(np.h);
            var tplCombo = kids.FirstOrDefault(c => Cls(c) == "ComboBox");
            var chosen = tplCombo == IntPtr.Zero ? "" : Text(tplCombo);
            if (string.IsNullOrEmpty(chosen) && tplCombo != IntPtr.Zero) chosen = GetText(tplCombo);
            if (!string.Equals(chosen, Path.GetFileName(template), StringComparison.OrdinalIgnoreCase))
            {
                L($"template combo shows '{chosen}', not '{Path.GetFileName(template)}': cancelling");
                var cancel = kids.FirstOrDefault(c => Cls(c) == "Button" && GetDlgCtrlID(c) == 2);
                if (cancel != IntPtr.Zero) PostMessage(cancel, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                return false;
            }
            // the "Create new: Project" radio must stay selected (it is the default)
            var radio = kids.FirstOrDefault(c => Cls(c) == "Button" && Text(c).Replace("&", "").Trim().Equals("Project", StringComparison.OrdinalIgnoreCase));
            if (radio != IntPtr.Zero && SendMessage(radio, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero) == IntPtr.Zero) { PostMessage(radio, BM_CLICK, IntPtr.Zero, IntPtr.Zero); L("selected the Project radio"); Thread.Sleep(300); }
            L($"New Project dialog before OK: {Dump(np.h)}");
            var ok = kids.FirstOrDefault(c => Cls(c) == "Button" && GetDlgCtrlID(c) == 1);
            if (ok != IntPtr.Zero) PostMessage(ok, BM_CLICK, IntPtr.Zero, IntPtr.Zero); else PostMessage(np.h, WM_COMMAND, (IntPtr)1, IntPtr.Zero);
            L("New Project OK pressed");
            return true;
        }
        catch (Exception ex) { L("driver EXC " + ex.GetType().Name + ": " + ex.Message); return false; }
    }

    // ---------------- close the active scratch document, never saving ----------------
    volatile bool _closeNo;

    async Task CloseDoc(string[] f)
    {
        var tag = f.Length > 1 ? f[1] : "";
        string before = "", after = "";
        await Api(app => before = DocSig(app), 5000);
        _closeNo = true;
        try
        {
            int cid = 0;
            await Api(app => cid = (int)(RevitCommandId.LookupCommandId("ID_REVIT_FILE_CLOSE")?.Id ?? 0), 5000);
            var me = Environment.ProcessId; var old = new HashSet<IntPtr>(AllWins().Select(w => w.h));
            WmCmd(cid);
            // the save prompt is a TaskDialog (DialogBoxShowing answers it "No") or, failing that, a Win32 box
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(500);
                var (ran, _) = await Api(app => after = DocSig(app), 1500);
                if (ran && after != before) break;
                foreach (var w in AllWins().Where(w => w.pid == me && !old.Contains(w.h) && w.cls == "#32770"))
                {
                    var no = Children(w.h).FirstOrDefault(c => Cls(c) == "Button" && Text(c).Replace("&", "").Trim().Equals("No", StringComparison.OrdinalIgnoreCase));
                    if (no != IntPtr.Zero) { PostMessage(no, BM_CLICK, IntPtr.Zero, IntPtr.Zero); Act($"closedoc: pressed No in '{w.title}'"); }
                }
            }
        }
        finally { _closeNo = false; }
        Act($"closedoc done {tag}: '{before}' -> '{after}'");
    }

    // ---------------- views ----------------
    async Task SetView(string[] f)
    {
        var tag = f[1]; var want = f[2];
        string msg = "";
        if (want == "Legend")
        {
            bool has = false;
            await Api(app => has = new FilteredElementCollector(app.ActiveUIDocument.Document).OfClass(typeof(View)).Cast<View>().Any(v => !v.IsTemplate && v.ViewType == ViewType.Legend), 5000);
            if (!has)
            {
                // no API creates a legend: start Revit's own New Legend View and accept its dialog
                _acceptNext = "";
                var item = Walk().FirstOrDefault(p => p.Item.Id == "ID_VIEW_NEW_LEGEND")?.Item;
                if (item != null && Autodesk.Windows.ComponentManager.CommandHandler.CanExecute(item))
                {
                    // if the New Legend View dialog does not come through DialogBoxShowing, press its OK (defaults) by Win32
                    var me = Environment.ProcessId; var oldWins = new HashSet<IntPtr>(AllWins().Select(w => w.h));
                    _ = Task.Run(() =>
                    {
                        (IntPtr h, string t) dlg = default;
                        if (!WaitFor(() => (dlg = NewDialog(oldWins, me, t => true)).h != IntPtr.Zero, 8000)) return;
                        var ok = Children(dlg.h).FirstOrDefault(c => Cls(c) == "Button" && GetDlgCtrlID(c) == 1);
                        if (ok != IntPtr.Zero) PostMessage(ok, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                        Act($"legend: pressed OK in '{dlg.t}'");
                    });
                    var h = Autodesk.Windows.ComponentManager.CommandHandler;
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => { try { h.Execute(item); } catch (Exception ex) { Err("legend", ex); } }));
                    await Task.Delay(4000);
                    msg += "legend via ID_VIEW_NEW_LEGEND; ";
                }
                else msg += "no ID_VIEW_NEW_LEGEND item; ";
                _acceptNext = null;
            }
        }
        var (ran, ex) = await Api(app => msg += ActivateView(app, want), 20000);
        await Task.Delay(3000); // WU1c: answers settle in the first seconds after an API view switch
        string state = "";
        await Api(app => state = $"context={ContextKey(app)} view='{app.ActiveUIDocument?.ActiveView?.Name}'", 5000);
        Act($"view done {tag}: {want} ran={ran} {(ex != null ? "EXC " + ex.GetType().Name + ": " + S(ex.Message) : "")} {msg} -> {state}");
    }

    string ActivateView(UIApplication app, string want)
    {
        var ui = app.ActiveUIDocument; var d = ui.Document;
        var views = new FilteredElementCollector(d).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).ToList();
        if (want == "Default") return "default view kept; ";
        if (!Enum.TryParse<ViewType>(want, out var vt)) throw new ArgumentException("unknown view type " + want);
        // a schedule context is an ordinary schedule, not a title block's revision schedule
        View pick = views.Where(v => v.ViewType == vt && !(v is ViewSchedule vs && (vs.IsTitleblockRevisionSchedule || vs.IsInternalKeynoteSchedule)))
            .OrderByDescending(v => v.Name == "Level 1" || v.Name == "South" || v.Name == "{3D}").ThenBy(v => v.Name).FirstOrDefault();
        string how = "existing";
        if (pick is View3D v3 && v3.IsPerspective) pick = views.OfType<View3D>().FirstOrDefault(x => !x.IsPerspective);
        if (pick == null)
        {
            if (d.IsFamilyDocument) throw new InvalidOperationException($"the family has no {want} view");
            using var t = new Transaction(d, "census setup " + want);
            t.Start();
            ViewFamilyType Vft(ViewFamily fam) => new FilteredElementCollector(d).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(x => x.ViewFamily == fam);
            switch (vt)
            {
                case ViewType.ThreeD: pick = View3D.CreateIsometric(d, Vft(ViewFamily.ThreeDimensional).Id); break;
                case ViewType.Section:
                {
                    var box = new BoundingBoxXYZ { Min = new XYZ(-20, -10, -10), Max = new XYZ(20, 30, 10) };
                    var tr = Transform.Identity; tr.Origin = XYZ.Zero; tr.BasisX = XYZ.BasisX; tr.BasisY = XYZ.BasisZ; tr.BasisZ = -XYZ.BasisY; box.Transform = tr;
                    pick = ViewSection.CreateSection(d, Vft(ViewFamily.Section).Id, box); break;
                }
                case ViewType.DrawingSheet:
                {
                    var tb = new FilteredElementCollector(d).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsElementType().FirstElementId();
                    pick = ViewSheet.Create(d, tb ?? ElementId.InvalidElementId); break;
                }
                case ViewType.DraftingView: pick = ViewDrafting.Create(d, Vft(ViewFamily.Drafting).Id); break;
                case ViewType.Schedule: pick = ViewSchedule.CreateSchedule(d, new ElementId(BuiltInCategory.OST_Walls)); break;
                default: t.RollBack(); throw new InvalidOperationException($"no {want} view and no way to create one");
            }
            t.Commit(); how = "created";
        }
        ui.ActiveView = pick;
        return $"{how} '{pick.Name}' ({pick.ViewType}); ";
    }
}
