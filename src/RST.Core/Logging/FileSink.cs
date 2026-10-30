// rst-c's own (not vendored): the in-house logger's file sink (decision #33). It keeps the rules the
// Serilog.Sinks.File configurations had, so the files on disk are named, rolled and pruned as before:
//   - RollingInterval.Infinite: one file at the path as given (the engine's rst_<yyyy-MM-dd_HHmmss>.log).
//   - RollingInterval.Day: the date goes before the extension, tray_.log -> tray_20261030.log; past the
//     size limit with RollOnFileSizeLimit, tray_20261030_001.log, _002 and so on.
//   - FileSizeLimitBytes (Serilog's default 1 GB): a file at the limit takes no more events, unless it
//     rolls to the next sequence number.
//   - RetainedFileCountLimit: after a roll, only the newest N files of this sink's own pattern are kept
//     (the current one included). Other files in the folder are never touched.
//   - Shared: several processes may append to one file (two trays of one user); each write takes a
//     session-local named mutex, seeks to the end and flushes. A wait that times out drops the event.
//   - The file is opened without FileShare.Delete (Serilog's FileShare.Read, or ReadWrite when shared):
//     while it is open, Health's clean-up, a second Revit's prune and the tray's retention cannot
//     delete it, and skip it as in use.
// UTF-8 without a BOM; every event is flushed to the OS as it is written. Opening or writing failing
// never throws: the event is dropped and the file is retried at most every few seconds.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace RST.Core.Logging;

public enum RollingInterval
{
    Infinite,
    Day,
}

public sealed class FileSinkOptions
{
    /// <summary>Serilog.Sinks.File's default size limit, 1 GB.</summary>
    public const long DefaultFileSizeLimitBytes = 1L * 1024 * 1024 * 1024;

    /// <summary>The file (Infinite) or the base path the date and sequence are inserted into (Day).</summary>
    public required string Path { get; init; }

    public string OutputTemplate { get; init; } = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

    public RollingInterval RollingInterval { get; init; } = RollingInterval.Infinite;

    /// <summary>Null: no limit.</summary>
    public long? FileSizeLimitBytes { get; init; } = DefaultFileSizeLimitBytes;

    public bool RollOnFileSizeLimit { get; init; }

    /// <summary>Null: keep every file. Only applies when the sink rolls (a Day interval, or RollOnFileSizeLimit).</summary>
    public int? RetainedFileCountLimit { get; init; } = 31;

    public bool Shared { get; init; }

    /// <summary>How long a shared write waits for the other writer's mutex before it drops the event (Serilog: 10 s).</summary>
    public TimeSpan SharedWaitTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>After a timed-out wait, how long writes skip the mutex.</summary>
    public TimeSpan SharedBackoff { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The clock (tests move it across midnight).</summary>
    public Func<DateTimeOffset>? Now { get; init; }
}

public sealed class FileSink : ILogSink
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(5);

    private readonly object _lock = new();
    private readonly FileSinkOptions _options;
    private readonly OutputTemplate _template;
    private readonly string _directory;
    private readonly string _prefix;
    private readonly string _extension;
    private readonly bool _rolls;
    private readonly Regex _pattern;
    private readonly Func<DateTimeOffset> _now;
    private readonly Mutex? _mutex;

    private FileStream? _stream;
    private string? _currentPath;
    private DateTime _currentPeriod;
    private int _currentSequence;
    private DateTimeOffset _retryAt = DateTimeOffset.MinValue;
    private DateTimeOffset _mutexBackoffUntil = DateTimeOffset.MinValue;
    private bool _disposed;

