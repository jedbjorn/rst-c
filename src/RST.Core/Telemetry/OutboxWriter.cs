// OutboxWriter.cs — single background thread owning the session file
// handle exclusively; everything else only enqueues.
//
// Durability rules (spec "Outbox & Durability"):
//   - append + Flush() per record; Flush(true) (fsync) on heartbeat and
//     session_end — the heartbeat is the crash-recovery anchor;
//   - the file opens with OutboxFiles.LiveFileShare so the dashboard
//     and a future shipper can read the live file while liveness probes
//     see it as locked;
//   - bounded in-memory queue (default 10k): when full the OLDEST record
//     drops and one degraded-mode warning logs per session;
//   - telemetry never takes Revit down — any IO failure drops the event,
//     logs once, and degrades the writer to off for the session.
//
// Lifecycle: construct + Start at OnStartup; Enqueue from handlers and
// the heartbeat timer; CompleteAndJoin (bounded) at OnShutdown before
// Log.CloseAndFlush(). The file is created lazily on the first write, so
// a session that never emits an event never creates a file.

using System.IO;
using System.Text;
using System.Threading;

namespace RST.Core.Telemetry;

public sealed class OutboxWriter : IDisposable
{
    public const int DefaultQueueCapacity = 10_000;

    private readonly object _gate = new();
    private readonly Queue<TelemetryEvent> _queue = new();
    private readonly int _capacity;
    private readonly Action<string>? _log;
    private readonly Thread _thread;

    private FileStream? _stream;
    private bool _started;
    private bool _completed;
    private bool _dropWarned;
    private volatile bool _dead;

    public OutboxWriter(
        string outboxDir,
        string installId,
        string sessionGuid,
        int queueCapacity = DefaultQueueCapacity,
        Action<string>? log = null)
    {
        OutboxDir = outboxDir;
        SessionFilePath = Path.Combine(outboxDir, OutboxFiles.SessionFileName(installId, sessionGuid));
        _capacity = Math.Max(1, queueCapacity);
        _log = log;
        _thread = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "RST.TelemetryOutbox",
        };
    }

    public string OutboxDir { get; }
    public string SessionFilePath { get; }

    /// <summary>True once an IO failure has degraded the writer to off
    /// for the rest of the session.</summary>
    public bool IsDegraded => _dead;

    public void Start()
    {
        lock (_gate)
        {
            if (_started || _completed) return;
            _started = true;
        }
        _thread.Start();
    }

    /// <summary>
    /// Queue one event for the writer thread. Never throws, never blocks
    /// on IO. When the queue is full (writer stalled) the oldest record
    /// drops and a single degraded-mode warning logs per session.
    /// </summary>
    public void Enqueue(TelemetryEvent e)
    {
        lock (_gate)
        {
            if (_completed || _dead) return;
            if (_queue.Count >= _capacity)
            {
                _queue.Dequeue();
                if (!_dropWarned)
                {
                    _dropWarned = true;
                    SafeLog("telemetry queue full — dropping oldest records (writer stalled?)");
                }
            }
            _queue.Enqueue(e);
            Monitor.Pulse(_gate);
        }
    }

    /// <summary>
    /// Stop accepting events, drain what's queued, and join the writer
    /// thread. Returns false when the thread failed to finish within
    /// <paramref name="timeout"/> (it is a background thread, so an
    /// unfinished drain never blocks Revit shutdown).
    /// </summary>
    public bool CompleteAndJoin(TimeSpan timeout)
    {
        bool started;
        lock (_gate)
        {
            _completed = true;
            started = _started;
            Monitor.Pulse(_gate);
        }
        if (!started)
        {
            CloseStream();
            return true;
        }
        var joined = _thread.Join(timeout);
        if (!joined) SafeLog("telemetry writer did not drain within shutdown timeout");
        return joined;
    }

    public void Dispose() => CompleteAndJoin(TimeSpan.FromSeconds(5));

    private void WriterLoop()
    {
        try
        {
            while (true)
            {
                TelemetryEvent[] batch;
                lock (_gate)
                {
                    while (_queue.Count == 0 && !_completed) Monitor.Wait(_gate);
                    if (_queue.Count == 0) break;
                    batch = _queue.ToArray();
                    _queue.Clear();
                }
                foreach (var e in batch) WriteOne(e);
            }
        }
        catch (Exception ex)
        {
            _dead = true;
            SafeLog("telemetry writer thread failed — telemetry off for this session: " + ex.Message);
        }
        finally
        {
            CloseStream();
        }
    }

    private void WriteOne(TelemetryEvent e)
    {
        if (_dead) return;
        try
        {
            if (_stream is null)
            {
                Directory.CreateDirectory(OutboxDir);
                _stream = new FileStream(SessionFilePath, FileMode.Append, FileAccess.Write, OutboxFiles.LiveFileShare);
            }
            var bytes = Encoding.UTF8.GetBytes(TelemetryJson.SerializeLine(e) + "\n");
            _stream.Write(bytes, 0, bytes.Length);
            _stream.Flush();
            if (e.EventType is TelemetryEventTypes.Heartbeat or TelemetryEventTypes.SessionEnd)
                _stream.Flush(flushToDisk: true);
        }
        catch (Exception ex)
        {
            _dead = true;
            SafeLog("telemetry write failed — telemetry off for this session: " + ex.Message);
        }
    }

    private void CloseStream()
    {
        try { _stream?.Dispose(); }
        catch { /* best-effort */ }
        _stream = null;
    }

    private void SafeLog(string message)
    {
        try { _log?.Invoke(message); }
        catch { /* logging must never take the writer down */ }
    }
}
