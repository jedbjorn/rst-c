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
// Coverage in RST-004:
//   live    — get_profiles, get_active_profile, load_profile, add_profile,
//             remove_profile, unload_profile, close_window, get_revit_version
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

namespace RST.UI.Loader;

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.AutoDual)]
public class LoaderBridge
{
    private readonly string _revitVersion;
    private readonly Action _closeRequested;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public LoaderBridge(string revitVersion, Action closeRequested)
    {
        _revitVersion = revitVersion ?? "";
        _closeRequested = closeRequested ?? (() => { });
    }

    // ---- live methods --------------------------------------------------

    public string GetProfiles(string _)
    {
        var entries = ProfileStore.List();
        var dtos = entries.Select(e => ToProfileDto(e.FileName, e.Profile)).ToArray();
        return Serialize(dtos);
    }

    public string GetActiveProfile(string _)
    {
        var ap = ActiveProfile.Read();
        if (ap.IsBlank)
        {
            return Serialize(new
            {
                id = (string?)null,
                name = (string?)null,
                hidden_tabs = Array.Empty<string>(),
                disable_non_required = false,
            });
        }
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
        var profileName = Deserialize<string>(profileNameJson) ?? "";
        var disableNonRequired = Deserialize<bool>(disableNonRequiredJson);
        var hiddenTabs = Deserialize<string[]>(hiddenTabsJson) ?? Array.Empty<string>();
        var profileId = Deserialize<string?>(profileIdJson);

        var entry = ProfileStore.Resolve(profileName, profileId);
        if (entry is null)
            return Serialize(new { ok = false, warnings = new[] { "Profile not found: " + profileName }, restart_needed = false, failed_disables = Array.Empty<object>() });

        var ap = ActiveProfile.FromProfile(entry.Profile, entry.FileName, hiddenTabs, disableNonRequired);
        try { ap.Write(); }
        catch (IOException ex)
        {
            return Serialize(new { ok = false, warnings = new[] { "Failed to write active profile: " + ex.Message }, restart_needed = false, failed_disables = Array.Empty<object>() });
        }

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
        // File-dialog import. Marshalled to UI thread by the host window.
        var path = FileDialogBridge.OpenJson();
        if (string.IsNullOrEmpty(path))
            return Serialize(new { ok = false, error = "cancelled" });

        Profile profile;
        try
        {
            using var fs = File.OpenRead(path!);
            profile = ProfileSerializer.Read(fs);
        }
        catch (ProfileLoadException ex)
        {
            return Serialize(new { ok = false, error = ex.Message });
        }
        catch (IOException ex)
        {
            return Serialize(new { ok = false, error = "Read failed: " + ex.Message });
        }

        var fileName = ProfileStore.Save(profile);
        return Serialize(new { ok = true, profile = ToProfileDto(fileName, profile) });
    }

    public string RemoveProfile(string profileNameJson, string profileIdJson)
    {
        var profileName = Deserialize<string>(profileNameJson) ?? "";
        var profileId = Deserialize<string?>(profileIdJson);

        var entry = ProfileStore.Resolve(profileName, profileId);
        if (entry is null)
            return Serialize(new { ok = false, error = "Profile not found" });

        ProfileStore.Delete(entry.FileName);

        // If the deleted profile was active, fall back to the blank stub.
        var ap = ActiveProfile.Read();
        if (!ap.IsBlank && (
            string.Equals(ap.ProfileId, entry.Profile.Id, StringComparison.Ordinal) ||
            string.Equals(ap.ProfileName, entry.Profile.ProfileName, StringComparison.Ordinal)))
        {
            ActiveProfile.WriteBlank();
        }
        return Serialize(new { ok = true });
    }

    public string UnloadProfile(string _)
    {
        ActiveProfile.WriteBlank();
        return Serialize(new { ok = true });
    }

    public string GetRevitVersion(string _) => Serialize(_revitVersion);

    public string CloseWindow(string _)
    {
        _closeRequested();
        return "";
    }

    // ---- stubs (features land in later flags) --------------------------

    public string GetAddinLookup(string _)    => Serialize(new Dictionary<string, object>());
    public string GetLoadedAddins(string _)   => Serialize(Array.Empty<object>());
    public string GetAllTabs(string _)        => Serialize(Array.Empty<string>());
    public string GetUserConfig(string _)     => Serialize(new { addins = new Dictionary<string, object>() });

    public string GetDisablePreview(string _) => Serialize(new
    {
        staying = Array.Empty<object>(),
        disabling = Array.Empty<object>(),
        tryDisable = Array.Empty<object>(),
        skipped = Array.Empty<object>(),
    });

    public string RestoreAddins(string _) => Serialize(new
    {
        ok = true,
        restart_needed = false,
        restored = Array.Empty<string>(),
    });

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
}
