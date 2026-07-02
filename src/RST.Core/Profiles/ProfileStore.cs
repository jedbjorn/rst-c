// ProfileStore.cs — disk catalogue of profiles under %AppData%\RST\profiles\.
//
// Each profile is one JSON file; filename pattern <safeName>_<safeDate>.json
// matches the pyRevit-era convention so existing installs round-trip.
// Resolution by id wins over name (id is stable; name is the user's label).
//
// Loader-side contract: best-effort. List() skips files that fail to parse
// rather than throwing — a corrupt file in the dir shouldn't blank the
// picker.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RST.Core.Configuration;

namespace RST.Core.Profiles;

public sealed record ProfileEntry(string FileName, Profile Profile);

public static class ProfileStore
{
    public static IReadOnlyList<ProfileEntry> List(string? dir = null, Action<string, Exception>? onSkip = null)
    {
        dir ??= AppDataPaths.ProfilesDir;
        if (!Directory.Exists(dir)) return Array.Empty<ProfileEntry>();

        var entries = new List<ProfileEntry>();
        foreach (var path in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                using var fs = File.OpenRead(path);
                var profile = ProfileSerializer.Read(fs);
                entries.Add(new ProfileEntry(Path.GetFileName(path), profile));
            }
            catch (ProfileLoadException ex)
            {
                // Skip unreadable files; a corrupt entry shouldn't hide the rest.
                onSkip?.Invoke(path, ex);
            }
            catch (IOException ex)
            {
                // Another process holds the file; ignore on this pass.
                onSkip?.Invoke(path, ex);
            }
        }
        return entries;
    }

    /// <summary>
    /// Resolve a profile by id (preferred) or name. Returns null when no
    /// match. Id match wins because names are user-editable.
    /// </summary>
    public static ProfileEntry? Resolve(string? name, string? id, string? dir = null)
    {
        var all = List(dir);
        if (!string.IsNullOrEmpty(id))
        {
            var byId = all.FirstOrDefault(e =>
                string.Equals(e.Profile.Id, id, StringComparison.Ordinal));
            if (byId is not null) return byId;
        }
        if (!string.IsNullOrEmpty(name))
        {
            return all.FirstOrDefault(e =>
                string.Equals(e.Profile.ProfileName, name, StringComparison.Ordinal));
        }
        return null;
    }

    /// <summary>
    /// Persist <paramref name="profile"/> to <c>profiles/&lt;safeName&gt;_&lt;safeId&gt;.json</c>.
    /// The filename is keyed on the profile <see cref="Profile.Id"/> (not its
    /// display name) so two distinct profiles that happen to share a name never
    /// collide. Any prior file for the *same id* (e.g. saved under an old name)
    /// is removed; a different profile that merely shares a name is left
    /// untouched. The write is atomic (temp file + swap). Returns the filename.
    /// </summary>
    public static string Save(Profile profile, string? dir = null)
    {
        if (profile is null) throw new ArgumentNullException(nameof(profile));
        dir ??= AppDataPaths.ProfilesDir;
        Directory.CreateDirectory(dir);

        if (string.IsNullOrEmpty(profile.Id))
            profile.Id = Guid.NewGuid().ToString();

        var fileName = CanonicalFileName(profile);
        var path = Path.Combine(dir, fileName);

        // Remove a prior file for THIS profile (same id) stored under a
        // different name — but never delete a different profile that merely
        // shares a display name (that was the data-loss bug).
        foreach (var existing in List(dir))
        {
            if (string.Equals(existing.Profile.Id, profile.Id, StringComparison.Ordinal) &&
                !string.Equals(existing.FileName, fileName, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(Path.Combine(dir, existing.FileName)); }
                catch (IOException) { /* best effort — the write below still lands */ }
            }
        }

        // Atomic write: serialise to a temp file, then swap into place so a
        // crash mid-write can't truncate or lose the profile.
        var tmp = path + ".rsttmp";
        using (var fs = File.Create(tmp))
            ProfileSerializer.Write(profile, fs);
        if (File.Exists(path)) File.Replace(tmp, path, destinationBackupFileName: null);
        else File.Move(tmp, path);

        return fileName;
    }

    public static bool Delete(string fileName, string? dir = null)
    {
        dir ??= AppDataPaths.ProfilesDir;
        var path = Path.Combine(dir, fileName);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    public static string CanonicalFileName(Profile profile)
    {
        var name = SafeFileSegment(profile.ProfileName);
        // Key the filename on the stable id, not the export date, so distinct
        // profiles sharing a display name get distinct files (no collision /
        // silent overwrite). Id is guaranteed non-empty by Save.
        var id = SafeFileSegment(string.IsNullOrEmpty(profile.Id)
            ? Guid.NewGuid().ToString()
            : profile.Id);
        return $"{name}_{id}.json";
    }

    private static string SafeFileSegment(string s)
    {
        if (string.IsNullOrEmpty(s)) return "_";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(Array.IndexOf(invalid, c) >= 0 || c == ' ' ? '_' : c);
        return sb.ToString();
    }
}
