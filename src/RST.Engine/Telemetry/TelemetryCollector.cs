// TelemetryCollector.cs — the Revit-facing half of activity telemetry:
// subscribes to Revit events, captures identity on the UI thread (cheap
// reads + enqueue only), and throttles pulses. Everything that must be
// provably ordered — enable/disable marker framing, session_start-once,
// the heartbeat timer, startup rollback, shutdown — lives in
// CollectorLifecycle (RST.Core), where it is unit-tested without Revit
// (spec Architecture + Threading & Safety; SC-028/SC-029).
//
// The two deployability rules, enforced here:
//   - handlers do cheap capture + enqueue ONLY — the one file read
//     (BasicFileInfo) happens solely on full-block events, and only
//     against a LocalPathGuard-verified local path;
//   - telemetry never takes Revit down — every handler body is guarded;
//     on failure the event drops, the first failure per site logs once
//     (via the injected callback → Serilog), and collection keeps going.
//
// Enable/disable semantics (spec Consent & Config): handlers pre-check
// the lifecycle's flag and return; nothing is ever unsubscribed while
// Revit runs. A session that starts disabled writes no file at all (the
// writer creates its file lazily on first write). Every emission runs
// under the lifecycle's transition gate, so the markers strictly frame
// the gap — but all Revit reads (identity capture, args fields, the
// BasicFileInfo read) happen BEFORE the gate; only the enabled check,
// throttle commit, ordering, and enqueue run inside (spec Threading &
// Safety: no locks held across Revit API calls; SC-030). Persisting the
// preference (and the first-run notice) is the consent UI's job, not
// the collector's.

using System;
using System.Threading.Tasks;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using RST.Core.Configuration;
using RST.Core.Telemetry;

namespace RST.Engine.Telemetry;

public sealed class TelemetryCollector : IDisposable
{
    private readonly Autodesk.Revit.ApplicationServices.ControlledApplication _app;
    private readonly UIControlledApplication _uiApp;
    private readonly Action<string> _log;
    private readonly string _installId;
    private readonly string _addinVersion;

    private readonly PulseThrottle _pulseThrottle = new();
    private readonly ViewActivationGate _viewGate = new();
    private readonly OpenDocTracker _tracker = new();
    private readonly System.Collections.Generic.HashSet<string> _warnedSites = new();

