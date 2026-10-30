// rst-c's own (not vendored): the RST tray's update checker (spec #20 §4, §6; F17 WU3). It ties RST.Core's
// pieces to the tray:
//
//   when      UpdateCheckLoop: 2–10 min after start, every 24 h, Check now (not within 30 s of the last check).
//             Before every automatic slot it asks automaticChecksOn(), which reads the user's setting and the
//             UpdateChecks preset fresh, on the loop's thread; off means no request at all. Check now always runs.
//   what      detection first (Windows Installer by UpgradeCode, then manifests: ProductDetection), then one HEAD
//             per product to its releases/latest (ReleaseFeed), through the tray's one HttpClient (TrayHttp).
//   failures  logged ONCE per product until it works again; retried at the next slot. Never a dialog or a
//             notification. A failed check never moves "last checked" (the last SUCCESSFUL check) and never claims
//             "up to date"; the pages say "Couldn't check: <reason>". Windows Installer that cannot be read is
//             "Unknown", never "not installed" or "installed manually".
//   announce  NewVersionsFound offers the versions not announced yet (UpdateState.ToNotify). A version is recorded
//             as announced (tray_updates.json) only when the tray has actually shown its notification and calls
//             MarkAnnounced; until then the next check offers it again.
//
// Everything the pages show is an immutable UpdateSnapshot, published through post() (the tray's dispatcher;
// inline in tests). Building it reads no file and no registry: the "automatic checks" flag is cached, refreshed
// at each slot (off the UI thread) and by AutomaticChecksChanged. Installs are WU4's: this delivery only notifies.

using System.Net.Http;
using RST.Core.Tray;
using RST.Core.Updates;

namespace RST.Tray.Updates;

internal sealed class TrayUpdates : IDisposable
{
    private readonly ProductCatalog _catalog;
    private readonly HttpMessageInvoker _http;
    private readonly IInstalledProducts _installed;
    private readonly Func<IReadOnlyDictionary<string, string>> _manifestClientIds;
    private readonly Func<bool> _automaticChecksOn;
    private readonly TimeProvider _time;
    private readonly string _statePath;
    private readonly Action<Action> _post;
    private readonly Action<string> _log;
    private readonly UpdateCheckLoop _loop;
    private readonly object _gate = new();
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failureLogged = new(StringComparer.Ordinal);
    private readonly UpdateState _state;
    private Dictionary<string, ProductInstallation> _installs = new(StringComparer.Ordinal);
    private string? _failureReason;
    private volatile bool _automaticChecks;
    private bool _detected;
    private bool _checking;
    private volatile bool _disposed;

    /// <param name="catalog">The products (ProductCatalog.Default).</param>
    /// <param name="http">The client; its handler must not follow redirects (TrayHttp).</param>
    /// <param name="installed">Windows Installer (MsiProducts).</param>
    /// <param name="manifestClientIds">ClientId to manifest path, from RST.Core's scanner.</param>
    /// <param name="automaticChecksOn">The setting and the preset, read fresh; asked before every automatic slot.</param>
    /// <param name="automaticChecksNow">Whether automatic checks are on now (the tray's already-read value), for the pages.</param>
    /// <param name="time">The clock (TimeProvider.System).</param>
    /// <param name="statePath">tray_updates.json.</param>
    /// <param name="post">Runs an action on the UI thread (events are raised there).</param>
    /// <param name="log">Information lines.</param>
    /// <param name="firstDelay">The start delay (default random 2–10 min).</param>
    public TrayUpdates(
        ProductCatalog catalog,
        HttpMessageInvoker http,
        IInstalledProducts installed,
        Func<IReadOnlyDictionary<string, string>> manifestClientIds,
        Func<bool> automaticChecksOn,
        bool automaticChecksNow,
        TimeProvider time,
        string statePath,
        Action<Action> post,
        Action<string> log,
        Func<TimeSpan>? firstDelay = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _installed = installed ?? throw new ArgumentNullException(nameof(installed));
        _manifestClientIds = manifestClientIds ?? throw new ArgumentNullException(nameof(manifestClientIds));
        _automaticChecksOn = automaticChecksOn ?? throw new ArgumentNullException(nameof(automaticChecksOn));
        _automaticChecks = automaticChecksNow;
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _statePath = statePath ?? throw new ArgumentNullException(nameof(statePath));
        _post = post ?? throw new ArgumentNullException(nameof(post));
        _log = log ?? (_ => { });
        _state = UpdateState.Read(statePath, _log);
        _loop = new UpdateCheckLoop(time, SlotPolicy, RunCheckAsync, firstDelay, _log);
    }