    public FileSink(FileSinkOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _template = new OutputTemplate(options.OutputTemplate);
        var full = System.IO.Path.GetFullPath(options.Path);
        _directory = System.IO.Path.GetDirectoryName(full) ?? ".";
        _prefix = System.IO.Path.GetFileNameWithoutExtension(full);
        _extension = System.IO.Path.GetExtension(full);
        _rolls = options.RollingInterval != RollingInterval.Infinite || options.RollOnFileSizeLimit;
        _pattern = new Regex(
            "^" + Regex.Escape(_prefix) + (options.RollingInterval == RollingInterval.Day ? "(?<date>\\d{8})" : "")
                + "(?:_(?<seq>\\d{3,}))?" + Regex.Escape(_extension) + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        _now = options.Now ?? (() => DateTimeOffset.Now);
        if (options.Shared)
        {
            // Session-local, as Serilog's was (review B3): only a process in this user's own session
            // can hold it. A Global\ name could be pre-created and held by another user.
            try { _mutex = new Mutex(false, SharedMutexName(full)); }
            catch { _mutex = null; }
        }
        lock (_lock) TryOpen(_now());
    }

    /// <summary>The file events go to now (null when it could not be opened).</summary>
    public string? CurrentPath
    {
        get { lock (_lock) return _stream is null ? null : _currentPath; }
    }

    public void Emit(LogEvent logEvent, IReadOnlyDictionary<string, object?> properties)
    {
        var bytes = Utf8.GetBytes(_template.Render(logEvent, properties));
        lock (_lock)
        {
            if (_disposed) return;
            var now = _now();
            if (_stream is null && !TryOpen(now)) return;
            if (_options.RollingInterval == RollingInterval.Day && now.LocalDateTime.Date != _currentPeriod)
            {
                Close();
                if (!TryOpen(now)) return;
            }
            WriteOrRoll(bytes, now);
        }
    }

    private void WriteOrRoll(byte[] bytes, DateTimeOffset now)
    {
        // At most one roll per event: a fresh file always takes it.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var taken = false;
            try
            {
                if (_mutex is not null && now >= _mutexBackoffUntil)
                {
                    try { taken = _mutex.WaitOne(_options.SharedWaitTimeout); }
                    catch (AbandonedMutexException) { taken = true; }
                    if (!taken)
                    {
                        // A stuck holder: drop this event, as Serilog did, and write without the mutex
                        // for a while, so it costs one stall, not one per line.
                        _mutexBackoffUntil = now + _options.SharedBackoff;
                        return;
                    }
                }
                var stream = _stream!;
                if (_options.Shared) stream.Seek(0, SeekOrigin.End);
                if (_options.FileSizeLimitBytes is { } limit && stream.Length >= limit)
                {
                    if (!_options.RollOnFileSizeLimit || attempt > 0) return; // full: Serilog drops the event
                }
                else
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush();
                    return;
                }
            }
            catch
            {
                // A failed write drops the event; the file is reopened on the next one.
                Close();
                _retryAt = now + RetryAfter;
                return;
            }
            finally
            {
                if (taken)
                {
                    try { _mutex!.ReleaseMutex(); }
                    catch { /* not owned */ }
                }
            }
            Close();
            if (!TryOpen(now, _currentSequence + 1)) return;
        }
    }

    /// <summary>
    /// Opens this period's file: for a rolling sink, the highest sequence already on disk for the period
    /// (or <paramref name="minimumSequence"/>), moving past a file that cannot be opened. Then prunes.
    /// </summary>
    private bool TryOpen(DateTimeOffset now, int minimumSequence = 0)
    {
        if (now < _retryAt) return false;
        try
        {
            Directory.CreateDirectory(_directory);
            var period = now.LocalDateTime.Date;
            var sequence = minimumSequence;
            if (_rolls && minimumSequence == 0)
                sequence = ExistingFiles().Where(f => f.Period == period || _options.RollingInterval == RollingInterval.Infinite)
                    .Select(f => f.Sequence).DefaultIfEmpty(0).Max();
            for (var attempt = 0; attempt < 3; attempt++, sequence++)
            {
                var path = PathFor(period, sequence);
                try
                {
                    // A shared writer seeks to the end before each write, which FileMode.Append forbids.
                    // No FileShare.Delete (review B1): the live log cannot be deleted while it is open.
                    var stream = _options.Shared
                        ? new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.None)
                        : new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.None);
                    stream.Seek(0, SeekOrigin.End);
                    _stream = stream;
                    _currentPath = path;
                    _currentPeriod = period;
                    _currentSequence = sequence;
                    _retryAt = DateTimeOffset.MinValue;
                    if (_rolls) ApplyRetention(path);
                    return true;
                }
                catch (IOException) when (_rolls && attempt < 2)
                {
                    // Locked by another writer: Serilog moves on to the next sequence number.
                }
            }
        }
        catch
        {
            // Falls through: no file this time.
        }
        _stream = null;
        _retryAt = now + RetryAfter;
        return false;
    }

    private string PathFor(DateTime period, int sequence)
    {
        var name = _prefix
            + (_options.RollingInterval == RollingInterval.Day ? period.ToString("yyyyMMdd", CultureInfo.InvariantCulture) : "")
            + (sequence > 0 ? "_" + sequence.ToString("000", CultureInfo.InvariantCulture) : "")
            + _extension;
        return System.IO.Path.Combine(_directory, name);
    }

    private IEnumerable<(string Path, DateTime Period, int Sequence)> ExistingFiles()
    {
        foreach (var path in Directory.EnumerateFiles(_directory, _prefix + "*" + _extension))
        {
            var m = _pattern.Match(System.IO.Path.GetFileName(path));
            if (!m.Success) continue;
            var period = DateTime.MinValue;
            if (m.Groups["date"].Success
                && !DateTime.TryParseExact(m.Groups["date"].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out period))
                continue;
            var sequence = m.Groups["seq"].Success ? int.Parse(m.Groups["seq"].Value, CultureInfo.InvariantCulture) : 0;
            yield return (path, period, sequence);
        }
    }

    /// <summary>Keeps the newest <c>RetainedFileCountLimit</c> files of this sink's pattern, the current one included.</summary>
    private void ApplyRetention(string current)
    {
        if (_options.RetainedFileCountLimit is not { } keep || keep < 1) return;
        try
        {
            var old = ExistingFiles()
                .Where(f => !string.Equals(f.Path, current, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.Period)
                .ThenByDescending(f => f.Sequence)
                .Skip(keep - 1)
                .ToList();
            foreach (var f in old)
            {
                try { File.Delete(f.Path); }
                catch { /* in use (another tray, a viewer): it goes on a later roll */ }
            }
        }
        catch
        {
            // Retention is best effort.
        }
    }

    private void Close()
    {
        try { _stream?.Flush(); } catch { /* best effort */ }
        try { _stream?.Dispose(); } catch { /* best effort */ }
        _stream = null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            Close();
        }
        try { _mutex?.Dispose(); } catch { /* best effort */ }
    }

    /// <summary>
    /// The shared-mode mutex: <c>Local\RST.Log.&lt;hash&gt;</c>, session-local (review B3), one per file (the
    /// path, case-folded and hashed: a name cannot hold another backslash).
    /// </summary>
    internal static string SharedMutexName(string fullPath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(fullPath.ToUpperInvariant()));
        return @"Local\RST.Log." + Convert.ToHexString(hash, 0, 16);
    }
}

