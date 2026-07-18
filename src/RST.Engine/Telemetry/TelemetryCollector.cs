// TelemetryCollector.cs — subscribes to Revit events, captures identity
// on the UI thread (cheap reads + enqueue only), throttles pulses, runs
// the heartbeat, and owns the collector side of the enable/disable
// toggle (spec Architecture + Threading & Safety).
//
// The two deployability rules, enforced here:
//   - handlers do cheap capture + enqueue ONLY — the one file read
//     (BasicFileInfo) happens solely on full-block events;
//   - telemetry never takes Revit down — every handler body is guarded;
//     on failure the event drops, the first failure per site logs once
//     (via the injected callback → Serilog), and collection keeps going.
//
// Enable/disable semantics (spec Consent & Config): handlers check one
// volatile bool and return; nothing is ever unsubscribed while Revit
// runs. A session that starts disabled writes no file at all (the
// writer creates its file lazily on first write). SetEnabled flips the
// flag, frames the gap with collection_disabled/enabled markers, and
// stops/starts the heartbeat — persisting the preference (and the
// first-run notice) is the consent UI's job, not the collector's.
//
// Closed-file contract at shutdown: once a session_start has been
// written the file MUST end with session_end even if collection is
// disabled by then — otherwise the next startup's recovery scanner
// would "recover" a session that shut down cleanly.

using System;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using RST.Core.Configuration;
using RST.Core.Telemetry;

namespace RST.Engine.Telemetry;

public sealed class TelemetryCollector : IDisposable
{
    /// <summary>Spec: heartbeat every 2 minutes — the crash-recovery
    /// anchor (the writer fsyncs when it sees one).</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(5);

    private readonly Autodesk.Revit.ApplicationServices.ControlledApplication _app;
    private readonly UIControlledApplication _uiApp;
    private readonly TelemetrySession _session;
    private readonly OutboxWriter _writer;
    private readonly Action<string> _log;
    private readonly string _installId;
    private readonly string _addinVersion;

    private readonly PulseThrottle _pulseThrottle = new();
    private readonly ViewActivationGate _viewGate = new();
    private readonly OpenDocTracker _tracker = new();
    private readonly System.Collections.Generic.HashSet<string> _warnedSites = new();

    private readonly object _startGate = new();
    private Timer? _heartbeat;
    private volatile bool _enabled;
    private volatile bool _sessionStartWritten;
    private volatile string? _autodeskUser;
    private bool _shutdown;

    private TelemetryCollector(
        UIControlledApplication uiApp,
        TelemetrySession session,
        OutboxWriter writer,
        string installId,
        string addinVersion,
        bool enabled,
        Action<string> log)
    {
        _uiApp = uiApp;
        _app = uiApp.ControlledApplication;
        _session = session;
        _writer = writer;
        _installId = installId;
        _addinVersion = addinVersion;
        _enabled = enabled;
        _log = log;
    }

    /// <summary>True while collection is on — the volatile flag every
    /// handler checks. Flip via <see cref="SetEnabled"/>.</summary>
    public bool IsEnabled => _enabled;

    /// <summary>
    /// Read prefs, resolve the install id, open the session, subscribe,
    /// and (when enabled) start the heartbeat; session_start itself is
    /// emitted at ApplicationInitialized (see EnsureSessionStart).
    /// Recovery + retention run on a background task afterwards — never
    /// on the startup path, and regardless of the enabled flag: they
    /// maintain outbox files from PAST sessions, which the user can
    /// still view while collection is off.
    /// </summary>
    public static TelemetryCollector Start(
        UIControlledApplication application, string addinVersion, Action<string> log)
    {
        var prefs = TelemetryPrefs.Read();
        var installId = InstallIdStore.GetOrCreate(AppDataPaths.TelemetryRoot, log);
        var session = new TelemetrySession();
        var writer = new OutboxWriter(
            AppDataPaths.TelemetryOutboxDir, installId, session.SessionGuid, log: log);

        var collector = new TelemetryCollector(
            application, session, writer, installId, addinVersion, prefs.Enabled, log);

        writer.Start();
        collector.Subscribe();
        // session_start is NOT emitted here: autodesk_user lives on
        // Application (not ControlledApplication), which first exists at
        // ApplicationInitialized — the normal emission point. Any doc
        // event that beats it lazily emits session_start first (null
        // autodesk_user), so it is always the file's first record.
        if (collector._enabled) collector.StartHeartbeat();

        var outboxDir = writer.OutboxDir;
        var retentionDays = prefs.RetentionDays;
        Task.Run(() =>
        {
            try
            {
                // Recovery first: it closes orphans, which is what makes
                // them eligible for the age prune that follows.
                RecoveryScanner.Scan(outboxDir, log);
                RetentionPruner.Prune(outboxDir, retentionDays, DateTimeOffset.UtcNow, log);
            }
            catch (Exception ex)
            {
                try { log("telemetry maintenance failed: " + ex.Message); } catch { /* never */ }
            }
        });

        return collector;
    }

