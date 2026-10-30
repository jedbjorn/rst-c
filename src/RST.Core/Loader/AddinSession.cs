// rst-c's own (not vendored): the curated add-in lookup the Loader reads at boot (spec #16, WU3),
// addin_lookup.json, in the shape of profile_loader.html's ADDIN_LOOKUP and LoaderBridge's
// GetAddinLookup. Spec #18 (feature #16) retired the scanned-manifest list (get_loaded_addins) and
// the get_user_config stub: the Loader's add-in states now come from RST.Core.Activation's inventory.

using System.Text.Json;
using System.Text.Json.Serialization;

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
