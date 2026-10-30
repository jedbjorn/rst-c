// rst-c's own (not vendored): Revit's Add-Ins Manager files, read and written exactly as the VM
// spike recorded them (shared/f15/addins-manager-spike.md §2 and §6):
//   AddInsSettings.json (per user, per year): {"DisableAllAddIns":bool,"AddInItemSettings":[
//     {"Disabled":bool,"GUID":"<lower-case ClientId>","LoadTime":ms,"Name":"…","Vendor":"…"}]}
//   compact UTF-8 without a BOM, no newline; keyed by the manifest's ClientId/AddInId.
//   AdminSettings.json (IT's lock, read only): {"DisableAllAddIns":bool,"AddInManifestAdminSettings":[
//     {"Path":"<manifest>","AddInItemAdminSettings":[{"AddInId":"…","LoadOption":0|1|2}]}]}
//
// Writes are read-modify-write over a JsonNode tree, so every field and entry rst-c does not know
// (or does not touch) is kept as it was; only "Disabled" is ever changed, and missing entries are
// appended. The structural guard (there is no version field) refuses anything that is not that
// shape, and the caller then reports FileError and leaves the file alone.

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RST.Core.Activation;

/// <summary>Whether a year's AddInsSettings.json could be used.</summary>
public enum SettingsFileState
{
    /// <summary>No file yet: everything loads; rst-c creates it on its first write.</summary>
    Missing,

    Ok,

    /// <summary>Unreadable or not the expected shape: reported as FileError, never overwritten.</summary>
    Unreadable,
}

/// <summary>The admin's per-add-in load option (Autodesk's AddInItemAdminLoadOptions).</summary>
public enum AdminLock
{
    None = 0,
    AlwaysEnabled = 1,
    AlwaysDisabled = 2,
}

/// <summary>Normalises a ClientId the way Revit keys it: a lower-case, hyphenated GUID.</summary>
public static class AddinIds
{
    public static string? Normalize(string? id) =>
        Guid.TryParse(id?.Trim().Trim('{', '}'), out var g) ? g.ToString("D") : null;
}

/// <summary>A parsed AddInsSettings.json that can be changed and saved without losing anything.</summary>
public sealed class AddinsSettingsDocument
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly JsonObject _root;
    private readonly JsonArray _items;

    private AddinsSettingsDocument(JsonObject root, JsonArray items)
    {
        _root = root;
        _items = items;
    }

    /// <summary>A new, empty file (what rst-c writes when Revit has not created one yet).</summary>
    public static AddinsSettingsDocument CreateEmpty()
    {
        var items = new JsonArray();
        return new AddinsSettingsDocument(new JsonObject { ["DisableAllAddIns"] = false, ["AddInItemSettings"] = items }, items);
    }

    /// <summary>
    /// Parses <paramref name="json"/>; null when it is not the Add-Ins Manager's shape (an object
    /// whose AddInItemSettings is an array of objects, each with a bool Disabled and a GUID string).
    /// </summary>
    public static AddinsSettingsDocument? TryParse(string json, out string? error)
    {
        error = null;
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = false });
        }
        catch (JsonException ex)
        {
            error = "not valid JSON: " + ex.Message;
            return null;
        }
        if (node is not JsonObject root)
        {
            error = "not a JSON object";
            return null;
        }
        if (root["AddInItemSettings"] is not JsonArray items)
        {
            error = "no AddInItemSettings list";
            return null;
        }
        foreach (var item in items)
        {
            if (item is not JsonObject o
                || o["Disabled"] is not JsonValue d || d.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False)
                || o["GUID"] is not JsonValue g || g.GetValueKind() != JsonValueKind.String
                || AddinIds.Normalize(g.GetValue<string>()) is null)
            {
                error = "an AddInItemSettings entry is not {Disabled, GUID}";
                return null;
            }
        }
        if (root["DisableAllAddIns"] is { } all && all.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
        {
            error = "DisableAllAddIns is not true or false";
            return null;
        }
        return new AddinsSettingsDocument(root, items);
    }

    /// <summary>"Disable all Add-Ins" is ticked in the Add-Ins Manager.</summary>
    public bool DisableAll => _root["DisableAllAddIns"] is JsonValue v && v.GetValueKind() == JsonValueKind.True;

    /// <summary>The add-in ids whose entry says Disabled:true (lower case).</summary>
    public IReadOnlySet<string> DisabledIds()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, o) in Entries())
            if (o["Disabled"]!.GetValueKind() == JsonValueKind.True) set.Add(id);
        return set;
    }

    /// <summary>True, false, or null when the file has no entry for <paramref name="addinId"/>.</summary>
    public bool? IsDisabled(string addinId)
    {
        var id = AddinIds.Normalize(addinId);
        foreach (var (key, o) in Entries())
            if (key == id) return o["Disabled"]!.GetValueKind() == JsonValueKind.True;
        return null;
    }

    /// <summary>
    /// Sets Disabled on <paramref name="addinId"/>'s entry (every entry with that id), touching
    /// nothing else; appends a new entry, in Revit's key order, when there is none. True when the
    /// document changed.
    /// </summary>
    public bool SetDisabled(string addinId, bool disabled, string? name, string? vendor)
    {
        var id = AddinIds.Normalize(addinId) ?? throw new ArgumentException("not a GUID: " + addinId, nameof(addinId));
        var found = false;
        var changed = false;
        foreach (var (key, o) in Entries())
        {
            if (key != id) continue;
            found = true;
            var was = o["Disabled"]!.GetValueKind() == JsonValueKind.True;
            if (was == disabled) continue;
            o["Disabled"] = disabled;
            changed = true;
        }
        if (found) return changed;
        _items.Add(new JsonObject
        {
            ["Disabled"] = disabled,
            ["GUID"] = id,
            ["LoadTime"] = 0,
            ["Name"] = name ?? "",
            ["Vendor"] = vendor ?? "",
        });
        return true;
    }

    /// <summary>The compact JSON Revit writes (no indentation, no newline).</summary>
    public string ToJson() => _root.ToJsonString(WriteOptions);

    /// <summary>UTF-8 without a BOM.</summary>
    public byte[] ToBytes() => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(ToJson());

    private IEnumerable<(string Id, JsonObject Item)> Entries()
    {
        foreach (var item in _items)
        {
            var o = (JsonObject)item!;
            yield return (AddinIds.Normalize(o["GUID"]!.GetValue<string>())!, o);
        }
    }
}

