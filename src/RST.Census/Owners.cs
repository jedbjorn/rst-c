// rst-c's own (not vendored): who owns a ribbon button (spec #34, "Stock only"; decision #110).
//
// The census classifies stock Revit only: Revit and the add-ins Autodesk ships with it. The VM baseline has no
// third-party add-ins any more, so this is a safety net: a button owned by a non-Autodesk add-in, and a native id
// bound by one, are skipped before anything else, get no row, and are counted in the run summary.
//
//   - Manifests: every .addin under Revit's own AddIns folder, the machine and user Addins\<year> folders and the
//     ApplicationPlugins bundles. Stock = VendorId ADSK / Autodesk, or a manifest inside Revit's install folder.
//   - Add-in buttons (an own CommandHandler, or a CustomCtrl_ id): the Revit API ribbon item behind the button
//     (UIApplication.GetRibbonPanels, mapped to the AdWindows item) gives the external command's assembly; that
//     assembly's manifest gives the vendor. Failing that, the handler's assembly.
//   - Native ids another add-in has bound (RevitCommandId.HasBinding): the journal records which application
//     registered the binding ("API registering command … by application <name>"); the name's manifest gives the vendor.
// An owner the census cannot place is treated as stock and listed in the summary as "owner unknown".

using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Autodesk.Revit.UI;
using RibbonItem = Autodesk.Windows.RibbonItem;

namespace RST.Census;

public sealed partial class CensusApp
{
    sealed class Manifest { public string File, Name, Assembly, Vendor, AddInId; public bool Stock; }
    sealed class Owner { public string Who = "", Vendor = "", How = ""; public bool Stock = true; public bool Known; }

    List<Manifest> _manifests;

