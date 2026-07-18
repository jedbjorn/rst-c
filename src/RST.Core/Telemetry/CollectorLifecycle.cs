// CollectorLifecycle.cs — the Revit-free half of the telemetry
// collector: enable/disable gating, session_start-once, the heartbeat
// timer, the marker framing around consent flips, and startup/shutdown
// rollback. Extracted from TelemetryCollector so these ordering and
// rollback rules are unit-testable without Revit (RST.Tests references
// RST.Core only); the collector keeps the Revit subscriptions and
// identity capture.
//
// Everything that must be provably ordered serializes behind ONE lock
// (_gate): a consent flip and any concurrent emission (doc handler on
// the UI thread, heartbeat on a timer thread) cannot interleave, so
// collection_disabled / collection_enabled strictly frame the gap — no
// record lands after the disabled marker or before the enabled marker
// (spec Consent & Config).
//
// The gate holds NO Revit API calls and no file IO (spec Threading &
// Safety; SC-030): callers capture everything Revit-derived BEFORE
// TryEmit, and the callbacks that do run under the gate (populate,
// admit, the enrichers) are in-process only — throttle state, cached
// fields, counters. Inside stays: the enabled/shutdown check, the
// throttle commit, session_start/seq ordering, and the enqueue.
//
// Closed-file contract: once session_start has been written the file
// MUST end with session_end even if collection is disabled by then.
// Shutdown writes it under the same lock and flips the shutdown latch
// there, after which every emission path refuses — session_end is
// provably the last record.

using System.Threading;

namespace RST.Core.Telemetry;

public sealed class CollectorLifecycle : IDisposable
{
    /// <summary>Spec: heartbeat every 2 minutes — the crash-recovery
    /// anchor (the writer fsyncs when it sees one).</summary>
    public static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromMinutes(2);

    /// <summary>Bounded writer drain at shutdown — a stuck drain never
    /// blocks Revit shutdown.</summary>
    public static readonly TimeSpan DefaultShutdownDrainTimeout = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly TelemetrySession _session;
    private readonly OutboxWriter _writer;
    private readonly Action<TelemetryEvent> _enrichSessionStart;
    private readonly Action<TelemetryEvent> _enrichHeartbeat;
    private readonly Action<string> _log;
    private readonly TimeSpan _heartbeatInterval;
    private readonly TimeSpan _shutdownDrainTimeout;

    private Timer? _heartbeat;
    private Action? _unsubscribe;
    private volatile bool _enabled;
    private volatile bool _shutdown;
    private bool _sessionStartWritten;
    private DateTimeOffset? _sessionStartUtc;
    private bool _heartbeatWarned;

    public CollectorLifecycle(
        TelemetrySession session,
        OutboxWriter writer,
        bool enabled,
        Action<TelemetryEvent> enrichSessionStart,
        Action<TelemetryEvent> enrichHeartbeat,
        Action<string> log,
        TimeSpan? heartbeatInterval = null,
        TimeSpan? shutdownDrainTimeout = null)
    {
        _session = session;
        _writer = writer;
        _enabled = enabled;
        _enrichSessionStart = enrichSessionStart;
        _enrichHeartbeat = enrichHeartbeat;
        _log = log;
        _heartbeatInterval = heartbeatInterval ?? DefaultHeartbeatInterval;
        _shutdownDrainTimeout = shutdownDrainTimeout ?? DefaultShutdownDrainTimeout;
    }

    /// <summary>Lock-free pre-check for the hot handler paths; every
    /// emission re-checks under the gate.</summary>
    public bool IsEnabled => _enabled;

    /// <summary>True once session_start has been enqueued this session.</summary>
    public bool SessionStartWritten { get { lock (_gate) return _sessionStartWritten; } }

    /// <summary>The session_start event's ts, once one has been written
    /// this session — the Activity tab's live-session anchor (spec:
    /// "ticking client-side from session_start.ts"). Null until then;
    /// stays null for a session that never collects.</summary>
    public DateTimeOffset? SessionStartUtc { get { lock (_gate) return _sessionStartUtc; } }

    internal bool HeartbeatRunning { get { lock (_gate) return _heartbeat is not null; } }

    /// <summary>
    /// Start the writer, subscribe, and (when enabled) start the
    /// heartbeat. Any failure — including a later startup step the
    /// caller runs through <paramref name="startMaintenance"/> — rolls
    /// the partial start back via <see cref="Shutdown"/> and rethrows,
    /// so no live handlers or writer thread outlive a failed start.
    /// <paramref name="unsubscribe"/> is registered BEFORE subscribe
    /// runs: a mid-sequence subscription failure still detaches
    /// whatever attached (event -= on a never-attached handler is a
    /// no-op).
    /// </summary>
    public void RunStartup(Action subscribe, Action unsubscribe, Action? startMaintenance = null)
    {
        try
        {
            _writer.Start();
            _unsubscribe = unsubscribe;
            subscribe();
            lock (_gate)
            {
                if (_enabled && !_shutdown) StartHeartbeatLocked();
            }
            startMaintenance?.Invoke();
        }
        catch
        {
            try { Shutdown(); }
            catch { /* rollback must never mask the startup failure */ }
            throw;
        }
    }

