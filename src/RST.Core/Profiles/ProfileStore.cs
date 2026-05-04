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
    public static IReadOnlyList<ProfileEntry> List(string? dir = null)
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
            catch (ProfileLoadException)
            {
                // Skip unreadable files; a corrupt entry shouldn't hide the rest.
            }
            catch (IOException)
            {
                // Another process holds the file; ignore on this pass.
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
    /// Persist <paramref name="profile"/> to <c>profiles/&lt;safeName&gt;_&lt;safeDate&gt;.json</c>.
    /// If a file with the same id or name already exists, it's deleted first
    /// (the new write replaces it under the canonical filename).
    /// Returns the chosen filename.
    /// </summary>
    public static string Save(Profile profile, string? dir = null)
    {
        if (profile is null) throw new ArgumentNullException(nameof(profile));
        dir ??= AppDataPaths.ProfilesDir;
        Directory.CreateDirectory(dir);

        if (string.IsNullOrEmpty(profile.Id))
            profile.Id = Guid.NewGuid().ToString();

        var fileName = CanonicalFileName(profile);
        var existing = Resolve(profile.ProfileName, profile.Id, dir);
        if (existing is not null && !string.Equals(existing.FileName, fileName, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(Path.Combine(dir, existing.FileName)); }
            catch (IOException) { /* keep going — write below will overwrite if same path */ }
        }

        var path = Path.Combine(dir, fileName);
        using (var fs = File.Create(path))
            ProfileSerializer.Write(profile, fs);

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
        var date = SafeFileSegment(string.IsNullOrEmpty(profile.ExportDate)
            ? DateTime.UtcNow.ToString("yyyy-MM-dd")
            : profile.ExportDate);
        return $"{name}_{date}.json";
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
