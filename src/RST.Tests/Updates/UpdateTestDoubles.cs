// rst-c's own: a TimeProvider whose clock moves only when a test says so (F17 WU3). Advance() fires every timer
// that comes due on the way, in order, at its own due time, so a test runs 48 hours of the tray's update loop in
// milliseconds. Shared as source with RST.RuntimeTests (linked there).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace RST.Tests.Updates;

internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = new();
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    public override long GetTimestamp() => GetUtcNow().UtcTicks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate) _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>How many timers are armed now.</summary>
    public int ArmedTimers
    {
        get { lock (_gate) return _timers.Count(t => t.Due is not null); }
    }

    /// <summary>
    /// Moves the clock forward by <paramref name="by"/>, firing each timer at its due time on the way (on this thread).
    /// <paramref name="afterFire"/> runs after each fire, at that moment (tests wait for the check it started).
    /// </summary>
    public void Advance(TimeSpan by, Action? afterFire = null)
    {
        DateTimeOffset target;
        lock (_gate) target = _now + by;
        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.Due is { } d && d <= target).OrderBy(t => t.Due).FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    return;
                }
                if (next.Due > _now) _now = next.Due!.Value;
                next.Due = next.Period > TimeSpan.Zero ? _now + next.Period : null;
            }
            next.Fire();
            afterFire?.Invoke();
        }
    }

    /// <summary>
    /// The PC sleeps for <paramref name="by"/>: the wall clock moves on, and every armed timer is pushed back by the
    /// same amount, as Windows timers pause in sleep. Nothing fires.
    /// </summary>
    public void Sleep(TimeSpan by)
    {
        lock (_gate)
        {
            _now += by;
            foreach (var t in _timers)
                if (t.Due is { } due) t.Due = due + by;
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;

        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        public DateTimeOffset? Due { get; set; }

        public TimeSpan Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_owner._gate)
            {
                Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : _owner._now + dueTime;
            }
            return true;
        }

        public void Fire() => _callback(_state);

        public void Dispose()
        {
            lock (_owner._gate)
            {
                Due = null;
                _owner._timers.Remove(this);
            }
        }

        public System.Threading.Tasks.ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }
    }
}

/// <summary>A scripted Windows Installer: UpgradeCode to the instances it reports.</summary>
internal sealed class FakeInstalledProducts : RST.Core.Updates.IInstalledProducts
{
    public Dictionary<string, List<RST.Core.Updates.InstalledProduct>> ByUpgradeCode { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int Queries { get; private set; }

    public FakeInstalledProducts With(string upgradeCode, string version,
                                      RST.Core.Updates.InstallContext context = RST.Core.Updates.InstallContext.User)
    {
        if (!ByUpgradeCode.TryGetValue(upgradeCode, out var list))
            ByUpgradeCode[upgradeCode] = list = new List<RST.Core.Updates.InstalledProduct>();
        list.Add(new RST.Core.Updates.InstalledProduct("{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}", version, context));
        return this;
    }

    public IReadOnlyList<RST.Core.Updates.InstalledProduct> Related(string upgradeCode)
    {
        Queries++;
        return ByUpgradeCode.TryGetValue(upgradeCode, out var list) ? list : new List<RST.Core.Updates.InstalledProduct>();
    }
}

/// <summary>An HttpMessageHandler that counts what it is sent and answers each request from a script.</summary>
internal sealed class CountingHandler : System.Net.Http.HttpMessageHandler
{
    private readonly Func<System.Net.Http.HttpRequestMessage, System.Net.Http.HttpResponseMessage> _answer;
    private readonly object _gate = new();

    public CountingHandler(Func<System.Net.Http.HttpRequestMessage, System.Net.Http.HttpResponseMessage>? answer = null) =>
        _answer = answer ?? Redirect(new Dictionary<string, string>());

    public List<(System.Net.Http.HttpMethod Method, Uri Uri)> Requests { get; } = new();

    public int Count
    {
        get { lock (_gate) return Requests.Count; }
    }

    protected override System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendAsync(
        System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_gate) Requests.Add((request.Method, request.RequestUri!));
        return System.Threading.Tasks.Task.FromResult(_answer(request));
    }

    /// <summary>GitHub's answer: 302 to /releases/tag/&lt;tag&gt; for each repo in <paramref name="tags"/> (owner/name to tag), else to /releases.</summary>
    public static Func<System.Net.Http.HttpRequestMessage, System.Net.Http.HttpResponseMessage> Redirect(IReadOnlyDictionary<string, string> tags) =>
        request =>
        {
            var path = request.RequestUri!.AbsolutePath; // /owner/name/releases/latest
            var repo = path.Substring(1, path.Length - 1 - "/releases/latest".Length);
            var response = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Found);
            response.Headers.Location = tags.TryGetValue(repo, out var tag)
                ? new Uri($"https://github.com/{repo}/releases/tag/{tag}")
                : new Uri($"https://github.com/{repo}/releases");
            return response;
        };
}
