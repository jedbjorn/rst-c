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
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using RST.Core.Configuration;
using RST.Core.Health;
using RST.Core.Profiles;
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
    /// Surface the cleanup targets the active profile carries, falling back
    /// to <see cref="CleanupDefaults.Build"/> when no profile is loaded or
    /// the profile predates RST-031. The Health viewer renders one checkbox
    /// per enabled target.
    /// </summary>
    public string GetCleanupTargets()
    {
        LogEntry(nameof(GetCleanupTargets));
        var targets = ResolveActiveTargets();
        // Hide disabled entries from the user — admins can re-enable in
        // the Builder; users only see what the admin currently authorises.
        var visible = targets.Where(t => t.Enabled).ToArray();
        Log.Information("Bridge.get_cleanup_targets → {Visible}/{Total} (active profile-aware)",
                        visible.Length, targets.Count);
        return Serialize(visible);
    }

    /// <summary>
    /// Run the cleaner against <paramref name="selectedIdsJson"/> — JSON
    /// array of <see cref="CleanupTarget.Id"/> strings the user checked.
    /// Bridge re-resolves targets from the active profile so the curated
    /// admin list stays the source of truth (the JS layer can't mint
    /// arbitrary paths). Returns { deleted, skipped } keyed by target id.
    /// </summary>
    public string CleanJunk(string selectedIdsJson)
    {
        LogEntry(nameof(CleanJunk), ("selected", selectedIdsJson));
        var ids = Deserialize<string[]>(selectedIdsJson) ?? Array.Empty<string>();
        var idSet = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        var targets = ResolveActiveTargets()
            .Where(t => t.Enabled && idSet.Contains(string.IsNullOrEmpty(t.Id) ? t.Name : t.Id))
            .ToArray();
        var result = HealthCleaner.Run(targets);
        return Serialize(new { deleted = result.Deleted, skipped = result.Skipped });
    }

    /// <summary>
    /// Read the active profile from disk and return its CleanupTargets
    /// list, or the OOB defaults when the active profile is blank,
    /// missing, or pre-RST-031 (CleanupTargets == null).
    /// </summary>
    private static List<CleanupTarget> ResolveActiveTargets()
    {
        try
        {
            var ap = ActiveProfile.Read();
            if (!ap.IsBlank)
            {
                var entry = ProfileStore.Resolve(ap.ProfileName, ap.ProfileId);
                if (entry is not null && entry.Profile.CleanupTargets is not null)
                {
                    return entry.Profile.CleanupTargets;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "HealthBridge.ResolveActiveTargets failed; falling back to defaults");
        }
        return CleanupDefaults.Build();
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
