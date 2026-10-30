// rst-c's own (not vendored): RST's own record of the add-ins it switched off,
// %AppData%\RST\addin_switches.json (spec #18, In Scope 4). Re-enabling only ever touches what is
// in here, so an add-in the user switched off in Autodesk's Add-Ins Manager is never turned back
// on by RST. An entry is cleared once the add-in is back on, or once it is no longer installed.
//
//   {"version":1,"switchedOff":[{"year":2026,"addinId":"…","name":"…","kind":"Setting",
//     "manifestPath":"…","at":"2026-10-30T09:00:00Z"}]}
//
// A "Setting" entry is keyed by (year, ClientId). A "V30Rename" entry (year, manifest path) marks
// a manifest RST 3.0 renamed that could not be renamed back (decision #21: the v3.0 undo is the only
// rename left): it is still RST's, so Unload and Restore All try again, but the one-time move does
// not repeat its report.
//
// Two Loaders (two Revit years) can run at once, so a save re-reads the file under a short lock
// file and re-applies this run's changes to what is on disk (review N9).

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RST.Core.Activation;

/// <summary>What a record entry stands for.</summary>
public enum LedgerKind
{
    /// <summary>RST set Disabled:true for this ClientId in the year's AddInsSettings.json.</summary>
    Setting,

    /// <summary>A manifest RST 3.0 renamed to .RSTdisabled that RST could not rename back yet.</summary>
    V30Rename,
}

/// <summary>One add-in RST switched off in one year.</summary>
public sealed record LedgerEntry(
    [property: JsonPropertyName("year")] int Year,
    [property: JsonPropertyName("addinId")] string? AddinId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] LedgerKind Kind,
    [property: JsonPropertyName("manifestPath")] string ManifestPath,
    [property: JsonPropertyName("at")] DateTime At);

/// <summary>The key a plan uses to clear a record entry.</summary>
public sealed record LedgerKey(int Year, LedgerKind Kind, string? AddinId, string ManifestPath);

public sealed class ActivationLedger
{
    public const string FileName = "addin_switches.json";

    public const string LockSuffix = ".lock";

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

    /// <summary>
    /// Re-reads the record at <paramref name="path"/> under a short lock file, applies
    /// <paramref name="changes"/> to it and saves it, so a second Loader's entries written meanwhile
    /// are kept. Returns the merged record. Waits up to <paramref name="wait"/> for the lock, then
    /// writes anyway (an unlocked merge is still better than losing this run's changes).
    /// </summary>
    public static ActivationLedger SaveMerged(string path, IReadOnlyList<Action<ActivationLedger>> changes, IActivationFileSystem? fs = null, TimeSpan? wait = null)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var held = AcquireLock(path + LockSuffix, wait ?? TimeSpan.FromSeconds(3));
        var merged = Load(path);
        foreach (var change in changes) change(merged);
        merged.Save(path, fs);
        return merged;
    }

    private static FileStream? AcquireLock(string lockPath, TimeSpan wait)
    {
        var until = DateTime.UtcNow + wait;
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (DateTime.UtcNow < until)
            {
                Thread.Sleep(50);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>RST switched <paramref name="addinId"/> off in <paramref name="year"/> through the settings file.</summary>
    public bool HasSetting(int year, string addinId) =>
        _entries.Any(e => e.Year == year && e.Kind == LedgerKind.Setting && e.AddinId == addinId);

    /// <summary>The v3.0 rename of the manifest at <paramref name="manifestPath"/> could not be undone in <paramref name="year"/>.</summary>
    public bool HasV30Rename(int year, string manifestPath) =>
        _entries.Any(e => e.Year == year && e.Kind == LedgerKind.V30Rename && SamePath(e.ManifestPath, manifestPath));

    public void Add(LedgerEntry entry)
    {
        if (!_entries.Any(e => Matches(e, KeyOf(entry)))) _entries.Add(entry);
    }

    public bool Remove(LedgerKey key) => _entries.RemoveAll(e => Matches(e, key)) > 0;

    public static LedgerKey KeyOf(LedgerEntry e) => new(e.Year, e.Kind, e.AddinId, e.ManifestPath);

    private static bool Matches(LedgerEntry e, LedgerKey k) =>
        e.Year == k.Year && e.Kind == k.Kind
        && (k.Kind == LedgerKind.Setting ? e.AddinId == k.AddinId : SamePath(e.ManifestPath, k.ManifestPath));

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private sealed class LedgerFile
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 1;

        [JsonPropertyName("switchedOff")] public List<LedgerEntry> SwitchedOff { get; set; } = [];
    }
}