    private readonly CollectorLifecycle _lifecycle;
    // Per-session constants, read once at construction so session_start
    // enrichment (which runs under the lifecycle gate) touches no Revit
    // API (SC-030).
    private readonly string? _revitVersion;
    private readonly string? _revitBuild;
    private volatile string? _autodeskUser;

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
        _installId = installId;
        _addinVersion = addinVersion;
        _log = log;
        _revitVersion = Get(() => _app.VersionNumber);
        _revitBuild = Get(() => _app.VersionBuild);
        _lifecycle = new CollectorLifecycle(
            session, writer, enabled,
            enrichSessionStart: EnrichSessionStart,
            enrichHeartbeat: e => e.SetField(TelemetryFields.OpenDocCount, _tracker.OpenCount),
            log: log);
    }

    /// <summary>True while collection is on. Flip via <see cref="SetEnabled"/>.</summary>
    public bool IsEnabled => _lifecycle.IsEnabled;

    /// <summary>
    /// Read prefs, resolve the install id, open the session, subscribe,
    /// and (when enabled) start the heartbeat; session_start itself is
    /// emitted at ApplicationInitialized (see OnApplicationInitialized).
    /// Recovery + retention run on a background task afterwards — never
    /// on the startup path, and regardless of the enabled flag: they
    /// maintain outbox files from PAST sessions, which the user can
    /// still view while collection is off. A failure at any step rolls
    /// the partial start back (handlers detached, writer completed)
    /// before rethrowing — no live thread survives a failed Start.
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

        // session_start is NOT emitted here: autodesk_user lives on
        // Application (not ControlledApplication), which first exists at
        // ApplicationInitialized — the normal emission point. Any doc
        // event that beats it lazily emits session_start first (null
        // autodesk_user), so it is always the file's first record.
        var outboxDir = writer.OutboxDir;
        var retentionDays = prefs.RetentionDays;
        collector._lifecycle.RunStartup(
            subscribe: collector.Subscribe,
            unsubscribe: collector.Unsubscribe,
            startMaintenance: () => Task.Run(() =>
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
            }));

        return collector;
    }

    /// <summary>Flip collection on/off (spec Consent & Config); the
    /// lifecycle serializes the markers, flag, and heartbeat under one
    /// gate. Persisting the preference is the caller's responsibility.</summary>
    public void SetEnabled(bool enabled) => _lifecycle.SetEnabled(enabled);

    /// <summary>
    /// Drain and close: stop the heartbeat, append session_end when a
    /// session_start was ever written, unsubscribe, and join the writer
    /// (bounded — a stuck drain never blocks Revit shutdown). Call
    /// before Log.CloseAndFlush().
    /// </summary>
    public void Shutdown() => _lifecycle.Shutdown();

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

    // ---- event emission -------------------------------------------------

    /// <summary>session_start enrichment — runs under the lifecycle
    /// gate at first emission, so autodesk_user is whatever
    /// ApplicationInitialized has captured by then. Reads cached fields
    /// and Environment only — no Revit API under the gate (SC-030).</summary>
    private void EnrichSessionStart(TelemetryEvent e)
    {
        e.SetField(TelemetryFields.MachineName, Get(() => Environment.MachineName));
        e.SetField(TelemetryFields.InstallId, _installId);
        e.SetField(TelemetryFields.OsUser, Get(() => Environment.UserName));
        e.SetField(TelemetryFields.AutodeskUser, _autodeskUser);
        e.SetField(TelemetryFields.RevitVersion, _revitVersion);
        e.SetField(TelemetryFields.RevitBuild, _revitBuild);
        e.SetField(TelemetryFields.AddinVersion, _addinVersion);
    }

    private void OnApplicationInitialized(object? sender, ApplicationInitializedEventArgs args)
    {
        Guarded("session_start", () =>
        {
            // Application.Username is the Autodesk sign-in; the
            // ControlledApplication we subscribe through doesn't carry it.
            _autodeskUser = Get(() =>
                (sender as Autodesk.Revit.ApplicationServices.Application)?.Username);
            _lifecycle.EmitSessionStartIfEnabled();
        });
    }

    private void OnDocumentOpening(object? sender, DocumentOpeningEventArgs args)
    {
        Guarded("doc_opening", () =>
        {
            if (!_lifecycle.IsEnabled) return;
            var path = Get(() => args.PathName);
            _lifecycle.TryEmit(TelemetryEventTypes.DocOpening,
                populate: e => e.SetField(TelemetryFields.LocalPath, path));
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
            if (!_lifecycle.IsEnabled) return;
            var identity = IdentityCapture.CaptureFull(doc!);
            _lifecycle.TryEmit(TelemetryEventTypes.DocOpened, populate: identity.WriteTo);
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
            if (!_lifecycle.IsEnabled) return;
            var keys = IdentityCapture.CaptureKeys(doc!);
            var closingId = args.DocumentId;
            _lifecycle.TryEmit(TelemetryEventTypes.DocClosing, populate: e =>
            {
                keys.WriteKeysTo(e);
                e.SetField(TelemetryFields.ClosingId, closingId);
            });
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
            if (!_lifecycle.IsEnabled) return;
            var closingId = args.DocumentId;
            _lifecycle.TryEmit(TelemetryEventTypes.DocClosed, populate: e =>
            {
                e.SetField(TelemetryFields.ClosingId, closingId);
                e.SetField(TelemetryFields.Status, status.ToString());
            });
        });
    }

    private void OnDocumentSaved(object? sender, DocumentSavedEventArgs args)
    {
        Guarded("doc_saved", () =>
        {
            if (!_lifecycle.IsEnabled) return;
            var doc = args.Document;
            if (!IdentityCapture.IsTrackable(doc)) return;
            var identity = IdentityCapture.CaptureFull(doc!);
            _lifecycle.TryEmit(TelemetryEventTypes.DocSaved, populate: identity.WriteTo);
        });
    }

    private void OnDocumentSavedAs(object? sender, DocumentSavedAsEventArgs args)
    {
        Guarded("doc_saved_as", () =>
        {
            if (!_lifecycle.IsEnabled) return;
            var doc = args.Document;
            if (!IdentityCapture.IsTrackable(doc)) return;
            var identity = IdentityCapture.CaptureFull(doc!);
            var previousPath = Get(() => args.OriginalPath);
            _lifecycle.TryEmit(TelemetryEventTypes.DocSavedAs, populate: e =>
            {
                identity.WriteTo(e);
                e.SetField(TelemetryFields.PreviousLocalPath, previousPath);
            });
        });
    }

    private void OnSyncStart(object? sender, DocumentSynchronizingWithCentralEventArgs args)
    {
        Guarded("sync_start", () =>
        {
            if (!_lifecycle.IsEnabled) return;
            var doc = args.Document;
            if (!IdentityCapture.IsTrackable(doc)) return;
            var keys = IdentityCapture.CaptureKeys(doc!);
            // args.Location is a fallback for a lost central-path read
            // only — never an overwrite, and never for cloud models,
            // whose central_path stays null (SC-032, decision #3).
            if (keys.CentralPath is null && keys.IsCloud != true)
                keys.CentralPath = Get(() => args.Location);
            var comment = Get(() => args.Comments);
            _lifecycle.TryEmit(TelemetryEventTypes.SyncStart, populate: e =>
            {
                keys.WriteKeysTo(e);
                e.SetField(TelemetryFields.Comment, comment);
            });
        });
    }

    private void OnSyncEnd(object? sender, DocumentSynchronizedWithCentralEventArgs args)
    {
        Guarded("sync_end", () =>
        {
            if (!_lifecycle.IsEnabled) return;
            var doc = args.Document;
            if (!IdentityCapture.IsTrackable(doc)) return;
            var keys = IdentityCapture.CaptureKeys(doc!);
            var status = args.Status.ToString();
            _lifecycle.TryEmit(TelemetryEventTypes.SyncEnd, populate: e =>
            {
                keys.WriteKeysTo(e);
                e.SetField(TelemetryFields.Status, status);
            });
        });
    }

    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs args)
    {
        // The hottest handler — fires per transaction. Peek the throttle
        // FIRST on the cheap tracking key (pure, no state change — a
        // refused TryAcquire records nothing either, so skipping here is
        // identical); identity keys are read only when a pulse will
        // actually emit, and they are read BEFORE the gate (SC-030). The
        // authoritative acquire commits inside TryEmit so a refusal by
        // the gate (a racing consent flip) can't burn a throttle window.
        // UI-thread-only state stays UI-thread-only — the gate adds
        // mutual ordering, not new callers.
        Guarded("doc_changed_pulse", () =>
        {
            if (!_lifecycle.IsEnabled) return;
            var doc = args.GetDocument();
            if (!IdentityCapture.IsTrackable(doc)) return;
            var key = IdentityCapture.TrackingKey(doc!);
            if (!_pulseThrottle.CanAcquire(key)) return;
            var keys = IdentityCapture.CaptureKeys(doc!);
            _lifecycle.TryEmit(TelemetryEventTypes.DocChangedPulse,
                populate: keys.WriteKeysTo,
                admit: () => _pulseThrottle.TryAcquire(key));
        });
    }

    private void OnViewActivated(object? sender, ViewActivatedEventArgs args)
    {
        // Keys are captured eagerly here (low-frequency handler): the
        // gate's ShouldEmit must stay a SINGLE call because a
        // throttle-refused document change still updates "current
        // document" — splitting it into peek + commit would let a later
        // within-doc view switch read as a change. In-process state
        // only inside the gate; capture stays outside (SC-030).
        Guarded("view_activated", () =>
        {
            if (!_lifecycle.IsEnabled) return;
            var doc = Get(() => args.Document);
            if (!IdentityCapture.IsTrackable(doc)) return;
            var key = IdentityCapture.TrackingKey(doc!);
            var keys = IdentityCapture.CaptureKeys(doc!);
            _lifecycle.TryEmit(TelemetryEventTypes.ViewActivated,
                populate: keys.WriteKeysTo,
                admit: () => _viewGate.ShouldEmit(key));
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
