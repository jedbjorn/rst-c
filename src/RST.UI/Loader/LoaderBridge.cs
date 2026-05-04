// LoaderBridge.cs — host object exposed to the WebView2-hosted loader UI.
//
// Method shape: every public method takes JSON-string args and returns a
// JSON-string result (or empty string for void). The JS-side shim
// (Assets/pywebview-shim.js) snake_case→PascalCase translates names,
// JSON.stringifies inbound args and JSON.parses returns. This keeps the
// COM surface trivial — only string IO crosses the boundary — while
// preserving the legacy `pywebview.api.foo(args).then(r => ...)` calls
// in the vendored HTML unchanged.
//
// Coverage:
//   live    — get_profiles, get_active_profile, load_profile, add_profile,
//             remove_profile, unload_profile, close_window, get_revit_version,
//             get_catalog, save_profile, export_profile (RST-006: builder)
//   stubbed — get_addin_lookup, get_loaded_addins, get_all_tabs,
//             get_user_config, get_disable_preview, restore_addins
//             (return empty/safe defaults; their features land in later flags)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using RST.Core.Configuration;
using RST.Core.Profiles;
using RST.Core.Scanning;
using Serilog;

namespace RST.UI.Loader;

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.AutoDual)]
public class LoaderBridge
{
    private readonly string _revitVersion;
    private readonly IReadOnlyList<ScannedCommand> _catalog;
    private readonly Action _closeRequested;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public LoaderBridge(string revitVersion, IReadOnlyList<ScannedCommand> catalog, Action closeRequested)
    {
        _revitVersion = revitVersion ?? "";
        _catalog = catalog ?? Array.Empty<ScannedCommand>();
        _closeRequested = closeRequested ?? (() => { });
        Log.Information("LoaderBridge ready: revit={RevitVersion}, catalog={CatalogCount} commands",
                        _revitVersion, _catalog.Count);
    }

    // ---- live methods --------------------------------------------------

    public string GetProfiles(string _)
    {
        LogEntry(nameof(GetProfiles));
        var entries = ProfileStore.List();
        var dtos = entries.Select(e => ToProfileDto(e.FileName, e.Profile)).ToArray();
        Log.Information("Bridge.get_profiles → {Count} profiles from {Dir}",
                        dtos.Length, AppDataPaths.ProfilesDir);
        return Serialize(dtos);
    }

    public string GetActiveProfile(string _)
    {
        LogEntry(nameof(GetActiveProfile));
        var ap = ActiveProfile.Read();
        if (ap.IsBlank)
        {
            Log.Debug("Bridge.get_active_profile → blank");
            return Serialize(new
            {
                id = (string?)null,
                name = (string?)null,
                hidden_tabs = Array.Empty<string>(),
                disable_non_required = false,
            });
        }
        Log.Information("Bridge.get_active_profile → name={Name} id={Id} hiddenTabs={Hidden} disableNonRequired={Disable}",
                        ap.ProfileName, ap.ProfileId, ap.HiddenTabs?.Length ?? 0, ap.DisableNonRequired);
        return Serialize(new
        {
            id = ap.ProfileId,
            name = ap.ProfileName,
            hidden_tabs = ap.HiddenTabs,
            disable_non_required = ap.DisableNonRequired,
        });
    }

    public string LoadProfile(string profileNameJson, string disableNonRequiredJson,
                              string revitVersionJson, string hiddenTabsJson, string profileIdJson)
    {
        LogEntry(nameof(LoadProfile),
                 ("name", profileNameJson), ("disable", disableNonRequiredJson),
                 ("revit", revitVersionJson), ("hiddenTabs", hiddenTabsJson),
                 ("id", profileIdJson));
        var profileName = Deserialize<string>(profileNameJson) ?? "";
        var disableNonRequired = Deserialize<bool>(disableNonRequiredJson);
        var hiddenTabs = Deserialize<string[]>(hiddenTabsJson) ?? Array.Empty<string>();
        var profileId = Deserialize<string?>(profileIdJson);

        var entry = ProfileStore.Resolve(profileName, profileId);
        if (entry is null)
        {
            Log.Warning("Bridge.load_profile: profile not found name={Name} id={Id}", profileName, profileId);
            return Serialize(new { ok = false, warnings = new[] { "Profile not found: " + profileName }, restart_needed = false, failed_disables = Array.Empty<object>() });
        }

        var ap = ActiveProfile.FromProfile(entry.Profile, entry.FileName, hiddenTabs, disableNonRequired);
        try { ap.Write(); }
        catch (IOException ex)
        {
            Log.Error(ex, "Bridge.load_profile: failed writing active profile {Name}", profileName);
            return Serialize(new { ok = false, warnings = new[] { "Failed to write active profile: " + ex.Message }, restart_needed = false, failed_disables = Array.Empty<object>() });
        }

        Log.Information("Bridge.load_profile OK: name={Name} id={Id} disableNonRequired={Disable} hiddenTabs={HiddenTabs}",
                        profileName, profileId, disableNonRequired, hiddenTabs.Length);
        // disable_non_required is wired into ActiveProfile but not yet executed —
        // the addin-disable subsystem lands in a later flag. UI shows the toggle;
        // we just record the user's intent for now.
        return Serialize(new
        {
            ok = true,
            warnings = Array.Empty<string>(),
            restart_needed = true,
            failed_disables = Array.Empty<object>(),
        });
    }

