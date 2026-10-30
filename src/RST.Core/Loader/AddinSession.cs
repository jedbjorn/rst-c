// rst-c's own (not vendored): the add-in facts the Loader reads at boot (spec #16, WU3): the
// curated lookup (addin_lookup.json), the scanned manifests (get_loaded_addins) and the
// get_user_config stub. Shapes follow profile_loader.html's ADDIN_LOOKUP, LOADED_ADDINS and
// USER_CONFIG, and LoaderBridge's GetAddinLookup / GetLoadedAddins / GetUserConfig.

using System.Text.Json;
using System.Text.Json.Serialization;
using RST.Core.Scanning;

namespace RST.Core.Loader;

/// <summary>One addin_lookup.json entry: <c>{ displayName, file, url }</c>.</summary>
public sealed record AddinLookupEntry(
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("file")] string? File,
    [property: JsonPropertyName("url")] string? Url);

/// <summary>
/// The curated add-in registry shipped as Assets/lookup/addin_lookup.json (keyed by ribbon tab
/// name, exact and case-sensitive, as the JS object lookup was).
/// </summary>
public sealed class AddinLookupService
{
    private readonly string _path;
    private readonly ILoaderLog _log;

    public AddinLookupService(string path, ILoaderLog? log = null)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _log = log ?? NullLoaderLog.Instance;
    }

    /// <summary>The file's text unchanged (as v2.2.0's bridge passed it through), or <c>{}</c> when missing or unreadable.</summary>
    public string ReadRawJson()
    {
        try
        {
            if (!System.IO.File.Exists(_path))
            {
                _log.Warning(null, "Loader.addin_lookup: {Path} missing — returning empty dict", _path);
                return "{}";
            }
            var raw = System.IO.File.ReadAllText(_path);
            _log.Information("Loader.addin_lookup: {Bytes} bytes from {Path}", raw.Length, _path);
            return raw;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Loader.addin_lookup: read failed; returning empty dict");
            return "{}";
        }
    }

    /// <summary>The registry, parsed; empty when the file is missing or not an object of entries.</summary>
    public IReadOnlyDictionary<string, AddinLookupEntry> Read() => Parse(ReadRawJson(), _log);

    /// <summary>Parses addin_lookup.json's shape; anything unreadable gives an empty registry.</summary>
    public static IReadOnlyDictionary<string, AddinLookupEntry> Parse(string json, ILoaderLog? log = null)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, AddinLookupEntry>>(json)
                   ?? new Dictionary<string, AddinLookupEntry>();
        }
        catch (JsonException ex)
        {
            (log ?? NullLoaderLog.Instance).Warning(ex, "Loader.addin_lookup: not a lookup object; using an empty registry");
            return new Dictionary<string, AddinLookupEntry>();
        }
    }
}

/// <summary>
/// One scanned .addin manifest (get_loaded_addins' DTO): the canonical file name, whether it is
/// disabled on disk, and a display name (the first entry's Name, else the file stem).
/// </summary>
public sealed record LoadedAddin(
    string FileName,
    string FilePath,
    bool IsDisabled,
    string DisplayName,
    string? AddinId,
    string? Assembly)
{
    public static LoadedAddin FromManifest(AddinManifest m)
    {
        var first = m.Entries.Count > 0 ? m.Entries[0] : null;
        var displayName = !string.IsNullOrWhiteSpace(first?.Name)
            ? first!.Name!
            : Path.GetFileNameWithoutExtension(m.FileName);
        return new LoadedAddin(m.FileName, m.FilePath, m.IsDisabled, displayName, first?.AddinId, first?.AssemblyPath);
    }
}

/// <summary>A per-add-in user override (<c>USER_CONFIG.addins[tab]</c>): enabled, origin, add-in file.</summary>
public sealed record UserAddinConfigEntry(bool? Enabled, string? Origin, string? AddinFile);

/// <summary>
/// The user's add-in config. <c>get_user_config</c> is still a stub (spec #16, Out of Scope), so
/// in practice <see cref="Addins"/> is always empty and the Installed / Disabled pills and the
/// origin badges stay unreachable, exactly as in v2.2.0.
/// </summary>
public sealed class UserAddinConfig
{
    public static readonly UserAddinConfig Empty = new(new Dictionary<string, UserAddinConfigEntry>());

    public UserAddinConfig(IReadOnlyDictionary<string, UserAddinConfigEntry> addins) => Addins = addins;

    public IReadOnlyDictionary<string, UserAddinConfigEntry> Addins { get; }

    public UserAddinConfigEntry? For(string tabName) =>
        Addins.TryGetValue(tabName, out var entry) ? entry : null;
}