    /// <summary>
    /// Flip collection on/off (spec Consent & Config). Off: writes the
    /// collection_disabled marker, stops the heartbeat; handlers keep
    /// their subscriptions and no-op via the flag. On: writes
    /// session_start first if this session never has (a session enabled
    /// mid-flight opens its file at the moment collection begins), then
    /// collection_enabled, and resumes the heartbeat. Persisting the
    /// preference is the caller's responsibility.
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        if (enabled == _enabled) return;
        if (enabled)
        {
            _enabled = true;
            EnsureSessionStart();
            _writer.Enqueue(_session.Create(TelemetryEventTypes.CollectionEnabled));
            StartHeartbeat();
        }
        else
        {
            // Marker only when the session file exists: a lone
            // collection_disabled with no session_start would create a
            // file the closed-file contract can never satisfy cleanly.
            if (_sessionStartWritten)
                _writer.Enqueue(_session.Create(TelemetryEventTypes.CollectionDisabled));
            _enabled = false;
            StopHeartbeat();
        }
    }

    /// <summary>
    /// Drain and close: stop the heartbeat, unsubscribe, append
    /// session_end when a session_start was ever written, and join the
    /// writer (bounded — a stuck drain never blocks Revit shutdown).
    /// Call before Log.CloseAndFlush().
    /// </summary>
    public void Shutdown()
    {
        if (_shutdown) return;
        _shutdown = true;

        StopHeartbeat();
        Unsubscribe();
        if (_sessionStartWritten)
            _writer.Enqueue(_session.Create(TelemetryEventTypes.SessionEnd));
        _writer.CompleteAndJoin(ShutdownDrainTimeout);
    }

    public void Dispose() => Shutdown();

    // ---- wiring ---------------------------------------------------------

    private void Subscribe()
    {
        _app.ApplicationInitialized += OnApplicationInitialized;
        _app.DocumentOpening += OnDocumentOpening;
        _app.DocumentOpened += OnDocumentOpened;
        _app.DocumentClosing += OnDocumentClosing;
        _app.DocumentClosed += OnDocumentClosed;
        _app.DocumentSaved += OnDocumentSaved;
        _app.DocumentSavedAs += OnDocumentSavedAs;
        _app.DocumentSynchronizingWithCentral += OnSyncStart;
        _app.DocumentSynchronizedWithCentral += OnSyncEnd;
        _app.DocumentChanged += OnDocumentChanged;
        _uiApp.ViewActivated += OnViewActivated;
    }

    private void Unsubscribe()
    {
        Guarded("unsubscribe", () =>
        {
            _app.ApplicationInitialized -= OnApplicationInitialized;
            _app.DocumentOpening -= OnDocumentOpening;
            _app.DocumentOpened -= OnDocumentOpened;
            _app.DocumentClosing -= OnDocumentClosing;
            _app.DocumentClosed -= OnDocumentClosed;
            _app.DocumentSaved -= OnDocumentSaved;
            _app.DocumentSavedAs -= OnDocumentSavedAs;
            _app.DocumentSynchronizingWithCentral -= OnSyncStart;
            _app.DocumentSynchronizedWithCentral -= OnSyncEnd;
            _app.DocumentChanged -= OnDocumentChanged;
            _uiApp.ViewActivated -= OnViewActivated;
        });
    }

    private void StartHeartbeat()
    {
        if (_heartbeat is not null) return;
        _heartbeat = new Timer(OnHeartbeat, null, HeartbeatInterval, HeartbeatInterval);
    }

    private void StopHeartbeat()
    {
        try { _heartbeat?.Dispose(); }
        catch { /* best-effort */ }
        _heartbeat = null;
    }

    // ---- event emission -------------------------------------------------

    private void OnApplicationInitialized(object? sender, ApplicationInitializedEventArgs args)
    {
        Guarded("session_start", () =>
        {
            // Application.Username is the Autodesk sign-in; the
            // ControlledApplication we subscribe through doesn't carry it.
            _autodeskUser = Get(() =>
                (sender as Autodesk.Revit.ApplicationServices.Application)?.Username);
            if (_enabled) EnsureSessionStart();
        });
    }

    /// <summary>
    /// Emit session_start exactly once, before whatever record prompted
    /// the call — every emit site runs through here, so session_start is
    /// always the file's first line even when a startup document open
    /// beats ApplicationInitialized (autodesk_user is null then).
    /// </summary>
    private void EnsureSessionStart()
    {
        if (_sessionStartWritten) return;
        lock (_startGate)
        {
            if (_sessionStartWritten) return;
            var e = _session.Create(TelemetryEventTypes.SessionStart);
            e.SetField(TelemetryFields.MachineName, Get(() => Environment.MachineName));
            e.SetField(TelemetryFields.InstallId, _installId);
            e.SetField(TelemetryFields.OsUser, Get(() => Environment.UserName));
            e.SetField(TelemetryFields.AutodeskUser, _autodeskUser);
            e.SetField(TelemetryFields.RevitVersion, Get(() => _app.VersionNumber));
            e.SetField(TelemetryFields.RevitBuild, Get(() => _app.VersionBuild));
            e.SetField(TelemetryFields.AddinVersion, _addinVersion);
            _writer.Enqueue(e);
            _sessionStartWritten = true;
        }
    }

    private void OnHeartbeat(object? state)
    {
        // Timer thread, not UI: touches only thread-safe state (session
        // seq is interlocked, tracker is locked, writer is locked).
        Guarded("heartbeat", () =>
        {
            if (!_enabled) return;
            EnsureSessionStart();
            var e = _session.Create(TelemetryEventTypes.Heartbeat);
            e.SetField(TelemetryFields.OpenDocCount, _tracker.OpenCount);
            _writer.Enqueue(e);
        });
    }

    private void OnDocumentOpening(object? sender, DocumentOpeningEventArgs args)
    {
        Guarded("doc_opening", () =>
        {
            if (!_enabled) return;
            EnsureSessionStart();
            var e = _session.Create(TelemetryEventTypes.DocOpening);
            e.SetField(TelemetryFields.LocalPath, Get(() => args.PathName));
            _writer.Enqueue(e);
        });
    }

    private void OnDocumentOpened(object? sender, DocumentOpenedEventArgs args)
    {
        Guarded("doc_opened", () =>
        {
            var doc = args.Document;
            if (!IdentityCapture.IsTrackable(doc)) return;
            // Tracked regardless of the flag so open_doc_count is honest
            // if collection is enabled mid-session.
            _tracker.Opened();
            if (!_enabled) return;
            EnsureSessionStart();
            var e = _session.Create(TelemetryEventTypes.DocOpened);
            IdentityCapture.CaptureFull(doc!).WriteTo(e);
            _writer.Enqueue(e);
        });
    }

    private void OnDocumentClosing(object? sender, DocumentClosingEventArgs args)
    {
        Guarded("doc_closing", () =>
        {
            var doc = args.Document;
            if (!IdentityCapture.IsTrackable(doc)) return;
            // DocumentClosed won't expose the Document — remember the id
            // now (regardless of the flag, to keep the count honest).
            _tracker.Closing(args.DocumentId);
            if (!_enabled) return;
            EnsureSessionStart();
            var e = _session.Create(TelemetryEventTypes.DocClosing);
            IdentityCapture.CaptureKeys(doc!).WriteKeysTo(e);
            e.SetField(TelemetryFields.ClosingId, args.DocumentId);
            _writer.Enqueue(e);
        });
    }

    private void OnDocumentClosed(object? sender, DocumentClosedEventArgs args)
    {
        Guarded("doc_closed", () =>
        {
            var status = args.Status;
            var succeeded = status == RevitAPIEventStatus.Succeeded;
            // Unknown id ⇒ a document we never emitted doc_closing for
            // (linked, untrackable) — stay symmetric and emit nothing.
            if (!_tracker.TryCompleteClosing(args.DocumentId, succeeded)) return;
            if (!_enabled) return;
            EnsureSessionStart();
            var e = _session.Create(TelemetryEventTypes.DocClosed);
            e.SetField(TelemetryFields.ClosingId, args.DocumentId);
            e.SetField(TelemetryFields.Status, status.ToString());
            _writer.Enqueue(e);
        });
    }

    private void OnDocumentSaved(object? sender, DocumentSavedEventArgs args)
    {
        Guarded("doc_saved", () =>
        {
            if (!_enabled) return;
            EnsureSessionStart();
            var doc = args.Document;
            if (!IdentityCapture.IsTrackable(doc)) return;
            var e = _session.Create(TelemetryEventTypes.DocSaved);
            IdentityCapture.CaptureFull(doc!).WriteTo(e);
            _writer.Enqueue(e);
        });
    }

    private void OnDocumentSavedAs(object? sender, DocumentSavedAsEventArgs args)
    {
        Guarded("doc_saved_as", () =>
        {
            if (!_enabled) return;
            EnsureSessionStart();
            var doc = args.Document;
            if (!IdentityCapture.IsTrackable(doc)) return;
            var e = _session.Create(TelemetryEventTypes.DocSavedAs);
            IdentityCapture.CaptureFull(doc!).WriteTo(e);
            e.SetField(TelemetryFields.PreviousLocalPath, Get(() => args.OriginalPath));
            _writer.Enqueue(e);
        });
    }

    private void OnSyncStart(object? sender, DocumentSynchronizingWithCentralEventArgs args)
    {
        Guarded("sync_start", () =>
        {
            if (!_enabled) return;
            EnsureSessionStart();
            var doc = args.Document;
            if (!IdentityCapture.IsTrackable(doc)) return;
            var e = _session.Create(TelemetryEventTypes.SyncStart);
            IdentityCapture.CaptureKeys(doc!).WriteKeysTo(e);
            e.SetField(TelemetryFields.CentralPath, Get(() => args.Location));
            e.SetField(TelemetryFields.Comment, Get(() => args.Comments));
            _writer.Enqueue(e);
        });
    }

    private void OnSyncEnd(object? sender, DocumentSynchronizedWithCentralEventArgs args)
    {
        Guarded("sync_end", () =>
        {
            if (!_enabled) return;
            EnsureSessionStart();
            var doc = args.Document;
            if (!IdentityCapture.IsTrackable(doc)) return;
            var e = _session.Create(TelemetryEventTypes.SyncEnd);
            IdentityCapture.CaptureKeys(doc!).WriteKeysTo(e);
            e.SetField(TelemetryFields.Status, args.Status.ToString());
            _writer.Enqueue(e);
        });
    }

    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs args)
    {
        // The hottest handler — fires per transaction. Throttle FIRST on
        // the cheap tracking key; identity keys are read only when a
        // pulse actually emits.
        Guarded("doc_changed_pulse", () =>
        {
            if (!_enabled) return;
            EnsureSessionStart();
            var doc = args.GetDocument();
            if (!IdentityCapture.IsTrackable(doc)) return;
            if (!_pulseThrottle.TryAcquire(IdentityCapture.TrackingKey(doc!))) return;
            var e = _session.Create(TelemetryEventTypes.DocChangedPulse);
            IdentityCapture.CaptureKeys(doc!).WriteKeysTo(e);
            _writer.Enqueue(e);
        });
    }

    private void OnViewActivated(object? sender, ViewActivatedEventArgs args)
    {
        Guarded("view_activated", () =>
        {
            if (!_enabled) return;
            EnsureSessionStart();
            var doc = Get(() => args.Document);
            if (!IdentityCapture.IsTrackable(doc)) return;
            if (!_viewGate.ShouldEmit(IdentityCapture.TrackingKey(doc!))) return;
            var e = _session.Create(TelemetryEventTypes.ViewActivated);
            IdentityCapture.CaptureKeys(doc!).WriteKeysTo(e);
            _writer.Enqueue(e);
        });
    }

    // ---- safety ---------------------------------------------------------

    /// <summary>
    /// The never-take-Revit-down guard: on failure the event drops and
    /// the FIRST failure per site logs (DocumentChanged fires per
    /// transaction — a persistent fault must not flood the log).
    /// </summary>
    private void Guarded(string site, Action body)
    {
        try
        {
            body();
        }
        catch (Exception ex)
        {
            try
            {
                bool warn;
                lock (_warnedSites) warn = _warnedSites.Add(site);
                if (warn) _log("telemetry " + site + " failed (dropping event): " + ex.Message);
            }
            catch { /* logging must never take a handler down */ }
        }
    }

    private static T? Get<T>(Func<T?> read)
    {
        try { return read(); }
        catch { return default; }
    }
}