    List<Manifest> Manifests()
    {
        if (_manifests != null) return _manifests;
        var o = new List<Manifest>();
        var revitDir = $@"C:\Program Files\Autodesk\Revit {_year}";
        var dirs = new List<(string dir, bool recurse)>
        {
            (Path.Combine(revitDir, "AddIns"), true),
            ($@"C:\ProgramData\Autodesk\Revit\Addins\{_year}", false),
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), $@"Autodesk\Revit\Addins\{_year}"), false),
            (@"C:\ProgramData\Autodesk\ApplicationPlugins", true),
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Autodesk\ApplicationPlugins"), true),
        };
        foreach (var (dir, rec) in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*.addin", rec ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).ToList(); } catch { continue; }
            foreach (var f in files)
            {
                try
                {
                    var x = XDocument.Load(f);
                    foreach (var a in x.Descendants().Where(e => e.Name.LocalName == "AddIn"))
                    {
                        string V(string n) => a.Elements().FirstOrDefault(e => e.Name.LocalName == n)?.Value?.Trim() ?? "";
                        var asm = V("Assembly");
                        if (asm != "" && !Path.IsPathRooted(asm)) asm = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(f), asm));
                        var vendor = V("VendorId");
                        o.Add(new Manifest
                        {
                            File = f, Name = V("Name"), Assembly = asm, Vendor = vendor, AddInId = V("AddInId") + V("ClientId"),
                            Stock = vendor.StartsWith("ADSK", StringComparison.OrdinalIgnoreCase) || vendor.Contains("Autodesk", StringComparison.OrdinalIgnoreCase)
                                    || f.StartsWith(revitDir, StringComparison.OrdinalIgnoreCase),
                        });
                    }
                }
                catch (Exception ex) { Err("manifest " + f, ex); }
            }
        }
        return _manifests = o;
    }

    Owner OwnerOfAssembly(string asmPath, string how)
    {
        if (string.IsNullOrEmpty(asmPath)) return new Owner { How = how + ": no assembly" };
        var ms = Manifests();
        var m = ms.FirstOrDefault(x => string.Equals(x.Assembly, asmPath, StringComparison.OrdinalIgnoreCase))
             ?? ms.Where(x => x.Assembly != "" && asmPath.StartsWith(Path.GetDirectoryName(x.Assembly) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                  .OrderByDescending(x => x.Assembly.Length).FirstOrDefault();
        if (m != null) return new Owner { Who = m.Name, Vendor = m.Vendor, Stock = m.Stock, Known = true, How = $"{how}: {Path.GetFileName(asmPath)} -> {Path.GetFileName(m.File)}" };
        if (asmPath.StartsWith($@"C:\Program Files\Autodesk\", StringComparison.OrdinalIgnoreCase))
            return new Owner { Who = Path.GetFileNameWithoutExtension(asmPath), Vendor = "(Autodesk install folder)", Stock = true, Known = true, How = how + ": " + asmPath };
        return new Owner { Who = Path.GetFileNameWithoutExtension(asmPath), How = how + ": no manifest for " + asmPath };
    }

    /// <summary>Add-in buttons: the external command assembly behind each AdWindows item, through the Revit API ribbon.</summary>
    Dictionary<RibbonItem, string> ApiButtonAssemblies(UIApplication app, IEnumerable<string> tabTitles)
    {
        var map = new Dictionary<RibbonItem, string>(ReferenceEqualityComparer.Instance as IEqualityComparer<RibbonItem>);
        var getItem = typeof(Autodesk.Revit.UI.RibbonItem).GetMethod("getRibbonItem", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        var panels = new List<Autodesk.Revit.UI.RibbonPanel>();
        void Try(Func<IList<Autodesk.Revit.UI.RibbonPanel>> f) { try { var l = f(); if (l != null) panels.AddRange(l); } catch { } }
        Try(() => app.GetRibbonPanels());
        Try(() => app.GetRibbonPanels(Tab.AddIns));
        Try(() => app.GetRibbonPanels(Tab.Analyze));
        foreach (var t in tabTitles.Distinct()) Try(() => app.GetRibbonPanels(t));
        void Add(Autodesk.Revit.UI.RibbonItem ai)
        {
            try
            {
                if (ai is PulldownButton pd) foreach (var k in pd.GetItems()) Add(k);
                if (ai is SplitButton sp) foreach (var k in sp.GetItems()) Add(k);
                var asm = (ai as PushButton)?.AssemblyName;
                if (asm == null) return;
                if (getItem?.Invoke(ai, null) is RibbonItem adw) map[adw] = asm;
            }
            catch { }
        }
        foreach (var p in panels.Distinct()) { try { foreach (var ai in p.GetItems()) Add(ai); } catch { } }
        return map;
    }

    /// <summary>Native ids an add-in has bound, by application name, from the journal.</summary>
    Dictionary<string, string> JournalBindings()
    {
        var o = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (string.IsNullOrEmpty(_journal) || !File.Exists(_journal)) return o;
            using var fs = new FileStream(_journal, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, System.Text.Encoding.Default);
            var rx = new Regex(@"registering command (?<id>[^\s,]+).*?by application (?<app>[^""\r\n]+)", RegexOptions.IgnoreCase);
            string line;
            while ((line = sr.ReadLine()) != null)
            {
                if (!line.Contains("registering command", StringComparison.OrdinalIgnoreCase)) continue;
                var m = rx.Match(line); if (!m.Success) continue;
                var app = m.Groups["app"].Value.Trim().TrimEnd('.');
                if (app.StartsWith("RST.Census", StringComparison.Ordinal)) continue;
                o[m.Groups["id"].Value] = app;
            }
        }
        catch (Exception ex) { Err("journal bindings", ex); }
        return o;
    }

    Owner OwnerOfApp(string appName)
    {
        var m = Manifests().FirstOrDefault(x => string.Equals(x.Name, appName, StringComparison.OrdinalIgnoreCase))
             ?? Manifests().FirstOrDefault(x => x.Name != "" && (appName.StartsWith(x.Name, StringComparison.OrdinalIgnoreCase) || x.Name.StartsWith(appName, StringComparison.OrdinalIgnoreCase)));
        if (m == null) return new Owner { Who = appName, How = "journal binding; no manifest named so" };
        return new Owner { Who = m.Name, Vendor = m.Vendor, Stock = m.Stock, Known = true, How = "journal binding -> " + Path.GetFileName(m.File) };
    }
}
