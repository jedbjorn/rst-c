// SessionBuilder.cs — fluent builder for synthetic outbox session files used by
// the rollup tests (spec #24): session_start with versions, timed opens, edit
// pulses, syncs, model_stats, closes (real and recovery-written), toggles and
// session_end, in seq order.

using System;
using System.Collections.Generic;
using System.IO;
using RST.Core.Telemetry;

namespace RST.Tests.Telemetry.Rollups;

internal sealed class SessionBuilder
{
    private readonly List<TelemetryEvent> _events = new();
    private long _seq;
    private int _closing;

    public SessionBuilder(string? sessionGuid = null)
    {
        SessionGuid = sessionGuid ?? Guid.NewGuid().ToString();
    }

    public string SessionGuid { get; }

    public IReadOnlyList<TelemetryEvent> Events => _events;

    private TelemetryEvent Add(string type, DateTimeOffset ts)
    {
        var e = OutboxTestData.Event(SessionGuid, ++_seq, type, ts);
        _events.Add(e);
        return e;
    }

    public SessionBuilder Start(DateTimeOffset ts, string? version = "2026", string? subversion = "2026.4")
    {
        var e = Add(TelemetryEventTypes.SessionStart, ts);
        if (version is not null) e.SetField(TelemetryFields.RevitVersion, version);
        if (subversion is not null) e.SetField(TelemetryFields.RevitSubversion, subversion);
        return this;
    }

    /// <summary>doc_opening at <paramref name="ts"/> then doc_opened after <paramref name="loadSeconds"/>.</summary>
    public SessionBuilder Open(DateTimeOffset ts, DocumentIdentity id, double loadSeconds = 10)
    {
        var opening = Add(TelemetryEventTypes.DocOpening, ts);
        opening.SetField(TelemetryFields.LocalPath, id.LocalPath);
        var opened = Add(TelemetryEventTypes.DocOpened, ts.AddSeconds(loadSeconds));
        id.WriteTo(opened);
        return this;
    }

    public SessionBuilder Pulse(DateTimeOffset ts, DocumentIdentity id)
    {
        id.WriteKeysTo(Add(TelemetryEventTypes.DocChangedPulse, ts));
        return this;
    }

    /// <summary>One pulse every <paramref name="everyMinutes"/> from <paramref name="from"/> while before <paramref name="until"/>.</summary>
    public SessionBuilder Pulses(DateTimeOffset from, DateTimeOffset until, DocumentIdentity id, double everyMinutes = 1)
    {
        for (var t = from; t < until; t = t.AddMinutes(everyMinutes)) Pulse(t, id);
        return this;
    }

    public SessionBuilder Sync(DateTimeOffset start, DocumentIdentity id, double seconds)
    {
        id.WriteKeysTo(Add(TelemetryEventTypes.SyncStart, start));
        id.WriteKeysTo(Add(TelemetryEventTypes.SyncEnd, start.AddSeconds(seconds)));
        return this;
    }

    public SessionBuilder Stats(DateTimeOffset ts, DocumentIdentity id, int? warnings, long? sizeBytes)
    {
        var e = Add(TelemetryEventTypes.ModelStats, ts);
        id.WriteKeysTo(e);
        e.SetField(TelemetryFields.WarningsCount, warnings);
        e.SetField(TelemetryFields.FileSizeBytes, sizeBytes);
        return this;
    }

    public SessionBuilder Close(DateTimeOffset ts, DocumentIdentity id)
    {
        var closingId = "c" + (++_closing);
        var closing = Add(TelemetryEventTypes.DocClosing, ts);
        id.WriteKeysTo(closing);
        closing.SetField(TelemetryFields.ClosingId, closingId);
        Add(TelemetryEventTypes.DocClosed, ts).SetField(TelemetryFields.ClosingId, closingId);
        return this;
    }

