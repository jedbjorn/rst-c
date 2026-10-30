// LoaderBridge.cs — host object exposed to the WebView2-hosted loader UI.
//
// Method shape: every public method returns a JSON-string result (or
// empty string for void). Methods that JS calls with arguments take one
// `string` parameter per JS arg — the JS-side shim
// (Assets/pywebview-shim.js) JSON.stringifies each arg before invoking
// the host object. snake_case→PascalCase happens in the same shim.
//
// **Arity matters across COM**: WebView2's IDispatch bridge will not
// coerce a zero-arg JS call into a 1-arg C# method — it returns
// 0x80070057 E_INVALIDARG. So zero-arg-from-JS methods (get_catalog,
// get_profiles, etc.) MUST be declared zero-arg in C# too, not
// `(string _)` as a placeholder. Don't add unused parameters here.
//
// Coverage: the Builder page only (spec #16, WU3). The Loader page moved to
// the native ProfileLoaderWindow; its methods (add/remove/unload profile,
// prefs, disable preview, restore, loaded add-ins, user config) went with it.
// The shared logic lives in the RST.Core.Loader services, which this bridge
// and the WPF Loader both call: get_profiles / get_active_profile
// (ProfileLibraryService), load_profile (ProfileLoadService, the Builder's
// auto-reload), get_addin_lookup (AddinLookupService).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using RST.Core.AddIns;
using RST.Core.Configuration;
using RST.Core.Loader;
using RST.Core.Profiles;
using RST.Core.Ribbon;
using RST.Core.Scanning;
using Serilog;

namespace RST.UI.Loader;

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.AutoDual)]
public class LoaderBridge
{
    private readonly string _revitVersion;
    private readonly IReadOnlyList<ScannedCommand> _catalog;
    private readonly IReadOnlyList<string> _allTabs;
    private readonly Action _closeRequested;
    private readonly IProfileSwitchScheduler? _switchScheduler;
    private readonly ProfileLibraryService _library;
    private readonly ProfileLoadService _load;
    private readonly AddinLookupService _lookup;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public LoaderBridge(string revitVersion,
                        IReadOnlyList<ScannedCommand> catalog,
                        Action closeRequested,
                        IProfileSwitchScheduler? switchScheduler = null,
                        IReadOnlyList<string>? allTabs = null)
    {
        _revitVersion = revitVersion ?? "";
        _catalog = catalog ?? Array.Empty<ScannedCommand>();
        _allTabs = allTabs ?? Array.Empty<string>();
        _closeRequested = closeRequested ?? (() => { });
        _switchScheduler = switchScheduler;
        var paths = LoaderPaths.Default;
        var log = SerilogLoaderLog.Instance;
        _library = new ProfileLibraryService(paths, log);
        _load = new ProfileLoadService(paths, _allTabs, _switchScheduler, new FileSystemAddinPasses(_revitVersion), log);
        _lookup = new AddinLookupService(LoaderAssets.LookupPath, log);
        try { BrandingDefaults.EnsureSeeded(); }
        catch (Exception ex) { Log.Warning(ex, "BrandingDefaults.EnsureSeeded failed (non-fatal)"); }
        Log.Information("LoaderBridge ready: revit={RevitVersion}, catalog={CatalogCount} commands, tabs={TabCount}, liveSwitch={LiveSwitch}",
                        _revitVersion, _catalog.Count, _allTabs.Count, _switchScheduler is not null);
    }

    // ---- live methods --------------------------------------------------

    public string GetProfiles()
    {
        LogEntry(nameof(GetProfiles));
        var dtos = _library.List().Select(e => ToProfileDto(e.FileName, e.Profile)).ToArray();
        return Serialize(dtos);
    }

    public string GetActiveProfile()
    {
        LogEntry(nameof(GetActiveProfile));
        var ap = _library.GetActive();
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

        var result = _load.Load(new LoadRequest(profileName, profileId, disableNonRequired, hiddenTabs));
        if (!result.Ok)
        {
            return Serialize(new { ok = false, warnings = result.Warnings, restart_needed = false, failed_disables = Array.Empty<object>() });
        }
        var qa = result.Qa ?? QaCheck.Clear;
        return Serialize(new
        {
            ok = true,
            warnings = Array.Empty<string>(),
            restart_needed = result.RestartNeeded,
            failed_disables = result.FailedDisables
                .Select(f => f.Error is null ? (object)new { fileName = f.FileName } : new { fileName = f.FileName, error = f.Error })
                .ToArray(),
            qa_check = new
            {
                restored = qa.Restored.Select(r => new { tabName = r.TabName, addinFile = r.AddinFile }).ToArray(),
                not_installed = qa.NotInstalled.Select(n => new { tabName = n.TabName, addinFile = n.AddinFile, url = n.Url }).ToArray(),
                all_clear = qa.AllClear,
            },
        });
    }

