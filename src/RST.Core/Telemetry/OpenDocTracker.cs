// OpenDocTracker.cs — open-document count for heartbeats, plus the
// doc_closing → doc_closed correlation (spec Event Schema).
//
// DocumentClosed does not expose the Document, only the closing id and a
// status — so the collector records each tracked closing here and asks
// at DocumentClosed time whether the id belongs to a document it emitted
// doc_closing for (linked and otherwise untracked documents never enter,
// so their DocumentClosed is silently ignored). A Cancelled/Failed close
// leaves the document open — the same reading the recovery scanner
// applies when replaying (sprint 6 ratified call #3).
//
// Counts, not identities: which documents were open lives in the outbox
// events themselves; this type only has to keep open_doc_count honest —
// including across Save-As, which changes a document's path but not the
// count. Locked because the heartbeat timer reads OpenCount from its
// timer thread while the UI thread mutates.

namespace RST.Core.Telemetry;

public sealed class OpenDocTracker
{
    private readonly object _gate = new();
    private readonly HashSet<int> _pendingClosings = new();
    private int _openCount;

    /// <summary>Documents currently open — the heartbeat's open_doc_count.</summary>
    public int OpenCount
    {
        get { lock (_gate) return _openCount; }
    }

    /// <summary>A tracked document finished opening.</summary>
    public void Opened()
    {
        lock (_gate) _openCount++;
    }

    /// <summary>A tracked document began closing under <paramref name="closingId"/>.</summary>
    public void Closing(int closingId)
    {
        lock (_gate) _pendingClosings.Add(closingId);
    }

    /// <summary>
    /// A DocumentClosed arrived for <paramref name="closingId"/>. True
    /// when the id matches a tracked doc_closing (i.e. the collector
    /// should emit doc_closed); the open count drops only when the close
    /// actually <paramref name="succeeded"/>. Unknown ids — linked or
    /// otherwise untracked documents — return false and change nothing.
    /// </summary>
    public bool TryCompleteClosing(int closingId, bool succeeded)
    {
        lock (_gate)
        {
            if (!_pendingClosings.Remove(closingId)) return false;
            if (succeeded && _openCount > 0) _openCount--;
            return true;
        }
    }
}
