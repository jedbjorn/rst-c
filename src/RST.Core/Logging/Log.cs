// rst-c's own (not vendored): the static logging facade (decision #33). The same members, overloads and
// argument order as Serilog's static Log class, so a call site moved from Serilog by changing only its
// using line: `using Serilog;` became `using RST.Core.Logging;`.

using System;
using System.Threading;

namespace RST.Core.Logging;

/// <summary>
/// The process's logger. RstApplication (the engine, inside Revit) and TrayLog (RST.Tray.exe) assign
/// <see cref="Logger"/> once at startup and call <see cref="CloseAndFlush"/> on shutdown; until then, and
/// after, every call is dropped. Every member is thread-safe, and none throws.
/// </summary>
public static class Log
{
    private static Logger _logger = Logging.Logger.None;

    /// <summary>The logger every call goes to. Setting it does not dispose the previous one (as in Serilog).</summary>
    public static Logger Logger
    {
        get => Volatile.Read(ref _logger);
        set => Volatile.Write(ref _logger, value ?? Logging.Logger.None);
    }

    /// <summary>Flushes and closes the current logger, and sets <see cref="Logger"/> back to <see cref="Logging.Logger.None"/>.</summary>
    public static void CloseAndFlush()
    {
        try { Interlocked.Exchange(ref _logger, Logging.Logger.None).Dispose(); }
        catch { /* never throws */ }
    }

    public static bool IsEnabled(LogLevel level) => Logger.IsEnabled(level);

    public static void Write(LogLevel level, Exception? exception, string messageTemplate, params object?[]? propertyValues) =>
        Logger.Write(level, exception, messageTemplate, propertyValues ?? Array.Empty<object?>());

    // The generic overloads keep a single array argument from expanding into several properties
    // (Serilog has the same set for the same reason).

    private static void W(LogLevel level, Exception? exception, string messageTemplate, object?[] values)
    {
        var logger = Logger;
        if (logger.IsEnabled(level)) logger.Write(level, exception, messageTemplate, values);
    }

    private static object?[] A<T0>(T0 a) => new object?[] { a };

    private static object?[] A<T0, T1>(T0 a, T1 b) => new object?[] { a, b };

    private static object?[] A<T0, T1, T2>(T0 a, T1 b, T2 c) => new object?[] { a, b, c };

    // Verbose
    public static void Verbose(string messageTemplate) => W(LogLevel.Verbose, null, messageTemplate, Array.Empty<object?>());
    public static void Verbose<T0>(string messageTemplate, T0 p0) { if (IsEnabled(LogLevel.Verbose)) W(LogLevel.Verbose, null, messageTemplate, A(p0)); }
    public static void Verbose<T0, T1>(string messageTemplate, T0 p0, T1 p1) { if (IsEnabled(LogLevel.Verbose)) W(LogLevel.Verbose, null, messageTemplate, A(p0, p1)); }
    public static void Verbose<T0, T1, T2>(string messageTemplate, T0 p0, T1 p1, T2 p2) { if (IsEnabled(LogLevel.Verbose)) W(LogLevel.Verbose, null, messageTemplate, A(p0, p1, p2)); }
    public static void Verbose(string messageTemplate, params object?[]? propertyValues) => W(LogLevel.Verbose, null, messageTemplate, propertyValues ?? Array.Empty<object?>());
    public static void Verbose(Exception? exception, string messageTemplate) => W(LogLevel.Verbose, exception, messageTemplate, Array.Empty<object?>());
    public static void Verbose<T0>(Exception? exception, string messageTemplate, T0 p0) { if (IsEnabled(LogLevel.Verbose)) W(LogLevel.Verbose, exception, messageTemplate, A(p0)); }
    public static void Verbose<T0, T1>(Exception? exception, string messageTemplate, T0 p0, T1 p1) { if (IsEnabled(LogLevel.Verbose)) W(LogLevel.Verbose, exception, messageTemplate, A(p0, p1)); }
    public static void Verbose<T0, T1, T2>(Exception? exception, string messageTemplate, T0 p0, T1 p1, T2 p2) { if (IsEnabled(LogLevel.Verbose)) W(LogLevel.Verbose, exception, messageTemplate, A(p0, p1, p2)); }
    public static void Verbose(Exception? exception, string messageTemplate, params object?[]? propertyValues) => W(LogLevel.Verbose, exception, messageTemplate, propertyValues ?? Array.Empty<object?>());

