// HealthCommand.cs — IExternalCommand wired to the "Health" ribbon button.
//
// Captures the live Revit context (version, build, username, active model
// name/path/size, ACC cloud-cache fallback for size, warning count) and
// hands it to RST.UI.Health.HealthHost.ShowModal which opens the
// WebView2-hosted viewer modally on the UI thread.
//
// In-process replacement for the upstream three-process Python flow
// (IronPython Snap → CPython viewer → CPython runner) — no JSON
// hand-off file, no subprocess, no py-3.12 dependency on the user's
// machine.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RST.Core.Health;
using RST.UI.Health;
using Serilog;

namespace RST.Engine.Commands;

[Transaction(TransactionMode.ReadOnly)]
[Regeneration(RegenerationOption.Manual)]
public sealed class HealthCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var context = CaptureContext(commandData);
            Log.Information("=== Health session opened: revit={Version}, model={Model} ===",
                            context.RevitVersion, context.ModelName);
            HealthHost.ShowModal(context);
            Log.Information("=== Health session closed: duration={Ms}ms ===", sw.ElapsedMilliseconds);
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Health session FAILED after {Ms}ms", sw.ElapsedMilliseconds);
            message = ex.Message;
            return Result.Failed;
        }
    }

    private static HealthContext CaptureContext(ExternalCommandData commandData)
    {
        var app = commandData.Application.Application;
        string version = SafeStr(() => app.VersionNumber);
        string build   = SafeStr(() => app.VersionBuild);
        string user    = SafeStr(() => app.Username);

        string modelName = "";
        string modelPath = "";
        double? modelSizeMb = null;
        int? warningsCount = null;

        var uidoc = commandData.Application.ActiveUIDocument;
        var doc = uidoc?.Document;
        if (doc is not null && !doc.IsFamilyDocument)
        {
            modelName = SafeStr(() => doc.Title ?? "");
            modelPath = SafeStr(() => doc.PathName ?? "");

            modelSizeMb = TryGetFileSizeMb(modelPath);

            // ACC / cloud fallback: doc.PathName is a cloud URL, not a real
            // file path. Walk %LocalAppData%\Autodesk\Revit\<ver>\CollaborationCache\
            // looking for a .rvt that matches the model GUID — fall back to
            // newest .rvt if GUID lookup fails (matches upstream Snap.py).
            if (modelSizeMb is null)
            {
                modelSizeMb = TryReadCollabCacheSize(doc);
            }

            try { warningsCount = doc.GetWarnings()?.Count; }
            catch (Exception ex) { Log.Debug(ex, "HealthCommand: GetWarnings failed"); }
        }

        return new HealthContext
        {
            RevitVersion = version,
            RevitBuild = build,
            RevitUsername = user,
            ModelName = modelName,
            ModelPath = modelPath,
            ModelSizeMb = modelSizeMb,
            WarningsCount = warningsCount,
        };
    }

    private static string SafeStr(Func<string?> getter)
    {
        try { return getter() ?? ""; }
        catch { return ""; }
    }

    private static double? TryGetFileSizeMb(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? Math.Round(fi.Length / (1024.0 * 1024.0), 1) : null;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "HealthCommand.TryGetFileSizeMb: {Path}", path);
            return null;
        }
    }

    private static double? TryReadCollabCacheSize(Document doc)
    {
        try
        {
            var guidTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var cmp = doc.GetCloudModelPath();
                if (cmp is not null && !cmp.Empty)
                {
                    void Add(string? s) { if (!string.IsNullOrEmpty(s)) guidTokens.Add(s!); }
                    try { Add(cmp.GetModelGUID().ToString()); } catch { }
                    try { Add(cmp.GetProjectGUID().ToString()); } catch { }
                }
            }
            catch (Exception ex) { Log.Debug(ex, "HealthCommand: GetCloudModelPath failed"); }

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(localAppData)) return null;
            var cacheRoot = Path.Combine(localAppData, "Autodesk", "Revit");
            if (!Directory.Exists(cacheRoot)) return null;

            (DateTime mtime, string path, long size)? bestByGuid = null;
            (DateTime mtime, string path, long size)? newestRvt = null;

            foreach (var verEntry in Directory.GetDirectories(cacheRoot))
            {
                var cc = Path.Combine(verEntry, "CollaborationCache");
                if (!Directory.Exists(cc)) continue;
                foreach (var rvt in EnumerateRvtFiles(cc))
                {
                    var pathLower = rvt.ToLowerInvariant();
                    bool pathHasGuid = guidTokens.Count > 0 && guidTokens.Any(g => pathLower.Contains(g.ToLowerInvariant()));
                    long size; DateTime mtime;
                    try
                    {
                        var fi = new FileInfo(rvt);
                        size = fi.Length;
                        mtime = fi.LastWriteTimeUtc;
                    }
                    catch { continue; }

                    var entry = (mtime, rvt, size);
                    if (newestRvt is null || mtime > newestRvt.Value.mtime)
                        newestRvt = entry;

                    var nameLower = Path.GetFileName(rvt).ToLowerInvariant();
                    bool nameHasGuid = guidTokens.Count > 0 && guidTokens.Any(g => nameLower.Contains(g.ToLowerInvariant()));
                    if (guidTokens.Count > 0 && (pathHasGuid || nameHasGuid))
                    {
                        if (bestByGuid is null || mtime > bestByGuid.Value.mtime)
                            bestByGuid = entry;
                    }
                }
            }

            var pick = bestByGuid ?? newestRvt;
            if (pick is null) return null;
            var sizeMb = Math.Round(pick.Value.size / (1024.0 * 1024.0), 1);
            Log.Information("HealthCommand: ACC cache pick ({Reason}) = {Path} ({SizeMb} MB)",
                            bestByGuid is not null ? "GUID match" : "newest .rvt",
                            pick.Value.path, sizeMb);
            return sizeMb;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "HealthCommand.TryReadCollabCacheSize failed");
            return null;
        }
    }

    private static IEnumerable<string> EnumerateRvtFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] files;
            try { files = Directory.GetFiles(dir, "*.rvt"); }
            catch { continue; }
            foreach (var f in files) yield return f;

            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch { continue; }
            foreach (var s in subs) stack.Push(s);
        }
    }
}