    public string AddProfile(string _)
    {
        LogEntry(nameof(AddProfile));
        // File-dialog import. Marshalled to UI thread by the host window.
        var path = FileDialogBridge.OpenJson();
        if (string.IsNullOrEmpty(path))
        {
            Log.Information("Bridge.add_profile: dialog cancelled");
            return Serialize(new { ok = false, error = "cancelled" });
        }

        Profile profile;
        try
        {
            using var fs = File.OpenRead(path!);
            profile = ProfileSerializer.Read(fs);
        }
        catch (ProfileLoadException ex)
        {
            Log.Warning(ex, "Bridge.add_profile: profile load failed for {Path}", path);
            return Serialize(new { ok = false, error = ex.Message });
        }
        catch (IOException ex)
        {
            Log.Error(ex, "Bridge.add_profile: read failed for {Path}", path);
            return Serialize(new { ok = false, error = "Read failed: " + ex.Message });
        }

        var fileName = ProfileStore.Save(profile);
        Log.Information("Bridge.add_profile OK: imported {Source} → {FileName}", path, fileName);
        return Serialize(new { ok = true, profile = ToProfileDto(fileName, profile) });
    }

    public string RemoveProfile(string profileNameJson, string profileIdJson)
    {
        LogEntry(nameof(RemoveProfile), ("name", profileNameJson), ("id", profileIdJson));
        var profileName = Deserialize<string>(profileNameJson) ?? "";
        var profileId = Deserialize<string?>(profileIdJson);

        var entry = ProfileStore.Resolve(profileName, profileId);
        if (entry is null)
        {
            Log.Warning("Bridge.remove_profile: not found name={Name} id={Id}", profileName, profileId);
            return Serialize(new { ok = false, error = "Profile not found" });
        }

        ProfileStore.Delete(entry.FileName);

        // If the deleted profile was active, fall back to the blank stub.
        var ap = ActiveProfile.Read();
        var wasActive = !ap.IsBlank && (
            string.Equals(ap.ProfileId, entry.Profile.Id, StringComparison.Ordinal) ||
            string.Equals(ap.ProfileName, entry.Profile.ProfileName, StringComparison.Ordinal));
        if (wasActive) ActiveProfile.WriteBlank();
        Log.Information("Bridge.remove_profile OK: {FileName} (wasActive={WasActive})", entry.FileName, wasActive);
        return Serialize(new { ok = true });
    }

    public string UnloadProfile(string _)
    {
        LogEntry(nameof(UnloadProfile));
        ActiveProfile.WriteBlank();
        Log.Information("Bridge.unload_profile OK");
        return Serialize(new { ok = true });
    }

    public string GetRevitVersion(string _)
    {
        LogEntry(nameof(GetRevitVersion));
        return Serialize(_revitVersion);
    }

    public string CloseWindow(string _)
    {
        LogEntry(nameof(CloseWindow));
        Log.Information("Bridge.close_window: window close requested");
        _closeRequested();
        return "";
    }

    // ---- builder (RST-006) ---------------------------------------------

