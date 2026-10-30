// rst-c's own (not vendored): rst-c's own record of the add-ins it switched off,
// %AppData%\RST\addin_switches.json (spec #18, In Scope 4). Re-enabling only ever touches what is
// in here, so an add-in the user switched off in Autodesk's Add-Ins Manager is never turned back
// on by rst-c. An entry is cleared once the add-in is back on, or once it is no longer installed.
//
//   {"version":1,"switchedOff":[{"year":2026,"addinId":"…","name":"…","mechanism":"SettingsFile",
//     "manifestPath":"…","at":"2026-10-30T09:00:00Z"}]}
//
// A settings-file entry is keyed by (year, ClientId); a rename entry (Revit 2025.0–2025.2) by
// (year, manifest path).

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RST.Core.Activation;

/// <summary>One add-in rst-c switched off in one year.</summary>
public sealed record LedgerEntry(
    [property: JsonPropertyName("year")] int Year,
    [property: JsonPropertyName("addinId")] string? AddinId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("mechanism")] SwitchMechanism Mechanism,
    [property: JsonPropertyName("manifestPath")] string ManifestPath,
    [property: JsonPropertyName("at")] DateTime At);

/// <summary>The key a plan uses to clear a ledger entry.</summary>
public sealed record LedgerKey(int Year, SwitchMechanism Mechanism, string? AddinId, string ManifestPath);

public sealed class ActivationLedger
{
    public const string FileName = "addin_switches.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly List<LedgerEntry> _entries;

    public ActivationLedger(IEnumerable<LedgerEntry>? entries = null) => _entries = [.. entries ?? []];

    /// <summary>%AppData%\RST\addin_switches.json.</summary>
    public static string DefaultPath => Path.Combine(Configuration.AppDataPaths.Root, FileName);

    public IReadOnlyList<LedgerEntry> Entries => _entries;

    /// <summary>True when the file existed but could not be read (it is kept as a .corrupt copy on the next save).</summary>
    public bool LoadedFromCorruptFile { get; private set; }

    public static ActivationLedger Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new ActivationLedger();
            var file = JsonSerializer.Deserialize<LedgerFile>(File.ReadAllText(path), Options);
            return new ActivationLedger((file?.SwitchedOff ?? []).Where(e => e is not null && !string.IsNullOrEmpty(e.ManifestPath)));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new ActivationLedger { LoadedFromCorruptFile = true };
        }
    }

    /// <summary>Writes the record atomically (temp file, then replace).</summary>
    public void Save(string path, IActivationFileSystem? fs = null)
    {
        fs ??= ActivationFileSystem.Instance;
        if (LoadedFromCorruptFile && fs.FileExists(path))
        {
            try { fs.Copy(path, path + ".corrupt"); }
            catch (IOException) { }
            LoadedFromCorruptFile = false;
        }
        var json = JsonSerializer.Serialize(new LedgerFile { SwitchedOff = [.. _entries] }, Options);
        fs.WriteAtomic(path, new UTF8Encoding(false).GetBytes(json));
    }

    /// <summary>rst-c switched <paramref name="addinId"/> off in <paramref name="year"/> through the settings file.</summary>
    public bool HasSetting(int year, string addinId) =>
        _entries.Any(e => e.Year == year && e.Mechanism == SwitchMechanism.SettingsFile && e.AddinId == addinId);

    /// <summary>rst-c renamed the manifest at <paramref name="manifestPath"/> in <paramref name="year"/>.</summary>
    public bool HasRename(int year, string manifestPath) =>
        _entries.Any(e => e.Year == year && e.Mechanism == SwitchMechanism.Rename && SamePath(e.ManifestPath, manifestPath));

    public void Add(LedgerEntry entry)
    {
        if (!_entries.Any(e => Matches(e, KeyOf(entry)))) _entries.Add(entry);
    }

    public bool Remove(LedgerKey key) => _entries.RemoveAll(e => Matches(e, key)) > 0;

    public static LedgerKey KeyOf(LedgerEntry e) => new(e.Year, e.Mechanism, e.AddinId, e.ManifestPath);

    private static bool Matches(LedgerEntry e, LedgerKey k) =>
        e.Year == k.Year && e.Mechanism == k.Mechanism
        && (k.Mechanism == SwitchMechanism.SettingsFile ? e.AddinId == k.AddinId : SamePath(e.ManifestPath, k.ManifestPath));

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private sealed class LedgerFile
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 1;

        [JsonPropertyName("switchedOff")] public List<LedgerEntry> SwitchedOff { get; set; } = [];
    }
}