/// <summary>One add-in lock in AdminSettings.json.</summary>
public sealed record AdminLockEntry(string AddinId, string? ManifestPath, AdminLock Option);

/// <summary>A year's AdminSettings.json (IT's locks). rst-c reads it to report locks and never writes it.</summary>
public sealed class AdminSettings
{
    public static readonly AdminSettings None = new(false, []);

    public AdminSettings(bool disableAll, IReadOnlyList<AdminLockEntry> locks)
    {
        DisableAll = disableAll;
        Locks = locks;
    }

    /// <summary>The admin's "Disable all Add-Ins".</summary>
    public bool DisableAll { get; }

    public IReadOnlyList<AdminLockEntry> Locks { get; }

    /// <summary>A missing or unreadable file locks nothing.</summary>
    public static AdminSettings Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return None;
            return Parse(File.ReadAllText(path));
        }
        catch (IOException) { return None; }
        catch (UnauthorizedAccessException) { return None; }
    }

    public static AdminSettings Parse(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { return None; }
        if (root is not JsonObject o) return None;
        var disableAll = o["DisableAllAddIns"] is JsonValue v && v.GetValueKind() == JsonValueKind.True;
        var locks = new List<AdminLockEntry>();
        if (o["AddInManifestAdminSettings"] is JsonArray manifests)
        {
            foreach (var m in manifests.OfType<JsonObject>())
            {
                var path = m["Path"] is JsonValue p && p.GetValueKind() == JsonValueKind.String ? p.GetValue<string>() : null;
                if (m["AddInItemAdminSettings"] is not JsonArray items) continue;
                foreach (var item in items.OfType<JsonObject>())
                {
                    var id = item["AddInId"] is JsonValue i && i.GetValueKind() == JsonValueKind.String ? AddinIds.Normalize(i.GetValue<string>()) : null;
                    if (id is null) continue;
                    var option = item["LoadOption"] is JsonValue lo && lo.GetValueKind() == JsonValueKind.Number && lo.TryGetValue<int>(out var n) ? n : 0;
                    var opt = option switch { 1 => AdminLock.AlwaysEnabled, 2 => AdminLock.AlwaysDisabled, _ => AdminLock.None };
                    if (opt != AdminLock.None) locks.Add(new AdminLockEntry(id, path, opt));
                }
            }
        }
        return new AdminSettings(disableAll, locks);
    }

    /// <summary>
    /// The lock on <paramref name="addinId"/> for the manifest at <paramref name="manifestPath"/>
    /// (Revit keys admin entries by manifest path plus id; an entry without a path matches any).
    /// </summary>
    public AdminLock LockFor(string addinId, string manifestPath)
    {
        var id = AddinIds.Normalize(addinId);
        var result = AdminLock.None;
        foreach (var l in Locks)
        {
            if (l.AddinId != id) continue;
            if (!string.IsNullOrEmpty(l.ManifestPath) && !SamePath(l.ManifestPath!, manifestPath)) continue;
            if (l.Option == AdminLock.AlwaysDisabled) return AdminLock.AlwaysDisabled;
            result = l.Option;
        }
        return result;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a.Replace('/', '\\').TrimEnd('\\'), b.Replace('/', '\\').TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