    public string GetCatalog(string _)
    {
        LogEntry(nameof(GetCatalog));
        try
        {
            var dtos = _catalog.Select(c => new
            {
                id           = c.Id,
                displayName  = c.DisplayName,
                origin       = c.Origin.ToString(),
                sourceTab    = c.SourceTab,
                sourcePanel  = c.SourcePanel,
                addinFile    = c.AddinFile,
                assemblyPath = c.AssemblyPath,
            }).ToArray();
            var json = Serialize(dtos);
            Log.Information("Bridge.get_catalog OK: {Count} commands, {Bytes} bytes JSON", dtos.Length, json.Length);
            return json;
        }
        catch (Exception ex)
        {
            // Bridge methods that throw across the COM boundary become opaque
            // HRESULT failures on the JS side — log here so the cause is in
            // Serilog rather than only console-logged via the proxy's catch.
            Log.Error(ex, "Bridge.get_catalog threw");
            return "[]";
        }
    }

    /// <summary>
    /// Persist a profile to disk (assigns Id + ExportDate when missing).
    /// Returns { ok, fileName, profile } on success.
    /// </summary>
    public string SaveProfile(string profileJson)
    {
        LogEntry(nameof(SaveProfile), ("profile", profileJson));
        Profile? profile;
        try { profile = JsonSerializer.Deserialize<Profile>(profileJson); }
        catch (JsonException ex)
        {
            Log.Warning(ex, "Bridge.save_profile: invalid profile JSON ({Bytes} bytes)", profileJson?.Length ?? 0);
            return Serialize(new { ok = false, error = "Invalid profile JSON: " + ex.Message });
        }
        if (profile is null)
        {
            Log.Warning("Bridge.save_profile: empty profile");
            return Serialize(new { ok = false, error = "Empty profile" });
        }
        if (string.IsNullOrWhiteSpace(profile.ProfileName))
        {
            Log.Warning("Bridge.save_profile: missing profile name");
            return Serialize(new { ok = false, error = "Profile name required" });
        }
        if (string.IsNullOrWhiteSpace(profile.Tab))
        {
            Log.Warning("Bridge.save_profile: missing tab for {Name}", profile.ProfileName);
            return Serialize(new { ok = false, error = "Tab name required" });
        }

        if (string.IsNullOrEmpty(profile.Id)) profile.Id = Guid.NewGuid().ToString();
        if (string.IsNullOrEmpty(profile.ExportDate))
            profile.ExportDate = DateTime.UtcNow.ToString("yyyy-MM-dd");

        try
        {
            var fileName = ProfileStore.Save(profile);
            Log.Information("Bridge.save_profile OK: name={Name} id={Id} panels={Panels} stacks={Stacks} → {FileName}",
                            profile.ProfileName, profile.Id, profile.Panels.Count, profile.Stacks.Count, fileName);
            return Serialize(new { ok = true, fileName, profile = ToProfileDto(fileName, profile) });
        }
        catch (IOException ex)
        {
            Log.Error(ex, "Bridge.save_profile: write failed for {Name}", profile.ProfileName);
            return Serialize(new { ok = false, error = "Write failed: " + ex.Message });
        }
    }

    /// <summary>
    /// Export the in-memory profile to a user-chosen JSON path (Save
    /// dialog). Does NOT touch the local profiles store.
    /// </summary>
    public string ExportProfile(string profileJson)
    {
        LogEntry(nameof(ExportProfile), ("profile", profileJson));
        Profile? profile;
        try { profile = JsonSerializer.Deserialize<Profile>(profileJson); }
        catch (JsonException ex)
        {
            Log.Warning(ex, "Bridge.export_profile: invalid profile JSON ({Bytes} bytes)", profileJson?.Length ?? 0);
            return Serialize(new { ok = false, error = "Invalid profile JSON: " + ex.Message });
        }
        if (profile is null)
        {
            Log.Warning("Bridge.export_profile: empty profile");
            return Serialize(new { ok = false, error = "Empty profile" });
        }

        var suggested = string.IsNullOrWhiteSpace(profile.ProfileName)
            ? "rst_profile.json"
            : ProfileStore.CanonicalFileName(profile);
        var path = FileDialogBridge.SaveJson(suggested);
        if (string.IsNullOrEmpty(path))
        {
            Log.Information("Bridge.export_profile: dialog cancelled");
            return Serialize(new { ok = false, error = "cancelled" });
        }

        try
        {
            using var fs = File.Create(path!);
            ProfileSerializer.Write(profile, fs);
            Log.Information("Bridge.export_profile OK: name={Name} → {Path}", profile.ProfileName, path);
            return Serialize(new { ok = true, path });
        }
        catch (IOException ex)
        {
            Log.Error(ex, "Bridge.export_profile: write failed for {Path}", path);
            return Serialize(new { ok = false, error = "Write failed: " + ex.Message });
        }
    }

