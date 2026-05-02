// spike/ScannerSpike.cs
//
// RST-001 scanner spike — *proof-of-approach only*. Not compiled into
// the product. The full scanner lands in RST-003 under
// src/RST.Engine/Scanning/.
//
// What it demonstrates:
//
//   1. Enumerating Revit's built-in commands via the PostableCommand
//      enum (no Revit DLLs reachable on Linux, but the call shape is
//      what RST-003 will use).
//
//   2. Walking the loaded UIApplication's ribbon to list every tab and
//      every push-button currently shown.
//
//   3. Parsing .addin XML manifests under
//      %ProgramData%\Autodesk\Revit\Addins\<ver>\ and
//      %AppData%\Autodesk\Revit\Addins\<ver>\
//      to recover (a) AddIn IDs, (b) Assembly DLL paths.
//
//   4. Joining (1)-(3) into a single in-memory catalog of
//      (DisplayName, Origin, AddinFile, AssemblyPath) tuples that will
//      drive the Profiler's tool-picker.
//
// The Python reference is RST/app/addin_scanner.py (~540 lines) —
// this file is intentionally a sketch.

#if SPIKE_BUILD          // never on by default
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;

// In RST.Engine these will be:
//   using Autodesk.Revit.UI;
//   using Autodesk.Revit.UI.Events;
//   using UIFrameworkServices;

namespace RST.Spike;

internal static class ScannerSpike
{
    // (1) Built-in command catalog — Revit ships these as a flat enum.
    //     RST-003 captures (Id, DisplayName fallback) for every value.
    public static IEnumerable<(string Id, string Name)> EnumerateBuiltInCommands()
    {
        // var values = Enum.GetValues(typeof(PostableCommand));
        // foreach (PostableCommand cmd in values)
        //     yield return (cmd.ToString(), CommandIdResolver.GetDisplayName(cmd));
        yield break;   // placeholder for the spike
    }

    // (2) Ribbon walk — what does Revit currently show?
    public static IEnumerable<RibbonTabInfo> WalkRibbon(/* UIApplication uiApp */)
    {
        // foreach (var tab in ComponentManager.Ribbon.Tabs)
        // {
        //     var info = new RibbonTabInfo(tab.Title, tab.Id, IsBuiltIn(tab));
        //     foreach (var panel in tab.Panels)
        //         info.Panels.Add(WalkPanel(panel));
        //     yield return info;
        // }
        yield break;
    }

    // (3) .addin XML parsing.
    public static IEnumerable<AddinManifest> ParseAddinManifests(string version)
    {
        var roots = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Autodesk", "Revit", "Addins", version),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Autodesk", "Revit", "Addins", version),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Autodesk", "ApplicationPlugins"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Autodesk", "ApplicationPlugins"),
        };

        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var path in Directory.EnumerateFiles(root, "*.addin", SearchOption.AllDirectories))
            {
                AddinManifest? manifest = null;
                try
                {
                    var doc = XDocument.Load(path);
                    manifest = AddinManifest.Parse(path, doc);
                }
                catch (Exception ex)
                {
                    // Log + continue — one bad manifest must not break the scan.
                    Console.Error.WriteLine($"[scanner] skip {path}: {ex.Message}");
                }
                if (manifest is not null) yield return manifest;
            }
        }
    }
}

// === Spike model — final shape lives in RST.Core. ===

internal sealed record RibbonTabInfo(string Title, string Id, bool IsBuiltIn)
{
    public List<RibbonPanelInfo> Panels { get; } = new();
}

internal sealed record RibbonPanelInfo(string Title, string Id);

internal sealed record AddinManifest(
    string FilePath,
    string AddinType,
    string? AssemblyPath,
    string? AddinId,
    string? Name,
    string? VendorId,
    string? VendorDescription)
{
    public static AddinManifest Parse(string path, XDocument doc)
    {
        var addinNode = doc.Root?.Element("AddIn");
        var type = addinNode?.Attribute("Type")?.Value ?? "Application";
        var asm  = addinNode?.Element("Assembly")?.Value;
        var id   = addinNode?.Element("AddInId")?.Value;
        var name = addinNode?.Element("Name")?.Value;
        var vid  = addinNode?.Element("VendorId")?.Value;
        var vd   = addinNode?.Element("VendorDescription")?.Value;
        return new AddinManifest(path, type, asm, id, name, vid, vd);
    }
}
#endif
