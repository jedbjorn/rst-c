// rst-c's own (not vendored): atomic writes, tolerant reads and recovery
// for the rollup store's JSON files (spec #24, Rollup Store, "Writes" and
// "Schema changes").
//
//   write    temp file → flush to disk → replace, keeping the previous file
//            as <name>.bak.json
//   read     a sharing violation retries briefly, then the reader falls back
//            to .bak.json
//   corrupt  (writer only) the unreadable file is renamed <name>.corrupt.json
//            and .bak.json restored in its place; the caller re-folds any
//            session still in the outbox
//   schema   older files migrate forward in code; a file newer than this
//            build is never discarded — readers read what they can, writers
//            refuse to write it

using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RST.Core.Telemetry.Rollups;

internal enum ReadMode
{
    /// <summary>Never touches the disk: falls back to .bak.json in memory.</summary>
    Reader,

    /// <summary>The tray's fold: renames a corrupt file and restores .bak.json in place.</summary>
    Writer,
}

internal enum ReadOutcome
{
    /// <summary>Neither the file nor its .bak.json exists.</summary>
    Missing,
    Ok,

    /// <summary>Reader mode: the file was unreadable, .bak.json was used.</summary>
    FromBackup,

    /// <summary>Writer mode: the file was missing or corrupt and .bak.json was restored —
    /// sessions folded after that backup must re-fold.</summary>
    Restored,

    /// <summary>The file and its .bak.json are both unreadable; the data is gone.</summary>
    Lost,
}

internal readonly record struct ReadResult<T>(T? Value, ReadOutcome Outcome) where T : class;

/// <summary>A file written by a newer rst-c: never overwritten, never renamed corrupt.</summary>
internal sealed class RollupSchemaException : IOException
{
    public RollupSchemaException(string message) : base(message) { }
}

/// <summary>Forward migrations, one step per schema version (version n → n + 1).
/// Version 1 is the first; there are no steps yet.</summary>
internal static class RollupMigrations
{
    public static readonly IReadOnlyDictionary<int, Func<JsonObject, JsonObject>> Steps =
        new Dictionary<int, Func<JsonObject, JsonObject>>();

    /// <summary>Migrate <paramref name="doc"/> from <paramref name="from"/> up to
    /// <paramref name="to"/>; null when a step is missing.</summary>
    public static JsonObject? Migrate(JsonObject doc, int from, int to,
        IReadOnlyDictionary<int, Func<JsonObject, JsonObject>> steps)
    {
        for (var v = from; v < to; v++)
        {
            if (!steps.TryGetValue(v, out var step)) return null;
            doc = step(doc);
            doc["schema_version"] = v + 1;
        }
        return doc;
    }
}

internal sealed class RollupFileIO
{
    private readonly Func<string, Stream> _openRead;
    private readonly int _attempts;
    private readonly TimeSpan _retryDelay;

    /// <param name="openRead">Test seam for the read open (to simulate a sharing violation).</param>
    public RollupFileIO(Func<string, Stream>? openRead = null, int attempts = 4, TimeSpan? retryDelay = null)
    {
        _openRead = openRead ?? (p => new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        _attempts = Math.Max(1, attempts);
        _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(50);
    }

    public static string BakPath(string path) => Stem(path) + ".bak.json";

    public static string CorruptPath(string path) => Stem(path) + ".corrupt.json";

    private static string Stem(string path) =>
        path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? path[..^5] : path;

    /// <summary>Write atomically, keeping the previous file as .bak.json.</summary>
    public void WriteAtomic(string path, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
        }
        if (File.Exists(path))
            File.Replace(tmp, path, BakPath(path), ignoreMetadataErrors: true);
        else
            File.Move(tmp, path);
    }

