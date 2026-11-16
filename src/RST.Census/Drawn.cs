// rst-c's own (not vendored): the not-drawn check (spec #34, "Not drawn").
//
// The ribbon model holds items Revit never draws (WU1b: In-Place Wall, Building Pad, the Site Settings launcher,
// Numbering, Stair by Sketch's fold-panel place in 2026), with nothing in the model to tell. So the census looks at
// what WPF actually realizes on screen, per visible tab, in-process (the run is offline, so this may take seconds):
//
//   1. activate the tab, let it lay out, and read the WPF visual tree of every PresentationSource: a place is drawn
//      when an element whose DataContext is that RibbonItem is visible with a non-zero size;
//   2. open what a user would open and read again: every dropdown (the WPF ToggleButton / Popup under a list
//      button's control), every panel slide-out, every collapsed panel's popup, repeating for nested dropdowns;
//   3. close everything and restore the tab that was active.
// A place never seen drawn is `not-drawn`. UI Automation is not used: after a tab switch it does not expose the new
// tab's panels (WU1b R3).

using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Autodesk.Windows;
using RibbonItem = Autodesk.Windows.RibbonItem;

namespace RST.Census;

public sealed partial class CensusApp
{
    static IEqualityComparer<RibbonItem> RefEq => ReferenceEqualityComparer.Instance as IEqualityComparer<RibbonItem>;

    /// <summary>Elements whose DataContext is a RibbonItem, from every PresentationSource on this thread.</summary>
    static List<(RibbonItem item, FrameworkElement fe)> Realized()
    {
        var o = new List<(RibbonItem, FrameworkElement)>();
        foreach (PresentationSource ps in PresentationSource.CurrentSources)
        {
            if (ps?.RootVisual == null) continue;
            var stack = new Stack<(DependencyObject o, RibbonItem cur)>();
            stack.Push((ps.RootVisual, null));
            while (stack.Count > 0)
            {
                var (d, cur) = stack.Pop();
                if (d is FrameworkElement fe && fe.DataContext is RibbonItem ri && !ReferenceEquals(ri, cur)) { cur = ri; o.Add((ri, fe)); }
                int n = 0; try { n = VisualTreeHelper.GetChildrenCount(d); } catch { }
                for (int i = n - 1; i >= 0; i--) { var c = VisualTreeHelper.GetChild(d, i); if (c != null) stack.Push((c, cur)); }
            }
        }
        return o;
    }

    static bool Shows(FrameworkElement fe)
    {
        try { return fe.IsVisible && fe.ActualWidth > 0.5 && fe.ActualHeight > 0.5 && fe.Opacity > 0.01; } catch { return false; }
    }

    static void Mark(HashSet<RibbonItem> drawn, StringBuilder log, string step)
    {
        int before = drawn.Count;
        foreach (var (item, fe) in Realized()) if (Shows(fe)) drawn.Add(item);
        log?.Append($"    {step}: drawn {drawn.Count} (+{drawn.Count - before})\r\n");
    }

