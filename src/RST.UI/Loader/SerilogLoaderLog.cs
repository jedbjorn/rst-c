// rst-c's own (not vendored): the Loader services' log lines go to Serilog (spec #16, WU3).

using System.IO;
using RST.Core.Loader;
using Serilog;

namespace RST.UI.Loader;

/// <summary>Writes the RST.Core Loader services' lines to the Serilog log (rst_*.log).</summary>
public sealed class SerilogLoaderLog : ILoaderLog
{
    public static readonly SerilogLoaderLog Instance = new();

    public void Debug(Exception? error, string template, params object?[] args) => Log.Debug(error, template, args);

    public void Information(string template, params object?[] args) => Log.Information(template, args);

    public void Warning(Exception? error, string template, params object?[] args) => Log.Warning(error, template, args);

    public void Error(Exception? error, string template, params object?[] args) => Log.Error(error, template, args);
}

/// <summary>The Loader's shipped files, next to RST.UI.dll (the same Assets folder RST.Engine copies into).</summary>
public static class LoaderAssets
{
    /// <summary>The folder holding RST.UI.dll and Assets\.</summary>
    public static string BaseDirectory => Path.GetDirectoryName(typeof(LoaderAssets).Assembly.Location)!;

    /// <summary>Assets\lookup\addin_lookup.json, the curated add-in registry.</summary>
    public static string LookupPath => Path.Combine(BaseDirectory, "Assets", "lookup", "addin_lookup.json");

    /// <summary>Assets\icons\pack, the colored icon pack (pack:&lt;name&gt;_&lt;color&gt; values).</summary>
    public static string IconPackDirectory => Path.Combine(BaseDirectory, "Assets", "icons", "pack");
}