    /// <summary>The pages' data changed (raised through post).</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// A check found versions not announced yet (raised through post). The handler shows the notification and,
    /// only if it was shown, calls <see cref="MarkAnnounced"/>; otherwise the next check offers them again.
    /// </summary>
    public event EventHandler<IReadOnlyList<ProductStatus>>? NewVersionsFound;

    /// <summary>What the pages show now. Reads no file and no registry.</summary>
    public UpdateSnapshot Current
    {
        get { lock (_gate) return BuildLocked(); }
    }

    /// <summary>The loop, for tests (the next slot, the running check).</summary>
    internal UpdateCheckLoop Loop => _loop;

    /// <summary>The policy the tray uses: the user's setting in <paramref name="settingsPath"/> and the presets, read fresh.</summary>
    public static Func<bool> SettingsAndPresetPolicy(string? settingsPath, Func<IReadOnlyList<int?>> presets) => () =>
        UpdateChecksPolicy.Resolve(TraySettings.Read(settingsPath).CheckForUpdates, presets()).Enabled;

    /// <summary>Arms the start slot. Detection waits for the window or the first check.</summary>
    public void Start() => _loop.Start();

    /// <summary>Checks now, whatever the setting. Joins a running check; within 30 s of the last one, makes no request.</summary>
    public Task CheckNowAsync() => _loop.CheckNowAsync();

    /// <summary>Detects what is installed again (no network), e.g. when the window opens.</summary>
    public Task RefreshInstalledAsync() => Task.Run(() =>
    {
        if (_disposed) return;
        var installs = Detect();
        lock (_gate)
        {
            _installs = installs;
            _detected = true;
        }
        Publish();
    });

    /// <summary>The user changed "Check for updates automatically" (<paramref name="on"/>: the setting and preset together).</summary>
    public void AutomaticChecksChanged(bool on)
    {
        _automaticChecks = on;
        if (on) _loop.ChecksTurnedOn();
        Publish();
    }

    /// <summary>The PC woke from sleep: a slot missed while asleep runs soon.</summary>
    public void Resumed() => _loop.Resync();

