// rst-c's own (not vendored): the Builder's editing rules (spec #16, WU4), ported from
// profile_builder.html: startFresh (1507-1529), loadIntoProfile (1592-1628), the dirty snapshot
// (snapshotPristine / isProfileDirty, 1534-1542) and the checks of saveAndMaybeReload (2966-3018).
// Behavior is unchanged, with two deliberate differences: the JS shared the edited panels with the
// dropdown's profile list (an edit leaked into the list until a reload), here the editor always
// works on its own copy; and rename-as-copy is retired (feature #18, spec #19): Save always writes
// the open profile's id, so an edited name is a rename (ProfileNaming), and Duplicate makes copies.

using System.Text.Json;
using System.Text.Json.Serialization;
using RST.Core.Profiles;

namespace RST.Core.Builder;

/// <summary>What Save does with the edited profile (the checks before the bridge's save_profile).</summary>
public enum SavePlanKind
{
    /// <summary>"Profile name required."</summary>
    MissingName,

    /// <summary>"Tab name required."</summary>
    MissingTab,

    /// <summary>Renamed onto another profile's name: "A profile named "X" already exists. …"</summary>
    NameCollision,

    /// <summary>Saved over itself, under its own id (a new profile, an edit, or a rename).</summary>
    InPlace,
}

public static class ProfileEditing
{
    // The JS profile went to the bridge through JSON.stringify, and came from get_profiles' DTO,
    // which drops null fields. A copy through the same shape keeps every value the round trip kept.
    private static readonly JsonSerializerOptions CopyOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>A deep copy of <paramref name="profile"/> through its JSON shape.</summary>
    public static Profile Clone(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(profile, CopyOptions), CopyOptions)!;
    }

    /// <summary>
    /// The clean-state fingerprint: the profile's JSON. The editor is dirty when the current
    /// fingerprint differs from the one taken at boot, on a dropdown switch and after a save.
    /// </summary>
    public static string Snapshot(Profile profile) => JsonSerializer.Serialize(profile, CopyOptions);

    /// <summary>
    /// <c>startFresh</c>'s blank profile (before its first panel is added): schema 1, no id, empty
    /// name and tab, Min RST version "0.1.0", opacity 100, no presets, and the default cleanup targets.
    /// </summary>
    public static Profile NewProfile() => new()
    {
        SchemaVersion = 1,
        Id = null,
        ProfileName = "",
        Tab = "",
        MinVersion = "0.1.0",
        ExportDate = "",
        PanelOpacity = 100,
        RequiredAddins = [],
        HideRules = [],
        Stacks = new(),
        Panels = [],
        Branding = null,
        Presets = new ProfilePresets { HiddenTabs = [], DisableUnusedAddins = false },
        CleanupTargets = CleanupDefaults.Build(),
    };

    /// <summary>
    /// <c>loadIntoProfile</c>: a copy of <paramref name="source"/> for editing, with the JS's
    /// defaults (schema <c>|| 1</c>, Min RST version <c>|| "0.1.0"</c>), branding dropped (it is machine-wide
    /// now), presets normalized, and the default cleanup targets for a profile that has none
    /// (pre-RST-031).
    /// </summary>
    public static Profile ForEditing(Profile source)
    {
        var p = Clone(source);
        if (p.SchemaVersion == 0) p.SchemaVersion = 1;
        p.ProfileName ??= "";
        p.Tab ??= "";
        if (string.IsNullOrEmpty(p.MinVersion)) p.MinVersion = "0.1.0";
        p.ExportDate ??= "";
        p.RequiredAddins ??= [];
        p.HideRules ??= [];
        p.Stacks ??= new();
        p.Panels ??= [];
        p.Branding = null;
        p.Presets = new ProfilePresets
        {
            HiddenTabs = p.Presets?.HiddenTabs is { } hidden ? [.. hidden] : [],
            DisableUnusedAddins = p.Presets?.DisableUnusedAddins ?? false,
        };
        p.CleanupTargets ??= CleanupDefaults.Build();
        return p;
    }

    /// <summary>
    /// Drops every stack no panel slot refers to (spec #17: an orphan stack is removed on save).
    /// Returns the names removed, in stack order.
    /// </summary>
    public static IReadOnlyList<string> RemoveOrphanStacks(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var orphans = ProfileSerializer.OrphanStacks(profile);
        foreach (var name in orphans) profile.Stacks.Remove(name);
        return orphans;
    }

    /// <summary>
    /// The checks Save makes before writing: name, then tab, then — in edit mode, when the name
    /// changed (trimmed) from the one loaded — the case-insensitive collision with another profile's
    /// name (by a different id, <see cref="ProfileNaming.IsTaken"/>). Otherwise the save is in
    /// place: the id is kept, so a changed name is a rename. <paramref name="profile"/> is not changed.
    /// </summary>
    /// <param name="profile">The edited profile.</param>
    /// <param name="editMode">The editor holds a profile loaded from the dropdown, or saved once.</param>
    /// <param name="originalName">The name when it was loaded (or last saved); "" for a new profile.</param>
    /// <param name="library">The profiles on disk (the dropdown's list).</param>
    public static SavePlanKind PlanSave(Profile profile, bool editMode, string? originalName, IEnumerable<Profile>? library)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.ProfileName)) return SavePlanKind.MissingName;
        if (string.IsNullOrWhiteSpace(profile.Tab)) return SavePlanKind.MissingTab;

        var renamedAway = editMode
                          && !string.IsNullOrEmpty(originalName)
                          && profile.ProfileName.Trim() != originalName.Trim();
        if (!renamedAway) return SavePlanKind.InPlace;
        return ProfileNaming.IsTaken(profile.ProfileName, profile.Id, library) ? SavePlanKind.NameCollision : SavePlanKind.InPlace;
    }
}
