// rst-c's own (not vendored): Rename and Duplicate (feature #18, spec #19), which replace
// rename-as-copy. Rename gives the working copy a new name and keeps its id: Save writes the same
// id, ProfileStore.Save removes the file under the old name, and active_profile.json (keyed by
// profile_id) stays valid, so a user who has it loaded keeps it. Duplicate writes a new profile with
// a new id from the working copy, unsaved edits included; the original file is not touched. Both
// names must be unique among the saved profiles (trimmed, case-insensitive), a rename excluding the
// profile itself.

using RST.Core.Profiles;

namespace RST.Core.Builder;

/// <summary>What a name check found.</summary>
public enum NameCheck
{
    Ok,

    /// <summary>"Profile name required."</summary>
    Missing,

    /// <summary>Another saved profile has that name: "A profile named "X" already exists. …"</summary>
    Taken,
}

/// <summary>A Duplicate's outcome: the name check, and when it passed, the save.</summary>
public sealed record DuplicateOutcome(NameCheck Check, SaveOutcome? Save)
{
    public bool Ok => Check == NameCheck.Ok && Save is { Ok: true };
}

public static class ProfileNaming
{
    /// <summary>
    /// True when a saved profile other than <paramref name="selfId"/> has <paramref name="name"/>
    /// (trimmed, case-insensitive). Profiles without an id are not compared (as the save's check
    /// always did): the editor cannot tell one apart from itself.
    /// </summary>
    public static bool IsTaken(string? name, string? selfId, IEnumerable<Profile>? library)
    {
        var wanted = (name ?? "").Trim();
        return (library ?? []).Any(p =>
            p is not null
            && !string.IsNullOrEmpty(p.Id)
            && (string.IsNullOrEmpty(selfId) || p.Id != selfId)
            && string.Equals((p.ProfileName ?? "").Trim(), wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A name is required, and unique among the saved profiles other than <paramref name="selfId"/>.</summary>
    public static NameCheck Check(string? name, string? selfId, IEnumerable<Profile>? library)
    {
        if (string.IsNullOrWhiteSpace(name)) return NameCheck.Missing;
        return IsTaken(name, selfId, library) ? NameCheck.Taken : NameCheck.Ok;
    }

    /// <summary>
    /// Rename: on a unique name, the working copy takes it (trimmed) and keeps its id. An unsaved
    /// change: Save writes it.
    /// </summary>
    public static NameCheck Rename(Profile working, string? newName, IEnumerable<Profile>? library)
    {
        ArgumentNullException.ThrowIfNull(working);
        var check = Check(newName, working.Id, library);
        if (check == NameCheck.Ok) working.ProfileName = newName!.Trim();
        return check;
    }

    /// <summary>The Duplicate dialog's default: "&lt;name&gt; copy", then "&lt;name&gt; copy 2", … until no saved profile has it.</summary>
    public static string DefaultDuplicateName(string? name, IEnumerable<Profile>? library)
    {
        var all = (library ?? []).ToList();
        var stem = (name ?? "").Trim() + " copy";
        var candidate = stem;
        for (var n = 2; IsTaken(candidate, null, all); n++) candidate = stem + " " + n;
        return candidate;
    }

    /// <summary>
    /// The profile Duplicate writes: a copy of the working copy (unsaved edits included) with the
    /// new name, no id and no export date, so the save mints both.
    /// </summary>
    public static Profile DuplicateOf(Profile working, string name)
    {
        ArgumentNullException.ThrowIfNull(working);
        var copy = ProfileEditing.Clone(working);
        copy.Id = null;
        copy.ExportDate = "";
        copy.ProfileName = (name ?? "").Trim();
        return copy;
    }

    /// <summary>
    /// Duplicate: on a unique name (among every saved profile, the original included), writes the
    /// copy through <paramref name="saver"/>. The working copy and the original's file are not
    /// changed; the editor switches to <see cref="SaveOutcome.Saved"/>.
    /// </summary>
    public static DuplicateOutcome Duplicate(Profile working, string? name, IEnumerable<Profile>? library, ProfileSaveService saver)
    {
        ArgumentNullException.ThrowIfNull(working);
        ArgumentNullException.ThrowIfNull(saver);
        var check = Check(name, null, library);
        if (check != NameCheck.Ok) return new DuplicateOutcome(check, null);
        return new DuplicateOutcome(check, saver.Save(DuplicateOf(working, name!)));
    }
}
