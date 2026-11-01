// rst-c's own (not vendored). SC-046 (T7): the tray marks every UI exception handled so it never vanishes from under
// the user. An exception that recurs on every dispatcher pass (a layout or render fault, a timer that throws each
// tick) then loops for ever, filling the log and spinning the CPU. This tells the tray when the same exception has
// come back too often in too short a time, so it can log that and quit cleanly instead.
// SC-046 / #166 N7: changing values in a message must not evade the same throwing site.

using System;
using System.Collections.Generic;

namespace RST.Core.Tray;

/// <summary>Counts one exception's repeats in a sliding time window.</summary>
public sealed class RepeatingFault
{
    /// <summary>The tray's limit: more than this many of the same exception within <see cref="TrayPeriod"/>.</summary>
    public const int TrayLimit = 20;

    /// <summary>The tray's window.</summary>
    public static readonly TimeSpan TrayPeriod = TimeSpan.FromSeconds(5);

    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly Func<DateTimeOffset> _now;
    private readonly Queue<DateTimeOffset> _seen = new();
    private readonly object _gate = new();
    private string? _signature;

    /// <param name="limit">How many of the same exception are tolerated within <paramref name="window"/>.</param>
    /// <param name="window">The time the count looks back over.</param>
    /// <param name="now">The clock (tests pass their own).</param>
    public RepeatingFault(int limit, TimeSpan window, Func<DateTimeOffset>? now = null)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
        _limit = limit;
        _window = window;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>The tray's breaker: <see cref="TrayLimit"/> within <see cref="TrayPeriod"/>.</summary>
    public static RepeatingFault ForTray() => new(TrayLimit, TrayPeriod);

    /// <summary>How many times the current exception was seen within the window (after the last <see cref="Record"/>).</summary>
    public int Count
    {
        get { lock (_gate) return _seen.Count; }
    }

    /// <summary>
    /// Records <paramref name="exception"/>; true when the same exception (type and throwing frame) has now
    /// been seen more than the limit within the window. A different exception starts the count again.
    /// </summary>
    public bool Record(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var signature = Signature(exception);
        var now = _now();
        lock (_gate)
        {
            if (!string.Equals(signature, _signature, StringComparison.Ordinal))
            {
                _signature = signature;
                _seen.Clear();
            }
            _seen.Enqueue(now);
            while (_seen.Count > 0 && now - _seen.Peek() > _window) _seen.Dequeue();
            return _seen.Count > _limit;
        }
    }

    /// <summary>The exception's type and first stack line: what makes two throws "the same".</summary>
    internal static string Signature(Exception exception)
    {
        var stack = exception.StackTrace ?? "";
        var newline = stack.IndexOf('\n');
        var top = (newline >= 0 ? stack[..newline] : stack).Trim();
        return exception.GetType().FullName + "|" + top;
    }
}
