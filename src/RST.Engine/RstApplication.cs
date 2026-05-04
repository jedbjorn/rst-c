// RstApplication.cs — Revit IExternalApplication entry point.
//
// Lifecycle:
//   OnStartup  — construct ribbon (branding panel + active profile), wire
//                handlers, kick off the scanner pre-warm.
//   OnShutdown — flush logs, dispose handlers, persist any pending state.

using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.UI;
using RST.Core.Configuration;
using RST.Core.Scanning;
using RST.Engine.Ribbon;
using RST.Engine.Scanning;
using Serilog;

namespace RST.Engine;

[Autodesk.Revit.Attributes.Regeneration(Autodesk.Revit.Attributes.RegenerationOption.Manual)]
[Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]
public sealed class RstApplication : IExternalApplication
{
    private static readonly object CatalogLock = new();
    private static IReadOnlyList<ScannedCommand>? _catalogCache;
    private static string? _catalogVersion;

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            ConfigureLogging();
            var rev = application.ControlledApplication.VersionNumber;
            Log.Information("=== RST {Version} starting on Revit {RevitVersion} ===",
                            ThisVersion, rev);
            Log.Information("Paths: appData={AppData}, profilesDir={ProfilesDir}, " +
                            "activeProfileFile={ActiveProfile}, banList={BanList}, " +
                            "engineAssembly={EngineDll}",
                            AppDataPaths.Root, AppDataPaths.ProfilesDir,
                            AppDataPaths.ActiveProfileFile, BanList.DefaultPath,
                            typeof(RstApplication).Assembly.Location);

            RibbonBuilder.Build(application);
            Log.Information("=== RST.OnStartup OK ===");
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "RST.OnStartup failed");
            return Result.Failed;
        }
    }

    /// <summary>
    /// Lazily build (and cache) the command catalog for the running Revit
    /// session. Built on first Loader open rather than at OnStartup so
    /// startup cost stays low. Subsequent calls return the cache.
    /// </summary>
    public static IReadOnlyList<ScannedCommand> GetOrBuildCatalog(string revitVersion)
    {
        lock (CatalogLock)
        {
            if (_catalogCache is not null && _catalogVersion == revitVersion)
            {
                Log.Debug("GetOrBuildCatalog: cache hit ({Count} commands, revit={Version})",
                          _catalogCache.Count, revitVersion);
                return _catalogCache;
            }

            Log.Information("GetOrBuildCatalog: cold cache for revit={Version}, building…", revitVersion);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var catalog = CommandCatalog.Build(revitVersion);
                _catalogCache = catalog.Commands;
                _catalogVersion = revitVersion;
                Log.Information("GetOrBuildCatalog OK: {Count} commands in {Ms}ms",
                                _catalogCache.Count, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "GetOrBuildCatalog FAILED after {Ms}ms; serving empty list", sw.ElapsedMilliseconds);
                _catalogCache = Array.Empty<ScannedCommand>();
                _catalogVersion = revitVersion;
            }
            return _catalogCache;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        Log.Information("RST shutting down");
        Log.CloseAndFlush();
        return Result.Succeeded;
    }

    private static void ConfigureLogging()
    {
        var logsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RST", "logs");
        Directory.CreateDirectory(logsDir);

        var sessionLog = Path.Combine(
            logsDir,
            $"rst_{DateTime.Now:yyyy-MM-dd_HHmmss}.log");

        // DEBUG minimum during dev so per-step traces (catalog stages, bridge
        // entry args, navigation source) land in the file. Promote to
        // Information once the loader/builder are stable.
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.WithProperty("Component", "RST.Engine")
            .WriteTo.File(sessionLog,
                          outputTemplate: "{Timestamp:HH:mm:ss.fff} {Level:u3} {Message:lj}{NewLine}{Exception}",
                          rollingInterval: RollingInterval.Infinite,
                          retainedFileCountLimit: 20)
            .CreateLogger();
    }

    private static string ThisVersion =>
        typeof(RstApplication).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}