/// <summary>
/// The sink's line format: {Timestamp[:format]}, {Level[:u3|w3]}, {Message[:lj]}, {NewLine}, {Exception}
/// and any logger property (for example {ProcessId}). Alignment works as in message templates.
/// </summary>
public sealed class OutputTemplate
{
    private readonly (string? Literal, string? Name, string? Format, int Alignment)[] _parts;

    public OutputTemplate(string template)
    {
        var parts = new List<(string?, string?, string?, int)>();
        var text = new StringBuilder();
        var i = 0;
        template ??= "";
        while (i < template.Length)
        {
            var c = template[i];
            if (c == '{' && i + 1 < template.Length && template[i + 1] == '{') { text.Append('{'); i += 2; continue; }
            if (c == '}' && i + 1 < template.Length && template[i + 1] == '}') { text.Append('}'); i += 2; continue; }
            if (c == '{')
            {
                var close = template.IndexOf('}', i + 1);
                if (close > i + 1)
                {
                    var inner = template.Substring(i + 1, close - i - 1);
                    string? format = null;
                    var alignment = 0;
                    var colon = inner.IndexOf(':');
                    if (colon >= 0) { format = inner.Substring(colon + 1); inner = inner.Substring(0, colon); }
                    var comma = inner.IndexOf(',');
                    if (comma >= 0)
                    {
                        int.TryParse(inner.Substring(comma + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out alignment);
                        inner = inner.Substring(0, comma);
                    }
                    if (text.Length > 0) { parts.Add((text.ToString(), null, null, 0)); text.Clear(); }
                    parts.Add((null, inner, format, alignment));
                    i = close + 1;
                    continue;
                }
            }
            text.Append(c);
            i++;
        }
        if (text.Length > 0) parts.Add((text.ToString(), null, null, 0));
        _parts = parts.ToArray();
    }

    public string Render(LogEvent e, IReadOnlyDictionary<string, object?>? properties = null)
    {
        var sb = new StringBuilder(128);
        foreach (var (literal, name, format, alignment) in _parts)
        {
            if (literal is not null) { sb.Append(literal); continue; }
            string value;
            switch (name)
            {
                case "Timestamp":
                    value = e.Timestamp.ToString(format, CultureInfo.InvariantCulture);
                    break;
                case "Level":
                    value = LevelText(e.Level, format);
                    break;
                case "Message":
                    value = e.RenderMessage();
                    break;
                case "NewLine":
                    value = Environment.NewLine;
                    break;
                case "Exception":
                    value = e.Exception is null ? "" : SafeText(e.Exception) + Environment.NewLine;
                    break;
                default:
                    if (properties is null || name is null || !properties.TryGetValue(name, out var p)) continue;
                    value = MessageTemplate.Render("{0" + (format is null ? "" : ":" + format) + "}", new[] { p });
                    break;
            }
            var width = Math.Abs(alignment);
            if (alignment > 0 && value.Length < width) sb.Append(' ', width - value.Length);
            sb.Append(value);
            if (alignment < 0 && value.Length < width) sb.Append(' ', width - value.Length);
        }
        return sb.ToString();
    }

    private static string SafeText(Exception ex)
    {
        try { return ex.ToString(); }
        catch { return ex.GetType().FullName + ": " + ex.Message; }
    }

    /// <summary>Serilog's level names: u3 is VRB DBG INF WRN ERR FTL, w3 the same in lower case, otherwise the full name.</summary>
    public static string LevelText(LogLevel level, string? format)
    {
        var three = level switch
        {
            LogLevel.Verbose => "VRB",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Fatal => "FTL",
            _ => level.ToString().ToUpperInvariant(),
        };
        return format switch
        {
            "u3" => three,
            "w3" => three.ToLowerInvariant(),
            "u" => level.ToString().ToUpperInvariant(),
            "w" => level.ToString().ToLowerInvariant(),
            _ => level.ToString(),
        };
    }
}
