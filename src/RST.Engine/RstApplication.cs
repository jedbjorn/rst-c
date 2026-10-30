// RstApplication.cs — Revit IExternalApplication entry point.
//
// Lifecycle:
//   OnStartup               — wire RST tab + Loader button (UIControlledApplication path),
//                             configure logging, subscribe to ApplicationInitialized.
//   ApplicationInitialized  — Revit's main loop is running; UIApplication is available.
//                             Start the RST tray if it isn't running (a thread-pool
//                             task, never awaited; spec #20 §2), build the active
//                             profile tab via ProfileTabBuilder (AdWindows-direct),
//                             then unsubscribe (one-shot).
//   OnShutdown              — flush logs, dispose handlers, persist any pending state.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using RST.Core.Configuration;
using RST.Core.Profiles;
using RST.Core.Scanning;
using RST.Engine.Ribbon;
using RST.Engine.Scanning;
using RST.Engine.Telemetry;
using RST.Engine.Tray;
using RST.Core.Logging;

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
    private UIControlledApplication? _uiControlledApp;
    private bool _reassertQueued;
    private bool _readyQueued;
    private static ProfileSwitchScheduler? _switchScheduler;
    private static TelemetryCollector? _telemetry;

    /// <summary>
    /// Single ExternalEvent-backed switch scheduler shared across every
    /// LoaderCommand invocation. Created at OnStartup so the queued
    /// rebuild survives the Loader window closing — disposing it per
    /// invocation would race with Revit's idle pump.
    /// </summary>
    internal static IProfileSwitchScheduler? GetSwitchScheduler() => _switchScheduler;

    /// <summary>
    /// The session's telemetry collector, for the consent/Activity
    /// wiring (HealthCommand reads the live session identity and routes
    /// the collection toggle here). Null when telemetry failed to start
    /// — callers treat that as "no live session". Static for the same
    /// reason as the switch scheduler: Revit holds one application
    /// instance, and command classes have no path to it.
    /// </summary>
    internal static TelemetryCollector? GetTelemetry() => _telemetry;

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
            LogInstallScope(rev);

            // Activity telemetry (doc #5). Handlers are cheap capture +
            // enqueue; all file IO lives on the collector's writer thread.
            // Its own guard: telemetry failing must never fail OnStartup.
            try { _telemetry = TelemetryCollector.Start(application, ThisVersion, m => Log.Warning("Telemetry: {Message}", m)); }
            catch (Exception ex) { Log.Warning(ex, "Telemetry startup failed — telemetry off for this session"); }

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

            // Revit rebuilds the ribbon tab set when the first document of
            // the session opens, silently un-hiding the tabs RSTify hid at
            // ApplicationInitialized (doc #4 addendum). Every view
            // activation queues a one-shot Idling re-assert — Idling runs
            // after the rebuild settles, and the re-assert is a no-op
            // unless a hide rule is in force and actually drifted.
            _uiControlledApp = application;
            application.ViewActivated += OnViewActivated;

            Log.Information("=== RST.OnStartup OK ===");
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "RST.OnStartup failed");
            // Result.Failed means Revit never calls OnShutdown — release
            // the collector's subscriptions and writer thread here or never.
            try { _telemetry?.Shutdown(); }
            catch (Exception tex) { Log.Debug(tex, "Telemetry rollback failed (non-fatal)"); }
            _telemetry = null;
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

        // Spec #20 §2 (AC2): how long Revit took to get here; then the tray launch, off Revit's UI thread and never
        // waited for; then, at Revit's first Idling, how long Revit took to be ready. That last line is AC2's
        // measure: it comes after the launch has started, so the launch's cost on Revit's start shows in it
        // (review B1).
        LogStartupTime("ApplicationInitialized");
        StartTrayInBackground();
        QueueReadyTime();

        var tab = System.Diagnostics.Stopwatch.StartNew();
        BuildProfileTab(sender);
        Log.Information("ApplicationInitialized: profile tab step took {Ms} ms", tab.ElapsedMilliseconds);

        // One-time consent notice (telemetry spec Consent & Config) —
        // after the profile tab is up, so the modal never sits between
        // Revit and the ribbon build. Runs on the paths that skip the
        // build too (no active profile); its own guard, never throws.
        ConsentNotice.ShowIfDue(_telemetry, m => Log.Warning("Telemetry: {Message}", m));
    }

    /// <summary>One line: the time from Revit.exe's start to <paramref name="point"/>.</summary>
    private static void LogStartupTime(string point)
    {
        try
        {
            using var revit = System.Diagnostics.Process.GetCurrentProcess();
            var elapsed = DateTime.Now - revit.StartTime;
            if (point == "ApplicationInitialized")
                Log.Information("Revit startup: ApplicationInitialized {Ms} ms after Revit.exe started", (long)elapsed.TotalMilliseconds);
            else
                Log.Information("Revit ready: first Idling {Ms} ms after Revit.exe started (after the tray launch was queued)", (long)elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Revit startup time not readable (non-fatal)");
        }
    }

    /// <summary>
    /// One-shot: at Revit's first Idling after ApplicationInitialized (the ribbon built, the start page up), log
    /// "Revit ready". Spec #20 AC2 compares this time with and without the tray launch (review B1).
    /// </summary>
    private void QueueReadyTime()
    {
        if (_uiControlledApp is null) return;
        try
        {
            _uiControlledApp.Idling += OnFirstIdling;
            _readyQueued = true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Revit ready time not queued (non-fatal)");
        }
    }

    private void OnFirstIdling(object? sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
    {
        if (!_readyQueued) return;
        _readyQueued = false;
        if (_uiControlledApp is not null)
        {
            try { _uiControlledApp.Idling -= OnFirstIdling; }
            catch (Exception ex) { Log.Debug(ex, "Idling unsubscribe failed (non-fatal)"); }
        }
        LogStartupTime("first Idling");
    }

    /// <summary>
    /// Starts the RST tray when it is not running (spec #20 §2): a thread-pool task that Revit never waits for. Its
    /// outcome is one log line (started, already running, switched off in Task Manager, Revit elevated, not
    /// installed, or why it failed). Nothing here can fail or delay Revit's startup.
    /// </summary>
    private static void StartTrayInBackground()
    {
        try
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var result = TrayLauncher.EnsureRunning(new MachineTrayLaunchEnvironment());
                    if (result.Outcome == TrayLaunchOutcome.Failed)
                        Log.Warning("Tray: {Message} ({Ms} ms, off the UI thread)", result.Message, sw.ElapsedMilliseconds);
                    else
                        Log.Information("Tray: {Message} ({Ms} ms, off the UI thread)", result.Message, sw.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Tray: launch check failed ({Ms} ms); Revit is unaffected", sw.ElapsedMilliseconds);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Tray: could not queue the launch check; Revit is unaffected");
        }
    }

    private static void BuildProfileTab(object? sender)
    {
        try
        {
            if (sender is not Application app)
            {
                Log.Warning("ApplicationInitialized: sender was not Application ({Type}); skipping profile build", sender?.GetType().FullName ?? "null");
                return;
            }
            var uiApp = new UIApplication(app);

            // Style the RST tools panel now that AwComponentManager.Ribbon
            // is populated. Applies regardless of whether a profile is
            // active — the four-button panel is permanent (RST-043).
            RibbonBuilder.ApplyToolsPanelStyling();

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

            // Apply hide rules at startup so the user sees the same ribbon
            // they last left. ApplyForActiveProfile both hides and flips
            // the icon, and shares its state-tracking with the live-switch
            // path so the two can't drift. Mirrors
            // /home/jedi/RST/startup.py lines 821-870.
            RstifyToggle.ApplyForActiveProfile(active.HiddenTabs);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ApplicationInitialized: failed to build profile tab");
        }
    }

    private void OnViewActivated(object? sender, Autodesk.Revit.UI.Events.ViewActivatedEventArgs e)
    {
        // Cheap gate on the UI thread: nothing to protect, nothing to do.
        if (_reassertQueued || !RstifyToggle.HasActiveHideRule) return;
        if (_uiControlledApp is null) return;
        try
        {
            // Defer to Idling — ViewActivated can fire before Revit
            // finishes rebuilding the tab set, and a re-hide applied
            // mid-rebuild would itself be reset.
            _uiControlledApp.Idling += OnIdlingReassert;
            _reassertQueued = true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ViewActivated: failed to queue RSTify re-assert");
        }
    }

    private void OnIdlingReassert(object? sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
    {
        _reassertQueued = false;
        if (_uiControlledApp is not null)
        {
            try { _uiControlledApp.Idling -= OnIdlingReassert; }
            catch (Exception ex) { Log.Debug(ex, "Idling unsubscribe failed (non-fatal)"); }
        }
        try { RstifyToggle.ReassertIfDrifted(); }
        catch (Exception ex) { Log.Warning(ex, "Idling: RSTify re-assert failed"); }
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
        if (_uiControlledApp is not null)
        {
            try { _uiControlledApp.ViewActivated -= OnViewActivated; }
            catch { /* fine */ }
            if (_reassertQueued)
            {
                try { _uiControlledApp.Idling -= OnIdlingReassert; }
                catch { /* fine */ }
            }
            if (_readyQueued)
            {
                try { _uiControlledApp.Idling -= OnFirstIdling; }
                catch { /* fine */ }
                _readyQueued = false;
            }
            _uiControlledApp = null;
        }
        try { _switchScheduler?.Dispose(); }
        catch (Exception ex) { Log.Debug(ex, "Switch scheduler dispose failed (non-fatal)"); }
        _switchScheduler = null;
        // Drain + join the telemetry writer (bounded) BEFORE the log
        // sink closes — the collector logs through Log.
        try { _telemetry?.Shutdown(); }
        catch (Exception ex) { Log.Debug(ex, "Telemetry shutdown failed (non-fatal)"); }
        _telemetry = null;
        Log.CloseAndFlush();
        return Result.Succeeded;
    }

    /// <summary>
    /// Hard cap on session logs we keep on disk. Each Revit launch produces
    /// two files under <c>%AppData%\RST\logs\</c> with a shared timestamp
    /// prefix: <c>rst_&lt;ts&gt;_boot.log</c> (written by RST.Bootstrap, see
    /// RST-033) and <c>rst_&lt;ts&gt;.log</c> (the engine session log). The
    /// prune glob <c>rst_*.log</c> matches both, so the cap is sized at
    /// 10 = 5 sessions × {boot, engine}. The file sink's own
    /// <c>RetainedFileCountLimit</c> doesn't help — it only prunes
    /// size-rolled files derived from the same base path. RST-032: also
    /// surfaced as an OOB cleanup target in <see cref="CleanupDefaults"/>
    /// so users have a hard exit via the Health tool if the boot-time
    /// prune ever fails.
    /// </summary>
    private const int MaxSessionLogs = 10;

    /// <summary>
    /// Once per startup (feature #17, spec #20 §7): which installer's copy is running, and, when RST is installed
    /// both for this user (RST.msi) and for everyone (RST-Machine.msi), that Revit loads the user's own copy and
    /// how to use the shared one. Information only; never fails OnStartup.
    /// </summary>
    private static void LogInstallScope(string revitVersion)
    {
        try
        {
            var engine = typeof(RstApplication).Assembly.Location;
            var scope = InstallLayout.ScopeOf(engine, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            Log.Information("Install scope: {Scope}",
                scope == RstInstallScope.Machine ? "for everyone on this PC (RST-Machine.msi)" : "for this user (RST.msi)");
            if (int.TryParse(revitVersion, out var year))
                foreach (var shadow in InstallLayout.FindShadows(new[] { year }))
                    Log.Warning("{Shadow}", InstallLayout.Describe(shadow));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Install scope check failed");
        }
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

        // Prune BEFORE the file sink opens its handle so we don't fight
        // the writer. The new file isn't on disk yet — the path string
        // we just minted is excluded defensively in case it survived
        // a prior crashed launch with the same second.
        PruneOldSessionLogs(logsDir, sessionLog);

        // DEBUG minimum during dev so per-step traces (catalog stages, window
        // actions, the theme detected) land in the file. Promote to
        // Information once the loader/builder are stable.
        // One file per session, no roll, Serilog's default 1 GB cap (decision #33: the in-house
        // logger keeps the Serilog configuration's file, format and limits).
        Log.Logger = new Logger(
            LogLevel.Debug,
            new ILogSink[]
            {
                new FileSink(new FileSinkOptions
                {
                    Path = sessionLog,
                    OutputTemplate = "{Timestamp:HH:mm:ss.fff} {Level:u3} {Message:lj}{NewLine}{Exception}",
                    RollingInterval = RollingInterval.Infinite,
                }),
            },
            new Dictionary<string, object?> { ["Component"] = "RST.Engine" });
    }

    private static void PruneOldSessionLogs(string logsDir, string newSessionPath)
    {
        try
        {
            var existing = Directory.GetFiles(logsDir, AppDataPaths.EngineSessionLogGlob)
                .Where(p => !string.Equals(p, newSessionPath, StringComparison.OrdinalIgnoreCase))
                .Select(p => new FileInfo(p))
                .Where(fi => fi.Exists)
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .ToArray();

            // We're about to add one new file; keep (Max - 1) most-recent
            // old files so the cap holds at Max once the new one writes.
            int keep = Math.Max(0, MaxSessionLogs - 1);
            foreach (var fi in existing.Skip(keep))
            {
                try { fi.Delete(); }
                catch
                {
                    // Best-effort. A locked file (concurrent Revit instance,
                    // tail/grep on the file) just lingers until the next
                    // launch — or the user can hit RST Logs in the Health
                    // cleanup modal as the hard exit.
                }
            }
        }
        catch
        {
            // Logger isn't up yet, so we can't surface this — and that's
            // the whole point of the OOB cleanup-target safety net. Boot
            // continues even if pruning fails entirely.
        }
    }

    private static string ThisVersion =>
        typeof(RstApplication).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}