    static IEnumerable<T> Descend<T>(DependencyObject root) where T : DependencyObject
    {
        var stack = new Stack<DependencyObject>(); stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (d is T t) yield return t;
            int n = 0; try { n = VisualTreeHelper.GetChildrenCount(d); } catch { }
            for (int i = 0; i < n; i++) { var c = VisualTreeHelper.GetChild(d, i); if (c != null) stack.Push(c); }
        }
    }

    static void SetProp(object o, string name, object v) { try { o?.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(o, v); } catch { } }

    static object Prop(object o, string name) { try { return o?.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(o); } catch { return null; } }

    /// <summary>Opens one dropdown: the control's toggle, else its popup. Returns the opener to close it again.</summary>
    static Action OpenDropDown(FrameworkElement fe, StringBuilder log)
    {
        foreach (var tb in Descend<ToggleButton>(fe))
        {
            if (!tb.IsVisible || tb.IsChecked == true) continue;
            try { tb.IsChecked = true; return () => { try { tb.IsChecked = false; } catch { } }; } catch { }
        }
        foreach (var p in Descend<FrameworkElement>(fe))
        {
            var popup = p as Popup ?? Prop(p, "Popup") as Popup;
            if (popup == null || popup.IsOpen) continue;
            try { popup.IsOpen = true; return () => { try { popup.IsOpen = false; } catch { } }; } catch { }
        }
        // AdWindows list buttons: an IsDropDownOpen-like property on the control
        foreach (var name in new[] { "IsDropDownOpen", "IsOpen", "IsSubMenuOpen" })
        {
            var pi = fe.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (pi?.PropertyType == typeof(bool) && pi.CanWrite) { try { pi.SetValue(fe, true); return () => { try { pi.SetValue(fe, false); } catch { } }; } catch { } }
        }
        return null;
    }

    /// <summary>The drawn places of the visible, non-contextual tabs. Runs on the UI thread, outside an API context.</summary>
    async Task<HashSet<RibbonItem>> DrawnSweep(List<Place> places, StringBuilder log)
    {
        var drawn = new HashSet<RibbonItem>(RefEq);
        var rib = ComponentManager.Ribbon; if (rib == null) return drawn;
        var wasActive = rib.ActiveTab;
        var tabs = places.Select(p => p.Tab).Distinct().Where(t => t.IsVisible && !t.IsContextualTab).ToList();
        var sw = Stopwatch.StartNew();
        foreach (var tab in tabs)
        {
            log?.Append($"  tab {tab.Id}\r\n");
            try { tab.IsActive = true; } catch (Exception ex) { log?.Append($"    activate failed {ex.GetType().Name}\r\n"); continue; }
            await Task.Delay(700);
            Mark(drawn, log, "tab");
            var onTab = new HashSet<RibbonItem>(places.Where(p => ReferenceEquals(p.Tab, tab)).Select(p => p.Item), RefEq);

            // collapsed panels and slide-outs: open each panel's popups
            foreach (var pc in Realized().Select(x => x.fe).Concat(AllPanelControls()).Distinct().Where(fe => fe.GetType().Name == "RibbonPanelControl").ToList())
            {
                var panel = Prop(pc, "Panel") as Autodesk.Windows.RibbonPanel;
                if (panel == null || !ReferenceEquals(panel.Tab, tab)) continue;
                if (Prop(pc, "IsCollapsedNow") is true && Prop(pc, "PanelPopup") is Popup pp)
                {
                    try { pp.IsOpen = true; await Task.Delay(400); Mark(drawn, log, $"collapsed panel {S(panel.Source?.Title)}"); await OpenAll(drawn, onTab, log); pp.IsOpen = false; } catch { }
                }
                if (places.Any(p => ReferenceEquals(p.Panel, panel) && p.Slide))
                {
                    SetProp(panel, "SlideOutMustOpenNow", true);
                    await Task.Delay(400);
                    Mark(drawn, log, $"slide-out {S(panel.Source?.Title)}");
                    await OpenAll(drawn, onTab, log);
                    SetProp(panel, "SlideOutMustCloseNow", true);
                    await Task.Delay(150);
                }
            }
            await OpenAll(drawn, onTab, log);
        }
        try { if (wasActive != null) wasActive.IsActive = true; } catch { }
        await Task.Delay(300);
        log?.Append($"  sweep {sw.ElapsedMilliseconds} ms, drawn {drawn.Count}\r\n");
        return drawn;
    }

    static IEnumerable<FrameworkElement> AllPanelControls()
    {
        foreach (PresentationSource ps in PresentationSource.CurrentSources)
            if (ps?.RootVisual != null) foreach (var fe in Descend<FrameworkElement>(ps.RootVisual)) if (fe.GetType().Name == "RibbonPanelControl") yield return fe;
    }

    /// <summary>Opens every drawn container of this tab (and the ones that appear inside them), one at a time.</summary>
    async Task OpenAll(HashSet<RibbonItem> drawn, HashSet<RibbonItem> onTab, StringBuilder log)
    {
        var opened = new HashSet<RibbonItem>(RefEq);
        for (int pass = 0; pass < 3; pass++)
        {
            var containers = Realized().Where(x => onTab.Contains(x.item) && Shows(x.fe) && !opened.Contains(x.item) && Kids(x.item)?.Count > 0
                                                     && x.item is RibbonListButton).GroupBy(x => x.item, RefEq).Select(g => g.First()).ToList();
            if (containers.Count == 0) break;
            foreach (var (item, fe) in containers)
            {
                opened.Add(item);
                var close = OpenDropDown(fe, log);
                if (close == null) { log?.Append($"    could not open {item.Id}\r\n"); continue; }
                await Task.Delay(350);
                Mark(drawn, log, "dropdown " + item.Id);
                close();
                await Task.Delay(120);
            }
        }
    }

    /// <summary>drawn|tag: the sweep on its own, one row per place (for checking the method against WU1b).</summary>
    async Task DrawnVerb(string[] f)
    {
        var tag = f.Length > 1 ? f[1] : "drawn";
        var log = new StringBuilder($"{DateTime.Now:o} drawn sweep\r\n");
        try
        {
            var places = Walk().Where(p => p.Tab.IsVisible && !p.Tab.IsContextualTab && !string.IsNullOrEmpty(p.Item.Id)).ToList();
            var drawn = await DrawnSweep(places, log);
            var sb = new StringBuilder("tab\tpanel\tpath\tid\tlabel\titemVisible\tparentsVisible\tdrawn\r\n");
            foreach (var p in places)
                sb.Append(p.TabId).Append('\t').Append(p.PanelTitle).Append('\t').Append(p.Path).Append('\t').Append(p.Item.Id).Append('\t').Append(p.Label).Append('\t')
                  .Append(p.Item.IsVisible).Append('\t').Append(p.ParentsVisible).Append('\t').Append(drawn.Contains(p.Item)).Append("\r\n");
            WriteFile(Path.Combine("drawn", tag + ".tsv"), sb.ToString());
        }
        catch (Exception ex) { log.Append("EXC " + ex); }
        WriteFile(Path.Combine("drawn", tag + ".log"), log.ToString());
        Act($"drawn done {tag}");
    }
}