    /// <summary>The file's bytes, retrying sharing violations; null when it does not exist.
    /// Throws <see cref="IOException"/> when it stays locked.</summary>
    public byte[]? ReadBytes(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var s = _openRead(path);
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (IOException) when (attempt < _attempts)
            {
                Thread.Sleep(_retryDelay);
            }
        }
    }

    /// <summary>Read one store file (rollup, index or ledger) with the recovery rules above.</summary>
    /// <param name="validate">Extra check on a parsed value (for example the model id matches the file name).</param>
    public ReadResult<T> Read<T>(string path, ReadMode mode, Func<T, bool>? validate = null) where T : class
    {
        var current = TryParse(path, validate);
        if (current.State == ParseState.Ok) return new(current.Value, ReadOutcome.Ok);
        if (current.State == ParseState.TooNew)
        {
            if (mode == ReadMode.Writer)
                throw new RollupSchemaException(Path.GetFileName(path) + " was written by a newer rst-c");
            return new(current.Value, current.Value is null ? ReadOutcome.Lost : ReadOutcome.Ok);
        }
        if (current.State == ParseState.Locked && mode == ReadMode.Writer)
            throw new IOException(Path.GetFileName(path) + " is locked");

        var bakPath = BakPath(path);
        var bak = TryParse(bakPath, validate);
        if (mode == ReadMode.Reader)
        {
            if (bak.State is ParseState.Ok or ParseState.TooNew && bak.Value is not null)
                return new(bak.Value, ReadOutcome.FromBackup);
            return new(null, current.State == ParseState.Missing && bak.State == ParseState.Missing
                ? ReadOutcome.Missing : ReadOutcome.Lost);
        }

        // Writer: the current file is missing or corrupt.
        if (bak.State == ParseState.TooNew)
            throw new RollupSchemaException(Path.GetFileName(bakPath) + " was written by a newer rst-c");
        if (bak.State == ParseState.Locked)
            throw new IOException(Path.GetFileName(bakPath) + " is locked");
        if (current.State == ParseState.Missing && bak.State == ParseState.Missing)
            return new(null, ReadOutcome.Missing);

        if (current.State == ParseState.Corrupt)
            File.Move(path, CorruptPath(path), overwrite: true);
        if (bak.State == ParseState.Ok)
        {
            File.Copy(bakPath, path, overwrite: true);
            return new(bak.Value, ReadOutcome.Restored);
        }
        return new(null, ReadOutcome.Lost);
    }

    private enum ParseState { Missing, Ok, Corrupt, Locked, TooNew }

    private (ParseState State, T? Value) TryParse<T>(string path, Func<T, bool>? validate) where T : class
    {
        byte[]? bytes;
        try { bytes = ReadBytes(path); }
        catch (IOException) { return (ParseState.Locked, null); }
        catch (UnauthorizedAccessException) { return (ParseState.Locked, null); }
        if (bytes is null) return (ParseState.Missing, null);

        try
        {
            if (JsonNode.Parse(bytes) is not JsonObject obj) return (ParseState.Corrupt, null);
            if (obj["schema_version"] is not JsonValue sv || !sv.TryGetValue<int>(out var version) || version < 1)
                return (ParseState.Corrupt, null);
            if (version > RollupSchema.Version)
            {
                // Newer than this build: read what we can, never discard it.
                T? best = null;
                try { best = obj.Deserialize<T>(RollupJson.Options); } catch { /* best effort */ }
                return (ParseState.TooNew, best);
            }
            if (version < RollupSchema.Version)
            {
                obj = RollupMigrations.Migrate(obj, version, RollupSchema.Version, RollupMigrations.Steps)
                      ?? throw new RollupSchemaException("no migration from schema " + version);
            }
            var value = obj.Deserialize<T>(RollupJson.Options);
            if (value is null || (validate is not null && !validate(value))) return (ParseState.Corrupt, null);
            return (ParseState.Ok, value);
        }
        catch (RollupSchemaException)
        {
            return (ParseState.TooNew, null); // unknown schema: treat like newer — keep, never rename
        }
        catch (Exception)
        {
            return (ParseState.Corrupt, null);
        }
    }
}
