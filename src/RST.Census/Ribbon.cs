// rst-c's own (not vendored): the census ribbon walk (spec #34, "Per-tool classification"; decision #100).
//
// Every item with a command id on every panel of every tab, including dropdowns, fold panels, slide-outs and dialog
// launchers (the WU1c walk). A place is recorded as:
//   tab   = the RibbonTab's Id (language-independent: "Architecture", "Home_Family", "Modify")
//   panel = the panel's title as drawn (English on the VM)
//   path  = the containers between the panel and the item: "/SlideOut" when the item is in the slide-out,
//           then one "/<Type>(<Id or text>)" per container, or "DialogLauncher"; empty for an item straight on the panel.
// Contextual tabs are never walked (decision #100). Which tabs are in scope is decided by the run: visible tabs only,
// never a hidden tab in a family (a CanExecute call there crashed Revit, WU1c).

using System.Collections;
using System.Reflection;
using Autodesk.Windows;
using RibbonItem = Autodesk.Windows.RibbonItem;
using RibbonPanel = Autodesk.Windows.RibbonPanel;

namespace RST.Census;

public sealed partial class CensusApp
{
    sealed class Place
    {
        public RibbonTab Tab; public RibbonPanel Panel; public RibbonItem Item; public string Path; public bool Slide;
        public bool ParentsVisible = true; public bool Launcher;
        public string TabId => Tab?.Id ?? "";
        public string PanelTitle => S(Panel?.Source?.Title ?? Panel?.Source?.Id ?? "");
        public string Label => S(Item?.Text ?? Item?.Name ?? Item?.Id ?? "").Replace("  ", " ");
    }

    static List<Place> Walk()
    {
        var o = new List<Place>();
        var rib = ComponentManager.Ribbon; if (rib == null) return o;
        foreach (var tab in rib.Tabs.ToList())
        {
            if (tab == null) continue;
            foreach (var panel in tab.Panels.ToList())
            {
                var src = panel?.Source; if (src == null) continue;
                bool slide = false;
                foreach (var it in src.Items.Cast<object>().ToList())
                {
                    if (it is RibbonPanelBreak) { slide = true; continue; }
                    Rec(o, tab, panel, it, slide ? "/SlideOut" : "", slide, true);
                }
                try { if (src.DialogLauncher is RibbonItem dl) o.Add(new Place { Tab = tab, Panel = panel, Item = dl, Path = "DialogLauncher", Launcher = true }); } catch { }
            }
        }
        return o;
    }

    static void Rec(List<Place> o, RibbonTab tab, RibbonPanel panel, object it, string path, bool slide, bool pv)
    {
        if (it == null) return;
        var ri = it as RibbonItem;
        if (ri != null) o.Add(new Place { Tab = tab, Panel = panel, Item = ri, Path = path, Slide = slide, ParentsVisible = pv });
        var kids = Kids(it); if (kids == null) return;
        var seg = "/" + it.GetType().Name + "(" + S(ri != null && !string.IsNullOrEmpty(ri.Id) ? ri.Id : Txt(it)) + ")";
        bool v = pv && (ri == null || ri.IsVisible);
        foreach (var k in kids) Rec(o, tab, panel, k, path + seg, slide, v);
    }

    static List<object> Kids(object it)
    {
        PropertyInfo pi = null;
        for (var t = it.GetType(); t != null && pi == null; t = t.BaseType)
            pi = t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).FirstOrDefault(p => p.Name == "Items" && p.GetIndexParameters().Length == 0);
        if (pi == null || !typeof(IEnumerable).IsAssignableFrom(pi.PropertyType) || pi.PropertyType == typeof(string)) return null;
        try { var e = pi.GetValue(it) as IEnumerable; return e?.Cast<object>().ToList(); } catch { return null; }
    }

    static string Txt(object it) => it is RibbonItem ri ? (ri.Text ?? ri.Name ?? ri.Id) : it.ToString();

    static bool IsContainerType(RibbonItem it) => it is RibbonSplitButton || it is RibbonMenuButton || it is RibbonCombo || it is RibbonRowPanel
        || it.GetType().Name.Contains("Combo") || it.GetType().Name.Contains("Gallery") || it.GetType().Name.Contains("Selector") || (Kids(it)?.Count > 0);

    /// <summary>The gate (WU1c): IsEnabled AND the item's own handler for add-in buttons, else the ribbon's global handler.</summary>
    static string Gate(RibbonItem it)
    {
        try
        {
            if (!it.IsEnabled) return "False(IsEnabled)";
            if (it is RibbonCommandItem rc && rc.CommandHandler != null) return rc.CommandHandler.CanExecute(rc) ? "True" : "False(own)";
            return ComponentManager.CommandHandler.CanExecute(it) ? "True" : "False";
        }
        catch (Exception ex) { return "ERR " + ex.GetType().Name; }
    }
}
