// UserProfilePrefs.cs — per-user, per-profile RSTify + disable-unused
// preferences, keyed by profile id. Single JSON file under
// %AppData%\RST\user_profile_prefs.json so switching profiles in the
// Loader picker swaps in *that* profile's saved state instead of
// reusing whatever the active profile carried.
//
// Why a separate file from active_profile.json:
//   - active_profile.json answers "what's loaded right now" (one
//     pointer, written on every Load Profile, read by RstApplication
//     at startup to apply hide rules).
//   - user_profile_prefs.json answers "what does THIS profile want
//     when the user picks it" (one entry per profile id, survives
//     profile switching, read by the Loader's selectCard to seed the
//     picker + footer toggle).
//
// Admin presets baked in via Profile.Presets are SEEDS only — they
// populate this file on first adoption (or refusal) and the user
// owns their copy from then on. RSTify is per-profile, per-user.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RST.Core.Configuration;

public sealed class UserProfilePrefsEntry
{
    [JsonPropertyName("hiddenTabs")]
    public List<string> HiddenTabs { get; set; } = new();

    [JsonPropertyName("disableUnusedAddins")]
    public bool DisableUnusedAddins { get; set; }

    /// <summary>
    /// Set true once the user has answered the "Adopt RSTify presets?"
    /// prompt for this profile (yes or no). The Loader uses this to
    /// avoid re-prompting on every selection.
    /// </summary>
    [JsonPropertyName("presetsAdopted")]
    public bool PresetsAdopted { get; set; }

    /// <summary>
    /// User's per-profile panel-opacity override (10-100). When set,
    /// REPLACES <c>profile.PanelOpacity</c> at ribbon-build time for
    /// every panel in the profile. Null means "no override; use the
    /// admin's value." Sticky once set — survives admin profile
    /// updates. See ProfileTabBuilder for the consumer.
    /// </summary>
    [JsonPropertyName("panelOpacityOverride")]
    public int? PanelOpacityOverride { get; set; }
}

public sealed class UserProfilePrefs
{
    [JsonPropertyName("profiles")]
    public Dictionary<string, UserProfilePrefsEntry> Profiles { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string DefaultPath => Path.Combine(AppDataPaths.Root, "user_profile_prefs.json");

    /// <summary>Read the prefs file. Returns an empty instance when missing or unreadable.</summary>
    public static UserProfilePrefs Read(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new UserProfilePrefs();
        try
        {
            var text = File.ReadAllText(path);
            return JsonSerializer.Deserialize<UserProfilePrefs>(text) ?? new UserProfilePrefs();
        }
        catch
        {
            return new UserProfilePrefs();
        }
    }

    public void Write(string? path = null)
    {
        path ??= DefaultPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    public UserProfilePrefsEntry? Get(string profileId)
    {
        if (string.IsNullOrEmpty(profileId)) return null;
        return Profiles.TryGetValue(profileId, out var e) ? e : null;
    }

    /// <summary>
    /// Upsert the entry for <paramref name="profileId"/> with the given
    /// state. Persists immediately. Caller may pass <c>presetsAdopted: null</c>
    /// to preserve whatever's already on the entry (so a Load Profile
    /// save doesn't accidentally reset the adoption flag).
    /// </summary>
    public void Set(
        string profileId,
        IReadOnlyList<string> hiddenTabs,
        bool disableUnusedAddins,
        bool? presetsAdopted = null,
        string? path = null)
    {
        if (string.IsNullOrEmpty(profileId))
            throw new ArgumentException("profileId required", nameof(profileId));

        if (!Profiles.TryGetValue(profileId, out var entry))
        {
            entry = new UserProfilePrefsEntry();
            Profiles[profileId] = entry;
        }
        entry.HiddenTabs = new List<string>(hiddenTabs ?? Array.Empty<string>());
        entry.DisableUnusedAddins = disableUnusedAddins;
        if (presetsAdopted.HasValue) entry.PresetsAdopted = presetsAdopted.Value;
        Write(path);
    }

    /// <summary>
    /// Upsert just the panel-opacity override for one profile. Pass
    /// <c>null</c> to clear it. Persists immediately. Kept separate
    /// from <see cref="Set"/> so the slider write path doesn't
    /// disturb the RSTify hidden-tabs / disable-unused state on the
    /// same entry.
    /// </summary>
    public void SetPanelOpacityOverride(string profileId, int? value, string? path = null)
    {
        if (string.IsNullOrEmpty(profileId))
            throw new ArgumentException("profileId required", nameof(profileId));

        if (!Profiles.TryGetValue(profileId, out var entry))
        {
            entry = new UserProfilePrefsEntry();
            Profiles[profileId] = entry;
        }
        entry.PanelOpacityOverride = value;
        Write(path);
    }
}