    /// <summary>
    /// Gated emission: under the transition lock, when enabled and not
    /// shut down, ensure session_start, ask <paramref name="admit"/>
    /// (the caller's throttle commit — refusal enqueues nothing but
    /// cannot burn a window on a gate refusal), then create the event
    /// (seq order = file order), apply <paramref name="populate"/>, and
    /// enqueue. Both callbacks run UNDER the gate and must be
    /// in-process only — no Revit API, no file IO (SC-030); capture
    /// Revit-derived data before calling. False when the gate or admit
    /// refused.
    /// </summary>
    public bool TryEmit(string eventType, Action<TelemetryEvent>? populate = null, Func<bool>? admit = null)
    {
        lock (_gate)
        {
            if (_shutdown || !_enabled) return false;
            EnsureSessionStartLocked();
            if (admit is not null && !admit()) return false;
            var e = _session.Create(eventType);
            populate?.Invoke(e);
            _writer.Enqueue(e);
            return true;
        }
    }

    /// <summary>session_start alone, if enabled — the
    /// ApplicationInitialized path, which carries no record of its own.</summary>
    public void EmitSessionStartIfEnabled()
    {
        lock (_gate)
        {
            if (_shutdown || !_enabled) return;
            EnsureSessionStartLocked();
        }
    }

    /// <summary>
    /// Flip collection on/off (spec Consent & Config). The whole
    /// transition — marker, flag, heartbeat — happens under the gate,
    /// so no concurrent emission can land on the wrong side of the
    /// marker. Off: collection_disabled (only if the session file
    /// exists — a lone marker could never satisfy the closed-file
    /// contract), heartbeat stops. On: session_start first if this
    /// session never wrote one, then collection_enabled, heartbeat
    /// resumes. Persisting the preference is the caller's job.
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (_shutdown || enabled == _enabled) return;
            if (enabled)
            {
                _enabled = true;
                EnsureSessionStartLocked();
                _writer.Enqueue(_session.Create(TelemetryEventTypes.CollectionEnabled));
                StartHeartbeatLocked();
            }
            else
            {
                if (_sessionStartWritten)
                    _writer.Enqueue(_session.Create(TelemetryEventTypes.CollectionDisabled));
                _enabled = false;
                StopHeartbeatLocked();
            }
        }
    }

    /// <summary>
    /// Stop the heartbeat, append session_end when a session_start was
    /// ever written, detach the subscriptions, and join the writer
    /// (bounded). Safe to call at any point of a partial start;
    /// idempotent.
    /// </summary>
    public void Shutdown()
    {
        lock (_gate)
        {
            if (_shutdown) return;
            _shutdown = true;
            StopHeartbeatLocked();
            if (_sessionStartWritten)
                _writer.Enqueue(_session.Create(TelemetryEventTypes.SessionEnd));
        }
        // From here every emission path refuses, so a still-subscribed
        // handler can no longer write after session_end — unsubscribing
        // outside the lock keeps Revit's event mutation off our gate.
        try { _unsubscribe?.Invoke(); }
        catch (Exception ex) { SafeLog("telemetry unsubscribe failed: " + ex.Message); }
        _writer.CompleteAndJoin(_shutdownDrainTimeout);
    }

    public void Dispose() => Shutdown();

    /// <summary>Timer callback + test seam. Never throws (an unhandled
    /// timer-thread exception kills Revit); first failure logs once.</summary>
    internal void EmitHeartbeat()
    {
        try
        {
            TryEmit(TelemetryEventTypes.Heartbeat, populate: _enrichHeartbeat);
        }
        catch (Exception ex)
        {
            bool warn;
            lock (_gate)
            {
                warn = !_heartbeatWarned;
                _heartbeatWarned = true;
            }
            if (warn) SafeLog("telemetry heartbeat failed (dropping event): " + ex.Message);
        }
    }

    private void EnsureSessionStartLocked()
    {
        if (_sessionStartWritten) return;
        var e = _session.Create(TelemetryEventTypes.SessionStart);
        _enrichSessionStart(e);
        _writer.Enqueue(e);
        _sessionStartWritten = true;
        _sessionStartUtc = e.Ts;
    }

    private void StartHeartbeatLocked()
    {
        _heartbeat ??= new Timer(_ => EmitHeartbeat(), null, _heartbeatInterval, _heartbeatInterval);
    }

    private void StopHeartbeatLocked()
    {
        // Timer.Dispose() never waits for in-flight callbacks, so
        // holding the gate here cannot deadlock; a racing callback
        // blocks on the gate and then refuses via the flag/latch.
        try { _heartbeat?.Dispose(); }
        catch { /* best-effort */ }
        _heartbeat = null;
    }

    private void SafeLog(string message)
    {
        try { _log(message); }
        catch { /* logging must never take telemetry down */ }
    }
}
