// rst-c's own (not vendored): LoaderBridge's GetUserProfilePrefs (read + one-time migration),
// RecordUserProfilePrefs and RecordPanelOpacityOverride (clamp), moved out of the bridge
// (spec #16, WU3) with unchanged behavior and file output.

using RST.Core.Configuration;
using RST.Core.Profiles;

namespace RST.Core.Loader;

/// <summary>The per-profile RSTify, disable-unused and opacity prefs (user_profile_prefs.json).</summary>
public sealed class LoaderPrefsService
{
    public const int MinOpacity = 10;
    public const int MaxOpacity = 100;

    private readonly LoaderPaths _paths;
    private readonly ILoaderLog _log;

    public LoaderPrefsService(LoaderPaths paths, ILoaderLog? log = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _log = log ?? NullLoaderLog.Instance;
    }

    /// <summary>The opacity override's range (the slider only sends 10-100; anything else is clamped).</summary>
    public static int ClampOpacity(int value) => Math.Max(MinOpacity, Math.Min(MaxOpacity, value));

    /// <summary>
    /// Reads the prefs. One-time migration: when the active profile carries hidden tabs or the
    /// disable flag in active_profile.json but has no prefs entry (a pre-RST-026 install), an entry
    /// is written for it with presetsAdopted=true, so the adopt prompt does not fire over state the
    /// user already chose. A read failure gives empty prefs.
    /// </summary>
    public UserProfilePrefs Read()
    {
        try
        {
            var prefs = UserProfilePrefs.Read(_paths.PrefsFile);
            try
            {
                var ap = ActiveProfile.Read(_paths.ActiveProfileFile);
                if (!ap.IsBlank && !string.IsNullOrEmpty(ap.ProfileId)
                    && prefs.Get(ap.ProfileId!) is null
                    && ((ap.HiddenTabs?.Length ?? 0) > 0 || ap.DisableNonRequired))
                {
                    prefs.Set(ap.ProfileId!, ap.HiddenTabs ?? [], ap.DisableNonRequired, presetsAdopted: true, path: _paths.PrefsFile);
                    _log.Information("Loader.user_profile_prefs: migrated active profile {Id} into user prefs", ap.ProfileId);
                }
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "Loader.user_profile_prefs: active-profile migration skipped (non-fatal)");
            }
            return prefs;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Loader.user_profile_prefs: read failed");
            return new UserProfilePrefs();
        }
    }

    /// <summary>Upserts one profile's hidden tabs, disable flag and adoption flag (the adopt prompt's answer).</summary>
    public OpResult Record(string? profileId, IReadOnlyList<string>? hiddenTabs, bool disableUnused, bool presetsAdopted)
    {
        if (string.IsNullOrEmpty(profileId)) return OpResult.Fail("profileId required");
        var hidden = hiddenTabs ?? [];
        try
        {
            var prefs = UserProfilePrefs.Read(_paths.PrefsFile);
            prefs.Set(profileId, hidden, disableUnused, presetsAdopted, _paths.PrefsFile);
            _log.Information("Loader.record_user_profile_prefs OK: id={Id} hidden={Hidden} disableUnused={D} adopted={A}",
                profileId, hidden.Count, disableUnused, presetsAdopted);
            return OpResult.Success;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Loader.record_user_profile_prefs: write failed for {Id}", profileId);
            return OpResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Upserts one profile's panel-opacity override (clamped to 10-100), or clears it with null.
    /// The override replaces the profile's own opacity at ribbon-build time.
    /// </summary>
    public OpResult RecordOpacityOverride(string? profileId, int? opacity)
    {
        if (string.IsNullOrEmpty(profileId)) return OpResult.Fail("profileId required");
        if (opacity.HasValue) opacity = ClampOpacity(opacity.Value);
        try
        {
            var prefs = UserProfilePrefs.Read(_paths.PrefsFile);
            prefs.SetPanelOpacityOverride(profileId, opacity, _paths.PrefsFile);
            _log.Information("Loader.record_panel_opacity_override OK: id={Id} opacity={Opacity}",
                profileId, opacity?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null");
            return OpResult.Success;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Loader.record_panel_opacity_override: write failed for {Id}", profileId);
            return OpResult.Fail(ex.Message);
        }
    }
}