    /// <summary>
    /// Records <paramref name="shown"/> as announced, once its notification has been shown, so the same version is
    /// never announced again (after a restart either).
    /// </summary>
    public void MarkAnnounced(IReadOnlyList<ProductStatus> shown)
    {
        ArgumentNullException.ThrowIfNull(shown);
        lock (_gate)
        {
            foreach (var status in shown)
                if (status.Latest is { } version && !(_state.Notified(status.Product.Id) is { } done && done >= version))
                    _state.SetNotified(status.Product.Id, version);
            _state.Write(_statePath, _log);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _loop.Dispose();
    }

    /// <summary>The loop's slot policy, on the loop's thread: reads the setting and preset, and caches the answer for the pages.</summary>
    private bool SlotPolicy()
    {
        bool on;
        try
        {
            on = _automaticChecksOn();
        }
        catch (Exception ex)
        {
            _log("update checks: could not read the setting (" + ex.Message + "); treated as off");
            on = false;
        }
        if (on != _automaticChecks)
        {
            _automaticChecks = on;
            Publish();
        }
        return on;
    }

    private async Task RunCheckAsync(UpdateCheckTrigger trigger, CancellationToken cancel)
    {
        lock (_gate) _checking = true;
        Publish();
        IReadOnlyList<ProductStatus> announce = Array.Empty<ProductStatus>();
        try
        {
            var installs = Detect();
            var results = new Dictionary<string, FeedResult>(StringComparer.Ordinal);
            foreach (var product in _catalog.Products)
            {
                cancel.ThrowIfCancellationRequested();
                results[product.Id] = await ReleaseFeed.GetLatestAsync(_http, product.Repo, cancel).ConfigureAwait(false);
            }
            cancel.ThrowIfCancellationRequested();

            UpdateSnapshot snapshot;
            lock (_gate)
            {
                _installs = installs;
                _detected = true;
                foreach (var product in _catalog.Products)
                    Apply(product, results[product.Id]);
                var failures = _catalog.Products.Where(p => results[p.Id].Outcome == FeedOutcome.Failed).ToList();
                // "Last checked" is the last SUCCESSFUL check: a failed one leaves it where it was.
                _failureReason = failures.Count == 0 ? null : results[failures[0].Id].Reason ?? "unknown error";
                if (failures.Count == 0) _state.LastCheckedUtc = _time.GetUtcNow();
                snapshot = BuildLocked();
                announce = _state.ToNotify(snapshot.Products);
                _state.Write(_statePath, _log);
            }
            _log($"update check ({(trigger == UpdateCheckTrigger.User ? "Check now" : "automatic")}): " + Summary(snapshot));
        }
        finally
        {
            lock (_gate) _checking = false;
            Publish();
        }
        if (announce.Count > 0 && !_disposed) _post(() => NewVersionsFound?.Invoke(this, announce));
    }

    /// <summary>Records one product's feed result. A failure keeps the last known latest and is logged once.</summary>
    private void Apply(ProductInfo product, FeedResult result)
    {
        switch (result.Outcome)
        {
            case FeedOutcome.Latest:
                _state.SetLatest(product.Id, result.Version);
                _state.SetNoRelease(product.Id, false);
                break;
            case FeedOutcome.NoRelease:
                _state.SetLatest(product.Id, null);
                _state.SetNoRelease(product.Id, true);
                break;
            default:
                _failed.Add(product.Id);
                if (_failureLogged.Add(product.Id))
                    _log($"update check: couldn't check {product.Name} ({result.Reason}); trying again at the next check, not logged again until it works");
                return;
        }
        _failed.Remove(product.Id);
        if (_failureLogged.Remove(product.Id)) _log($"update check: {product.Name} checks work again");
    }

    private Dictionary<string, ProductInstallation> Detect()
    {
        IReadOnlyDictionary<string, string> manifests;
        try
        {
            manifests = _manifestClientIds();
        }
        catch (Exception ex)
        {
            _log("detection: manifest scan failed: " + ex.Message);
            manifests = new Dictionary<string, string>();
        }
        var installs = new Dictionary<string, ProductInstallation>(StringComparer.Ordinal);
        foreach (var product in _catalog.Products)
            installs[product.Id] = ProductDetection.Decide(product, ProductDetection.Query(_installed, product, _log), manifests);
        return installs;
    }

    private UpdateSnapshot BuildLocked() => new(
        _catalog.Products.Select(p => new ProductStatus(
            p,
            _installs.TryGetValue(p.Id, out var install) ? install : ProductInstallation.None,
            _state.Latest(p.Id),
            _failed.Contains(p.Id),
            _state.NoRelease(p.Id))).ToList(),
        _state.LastCheckedUtc,
        _checking,
        _automaticChecks,
        _detected,
        _failureReason);

    private void Publish()
    {
        if (_disposed) return;
        _post(() => Changed?.Invoke(this, EventArgs.Empty));
    }

    private static string Summary(UpdateSnapshot snapshot) => string.Join("; ", snapshot.Products.Select(s =>
    {
        var installed = s.Installation.Kind switch
        {
            InstallKind.Installer => s.Installation.Installed!.VersionText + " (" + s.Installation.Installed.Context + ")",
            InstallKind.Manual => "manual",
            InstallKind.Unknown => "unknown (Windows Installer unreadable)",
            _ => "not installed",
        };
        var latest = s.LatestFailed ? "failed" : s.NoRelease ? "no release" : s.Latest?.ToString() ?? "none";
        return $"{s.Product.Name} {installed}, latest {latest}: {s.State}";
    }));
}
