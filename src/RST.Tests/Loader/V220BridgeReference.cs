// rst-c's own (not vendored): AC6's reference (spec #16, WU3). The file writes LoaderBridge
// made in v2.2.0 (git show v2.2.0:src/RST.UI/Loader/LoaderBridge.cs), copied method for method:
// RecordUserProfilePrefs, RecordPanelOpacityOverride, the writes of LoadProfile, UnloadProfile,
// RemoveProfile and GetUserProfilePrefs' migration. The only change: the bridge used the
// default %AppData%\RST paths; here each takes the file it wrote, and LoadProfile's loaded_at
// is fixed so two runs can be compared byte for byte. RST.RuntimeTests compiles this file too.

using System.IO;
using RST.Core.Configuration;
using RST.Core.Profiles;

namespace RST.Tests.Loader;

public static class V220BridgeReference
{
    /// <summary>v2.2.0 RecordUserProfilePrefs (LoaderBridge.cs:940-965).</summary>
    public static void RecordUserProfilePrefs(string prefsPath, string profileId, string[] hiddenTabs, bool disableUnused, bool presetsAdopted)
    {
        var prefs = UserProfilePrefs.Read(prefsPath);
        prefs.Set(profileId, hiddenTabs, disableUnused, presetsAdopted, prefsPath);
    }

    /// <summary>v2.2.0 RecordPanelOpacityOverride (LoaderBridge.cs:976-1005).</summary>
    public static void RecordPanelOpacityOverride(string prefsPath, string profileId, int? opacity)
    {
        if (opacity.HasValue) opacity = Math.Max(10, Math.Min(100, opacity.Value));
        var prefs = UserProfilePrefs.Read(prefsPath);
        prefs.SetPanelOpacityOverride(profileId, opacity, prefsPath);
    }

    /// <summary>v2.2.0 LoadProfile's file writes (LoaderBridge.cs:123-155): active_profile.json, then the prefs entry.</summary>
    public static void LoadProfileWrites(string profilesDir, string activePath, string prefsPath,
                                         string profileName, string? profileId, bool disableNonRequired, string[] hiddenTabs,
                                         string loadedAt)
    {
        var entry = ProfileStore.Resolve(profileName, profileId, profilesDir)
                    ?? throw new InvalidOperationException("profile not found: " + profileName);
        var ap = ActiveProfile.FromProfile(entry.Profile, entry.FileName, hiddenTabs, disableNonRequired);
        ap.LoadedAt = loadedAt;
        ap.Write(activePath);
        if (!string.IsNullOrEmpty(entry.Profile.Id))
        {
            var prefs = UserProfilePrefs.Read(prefsPath);
            prefs.Set(entry.Profile.Id!, hiddenTabs, disableNonRequired, path: prefsPath);
        }
    }

    /// <summary>v2.2.0 UnloadProfile's file write (LoaderBridge.cs:370-384).</summary>
    public static void UnloadProfile(string activePath) => ActiveProfile.WriteBlank(activePath);

    /// <summary>v2.2.0 RemoveProfile (LoaderBridge.cs:345-368).</summary>
    public static void RemoveProfile(string profilesDir, string activePath, string profileName, string? profileId)
    {
        var entry = ProfileStore.Resolve(profileName, profileId, profilesDir)
                    ?? throw new InvalidOperationException("profile not found: " + profileName);
        ProfileStore.Delete(entry.FileName, profilesDir);
        var ap = ActiveProfile.Read(activePath);
        var wasActive = !ap.IsBlank && (
            string.Equals(ap.ProfileId, entry.Profile.Id, StringComparison.Ordinal) ||
            string.Equals(ap.ProfileName, entry.Profile.ProfileName, StringComparison.Ordinal));
        if (wasActive) ActiveProfile.WriteBlank(activePath);
    }

    /// <summary>v2.2.0 GetUserProfilePrefs' one-time migration (LoaderBridge.cs:895-932).</summary>
    public static UserProfilePrefs GetUserProfilePrefs(string prefsPath, string activePath)
    {
        var prefs = UserProfilePrefs.Read(prefsPath);
        var ap = ActiveProfile.Read(activePath);
        if (!ap.IsBlank && !string.IsNullOrEmpty(ap.ProfileId)
            && prefs.Get(ap.ProfileId!) is null
            && ((ap.HiddenTabs?.Length ?? 0) > 0 || ap.DisableNonRequired))
        {
            prefs.Set(ap.ProfileId!, ap.HiddenTabs ?? Array.Empty<string>(), ap.DisableNonRequired, presetsAdopted: true, path: prefsPath);
        }
        return prefs;
    }

    /// <summary>A fresh copy of the v2.2.0 fixture set (profiles, prefs, active profile) under a new temp root.</summary>
    public static string CopyFixtures(string fixturesDir)
    {
        var root = Path.Combine(Path.GetTempPath(), "rst-v220-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "profiles"));
        foreach (var file in Directory.GetFiles(Path.Combine(fixturesDir, "profiles")))
            File.Copy(file, Path.Combine(root, "profiles", Path.GetFileName(file)));
        File.Copy(Path.Combine(fixturesDir, "user_profile_prefs.json"), Path.Combine(root, "user_profile_prefs.json"));
        File.Copy(Path.Combine(fixturesDir, "active_profile.json"), Path.Combine(root, "active_profile.json"));
        return root;
    }

    public const string ArchitectureId = "1f0c6a52-6b0e-4d6f-9a3e-2c8d4b7e9a10";
    public const string StructureId = "7b2e9d14-3c5a-4f81-b6d0-8e1a2f4c6b38";

    /// <summary>The fixed loaded_at both runs stamp (an unspecified-kind time: no machine offset).</summary>
    public static readonly DateTime LoadedAt = new(2026, 10, 30, 9, 15, 0, DateTimeKind.Unspecified);
}
