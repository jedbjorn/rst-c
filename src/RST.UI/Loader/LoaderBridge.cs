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
// Coverage:
//   live    — get_profiles, get_active_profile, load_profile, add_profile,
//             remove_profile, unload_profile, close_window, get_revit_version,
//             get_catalog, save_profile, export_profile (RST-006: builder)
//   stubbed — get_user_config (returns empty/safe default; lands in a
//             later flag)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RST.Core.AddIns;
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
    private readonly IReadOnlyList<string> _allTabs;
    private readonly Action _closeRequested;
    private readonly IProfileSwitchScheduler? _switchScheduler;

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
        try { BrandingDefaults.EnsureSeeded(); }
        catch (Exception ex) { Log.Warning(ex, "BrandingDefaults.EnsureSeeded failed (non-fatal)"); }
        Log.Information("LoaderBridge ready: revit={RevitVersion}, catalog={CatalogCount} commands, tabs={TabCount}, liveSwitch={LiveSwitch}",
                        _revitVersion, _catalog.Count, _allTabs.Count, _switchScheduler is not null);
    }

    // ---- live methods --------------------------------------------------

    public string GetProfiles()
    {
        LogEntry(nameof(GetProfiles));
        var entries = ProfileStore.List();
        var dtos = entries.Select(e => ToProfileDto(e.FileName, e.Profile)).ToArray();
        Log.Information("Bridge.get_profiles → {Count} profiles from {Dir}",
                        dtos.Length, AppDataPaths.ProfilesDir);
        return Serialize(dtos);
    }

    public string GetActiveProfile()
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

        // Disable non-required addins by renaming .addin → .addin.RSTdisabled.
        // Effective on the next Revit launch — already-loaded DLLs stay
        // resident this session, so any disable forces restart_needed=true
        // regardless of live-switch availability.
        var failedDisables = new List<object>();
        bool disableForcedRestart = false;
        if (disableNonRequired)
        {
            try
            {
                var result = AddinDisabler.DisableNonRequired(
                    _revitVersion,
                    entry.Profile.RequiredAddins,
                    onError: (path, ex) => Log.Warning(ex, "AddinDisabler: rename failed for {Path}", path));
                Log.Information("Bridge.load_profile: disabled {Count} addins, skippedReadOnly={ReadOnly}, alreadyDisabled={Already}, failed={Failed}",
                                result.DisabledCount, result.SkippedReadOnly, result.SkippedAlreadyDisabled, result.Failed);
                foreach (var fname in result.FailedFiles)
                    failedDisables.Add(new { fileName = fname });
                if (result.DisabledCount > 0) disableForcedRestart = true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Bridge.load_profile: AddinDisabler threw for {Name}", profileName);
                failedDisables.Add(new { fileName = "(disable subsystem failure)", error = ex.Message });
                disableForcedRestart = true;   // be conservative — assume DLLs need a restart
            }
        }

        // RST-020: schedule a live ribbon rebuild on the Revit UI thread.
        // The ExternalEvent fires after the modal Loader window closes
        // (Revit's main loop resumes pumping then). When no scheduler is
        // wired (e.g. running outside Revit), we fall back to the legacy
        // restart-required behavior so the UI can still surface that.
        bool restartNeeded = true;
        if (_switchScheduler is not null)
        {
            try
            {
                _switchScheduler.Schedule(entry.Profile);
                restartNeeded = false;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Bridge.load_profile: switch scheduler failed; falling back to restart-required for {Name}", profileName);
                restartNeeded = true;
            }
        }
        if (disableForcedRestart) restartNeeded = true;

        return Serialize(new
        {
            ok = true,
            warnings = Array.Empty<string>(),
            restart_needed = restartNeeded,
            failed_disables = failedDisables,
        });
    }

    public string AddProfile()
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

    public string UnloadProfile()
    {
        LogEntry(nameof(UnloadProfile));
        ActiveProfile.WriteBlank();
        // RST-020: tear down the live profile tab too. Schedule(null) is
        // the agreed-upon "unload" signal — ProfileTabBuilder removes its
        // panels and our created tab without rebuilding.
        if (_switchScheduler is not null)
        {
            try { _switchScheduler.Schedule(null); }
            catch (Exception ex) { Log.Error(ex, "Bridge.unload_profile: switch scheduler failed"); }
        }
        Log.Information("Bridge.unload_profile OK");
        return Serialize(new { ok = true });
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
            // Try: load → resize 48x48 → PNG-encode → save. Matches upstream
            // tab_creator.py PickLogo (PIL.Image.resize((48,48), LANCZOS) +
            // save as PNG). Branding panel renders at ~96px so 48x48 is the
            // smallest size that holds up at HiDPI without bloating memory
            // — and forces a known format so any downstream code (panel
            // background brush, etc.) doesn't have to sniff JPEG vs PNG.
            try
            {
                EncodeAsBranding(source!, BrandingDefaults.LogoPath!, targetSize: 48);
                Log.Information("Bridge.pick_logo_file OK (resized 48x48 PNG): {Source} → {Dest}",
                                source, BrandingDefaults.LogoPath);
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
    private static void EncodeAsBranding(string sourcePath, string destPath, int targetSize)
    {
        var src = new BitmapImage();
        src.BeginInit();
        src.UriSource = new Uri(sourcePath, UriKind.Absolute);
        // OnLoad detaches the BitmapImage from the source file so we can
        // safely overwrite destPath even when src and dest are the same
        // path (re-pick the existing logo).
        src.CacheOption = BitmapCacheOption.OnLoad;
        src.EndInit();
        if (src.CanFreeze) src.Freeze();

        // Non-uniform scale to force exact targetSize × targetSize, mirroring
        // PIL.Image.resize((48,48)) which distorts non-square inputs.
        var scaleX = (double)targetSize / Math.Max(1, src.PixelWidth);
        var scaleY = (double)targetSize / Math.Max(1, src.PixelHeight);
        var resized = new TransformedBitmap(src, new ScaleTransform(scaleX, scaleY));
        if (resized.CanFreeze) resized.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(resized));
        using var fs = File.Create(destPath);
        encoder.Save(fs);
    }

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
        try
        {
            var assetsDir = Path.Combine(
                Path.GetDirectoryName(typeof(LoaderBridge).Assembly.Location)!,
                "Assets", "lookup");
            var path = Path.Combine(assetsDir, "addin_lookup.json");
            if (!File.Exists(path))
            {
                Log.Warning("Bridge.get_addin_lookup: {Path} missing — returning empty dict", path);
                return Serialize(new Dictionary<string, object>());
            }
            // Pass through verbatim — the JSON is already in the shape the
            // JS expects ({key: {displayName, file, url}, ...}). Parsing
            // and re-serializing would lose nothing but cost a round-trip.
            var raw = File.ReadAllText(path);
            Log.Information("Bridge.get_addin_lookup: {Bytes} bytes from {Path}", raw.Length, path);
            return raw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Bridge.get_addin_lookup: read failed; returning empty dict");
            return Serialize(new Dictionary<string, object>());
        }
    }

    /// <summary>
    /// Surface every .addin (and .addin.RSTdisabled) we can find under
    /// the running Revit version's addin search paths. The UI uses this
    /// for the "available add-ins" picker in the Builder and for the
    /// required-addins matching display. Each entry exposes the canonical
    /// file name, the first AddInId/Assembly we parsed out, and a flag
    /// indicating whether it's currently disabled on disk.
    /// </summary>
    public string GetLoadedAddins()
    {
        LogEntry(nameof(GetLoadedAddins));
        var manifests = AddinDirectoryScanner.Scan(_revitVersion,
            onSkip: (p, ex) => Log.Debug(ex, "AddinDirectoryScanner: skipped {Path}", p));
        var dtos = manifests.Select(m =>
        {
            var first = m.Entries.Count > 0 ? m.Entries[0] : null;
            return new
            {
                fileName = m.FileName,
                filePath = m.FilePath,
                isDisabled = m.IsDisabled,
                displayName = !string.IsNullOrWhiteSpace(first?.Name)
                    ? first!.Name
                    : Path.GetFileNameWithoutExtension(m.FileName),
                addinId = first?.AddinId,
                assembly = first?.AssemblyPath,
            };
        }).ToArray();
        Log.Information("Bridge.get_loaded_addins → {Count} manifests scanned for Revit {Ver}", dtos.Length, _revitVersion);
        return Serialize(dtos);
    }

    /// <summary>
    /// Distinct non-contextual ribbon tab titles, snapshotted when the
    /// loader window opened. Drives the RSTify "Hide These Tabs" picker
    /// (profile_loader.html:971 renderTabToggles). Snapshot is fine
    /// because the modal blocks the UI thread, so the ribbon can't
    /// change while the picker is shown.
    /// </summary>
    public string GetAllTabs()
    {
        LogEntry(nameof(GetAllTabs));
        return Serialize(_allTabs);
    }

    /// <summary>
    /// User-config knobs (per-addin overrides etc). Phase-2 follow-up.
    /// Returns the upstream-expected shape so the loader UI doesn't
    /// throw when destructuring `addins`.
    /// </summary>
    public string GetUserConfig()
    {
        LogEntry(nameof(GetUserConfig));
        return Serialize(new { addins = new Dictionary<string, object>() });
    }

    /// <summary>
    /// Compute the disable-preview classification for the named profile:
    /// staying (required), disabling (writeable + non-required),
    /// tryDisable (read-only + non-required — Revit install dir),
    /// skipped (already .addin.RSTdisabled). Pure — no rename happens
    /// here.
    /// </summary>
    public string GetDisablePreview(string profileNameJson)
    {
        LogEntry(nameof(GetDisablePreview), ("name", profileNameJson));
        var profileName = Deserialize<string>(profileNameJson) ?? "";
        var entry = ProfileStore.Resolve(profileName, id: null);
        if (entry is null)
        {
            Log.Warning("Bridge.get_disable_preview: profile not found {Name}", profileName);
            return Serialize(new
            {
                error = "Profile not found: " + profileName,
                staying = Array.Empty<object>(),
                disabling = Array.Empty<object>(),
                tryDisable = Array.Empty<object>(),
                skipped = Array.Empty<object>(),
            });
        }

        var preview = DisablePreviewBuilder.Build(_revitVersion, entry.Profile.RequiredAddins);
        Log.Information("Bridge.get_disable_preview: name={Name} staying={S} disabling={D} tryDisable={T} skipped={Sk}",
                        profileName, preview.Staying.Count, preview.Disabling.Count, preview.TryDisable.Count, preview.Skipped.Count);

        return Serialize(new
        {
            staying = preview.Staying.Select(ToPreviewDto).ToArray(),
            disabling = preview.Disabling.Select(ToPreviewDto).ToArray(),
            tryDisable = preview.TryDisable.Select(ToPreviewDto).ToArray(),
            skipped = preview.Skipped.Select(ToPreviewDto).ToArray(),
        });
    }

    /// <summary>
    /// Walk every search path for the running Revit version and rename
    /// any .addin.RSTdisabled back to .addin. Effective on the next
    /// Revit launch (DLLs already loaded stay resident this session,
    /// so restart_needed=true whenever anything was actually restored).
    /// </summary>
    public string RestoreAddins()
    {
        LogEntry(nameof(RestoreAddins));
        try
        {
            var result = AddinDisabler.RestoreAll(_revitVersion,
                onError: (path, ex) => Log.Warning(ex, "AddinDisabler.Restore: rename failed for {Path}", path));
            Log.Information("Bridge.restore_addins → restored={Restored}, failed={Failed}",
                            result.RestoredCount, result.Failed);
            return Serialize(new
            {
                ok = true,
                restart_needed = result.RestoredCount > 0,
                restored = result.RestoredFiles,
                failed = result.FailedFiles,
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Bridge.restore_addins: AddinDisabler.RestoreAll threw");
            return Serialize(new { ok = false, error = ex.Message, restart_needed = false });
        }
    }

    private static object ToPreviewDto(AddinPreviewEntry e) => new
    {
        // The loader's confirm-overlay reads `displayName` (with `tabName`
        // as a fallback). We synthesise displayName from filename so the
        // user sees something meaningful when no <Name> is set on the
        // first AddIn entry.
        displayName = Path.GetFileNameWithoutExtension(e.FileName),
        fileName = e.FileName,
        filePath = e.FilePath,
        addinId = e.FirstAddinId,
        assembly = e.FirstAssemblyPath,
        sourceKind = e.SourceKind.ToString(),
    };

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
