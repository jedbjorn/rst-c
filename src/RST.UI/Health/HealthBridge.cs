// HealthBridge.cs — host object exposed to the WebView2-hosted health
// viewer. Mirrors the JS API the upstream pywebview HealthViewerAPI
// surfaces (get_snapshot / run_scan / clean_junk / close_window) so the
// HTML viewer ports unchanged.
//
// Method-arity rule from LoaderBridge applies here too: zero-arg JS
// calls MUST be zero-arg in C# (IDispatch will not coerce), and
// methods that take args take one `string` per JS arg — the
// pywebview-shim JSON.stringifies each before dispatch.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using RST.Core.Configuration;
using RST.Core.Health;
using Serilog;

namespace RST.UI.Health;

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.AutoDual)]
public class HealthBridge
{
    /// <summary>%AppData%\RST\health_scan.json — snapshot persistence target.</summary>
    public static string SnapshotPath => Path.Combine(AppDataPaths.Root, "health_scan.json");

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HealthContext _context;
    private readonly Action _closeRequested;

    public HealthBridge(HealthContext context, Action closeRequested)
    {
        _context = context ?? HealthContext.Empty;
        _closeRequested = closeRequested ?? (() => { });
        Log.Information("HealthBridge ready: revit={Revit}, model={Model}",
                        _context.RevitVersion, _context.ModelName);
    }

    /// <summary>Read-only — pulls the latest persisted snapshot if any.</summary>
    public string GetSnapshot()
    {
        LogEntry(nameof(GetSnapshot));
        var snap = HealthScanner.Load(SnapshotPath);
        if (snap is null)
        {
            Log.Information("Bridge.get_snapshot → null (no snapshot at {Path})", SnapshotPath);
            return Serialize<object?>(null);
        }
        return Serialize(snap);
    }

    /// <summary>
    /// Run the in-process scanner using the captured Revit context, save
    /// the snapshot to disk, and return { ok, data } (or { ok:false, error }
    /// on failure).
    /// </summary>
    public string RunScan()
    {
        LogEntry(nameof(RunScan));
        try
        {
            var snap = HealthScanner.Capture(
                revitVersion: NullIfEmpty(_context.RevitVersion),
                revitBuild:   NullIfEmpty(_context.RevitBuild),
                revitUsername: NullIfEmpty(_context.RevitUsername),
                modelName:    NullIfEmpty(_context.ModelName),
                modelPath:    NullIfEmpty(_context.ModelPath),
                modelSizeMb:  _context.ModelSizeMb,
                warningsCount: _context.WarningsCount);
            HealthScanner.Save(snap, SnapshotPath);
            return Serialize(new { ok = true, error = (string?)null, data = snap });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Bridge.run_scan failed");
            return Serialize(new { ok = false, error = ex.Message, data = (HealthSnapshot?)null });
        }
    }

    /// <summary>
    /// Run cleanup categories and return { deleted, skipped } per-category
    /// counts. <paramref name="categoriesJson"/> is the JSON object written
    /// by the JS shim (one boolean per known key).
    /// </summary>
    public string CleanJunk(string categoriesJson)
    {
        LogEntry(nameof(CleanJunk), ("cats", categoriesJson));
        var dict = Deserialize<Dictionary<string, bool>>(categoriesJson) ?? new();
        var cats = new CleanCategories
        {
            Temp        = dict.TryGetValue("temp",        out var t) && t,
            PacCache    = dict.TryGetValue("pacCache",    out var p) && p,
            Journals    = dict.TryGetValue("journals",    out var j) && j,
            CollabCache = dict.TryGetValue("collabCache", out var c) && c,
            RecentFiles = dict.TryGetValue("recentFiles", out var r) && r,
        };
        var result = HealthCleaner.Run(cats);
        return Serialize(new { deleted = result.Deleted, skipped = result.Skipped });
    }

    public string CloseWindow()
    {
        LogEntry(nameof(CloseWindow));
        try { _closeRequested(); }
        catch (Exception ex) { Log.Warning(ex, "Bridge.close_window: close action threw"); }
        return "";
    }

    // ── helpers ────────────────────────────────────────────────────────

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, WriteOptions);

    private static T? Deserialize<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "null") return default;
        try { return JsonSerializer.Deserialize<T>(json!, ReadOptions); }
        catch (Exception ex)
        {
            Log.Warning(ex, "HealthBridge.Deserialize<{T}> failed for {Json}", typeof(T).Name, json);
            return default;
        }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static void LogEntry(string method, params (string Key, string Value)[] args)
    {
        if (args is null || args.Length == 0)
        {
            Log.Information("Bridge.{Method}", method);
            return;
        }
        var pairs = string.Join(", ", System.Linq.Enumerable.Select(args, a => $"{a.Key}={a.Value}"));
        Log.Information("Bridge.{Method} {Args}", method, pairs);
    }
}
