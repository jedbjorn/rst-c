// rst-c's own (not vendored): the in-house logger that replaced Serilog (decision #33). RST runs in
// Revit's shared default load context, where a same-named DLL another add-in ships can bind in place
// of RST's (Revit 2027 binds from other add-ins' folders before any resolver runs). So RST ships no
// third-party DLL, and logs through this small logger instead. It keeps Serilog's shape where the code
// relied on it: the static Log facade (Log.cs), message templates (MessageTemplate.cs) and a file sink
// with Serilog's file names, rolling and retention rules (FileSink.cs).

using System;
using System.Collections.Generic;
using System.Threading;

namespace RST.Core.Logging;

/// <summary>The levels, in Serilog's order and with its three-letter names (VRB … FTL).</summary>
public enum LogLevel
{
    Verbose,
    Debug,
    Information,
    Warning,
    Error,
    Fatal,
}

/// <summary>One log call: when, how severe, the template and its arguments, and the exception.</summary>
public sealed class LogEvent
{
    public LogEvent(DateTimeOffset timestamp, LogLevel level, string messageTemplate, object?[] propertyValues, Exception? exception)
    {
        Timestamp = timestamp;
        Level = level;
        MessageTemplate = messageTemplate ?? "";
        PropertyValues = propertyValues ?? Array.Empty<object?>();
        Exception = exception;
    }

    public DateTimeOffset Timestamp { get; }

    public LogLevel Level { get; }

    public string MessageTemplate { get; }

    /// <summary>The template's arguments, in call order.</summary>
    public IReadOnlyList<object?> PropertyValues { get; }

    public Exception? Exception { get; }

    /// <summary>The message with its arguments filled in (see <see cref="Logging.MessageTemplate"/>).</summary>
    public string RenderMessage() => Logging.MessageTemplate.Render(MessageTemplate, PropertyValues);
}

/// <summary>Where events go. <see cref="Emit"/> may throw; the logger catches it.</summary>
public interface ILogSink : IDisposable
{
    void Emit(LogEvent logEvent, IReadOnlyDictionary<string, object?> properties);
}

/// <summary>
/// A minimum level, a set of sinks and the properties every event carries (Serilog's enrichers, such as
/// <c>ProcessId</c>). Thread-safe; a write never throws. Disposing it flushes and closes its sinks, after
/// which writes are dropped.
/// </summary>
public sealed class Logger : IDisposable
{
    private static readonly IReadOnlyDictionary<string, object?> NoProperties = new Dictionary<string, object?>();

    private readonly ILogSink[] _sinks;
    private readonly IReadOnlyDictionary<string, object?> _properties;
    private int _disposed;

    public Logger(LogLevel minimumLevel, IEnumerable<ILogSink> sinks, IReadOnlyDictionary<string, object?>? properties = null)
    {
        MinimumLevel = minimumLevel;
        _sinks = sinks is null ? Array.Empty<ILogSink>() : new List<ILogSink>(sinks).ToArray();
        _properties = properties ?? NoProperties;
    }

    public Logger(LogLevel minimumLevel, params ILogSink[] sinks)
        : this(minimumLevel, (IEnumerable<ILogSink>)sinks)
    {
    }

    /// <summary>Writes nothing (the logger before configuration, and after <see cref="Log.CloseAndFlush"/>).</summary>
    public static Logger None { get; } = new(LogLevel.Fatal + 1, Array.Empty<ILogSink>());

    public LogLevel MinimumLevel { get; }

    public bool IsEnabled(LogLevel level) => level >= MinimumLevel && _sinks.Length > 0 && Volatile.Read(ref _disposed) == 0;

    public void Write(LogLevel level, Exception? exception, string messageTemplate, object?[] propertyValues)
    {
        try
        {
            if (!IsEnabled(level)) return;
            var e = new LogEvent(DateTimeOffset.Now, level, messageTemplate, propertyValues, exception);
            foreach (var sink in _sinks)
            {
                try { sink.Emit(e, _properties); }
                catch { /* a log call never throws (decision #33) */ }
            }
        }
        catch
        {
            // Never throw from a log call.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var sink in _sinks)
        {
            try { sink.Dispose(); }
            catch { /* best effort */ }
        }
    }
}
