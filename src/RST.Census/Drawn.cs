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

    /// <summary>
    /// Opens one dropdown as a click on its arrow would: AdWindows' list buttons have a MenuToggleButton named
    /// mFlyoutButtonShowFlyout and a RibbonListButtonPopup named mFlyoutPopup (the ComboButton next to them is the
    /// execute half, also a ToggleButton: never that one). Returns the action that closes it again.
    /// </summary>
    static Action OpenDropDown(FrameworkElement fe, StringBuilder log)
    {
        var all = Descend<FrameworkElement>(fe).ToList();
        var tb = all.OfType<ToggleButton>().FirstOrDefault(t => t.Name == "mFlyoutButtonShowFlyout" && t.IsVisible)
              ?? all.OfType<ToggleButton>().FirstOrDefault(t => t.GetType().Name == "MenuToggleButton" && t.IsVisible);
        if (tb != null && tb.IsChecked != true)
        {
            try { tb.IsChecked = true; return () => { try { tb.IsChecked = false; } catch { } }; } catch { }
        }
        var popup = all.OfType<Popup>().FirstOrDefault(p => p.Name == "mFlyoutPopup") ?? all.OfType<Popup>().FirstOrDefault();
        if (popup != null && !popup.IsOpen)
        {
            try { popup.IsOpen = true; return () => { try { popup.IsOpen = false; } catch { } }; } catch { }
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
            var onTab = places.Where(p => ReferenceEquals(p.Tab, tab)).ToList();
            var panelControls = AllPanelControls().Where(pc => ReferenceEquals((Prop(pc, "Panel") as Autodesk.Windows.RibbonPanel)?.Tab, tab)).ToList();
            foreach (var pc in panelControls)
            {
                var panel = (Autodesk.Windows.RibbonPanel)Prop(pc, "Panel");
                var inPanel = new HashSet<RibbonItem>(onTab.Where(p => ReferenceEquals(p.Panel, panel)).Select(p => p.Item), RefEq);
                // a panel collapsed by the ribbon's width shows its items in a popup
                if (Prop(pc, "IsCollapsedNow") is true && Prop(pc, "PanelPopup") is Popup pp)
                {
                    try { pp.IsOpen = true; await Task.Delay(400); Mark(drawn, log, $"collapsed panel {S(panel.Source?.Title)}"); await OpenAll(drawn, inPanel, log); pp.IsOpen = false; await Task.Delay(150); } catch { }
                }
                // the slide-out (items after the panel break): try the panel's own flag, then the title bar's toggle, then its popup
                var slide = new HashSet<RibbonItem>(onTab.Where(p => ReferenceEquals(p.Panel, panel) && p.Slide).Select(p => p.Item), RefEq);
                if (slide.Count == 0) continue;
                Action close = null; string how = "";
                foreach (var attempt in new[] { "flag", "toggle", "popup" })
                {
                    if (attempt == "flag") { SetProp(panel, "SlideOutMustOpenNow", true); close = () => SetProp(panel, "SlideOutMustCloseNow", true); }
                    else if (attempt == "toggle")
                    {
                        var tb = Descend<ToggleButton>(pc).FirstOrDefault(t => t.IsVisible && t.IsChecked != true && t.DataContext is not RibbonItem);
                        if (tb == null) continue;
                        tb.IsChecked = true; close = () => { try { tb.IsChecked = false; } catch { } };
                    }
                    else
                    {
                        var pop = Descend<FrameworkElement>(pc).OfType<Popup>().FirstOrDefault(x => !x.IsOpen && x.GetType().Name.Contains("SlideOut"));
                        if (pop == null) continue;
                        pop.IsOpen = true; close = () => { try { pop.IsOpen = false; } catch { } };
                    }
                    await Task.Delay(450);
                    int before = slide.Count(drawn.Contains);
                    Mark(drawn, null, "");
                    if (slide.Count(drawn.Contains) > before || Realized().Any(x => slide.Contains(x.item) && Shows(x.fe))) { how = attempt; break; }
                    close?.Invoke(); close = null; await Task.Delay(150);
                }
                log?.Append($"    slide-out {S(panel.Source?.Title)}: opened by {(how == "" ? "nothing" : how)}, drawn {slide.Count(drawn.Contains)}/{slide.Count}\r\n");
                if (how != "") await OpenAll(drawn, slide, log);
                close?.Invoke();
                await Task.Delay(200);
            }
            await OpenAll(drawn, new HashSet<RibbonItem>(onTab.Where(p => !p.Slide).Select(p => p.Item), RefEq), log);
        }
        try { if (wasActive != null) wasActive.IsActive = true; } catch { }
        await Task.Delay(300);
        // a submenu's items show on hover: they are drawn when their submenu item (a menu item with items of its own,
        // inside an opened dropdown) is drawn and the item itself is visible
        int sub = 0;
        for (int pass = 0; pass < 3; pass++)
            foreach (var p in places)
                if (!drawn.Contains(p.Item) && p.Parent is RibbonItem par && par is not RibbonListButton && par is not RibbonRowPanel && drawn.Contains(par) && p.Item.IsVisible)
                { drawn.Add(p.Item); sub++; }
        log?.Append($"  submenu items counted drawn through their drawn submenu item: {sub}\r\n");
        log?.Append($"  sweep {sw.ElapsedMilliseconds} ms, drawn {drawn.Count}\r\n");
        return drawn;
    }

    static IEnumerable<FrameworkElement> AllPanelControls()
    {
        foreach (PresentationSource ps in PresentationSource.CurrentSources)
            if (ps?.RootVisual != null) foreach (var fe in Descend<FrameworkElement>(ps.RootVisual)) if (fe.GetType().Name == "RibbonPanelControl") yield return fe;
    }

    /// <summary>Opens every drawn container of this tab (and the ones that appear inside them), one at a time.</summary>
    async Task OpenAll(HashSet<RibbonItem> drawn, HashSet<RibbonItem> scope, StringBuilder log)
    {
        var opened = new HashSet<RibbonItem>(RefEq);
        for (int pass = 0; pass < 3; pass++)
        {
            // a container is opened when it is in scope or holds an item in scope (containers without an id are not places)
            var containers = Realized().Where(x => Shows(x.fe) && !opened.Contains(x.item) && x.item is RibbonListButton && Kids(x.item) is { Count: > 0 } k
                                                     && (scope.Contains(x.item) || k.OfType<RibbonItem>().Any(scope.Contains)))
                                       .GroupBy(x => x.item, RefEq).Select(g => g.First()).ToList();
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

    /// <summary>vtxopen|tag|containerId-or-text|childId: open the container's dropdown, dump the child's controls, close.</summary>
    async Task VtxOpen(string[] f)
    {
        var sb = new StringBuilder();
        var target = Realized().Where(x => (x.item.Id == f[2] || (string.IsNullOrEmpty(x.item.Id) && S(x.item.Text) == f[2])) && Shows(x.fe)).ToList();
        sb.Append($"targets {target.Count}\r\n");
        foreach (var (item, fe) in target.Take(1))
        {
            sb.Append($"control {fe.GetType().FullName}; toggles: {string.Join(", ", Descend<ToggleButton>(fe).Select(t => t.GetType().Name + ":" + t.Name + ":" + t.IsVisible))}; popups: {string.Join(", ", Descend<FrameworkElement>(fe).OfType<Popup>().Select(p => p.GetType().Name + ":" + p.Name))}\r\n");
            var close = OpenDropDown(fe, null);
            sb.Append($"opened={close != null}\r\n");
            await Task.Delay(600);
            foreach (var (ci, cfe) in Realized().Where(x => x.item.Id == f[3]))
                sb.Append($"child {ci.Id} control {cfe.GetType().FullName} shows={Shows(cfe)} vis={cfe.IsVisible} {cfe.ActualWidth}x{cfe.ActualHeight}; props: {string.Join(", ", cfe.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(pi => pi.PropertyType == typeof(bool)).Select(pi => { object v = null; try { v = pi.GetValue(cfe); } catch { } return pi.Name + "=" + v; }))}\r\n");
            var kidsShown = Kids(item)?.OfType<RibbonItem>().Select(k => k.Id + ":" + Realized().Any(x => ReferenceEquals(x.item, k) && Shows(x.fe))).ToList();
            sb.Append("kids shown: " + string.Join(", ", kidsShown ?? new List<string>()) + "\r\n");
            close?.Invoke();
        }
        WriteFile(Path.Combine("drawn", f[1] + "-vtxopen.txt"), sb.ToString());
        Act($"vtxopen done {f[1]}");
    }

    /// <summary>vtx|tag|id: the visual subtree of an item's realized controls (debugging the sweep).</summary>
    void VtxVerb(string[] f)
    {
        var sb = new StringBuilder();
        IEnumerable<(object, FrameworkElement)> targets = f[2].StartsWith("panel:")
            ? AllPanelControls().Where(pc => (Prop(pc, "Panel") as Autodesk.Windows.RibbonPanel)?.Source?.Title == f[2].Substring(6)).Select(pc => ((object)Prop(pc, "Panel"), pc))
            : Realized().Where(x => x.item.Id == f[2]).Select(x => ((object)x.item, x.fe));
        foreach (var (target, fe) in targets)
        {
            sb.Append($"== {(target as RibbonItem)?.Id ?? (target as Autodesk.Windows.RibbonPanel)?.Source?.Title} {target?.GetType().Name} control {fe.GetType().FullName} visible={fe.IsVisible} {fe.ActualWidth}x{fe.ActualHeight}\r\n");
            sb.Append("   props: " + string.Join(", ", fe.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Where(pi => pi.PropertyType == typeof(bool) || typeof(Popup).IsAssignableFrom(pi.PropertyType)).Select(pi => { object v = null; try { v = pi.GetValue(fe); } catch { } return pi.Name + "=" + v; })) + "\r\n");
            if (target is Autodesk.Windows.RibbonPanel rp) sb.Append("   panel props: " + string.Join(", ", rp.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Where(pi => pi.PropertyType == typeof(bool)).Select(pi => { object v = null; try { v = pi.GetValue(rp); } catch { } return pi.Name + "=" + v; })) + "\r\n");
            var stack = new Stack<(DependencyObject, int)>(); stack.Push((fe, 0));
            while (stack.Count > 0)
            {
                var (d, depth) = stack.Pop();
                if (depth > (f.Length > 3 ? int.Parse(f[3]) : 12)) continue;
                var e = d as FrameworkElement;
                string extra = d is ToggleButton tb ? $" IsChecked={tb.IsChecked}" : d is Popup pp ? $" Popup IsOpen={pp.IsOpen}" : "";
                foreach (var pn in new[] { "IsDropDownOpen", "IsOpen", "Source" }) { var v = Prop(d, pn); if (v != null) extra += $" {pn}={v}"; }
                sb.Append(new string(' ', depth * 2)).Append(d.GetType().Name).Append(" name=").Append(e?.Name).Append(" dc=").Append(e?.DataContext?.GetType().Name).Append(e?.DataContext is RibbonItem ri ? "(" + ri.Id + ")" : "").Append(" vis=").Append(e?.IsVisible).Append(extra).Append("\r\n");
                int n = 0; try { n = VisualTreeHelper.GetChildrenCount(d); } catch { }
                for (int i = n - 1; i >= 0; i--) stack.Push((VisualTreeHelper.GetChild(d, i), depth + 1));
            }
        }
        var t = ComponentManager.Ribbon.Tabs.SelectMany(x => x.Panels).SelectMany(p => Walk().Where(w => w.Item.Id == f[2])).FirstOrDefault();
        foreach (var p in Walk().Where(w => w.Item.Id == f[2])) sb.Append($"model: {p.TabId}/{p.PanelTitle}{p.Path} type={p.Item.GetType().FullName} kids={Kids(p.Item)?.Count}\r\n");
        WriteFile(Path.Combine("drawn", f[1] + "-vtx.txt"), sb.ToString());
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
            var sb = new StringBuilder("tab\tpanel\tpath\tid\tlabel\titemVisible\tparentsVisible\tpanelVisible\tdrawn\r\n");
            foreach (var p in places)
                sb.Append(p.TabId).Append('\t').Append(p.PanelTitle).Append('\t').Append(p.Path).Append('\t').Append(p.Item.Id).Append('\t').Append(p.Label).Append('\t')
                  .Append(p.Item.IsVisible).Append('\t').Append(p.ParentsVisible).Append('\t').Append(p.Panel.IsVisible).Append('\t').Append(drawn.Contains(p.Item)).Append("\r\n");
            WriteFile(Path.Combine("drawn", tag + ".tsv"), sb.ToString());
        }
        catch (Exception ex) { log.Append("EXC " + ex); }
        WriteFile(Path.Combine("drawn", tag + ".log"), log.ToString());
        Act($"drawn done {tag}");
    }
}
