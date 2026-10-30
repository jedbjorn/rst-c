// rst-c's own (not vendored): the logging seam for the Loader services (spec #16, WU3).
//
// RST.Core has no logging package; the services extracted from LoaderBridge log through this
// interface instead, with Serilog-style message templates. RST.UI passes an adapter to RST.Core.Logging.Log, so
// the lines land in the same rst_*.log file the bridge wrote to. Tests pass nothing.

namespace RST.Core.Loader;

/// <summary>Where the Loader services write their log lines (message templates, Serilog style).</summary>
public interface ILoaderLog
{
    void Debug(Exception? error, string template, params object?[] args);

    void Information(string template, params object?[] args);

    void Warning(Exception? error, string template, params object?[] args);

    void Error(Exception? error, string template, params object?[] args);
}

/// <summary>Drops every line (tests, and callers that pass no log).</summary>
public sealed class NullLoaderLog : ILoaderLog
{
    public static readonly NullLoaderLog Instance = new();

    public void Debug(Exception? error, string template, params object?[] args) { }

    public void Information(string template, params object?[] args) { }

    public void Warning(Exception? error, string template, params object?[] args) { }

    public void Error(Exception? error, string template, params object?[] args) { }
}