    // Debug
    public static void Debug(string messageTemplate) => W(LogLevel.Debug, null, messageTemplate, Array.Empty<object?>());
    public static void Debug<T0>(string messageTemplate, T0 p0) { if (IsEnabled(LogLevel.Debug)) W(LogLevel.Debug, null, messageTemplate, A(p0)); }
    public static void Debug<T0, T1>(string messageTemplate, T0 p0, T1 p1) { if (IsEnabled(LogLevel.Debug)) W(LogLevel.Debug, null, messageTemplate, A(p0, p1)); }
    public static void Debug<T0, T1, T2>(string messageTemplate, T0 p0, T1 p1, T2 p2) { if (IsEnabled(LogLevel.Debug)) W(LogLevel.Debug, null, messageTemplate, A(p0, p1, p2)); }
    public static void Debug(string messageTemplate, params object?[]? propertyValues) => W(LogLevel.Debug, null, messageTemplate, propertyValues ?? Array.Empty<object?>());
    public static void Debug(Exception? exception, string messageTemplate) => W(LogLevel.Debug, exception, messageTemplate, Array.Empty<object?>());
    public static void Debug<T0>(Exception? exception, string messageTemplate, T0 p0) { if (IsEnabled(LogLevel.Debug)) W(LogLevel.Debug, exception, messageTemplate, A(p0)); }
    public static void Debug<T0, T1>(Exception? exception, string messageTemplate, T0 p0, T1 p1) { if (IsEnabled(LogLevel.Debug)) W(LogLevel.Debug, exception, messageTemplate, A(p0, p1)); }
    public static void Debug<T0, T1, T2>(Exception? exception, string messageTemplate, T0 p0, T1 p1, T2 p2) { if (IsEnabled(LogLevel.Debug)) W(LogLevel.Debug, exception, messageTemplate, A(p0, p1, p2)); }
    public static void Debug(Exception? exception, string messageTemplate, params object?[]? propertyValues) => W(LogLevel.Debug, exception, messageTemplate, propertyValues ?? Array.Empty<object?>());

    // Information
    public static void Information(string messageTemplate) => W(LogLevel.Information, null, messageTemplate, Array.Empty<object?>());
    public static void Information<T0>(string messageTemplate, T0 p0) { if (IsEnabled(LogLevel.Information)) W(LogLevel.Information, null, messageTemplate, A(p0)); }
    public static void Information<T0, T1>(string messageTemplate, T0 p0, T1 p1) { if (IsEnabled(LogLevel.Information)) W(LogLevel.Information, null, messageTemplate, A(p0, p1)); }
    public static void Information<T0, T1, T2>(string messageTemplate, T0 p0, T1 p1, T2 p2) { if (IsEnabled(LogLevel.Information)) W(LogLevel.Information, null, messageTemplate, A(p0, p1, p2)); }
    public static void Information(string messageTemplate, params object?[]? propertyValues) => W(LogLevel.Information, null, messageTemplate, propertyValues ?? Array.Empty<object?>());
    public static void Information(Exception? exception, string messageTemplate) => W(LogLevel.Information, exception, messageTemplate, Array.Empty<object?>());
    public static void Information<T0>(Exception? exception, string messageTemplate, T0 p0) { if (IsEnabled(LogLevel.Information)) W(LogLevel.Information, exception, messageTemplate, A(p0)); }
    public static void Information<T0, T1>(Exception? exception, string messageTemplate, T0 p0, T1 p1) { if (IsEnabled(LogLevel.Information)) W(LogLevel.Information, exception, messageTemplate, A(p0, p1)); }
    public static void Information<T0, T1, T2>(Exception? exception, string messageTemplate, T0 p0, T1 p1, T2 p2) { if (IsEnabled(LogLevel.Information)) W(LogLevel.Information, exception, messageTemplate, A(p0, p1, p2)); }
    public static void Information(Exception? exception, string messageTemplate, params object?[]? propertyValues) => W(LogLevel.Information, exception, messageTemplate, propertyValues ?? Array.Empty<object?>());

    // Warning
    public static void Warning(string messageTemplate) => W(LogLevel.Warning, null, messageTemplate, Array.Empty<object?>());
    public static void Warning<T0>(string messageTemplate, T0 p0) { if (IsEnabled(LogLevel.Warning)) W(LogLevel.Warning, null, messageTemplate, A(p0)); }
    public static void Warning<T0, T1>(string messageTemplate, T0 p0, T1 p1) { if (IsEnabled(LogLevel.Warning)) W(LogLevel.Warning, null, messageTemplate, A(p0, p1)); }
    public static void Warning<T0, T1, T2>(string messageTemplate, T0 p0, T1 p1, T2 p2) { if (IsEnabled(LogLevel.Warning)) W(LogLevel.Warning, null, messageTemplate, A(p0, p1, p2)); }
    public static void Warning(string messageTemplate, params object?[]? propertyValues) => W(LogLevel.Warning, null, messageTemplate, propertyValues ?? Array.Empty<object?>());
    public static void Warning(Exception? exception, string messageTemplate) => W(LogLevel.Warning, exception, messageTemplate, Array.Empty<object?>());
    public static void Warning<T0>(Exception? exception, string messageTemplate, T0 p0) { if (IsEnabled(LogLevel.Warning)) W(LogLevel.Warning, exception, messageTemplate, A(p0)); }
    public static void Warning<T0, T1>(Exception? exception, string messageTemplate, T0 p0, T1 p1) { if (IsEnabled(LogLevel.Warning)) W(LogLevel.Warning, exception, messageTemplate, A(p0, p1)); }
    public static void Warning<T0, T1, T2>(Exception? exception, string messageTemplate, T0 p0, T1 p1, T2 p2) { if (IsEnabled(LogLevel.Warning)) W(LogLevel.Warning, exception, messageTemplate, A(p0, p1, p2)); }
    public static void Warning(Exception? exception, string messageTemplate, params object?[]? propertyValues) => W(LogLevel.Warning, exception, messageTemplate, propertyValues ?? Array.Empty<object?>());