    public string GetRevitVersion()
    {
        LogEntry(nameof(GetRevitVersion));
        return Serialize(_revitVersion);
    }

    public string CloseWindow()
    {
        LogEntry(nameof(CloseWindow));
        Log.Information("Bridge.close_window: window close requested");
        _closeRequested();
        return "";
    }

    // ---- builder (RST-006) ---------------------------------------------

    public string GetCatalog()
    {
        LogEntry(nameof(GetCatalog));
        try
        {
            // Host-tab promotion (port of pyRevit RST addin_panels rule):
            // tabs like "Add-Ins" host panels from many unrelated vendors.
            // For catalog grouping the host tab is uninformative — promote
            // sourcePanel to sourceTab so each vendor's panel becomes its own
            // group ("Kinship", "Enscape", …) instead of one giant "Add-Ins".
            // Original location is preserved as hostTab/literalPanel for the
            // builder UI to surface if it ever wants to.
            var promoted = 0;
            var dtos = _catalog.Select(c =>
            {
                var isHost = HostTabs.Contains(c.SourceTab);
                string? group = isHost ? (c.SourcePanel ?? c.SourceTab) : c.SourceTab;
                if (isHost && !string.IsNullOrEmpty(c.SourcePanel)) promoted++;
                return new
                {
                    id            = c.Id,
                    displayName   = c.DisplayName,
                    origin        = c.Origin.ToString(),
                    sourceTab     = group,            // catalog grouping key
                    sourcePanel   = isHost ? null : c.SourcePanel,
                    hostTab       = isHost ? c.SourceTab : null,    // literal Revit tab when promoted
                    literalPanel  = isHost ? c.SourcePanel : null,
                    addinFile     = c.AddinFile,
                    assemblyPath  = c.AssemblyPath,
                };
            }).ToArray();
            var json = Serialize(dtos);
            Log.Information("Bridge.get_catalog OK: {Count} commands ({Promoted} host-tab promoted), {Bytes} bytes JSON",
                            dtos.Length, promoted, json.Length);
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
    /// Enumerate the vendored iconpack as a logical catalogue — one
    /// entry per icon design that ships a complete seven-variant set:
    /// <c>[{ name, colors[] }]</c> with colors in canonical palette
    /// order (never filesystem order). Built on the shared Core contract
    /// (<see cref="IconPack.BuildCatalogue"/>) over Assets/icons/pack/
    /// next to RST.UI.dll. The Builder's icon picker renders one tile
    /// per design plus its color variants; the chosen value persists as
    /// <c>slot.iconFile = "pack:&lt;name&gt;_&lt;color&gt;"</c> and
    /// ProfileTabBuilder resolves it back to the exact PNG at
    /// ribbon-build time. Bare blue aliases (<c>32_&lt;name&gt;.png</c>)
    /// are compatibility files and never appear as entries. A design
    /// with an incomplete variant set is logged once and omitted, so
    /// the UI never promises a missing choice; a missing pack directory
    /// returns <c>[]</c> with a warning, preserving the no-throw
    /// COM-boundary behavior.
    /// </summary>
    public string ListIconpack() => ListIconpackCore(
        Path.Combine(
            Path.GetDirectoryName(typeof(LoaderBridge).Assembly.Location)!,
            "Assets", "icons", "pack"));

    /// <summary>
    /// Pack-dir-injectable core of <see cref="ListIconpack"/> — internal
    /// so RST.RuntimeTests can drive the missing/unreadable-directory
    /// and incomplete-set branches against temp fixtures. Production
    /// callers use the parameterless entry point.
    /// </summary>
    internal string ListIconpackCore(string assetsDir)
    {
        LogEntry(nameof(ListIconpack));
        try
        {
            var catalogue = IconPack.BuildCatalogue(assetsDir);
            if (!catalogue.DirectoryExists)
            {
                Log.Warning("Bridge.list_iconpack: missing pack dir {Dir}", assetsDir);
                return "[]";
            }
            foreach (var name in catalogue.IncompleteNames)
            {
                Log.Warning("Bridge.list_iconpack: {Name} omitted — incomplete color-variant set in {Dir}",
                            name, assetsDir);
            }
            var dtos = catalogue.Entries
                .Select(e => new { name = e.Name, colors = e.Colors })
                .ToArray();
            Log.Information("Bridge.list_iconpack OK: {Count} icons from {Dir}", dtos.Length, assetsDir);
            return Serialize(dtos);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Bridge.list_iconpack threw");
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

        // Full validation BEFORE the write — the read path (ProfileStore.List)
        // enforces these rules and silently drops violators, so a profile
        // saved without them would vanish from every picker on next load.
        var validationErrors = ProfileSerializer.Validate(profile);
        if (validationErrors.Count > 0)
        {
            Log.Warning("Bridge.save_profile: validation failed for {Name}: {Errors}",
                        profile.ProfileName, string.Join("; ", validationErrors));
            return Serialize(new { ok = false, error = "Profile validation failed: " + string.Join("; ", validationErrors) });
        }

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
    /// Export the in-memory profile to a user-chosen .zip path (Save
    /// dialog). RST-018: bundles the resolved branding (per-profile if
    /// set, else machine-wide default) so the receiver gets a true
    /// copy. Does NOT touch the local profiles store.
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

        var safeName = string.IsNullOrWhiteSpace(profile.ProfileName)
            ? "rst_profile"
            : Path.GetFileNameWithoutExtension(ProfileStore.CanonicalFileName(profile));
        var suggested = safeName + ".zip";
        var path = FileDialogBridge.SaveZip(suggested);
        if (string.IsNullOrEmpty(path))
        {
            Log.Information("Bridge.export_profile: dialog cancelled");
            return Serialize(new { ok = false, error = "cancelled" });
        }

        // Snapshot the resolved branding for this profile. Per-profile
        // override wins if set; otherwise picks up the machine-wide
        // default the admin has installed (BrandingDefaults.Resolve).
        var (logoPath, url) = BrandingDefaults.Resolve(profile);

        try
        {
            using var fs = File.Create(path!);
            ProfileZip.Pack(profile, logoPath, url, fs);
            Log.Information("Bridge.export_profile OK: name={Name} hasLogo={HasLogo} → {Path}",
                            profile.ProfileName, logoPath is not null, path);
            return Serialize(new { ok = true, path });
        }
        catch (IOException ex)
        {
            Log.Error(ex, "Bridge.export_profile: write failed for {Path}", path);
            return Serialize(new { ok = false, error = "Write failed: " + ex.Message });
        }
    }

    /// <summary>
    /// Open a file-dialog for company branding logo selection. The picked
    /// file is copied to %AppData%\RST\branding.png (writer wins) and the
    /// machine-wide default logo is updated for every profile that does
    /// not carry its own override.
    /// Returns { ok:true, fileName:"branding.png", source:&lt;picked-path&gt; }
    /// on success, { ok:false, error:"cancelled" } on cancel,
    /// { ok:false, error:&lt;msg&gt; } on copy failure.
    /// </summary>
    public string PickLogoFile()
    {
        LogEntry(nameof(PickLogoFile));
        var source = FileDialogBridge.OpenImage();
        if (string.IsNullOrEmpty(source))
        {
            Log.Information("Bridge.pick_logo_file: dialog cancelled");
            return Serialize(new { ok = false, error = "cancelled" });
        }
        try
        {
            AppDataPaths.EnsureCreated();
            // Resize to RST.Engine PanelStyling.BrandingPanelSizePx (85)
            // and PNG-encode. Square non-uniform scale matches the panel
            // shape — RST-021 fixed the panel at 85×85 with the image
            // filling the body, so encoding to that exact size avoids
            // runtime up/down-sampling at every ribbon paint.
            try
            {
                EncodeAsBranding(source!, BrandingDefaults.LogoPath!, targetSize: BrandingDefaults.PanelSizePx);
                Log.Information("Bridge.pick_logo_file OK (resized {Size}x{Size} PNG): {Source} → {Dest}",
                                BrandingDefaults.PanelSizePx, BrandingDefaults.PanelSizePx, source, BrandingDefaults.LogoPath);
            }
            catch (Exception ex)
            {
                // Fallback: raw copy. Mirrors upstream behaviour when PIL
                // is missing — better to ship something the user picked
                // than reject the upload outright.
                Log.Warning(ex, "Bridge.pick_logo_file: encode/resize failed; falling back to raw copy");
                File.Copy(source, BrandingDefaults.LogoPath, overwrite: true);
                Log.Information("Bridge.pick_logo_file OK (raw copy): {Source} → {Dest}",
                                source, BrandingDefaults.LogoPath);
            }
            return Serialize(new { ok = true, fileName = BrandingDefaults.LogoFileName, source });
        }
        catch (IOException ex)
        {
            Log.Error(ex, "Bridge.pick_logo_file: copy failed {Source} → {Dest}", source, BrandingDefaults.LogoPath);
            return Serialize(new { ok = false, error = "Copy failed: " + ex.Message });
        }
    }

    /// <summary>
    /// Decode <paramref name="sourcePath"/> (PNG or JPEG), force-resize to
    /// <paramref name="targetSize"/>×<paramref name="targetSize"/>, and
    /// PNG-encode the result to <paramref name="destPath"/>. Throws on
    /// any failure — caller decides whether to fall back.
    /// </summary>
    private static void EncodeAsBranding(string sourcePath, string destPath, int targetSize) =>
        RST.UI.Builder.BrandingLogo.Encode(sourcePath, destPath, targetSize);

    /// <summary>
    /// Read the per-machine company branding (logo + URL). Returns
    /// { hasLogo:bool, fileName:"branding.png"|null, url:string|null }.
    /// fileName is non-null only when %AppData%\RST\branding.png exists.
    /// </summary>
    public string LoadDefaultBranding()
    {
        LogEntry(nameof(LoadDefaultBranding));
        var defaults = BrandingDefaults.Load();
        var hasLogo = BrandingDefaults.HasLogo;
        Log.Debug("Bridge.load_default_branding → hasLogo={HasLogo}, urlSet={UrlSet}", hasLogo, !string.IsNullOrEmpty(defaults.Url));
        return Serialize(new
        {
            hasLogo,
            fileName = hasLogo ? BrandingDefaults.LogoFileName : null,
            url = defaults.Url,
        });
    }

    /// <summary>
    /// Update the per-machine branding URL. Empty/whitespace clears it.
    /// Returns { ok:true } on success, { ok:false, error:&lt;msg&gt; } on
    /// write failure.
    /// </summary>
    public string SaveDefaultBrandingUrl(string urlJson)
    {
        LogEntry(nameof(SaveDefaultBrandingUrl));
        var url = Deserialize<string>(urlJson);
        try
        {
            var defaults = BrandingDefaults.Load();
            defaults.Url = string.IsNullOrWhiteSpace(url) ? null : url!.Trim();
            defaults.Save();
            Log.Information("Bridge.save_default_branding_url OK: urlSet={UrlSet}", !string.IsNullOrEmpty(defaults.Url));
            return Serialize(new { ok = true });
        }
        catch (IOException ex)
        {
            Log.Error(ex, "Bridge.save_default_branding_url: write failed");
            return Serialize(new { ok = false, error = "Write failed: " + ex.Message });
        }
    }

    /// <summary>
    /// Clear the per-machine logo (delete %AppData%\RST\branding.png).
    /// Returns { ok:true } on success or when file did not exist;
    /// { ok:false, error:&lt;msg&gt; } on delete failure.
    /// </summary>
    public string ClearDefaultLogo()
    {
        LogEntry(nameof(ClearDefaultLogo));
        try
        {
            if (File.Exists(BrandingDefaults.LogoPath))
            {
                File.Delete(BrandingDefaults.LogoPath);
                Log.Information("Bridge.clear_default_logo OK: deleted {Path}", BrandingDefaults.LogoPath);
            }
            return Serialize(new { ok = true });
        }
        catch (IOException ex)
        {
            Log.Error(ex, "Bridge.clear_default_logo: delete failed");
            return Serialize(new { ok = false, error = "Delete failed: " + ex.Message });
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

    /// <summary>
    /// Curated addin registry: name → {displayName, file, url}. Vendored
    /// from upstream pyRevit RST/lookup/addin_lookup.json under
    /// Assets/lookup/ so it ships with the bundle. Read once; the JSON is
    /// small (~5KB, ~30 entries) so we don't cache.
    ///
    /// Used by the Builder at profile-create time to bake addinFile + url
    /// into profile.requiredAddins (admin's machine consults this; the
    /// resulting profile is self-contained and travels to user machines
    /// without the lookup). Returns an empty dict if the bundled file is
    /// missing — no enrichment, profiles still save with tab names alone.
    /// </summary>
    public string GetAddinLookup()
    {
        LogEntry(nameof(GetAddinLookup));
        return _lookup.ReadRawJson();
    }

    /// <summary>
    /// Distinct non-contextual ribbon tab titles, snapshotted when the
    /// window opened. Drives the Builder's RST Presets tab toggles
    /// and its required-add-in detection. Snapshot is fine
    /// because the modal blocks the UI thread, so the ribbon can't
    /// change while the picker is shown.
    /// </summary>
    public string GetAllTabs()
    {
        LogEntry(nameof(GetAllTabs));
        return Serialize(_allTabs);
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
        presets = p.Presets,
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
