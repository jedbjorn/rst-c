// BanList.cs — admin-curated denylist of catalog command Ids.
//
// Persisted next to the addin DLLs (typically
// %AppData%\Autodesk\Revit\Addins\<ver>\<DllSubfolder>\bans.json) so a
// single staged folder can be pasted whole into the addins dir — bans
// travel with the install. Per-Windows-user by virtue of %AppData%;
// per-Revit-major by virtue of the addin folder split. Format:
//
//   {
//     "version": 1,
//     "bannedIds": ["...", "..."]
//   }
//
// Loader contract: missing file → empty list (no signal); corrupt file →
// empty list (best-effort, never throws on Load).
//
// UI for editing this list lands in RST-015. Until then, admins hand-edit
// the JSON. Comments are tolerated on read (// ... lines stripped).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RST.Core.Configuration;

public sealed class BanList
{
    private readonly HashSet<string> _ids;

    private BanList(IEnumerable<string> ids)
    {
        _ids = new HashSet<string>(ids, StringComparer.Ordinal);
    }

    public static BanList Empty() => new(Array.Empty<string>());

    public IReadOnlyCollection<string> BannedIds => _ids;

    public int Count => _ids.Count;

    public bool IsBanned(string id) => _ids.Contains(id);

    public bool Add(string id) => _ids.Add(id);

    public bool Remove(string id) => _ids.Remove(id);

    /// <summary>
    /// Default location for <c>bans.json</c> — next to the BanList assembly.
    /// In a Revit install that resolves to the addin's DLL folder
    /// (e.g. <c>%AppData%\Autodesk\Revit\Addins\2026\RST\bans.json</c>) so
    /// the staged folder can be pasted whole into the addins dir.
    /// Falls back to <c>%AppData%\RST\bans.json</c> for hosted scenarios
    /// where <see cref="System.Reflection.Assembly.Location"/> is empty
    /// (e.g. single-file deployment or test runners).
    /// </summary>
    public static string DefaultPath
    {
        get
        {
            var location = typeof(BanList).Assembly.Location;
            if (string.IsNullOrEmpty(location))
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "RST", "bans.json");
            }
            var dir = Path.GetDirectoryName(location)!;
            return Path.Combine(dir, "bans.json");
        }
    }

    public static BanList Load(string path)
    {
        if (!File.Exists(path)) return Empty();
        try
        {
            var text = File.ReadAllText(path);
            var doc = JsonSerializer.Deserialize<BanListDocument>(text, JsonOptions);
            if (doc?.BannedIds is null) return Empty();
            return new BanList(doc.BannedIds);
        }
        catch
        {
            return Empty();
        }
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var doc = new BanListDocument(Version: 1, BannedIds: _ids.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        var text = JsonSerializer.Serialize(doc, JsonOptions);
        File.WriteAllText(path, text);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed record BanListDocument(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("bannedIds")] IReadOnlyList<string> BannedIds);
}