    /// <summary>doc_closing at <paramref name="closingTs"/>, doc_closed at <paramref name="closedTs"/>
    /// (with an optional status such as "Cancelled").</summary>
    public SessionBuilder Close(DateTimeOffset closingTs, DocumentIdentity id, DateTimeOffset closedTs, string? status = null)
    {
        var closingId = "c" + (++_closing);
        var closing = Add(TelemetryEventTypes.DocClosing, closingTs);
        id.WriteKeysTo(closing);
        closing.SetField(TelemetryFields.ClosingId, closingId);
        var closed = Add(TelemetryEventTypes.DocClosed, closedTs);
        closed.SetField(TelemetryFields.ClosingId, closingId);
        if (status is not null) closed.SetField(TelemetryFields.Status, status);
        return this;
    }

    /// <summary>A doc_closing with no doc_closed (Revit stopped while closing).</summary>
    public SessionBuilder ClosingOnly(DateTimeOffset ts, DocumentIdentity id)
    {
        var closing = Add(TelemetryEventTypes.DocClosing, ts);
        id.WriteKeysTo(closing);
        closing.SetField(TelemetryFields.ClosingId, "c" + (++_closing));
        return this;
    }

    /// <summary>doc_opened with no doc_opening before it: open, but no timed open.</summary>
    public SessionBuilder OpenedOnly(DateTimeOffset ts, DocumentIdentity id)
    {
        id.WriteTo(Add(TelemetryEventTypes.DocOpened, ts));
        return this;
    }

    /// <summary>The synthetic doc_closed recovery writes for a crashed session (keys, no closing_id).</summary>
    public SessionBuilder RecoveryClose(DateTimeOffset ts, DocumentIdentity id)
    {
        var e = Add(TelemetryEventTypes.DocClosed, ts);
        e.Source = TelemetrySources.Recovery;
        e.Synthetic = true;
        id.WriteKeysTo(e);
        return this;
    }

    public SessionBuilder RecoveryEnd(DateTimeOffset ts)
    {
        var e = Add(TelemetryEventTypes.SessionEnd, ts);
        e.Source = TelemetrySources.Recovery;
        e.Synthetic = true;
        return this;
    }

    public SessionBuilder Disable(DateTimeOffset ts)
    {
        Add(TelemetryEventTypes.CollectionDisabled, ts);
        return this;
    }

    public SessionBuilder Enable(DateTimeOffset ts)
    {
        Add(TelemetryEventTypes.CollectionEnabled, ts);
        return this;
    }

    public SessionBuilder Heartbeat(DateTimeOffset ts)
    {
        Add(TelemetryEventTypes.Heartbeat, ts);
        return this;
    }

    public SessionBuilder End(DateTimeOffset ts)
    {
        Add(TelemetryEventTypes.SessionEnd, ts);
        return this;
    }

    public List<TelemetryEvent> ToList() => new(_events);

    /// <summary>Write the session file into <paramref name="dir"/>; its last-write time is set to
    /// <paramref name="lastWriteUtc"/> (default: the last event's ts) so "oldest first" is stable.</summary>
    public string Write(string dir, DateTimeOffset? lastWriteUtc = null)
    {
        Directory.CreateDirectory(dir);
        var path = OutboxTestData.WriteSessionFile(dir, SessionGuid, _events.ToArray());
        File.SetLastWriteTimeUtc(path, (lastWriteUtc ?? _events[^1].Ts).UtcDateTime);
        return path;
    }

    public static DocumentIdentity Central(string name) => new()
    {
        CreationGuid = Guid.NewGuid().ToString(),
        CentralPath = @"\\server\projects\" + name + ".rvt",
        LocalPath = @"C:\Users\me\Documents\" + name + "_me.rvt",
        Title = name,
        IsWorkshared = true,
        IsCloud = false,
    };

    public static DocumentIdentity Local(string name) => new()
    {
        CreationGuid = Guid.NewGuid().ToString(),
        LocalPath = @"C:\models\" + name + ".rvt",
        Title = name,
        IsWorkshared = false,
        IsCloud = false,
    };
}
