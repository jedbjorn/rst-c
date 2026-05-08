// RstApplication.cs — Revit IExternalApplication entry point.
//
// Lifecycle:
//   OnStartup               — wire RST tab + Loader button (UIControlledApplication path),
//                             configure logging, subscribe to ApplicationInitialized.
//   ApplicationInitialized  — Revit's main loop is running; UIApplication is available.
//                             Build the active profile tab via ProfileTabBuilder
//                             (AdWindows-direct), then unsubscribe (one-shot).
//   OnShutdown              — flush logs, dispose handlers, persist any pending state.

using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using RST.Core.Configuration;
using RST.Core.Profiles;
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

    private ControlledApplication? _controlledApp;
    private EventHandler<ApplicationInitializedEventArgs>? _initializedHandler;
    private static ProfileSwitchScheduler? _switchScheduler;

    /// <summary>
    /// Single ExternalEvent-backed switch scheduler shared across every
    /// LoaderCommand invocation. Created at OnStartup so the queued
    /// rebuild survives the Loader window closing — disposing it per
    /// invocation would race with Revit's idle pump.
    /// </summary>
    internal static IProfileSwitchScheduler? GetSwitchScheduler() => _switchScheduler;

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

            // Live switch infrastructure (RST-020). Created here so the
            // ExternalEvent outlives any single LoaderCommand session;
            // Revit fires the handler on its idle pump *after* the modal
            // window closes, so we can't tie disposal to the command.
            try { _switchScheduler = new ProfileSwitchScheduler(); }
            catch (Exception ex) { Log.Error(ex, "Failed to create ProfileSwitchScheduler — live switching will fall back to restart-required."); }

            // Defer the profile-tab build until Revit is fully initialised
            // and we have a UIApplication. ProfileTabBuilder uses AdWindows-
            // direct construction (the same path that powers live switching),
            // so the same code runs at first build and on every subsequent
            // Apply.
            _controlledApp = application.ControlledApplication;
            _initializedHandler = OnApplicationInitialized;
            _controlledApp.ApplicationInitialized += _initializedHandler;

            Log.Information("=== RST.OnStartup OK ===");
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "RST.OnStartup failed");
            return Result.Failed;
        }
    }

    private void OnApplicationInitialized(object? sender, ApplicationInitializedEventArgs e)
    {
        // One-shot — unhook so subsequent document opens don't re-fire us.
        if (_controlledApp is not null && _initializedHandler is not null)
        {
            try { _controlledApp.ApplicationInitialized -= _initializedHandler; }
            catch (Exception ex) { Log.Debug(ex, "ApplicationInitialized unsubscribe failed (non-fatal)"); }
            _initializedHandler = null;
        }

        try
        {
            if (sender is not Application app)
            {
                Log.Warning("ApplicationInitialized: sender was not Application ({Type}); skipping profile build", sender?.GetType().FullName ?? "null");
                return;
            }
            var uiApp = new UIApplication(app);

            var active = ActiveProfile.Read();
            if (active.IsBlank)
            {
                Log.Information("ApplicationInitialized: no active profile — ribbon shows Loader only.");
                return;
            }
            var entry = ProfileStore.Resolve(active.ProfileName, active.ProfileId);
            if (entry is null)
            {
                Log.Warning("ApplicationInitialized: active profile {Name} ({Id}) not found on disk — ribbon shows Loader only.",
                            active.ProfileName, active.ProfileId);
                return;
            }
            ProfileTabBuilder.BuildOrRebuild(uiApp, entry.Profile);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ApplicationInitialized: failed to build profile tab");
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
        if (_controlledApp is not null && _initializedHandler is not null)
        {
            try { _controlledApp.ApplicationInitialized -= _initializedHandler; }
            catch { /* fine — may have already unsubscribed */ }
        }
        try { _switchScheduler?.Dispose(); }
        catch (Exception ex) { Log.Debug(ex, "Switch scheduler dispose failed (non-fatal)"); }
        _switchScheduler = null;
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