    /// <summary>
    /// Browser → Serilog. Lets the WebView2-side JS write into the same
    /// rst_*.log file as everything else, so render outcomes / catches
    /// can be diagnosed without F12-ing dev tools on a live install.
    /// Level: "info" | "warn" | "error" (anything else maps to info).
    /// </summary>
    public string LogEvent(string levelJson, string messageJson, string payloadJson)
    {
        // Intentionally no LogEntry — this method IS the entry, and would
        // recurse-confuse the log if it logged itself.
        var level = (Deserialize<string>(levelJson) ?? "info").ToLowerInvariant();
        var message = Deserialize<string>(messageJson) ?? "";
        var payload = payloadJson;   // raw JSON — keep as-is so the structured field stays parseable
        if (string.IsNullOrWhiteSpace(payload) || payload == "null" || payload == "undefined") payload = "";

        switch (level)
        {
            case "error": Log.Error("UI: {Message} {Payload}", message, payload); break;
            case "warn":
            case "warning": Log.Warning("UI: {Message} {Payload}", message, payload); break;
            default: Log.Information("UI: {Message} {Payload}", message, payload); break;
        }
        return "";
    }

    // ---- stubs (features land in later flags) --------------------------

    public string GetAddinLookup(string _)
    {
        LogEntry(nameof(GetAddinLookup));
        return Serialize(new Dictionary<string, object>());
    }
    public string GetLoadedAddins(string _)
    {
        LogEntry(nameof(GetLoadedAddins));
        return Serialize(Array.Empty<object>());
    }
    public string GetAllTabs(string _)
    {
        LogEntry(nameof(GetAllTabs));
        return Serialize(Array.Empty<string>());
    }
    public string GetUserConfig(string _)
    {
        LogEntry(nameof(GetUserConfig));
        return Serialize(new { addins = new Dictionary<string, object>() });
    }

    public string GetDisablePreview(string _)
    {
        LogEntry(nameof(GetDisablePreview));
        return Serialize(new
        {
            staying = Array.Empty<object>(),
            disabling = Array.Empty<object>(),
            tryDisable = Array.Empty<object>(),
            skipped = Array.Empty<object>(),
        });
    }

    public string RestoreAddins(string _)
    {
        LogEntry(nameof(RestoreAddins));
        return Serialize(new
        {
            ok = true,
            restart_needed = false,
            restored = Array.Empty<string>(),
        });
    }

    // ---- helpers -------------------------------------------------------

    private static object ToProfileDto(string fileName, Profile p) => new
    {
        schemaVersion = p.SchemaVersion,
        id = p.Id,
        profile = p.ProfileName,
        tab = p.Tab,
        min_version = p.MinVersion,
        exportDate = p.ExportDate,
        panelOpacity = p.PanelOpacity,
        requiredAddins = p.RequiredAddins,
        hideRules = p.HideRules,
        stacks = p.Stacks,
        panels = p.Panels,
        branding = p.Branding,
        _filename = fileName,
    };

    private static string Serialize(object? value) =>
        JsonSerializer.Serialize(value, WriteOptions);

    private static T? Deserialize<T>(string json)
    {
        if (string.IsNullOrEmpty(json) || json == "null" || json == "undefined")
            return default;
        try { return JsonSerializer.Deserialize<T>(json); }
        catch (JsonException) { return default; }
    }

    /// <summary>
    /// Entry-time DEBUG line for a bridge method. Inputs are truncated to
    /// keep the log readable when callers send large profile blobs.
    /// </summary>
    private static void LogEntry(string method, params (string name, string? value)[] args)
    {
        if (!Log.IsEnabled(Serilog.Events.LogEventLevel.Debug)) return;
        if (args.Length == 0)
        {
            Log.Debug("Bridge.{Method} called", method);
            return;
        }
        var summarised = new string[args.Length];
        for (int i = 0; i < args.Length; i++)
        {
            var raw = args[i].value ?? "";
            var trimmed = raw.Length > 200 ? raw.Substring(0, 200) + "…(" + raw.Length + "B)" : raw;
            summarised[i] = args[i].name + "=" + trimmed;
        }
        Log.Debug("Bridge.{Method} called: {Args}", method, string.Join(", ", summarised));
    }
}