    // Error
    public static void Error(string messageTemplate) => W(LogLevel.Error, null, messageTemplate, Array.Empty<object?>());
    public static void Error<T0>(string messageTemplate, T0 p0) { if (IsEnabled(LogLevel.Error)) W(LogLevel.Error, null, messageTemplate, A(p0)); }
    public static void Error<T0, T1>(string messageTemplate, T0 p0, T1 p1) { if (IsEnabled(LogLevel.Error)) W(LogLevel.Error, null, messageTemplate, A(p0, p1)); }
    public static void Error<T0, T1, T2>(string messageTemplate, T0 p0, T1 p1, T2 p2) { if (IsEnabled(LogLevel.Error)) W(LogLevel.Error, null, messageTemplate, A(p0, p1, p2)); }
    public static void Error(string messageTemplate, params object?[]? propertyValues) => W(LogLevel.Error, null, messageTemplate, propertyValues ?? Array.Empty<object?>());
    public static void Error(Exception? exception, string messageTemplate) => W(LogLevel.Error, exception, messageTemplate, Array.Empty<object?>());
    public static void Error<T0>(Exception? exception, string messageTemplate, T0 p0) { if (IsEnabled(LogLevel.Error)) W(LogLevel.Error, exception, messageTemplate, A(p0)); }
    public static void Error<T0, T1>(Exception? exception, string messageTemplate, T0 p0, T1 p1) { if (IsEnabled(LogLevel.Error)) W(LogLevel.Error, exception, messageTemplate, A(p0, p1)); }
    public static void Error<T0, T1, T2>(Exception? exception, string messageTemplate, T0 p0, T1 p1, T2 p2) { if (IsEnabled(LogLevel.Error)) W(LogLevel.Error, exception, messageTemplate, A(p0, p1, p2)); }
    public static void Error(Exception? exception, string messageTemplate, params object?[]? propertyValues) => W(LogLevel.Error, exception, messageTemplate, propertyValues ?? Array.Empty<object?>());

    // Fatal
    public static void Fatal(string messageTemplate) => W(LogLevel.Fatal, null, messageTemplate, Array.Empty<object?>());
    public static void Fatal<T0>(string messageTemplate, T0 p0) { if (IsEnabled(LogLevel.Fatal)) W(LogLevel.Fatal, null, messageTemplate, A(p0)); }
    public static void Fatal<T0, T1>(string messageTemplate, T0 p0, T1 p1) { if (IsEnabled(LogLevel.Fatal)) W(LogLevel.Fatal, null, messageTemplate, A(p0, p1)); }
    public static void Fatal<T0, T1, T2>(string messageTemplate, T0 p0, T1 p1, T2 p2) { if (IsEnabled(LogLevel.Fatal)) W(LogLevel.Fatal, null, messageTemplate, A(p0, p1, p2)); }
    public static void Fatal(string messageTemplate, params object?[]? propertyValues) => W(LogLevel.Fatal, null, messageTemplate, propertyValues ?? Array.Empty<object?>());
    public static void Fatal(Exception? exception, string messageTemplate) => W(LogLevel.Fatal, exception, messageTemplate, Array.Empty<object?>());
    public static void Fatal<T0>(Exception? exception, string messageTemplate, T0 p0) { if (IsEnabled(LogLevel.Fatal)) W(LogLevel.Fatal, exception, messageTemplate, A(p0)); }
    public static void Fatal<T0, T1>(Exception? exception, string messageTemplate, T0 p0, T1 p1) { if (IsEnabled(LogLevel.Fatal)) W(LogLevel.Fatal, exception, messageTemplate, A(p0, p1)); }
    public static void Fatal<T0, T1, T2>(Exception? exception, string messageTemplate, T0 p0, T1 p1, T2 p2) { if (IsEnabled(LogLevel.Fatal)) W(LogLevel.Fatal, exception, messageTemplate, A(p0, p1, p2)); }
    public static void Fatal(Exception? exception, string messageTemplate, params object?[]? propertyValues) => W(LogLevel.Fatal, exception, messageTemplate, propertyValues ?? Array.Empty<object?>());
}
