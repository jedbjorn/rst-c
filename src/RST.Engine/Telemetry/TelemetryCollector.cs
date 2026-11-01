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
//     against a LocalPathGuard-verified local path. The heavier
//     model_stats capture (the local file's length, again only through
//     LocalPathGuard, plus Document.GetWarnings after a save or sync —
//     never after an open, decision #71) never runs inside an event
//     handler: open/save/sync only REQUEST it for that exact document,
//     and it runs at the next Revit Idling (spec #24 Capture Additions,
//     ModelStatsGate). The exception is a document that is closing —
//     it is gone before Idling, so its pending or during-close capture
//     runs in the closing/save/sync handler while the Document is valid;
//   - telemetry never takes Revit down — every handler body is guarded;
//     on failure the event drops, the first failure per site logs once
//     (via the injected callback → Log), and collection keeps going.
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
// preference is the consent UI's job (the Collection switch on Health →
// Activity), not the collector's. There is no first-run dialog
// (decision #49).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
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
    private readonly Action<string>? _info;
    private readonly string _installId;
    private readonly string _addinVersion;

    private readonly PulseThrottle _pulseThrottle = new();
    private readonly ViewActivationGate _viewGate = new();
    private readonly OpenDocTracker _tracker = new();
    private readonly ModelStatsGate _statsGate = new();
    // UI-thread-only, like the gate: true while OnIdlingModelStats is
    // subscribed. Idling is hooked only while a capture is pending —
    // never left on, since it fires continuously while Revit is idle.
    private bool _statsIdlingHooked;
    // DocumentClosed carries only the event id (and the Document is gone
    // by then): the IdentityCapture.DocumentId each closing document had,
    // so the gate's closing state can be ended without touching it.
    private readonly Dictionary<int, string> _closingStatsDocs = new();
    private readonly System.Collections.Generic.HashSet<string> _warnedSites = new();

    private readonly CollectorLifecycle _lifecycle;
    private readonly TelemetrySession _session;
    // Per-session constants, read once at construction so session_start
    // enrichment (which runs under the lifecycle gate) touches no Revit
    // API (SC-030).
    private readonly string? _revitVersion;
    private readonly string? _revitBuild;
    private readonly string? _revitSubversion;
    private volatile string? _autodeskUser;

    private TelemetryCollector(
        UIControlledApplication uiApp,
        TelemetrySession session,
        OutboxWriter writer,
        string installId,
        string addinVersion,
        bool enabled,
        Action<string> log,
        Action<string>? info)
    {
        _uiApp = uiApp;
        _app = uiApp.ControlledApplication;
        _session = session;
        _installId = installId;
        _addinVersion = addinVersion;
        _log = log;
        _info = info;
        _revitVersion = Get(() => _app.VersionNumber);
        _revitBuild = Get(() => _app.VersionBuild);
        _revitSubversion = Get(() => _app.SubVersionNumber);
        _lifecycle = new CollectorLifecycle(
            session, writer, enabled,
            enrichSessionStart: EnrichSessionStart,
            enrichHeartbeat: e => e.SetField(TelemetryFields.OpenDocCount, _tracker.OpenCount),
            log: log);
    }

    /// <summary>True while collection is on. Flip via <see cref="SetEnabled"/>.</summary>
    public bool IsEnabled => _lifecycle.IsEnabled;

    /// <summary>This session's guid — the Activity tab hands it to the
    /// aggregator so this session's outbox file reads as live.</summary>
    public string SessionGuid => _session.SessionGuid;

    /// <summary>ts of this session's session_start event, once written;
    /// null until then (and for the whole session when collection never
    /// runs). The Activity tab's current-session clock ticks from it.</summary>
    public DateTimeOffset? SessionStartUtc => _lifecycle.SessionStartUtc;

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
    /// <paramref name="log"/> takes failures (warning level);
    /// <paramref name="info"/>, when given, takes the per-capture
    /// model_stats timing line (information level).
    /// </summary>
    public static TelemetryCollector Start(
        UIControlledApplication application, string addinVersion, Action<string> log,
        Action<string>? info = null)
    {
        var prefs = TelemetryPrefs.Read();
        var installId = InstallIdStore.GetOrCreate(AppDataPaths.TelemetryRoot, log);
        var session = new TelemetrySession();
        var writer = new OutboxWriter(
            AppDataPaths.TelemetryOutboxDir, installId, session.SessionGuid, log: log);

        var collector = new TelemetryCollector(
            application, session, writer, installId, addinVersion, prefs.Enabled, log, info);

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
            if (_statsIdlingHooked)
            {
                _uiApp.Idling -= OnIdlingModelStats;
                _statsIdlingHooked = false;
            }
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
        e.SetField(TelemetryFields.RevitSubversion, _revitSubversion);
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
            if (!DocumentCapturePolicy.AllowsOpening(path)) return;
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
            RequestModelStats(doc!, ModelStatsTrigger.Open);
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
            // A pending model_stats request would be dropped at Idling
            // (the model is gone by then): capture it now, while the
            // Document is valid, before doc_closing (review WU2-01).
            ModelStatsClosing(doc!, args.DocumentId);
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
            ModelStatsClosingEnded(args.DocumentId);
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
            RequestModelStats(doc!, ModelStatsTrigger.Save);
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
            RequestModelStats(doc!, ModelStatsTrigger.Save);
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
            // only — never an overwrite, and only for a KNOWN non-cloud
            // model: cloud and unknown-cloud-ness alike keep central_path
            // null (SC-032, decision #3).
            if (keys.CentralPath is null && DocumentIdentity.AllowsCentralPath(keys.IsCloud))
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
            RequestModelStats(doc!, ModelStatsTrigger.Sync);
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

    // ---- model_stats (spec #24 Capture Additions) --------------------------

    /// <summary>
    /// After an open, save or sync: ask for a model_stats capture of THIS
    /// document at the next Idling. Cheap — two key reads, a list insert,
    /// and (for the first pending document) one Idling subscription. The
    /// one exception: a save or sync while the document is closing (Close
    /// → Save, Synchronize and Close) captures at once, since the document
    /// is gone before Idling (review WU2-01). Its own guard, so a failure
    /// here never costs the open/save/sync record already emitted.
    /// </summary>
    private void RequestModelStats(Document doc, ModelStatsTrigger trigger)
    {
        Guarded("model_stats_request", () =>
        {
            if (!_lifecycle.IsEnabled) return;
            var key = IdentityCapture.TrackingKey(doc);
            switch (_statsGate.Request(IdentityCapture.DocumentId(doc), key, trigger))
            {
                case ModelStatsDecision.CaptureNow:
                    CaptureModelStats(doc, key, trigger.MeasuresWarnings());
                    break;
                case ModelStatsDecision.Defer when !_statsIdlingHooked:
                    _uiApp.Idling += OnIdlingModelStats;
                    _statsIdlingHooked = true;
                    break;
            }
        });
    }

    /// <summary>
    /// DocumentClosing: mark this document closing (so a save or sync
    /// during the close captures at once), and capture its own request
    /// still pending from an earlier open, save or sync. Another open copy
    /// of the same model keeps its request. Runs whatever the collection
    /// flag (marking is in-process only); captures only while collecting.
    /// </summary>
    private void ModelStatsClosing(Document doc, int closingId)
    {
        Guarded("model_stats_closing", () =>
        {
            var documentId = IdentityCapture.DocumentId(doc);
            _closingStatsDocs[closingId] = documentId;
            if (_statsGate.Closing(documentId) is { } request && _lifecycle.IsEnabled)
                CaptureModelStats(doc, request.ModelKey, request.MeasureWarnings);
        });
    }

    /// <summary>DocumentClosed, succeeded or cancelled: the document is no
    /// longer closing (a cancelled close's later saves defer again). Uses
    /// the id remembered at closing; never touches the closed Document.</summary>
    private void ModelStatsClosingEnded(int closingId)
    {
        Guarded("model_stats_closed", () =>
        {
            if (_closingStatsDocs.Remove(closingId, out var documentId))
                _statsGate.ClosingEnded(documentId);
        });
    }

    /// <summary>
    /// One-shot per batch of requests: unhook, then capture each requested
    /// document that is still open — matched by its document id, so with
    /// two copies of one model open the one that asked is the one sampled
    /// (rc.1 VM, F2). The open documents are found through Idling's sender
    /// (the UIApplication) rather than held from the requesting event, so
    /// a document closed before Idling is simply not found. Each capture
    /// has its own guard: one failing capture never stops the others.
    /// </summary>
    private void OnIdlingModelStats(object? sender, Autodesk.Revit.UI.Events.IdlingEventArgs args)
    {
        Guarded("model_stats_idling", () =>
        {
            if (_statsIdlingHooked)
            {
                _uiApp.Idling -= OnIdlingModelStats;
                _statsIdlingHooked = false;
            }

            var requested = _statsGate.TakePending();
            if (requested.Count == 0 || !_lifecycle.IsEnabled) return;

            var documents = (sender as UIApplication)?.Application.Documents;
            if (documents is null) return;
            var open = new List<Document>();
            foreach (Document doc in documents) open.Add(doc);

            var resolved = ModelStatsGate.Resolve(requested, open,
                doc => IdentityCapture.IsTrackable(doc) ? IdentityCapture.DocumentId(doc) : null);
            foreach (var (doc, request) in resolved)
            {
                Guarded("model_stats", () =>
                {
                    if (!_statsGate.CanEmit(request.ModelKey)) return;
                    CaptureModelStats(doc, request.ModelKey, request.MeasureWarnings);
                });
            }
        });
    }

    /// <summary>
    /// Read the stats (Revit reads BEFORE the lifecycle gate, SC-030)
    /// and emit. file_size_bytes is the local file's length through
    /// LocalPathGuard only; null for cloud, network and unsaved models.
    /// warnings_count is read only when <paramref name="measureWarnings"/>
    /// (after a save or sync); an open-only capture writes it null, "not
    /// measured" (decision #71: GetWarnings froze Revit 2026 for 3.2 s
    /// after an open). The timing line is AC 10's evidence ("under 100 ms
    /// per capture").
    /// </summary>
    private void CaptureModelStats(Document doc, string key, bool measureWarnings)
    {
        var total = Stopwatch.StartNew();
        var keys = IdentityCapture.CaptureKeys(doc);

        int? warnings = null;
        long warningsMs = 0;
        if (measureWarnings)
        {
            var warningsWatch = Stopwatch.StartNew();
            warnings = Get<int?>(() => doc.GetWarnings().Count);
            warningsMs = warningsWatch.ElapsedMilliseconds;
        }

        var size = keys.IsCloud == true
            ? null
            : LocalPathGuard.LocalFileLength(Get(() => doc.PathName));
        total.Stop();

        var emitted = _lifecycle.TryEmit(TelemetryEventTypes.ModelStats,
            populate: e =>
            {
                keys.WriteKeysTo(e);
                e.SetField(TelemetryFields.WarningsCount, warnings);
                e.SetField(TelemetryFields.FileSizeBytes, size);
            },
            admit: () => _statsGate.TryAcquire(key));
        if (!emitted) return;

        try
        {
            var warningsPart = measureWarnings
                ? "GetWarnings " + warningsMs + " ms, " + (warnings?.ToString() ?? "unknown") + " warnings"
                : "GetWarnings skipped (open)";
            _info?.Invoke(
                "model_stats captured: " + warningsPart + ", file size " +
                (size is null ? "not read" : "read") + ", capture " + total.ElapsedMilliseconds + " ms");
        }
        catch { /* logging must never take a handler down */ }
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
