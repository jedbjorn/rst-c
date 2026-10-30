// rst-c's own (not vendored): installs from the RST tray (spec #20 §5, §6; research Q5; decision #15; F17 WU4). One
// click on Install or Update runs, for that product:
//
//   1. what is installed now (Windows Installer, ProductDetection.Query): unreadable is refused ("couldn't tell");
//      a product installed for everyone on this PC (or a tray that runs from a per-machine install) is never
//      installed over: the page says who can update it
//   2. VerifiedDownload: the four checks; any refusal stops here, with a logged reason
//   3. never while Revit runs: if any Revit.exe runs in this session the product waits ("Install when Revit
//      closes"), watching the process exit (IRevitWatch), cancellable from the page
//   4. RST itself: ISelfUpdater copies the tray to the updater folder and starts it with --apply; the tray then
//      quits and the copy installs (SelfUpdateApply)
//      a companion: msiexec /i <msi> /qn /norestart /l*v <log>, one at a time (IMsiexec):
//        0     installed; the cache keeps this MSI as the product's "previous version" from now on
//        3010  installed, Windows needs a restart: the user is told; the tray never restarts the PC
//        1618  another installation is running: tried again after 3 minutes, three tries in all
//        else  failed, with the log path; if it was an upgrade and the product is now gone (the companions'
//              afterInstallValidate MajorUpgrade removes the old version first), "Reinstall previous version"
//
// The pages read Current (an immutable map) and are told through Changed, raised through post (the tray's
// dispatcher). Pure apart from its interfaces, so RST.Tests drives every path on Linux.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using RST.Core.Tray;

namespace RST.Core.Updates;

/// <summary>Where one product's install is.</summary>
public enum InstallPhase
{
    Idle,
    Downloading,
    WaitingForRevit,
    Installing,

    /// <summary>msiexec said another installation runs (1618): tried again soon.</summary>
    RetryLater,

    /// <summary>RST: the updater copy has started; the tray is quitting.</summary>
    HandingOff,

    Installed,

    /// <summary>A check refused it; msiexec never ran.</summary>
    Refused,

    /// <summary>msiexec ran and failed, or the updater could not start.</summary>
    Failed,
}

/// <summary>One product's install state, for the pages. Immutable.</summary>
/// <param name="Phase">Where it is.</param>
/// <param name="Version">The version being installed, or installed.</param>
/// <param name="Problem">Why it was refused or failed.</param>
/// <param name="Net">For <see cref="InstallProblem.Network"/>: the kind of failure.</param>
/// <param name="Status">An HTTP status, when the failure is one.</param>
/// <param name="ExitCode">msiexec's exit code.</param>
/// <param name="LogPath">msiexec's verbose log.</param>
/// <param name="RestartNeeded">Installed, Windows asks for a restart (3010).</param>
/// <param name="Previous">After a failed upgrade that left nothing installed: the version to reinstall.</param>
public sealed record InstallProgress(
    InstallPhase Phase,
    ReleaseVersion? Version = null,
    InstallProblem Problem = InstallProblem.None,
    NetFailure? Net = null,
    int? Status = null,
    int? ExitCode = null,
    string? LogPath = null,
    bool RestartNeeded = false,
    ReleaseVersion? Previous = null)
{
    public static InstallProgress Idle { get; } = new(InstallPhase.Idle);

    /// <summary>Something is under way: the product's button is not offered.</summary>
    public bool IsBusy => Phase is InstallPhase.Downloading or InstallPhase.WaitingForRevit or InstallPhase.Installing
                          or InstallPhase.RetryLater or InstallPhase.HandingOff;
}

/// <summary>Runs msiexec (RST.Tray: System32's msiexec.exe, waited on by its own handle). Returns its exit code.</summary>
public interface IMsiexec
{
    Task<int> InstallAsync(string msiPath, string logPath, CancellationToken cancel);
}

/// <summary>Revit in this user's session (RST.Tray: Revit.exe processes, their exit events).</summary>
public interface IRevitWatch
{
    /// <summary>Any Revit.exe runs in this session now.</summary>
    bool AnyRunning();

    /// <summary>Completes when no Revit.exe runs in this session (at once when none does).</summary>
    Task WaitUntilNoneAsync(CancellationToken cancel);
}

/// <summary>RST updating itself (RST.Tray: the updater copy).</summary>
public interface ISelfUpdater
{
    /// <summary>This tray runs from a per-machine install (Program Files): it never installs RST.</summary>
    bool PerMachineTray { get; }

    /// <summary>
    /// Copies the tray to the updater folder under another exe name and starts it with --apply
    /// <paramref name="msiPath"/> --wait-pid &lt;this process&gt;. Null when it started; otherwise why not (for the log).
    /// </summary>
    string? Start(string msiPath);
}

/// <param name="CacheRoot">%LocalAppData%\RST\Tray\cache.</param>
/// <param name="LogsDir">%LocalAppData%\RST\Tray\logs (msiexec's logs).</param>
/// <param name="SelfUpdateResultFile">%LocalAppData%\RST\Tray\selfupdate.json (written by the updater).</param>
public sealed record InstallPaths(string CacheRoot, string LogsDir, string SelfUpdateResultFile)
{
    /// <summary><c>install-&lt;product&gt;-&lt;yyyyMMdd-HHmmss&gt;.log</c>.</summary>
    public string LogFor(string productId, DateTimeOffset at) =>
        Path.Combine(LogsDir, string.Create(CultureInfo.InvariantCulture, $"install-{productId}-{at.ToLocalTime():yyyyMMdd-HHmmss}.log"));
}

/// <summary>What the updater copy did, for the new tray to show (selfupdate.json).</summary>
public sealed record SelfUpdateResult(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("exitCode")] int? ExitCode,
    [property: JsonPropertyName("problem")] InstallProblem Problem,
    [property: JsonPropertyName("log")] string? Log,
    [property: JsonPropertyName("atUtc")] DateTimeOffset AtUtc)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static SelfUpdateResult? TryParse(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<SelfUpdateResult>(text, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public static class MsiExit
{
    public const int Success = 0;
    public const int RestartRequired = 3010;
    public const int RestartInitiated = 1641;
    public const int AnotherInstallRunning = 1618;
}

public sealed class InstallCoordinator : IDisposable
{
    /// <summary>The wait before trying again after msiexec 1618.</summary>
    public static readonly TimeSpan BusyRetryDelay = TimeSpan.FromMinutes(3);

    /// <summary>How many times msiexec is run when it keeps answering 1618.</summary>
    public const int BusyAttempts = 3;

    private readonly VerifiedDownload _download;
    private readonly IInstalledProducts _installed;
    private readonly IMsiexec _msiexec;
    private readonly IRevitWatch _revit;
    private readonly ISelfUpdater _self;
    private readonly InstallPaths _paths;
    private readonly TimeProvider _time;
    private readonly Action<Action> _post;
    private readonly Action<string> _log;
    private readonly Action _quitForSelfUpdate;
    private readonly Action _installsChanged;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _msiexecGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);
    private Dictionary<string, InstallProgress> _progress = new(StringComparer.Ordinal);
    private volatile bool _disposed;

    /// <param name="products">The catalog's products: a failed upgrade recorded in the cache is offered again.</param>
    /// <param name="quitForSelfUpdate">Quits the tray once the updater copy has started.</param>
    /// <param name="installsChanged">Something was installed or removed: detect again.</param>
    public InstallCoordinator(
        IEnumerable<ProductInfo> products,
        HttpClient http,
        IInstalledProducts installed,
        IMsiReader msi,
        IMsiexec msiexec,
        IRevitWatch revit,
        ISelfUpdater self,
        InstallPaths paths,
        TimeProvider time,
        Action<Action> post,
        Action<string> log,
        Action quitForSelfUpdate,
        Action installsChanged)
    {
        ArgumentNullException.ThrowIfNull(products);
        _installed = installed ?? throw new ArgumentNullException(nameof(installed));
        _msiexec = msiexec ?? throw new ArgumentNullException(nameof(msiexec));
        _revit = revit ?? throw new ArgumentNullException(nameof(revit));
        _self = self ?? throw new ArgumentNullException(nameof(self));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _post = post ?? throw new ArgumentNullException(nameof(post));
        _log = log ?? (_ => { });
        _quitForSelfUpdate = quitForSelfUpdate ?? throw new ArgumentNullException(nameof(quitForSelfUpdate));
        _installsChanged = installsChanged ?? (() => { });
        _download = new VerifiedDownload(http, msi, paths.CacheRoot, _log, time);

        foreach (var product in products)
        {
            var failed = InstallCache.ReadState(paths.CacheRoot, product.Id, _log).FailedUpgrade;
            if (failed is not null && ReleaseVersion.TryParseTag("v" + failed.From) is { } from)
                _progress[product.Id] = new InstallProgress(InstallPhase.Failed, ReleaseVersion.TryParseTag("v" + failed.To),
                    InstallProblem.InstallFailed, ExitCode: failed.ExitCode, LogPath: failed.Log, Previous: from);
        }
        ReadSelfUpdateResult();
    }

    /// <summary>The install state changed (raised through post).</summary>
    public event EventHandler? Changed;

    /// <summary>Every product's install state (Idle when absent).</summary>
    public IReadOnlyDictionary<string, InstallProgress> Current
    {
        get { lock (_gate) return _progress; }
    }

    public InstallProgress For(string productId)
    {
        lock (_gate) return _progress.TryGetValue(productId, out var p) ? p : InstallProgress.Idle;
    }

    /// <summary>Any Revit.exe runs in this session now (the button then says "… when Revit closes").</summary>
    public bool RevitRunning
    {
        get
        {
            try
            {
                return _revit.AnyRunning();
            }
            catch (Exception ex)
            {
                _log("install: could not list Revit processes: " + ex.Message);
                return false;
            }
        }
    }

    /// <summary>Install or Update: <paramref name="version"/> of <paramref name="product"/>. Never throws.</summary>
    public Task InstallAsync(ProductInfo product, ReleaseVersion version) => RunAsync(product, version, reinstall: false);

    /// <summary>"Reinstall previous version", after a failed upgrade left nothing installed. Never throws.</summary>
    public Task ReinstallPreviousAsync(ProductInfo product)
    {
        ArgumentNullException.ThrowIfNull(product);
        if (For(product.Id).Previous is not { } previous) return Task.CompletedTask;
        return RunAsync(product, previous, reinstall: true);
    }

    /// <summary>Stops the product's install while it downloads, waits for Revit or waits to retry.</summary>
    public void Cancel(string productId)
    {
        CancellationTokenSource? cts;
        lock (_gate) _running.TryGetValue(productId, out cts);
        if (cts is null) return;
        _log($"install: {productId} cancelled by the user");
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _stop.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task RunAsync(ProductInfo product, ReleaseVersion version, bool reinstall)
    {
        ArgumentNullException.ThrowIfNull(product);
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_disposed || _running.ContainsKey(product.Id) || For(product.Id).IsBusy) return;
            cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            _running[product.Id] = cts;
        }
        var before = For(product.Id);
        var previous = before.Previous;
        try
        {
            _log($"install: {product.Name} {version} asked for ({(reinstall ? "reinstall previous version" : "Install/Update")})");
            Set(product, new InstallProgress(InstallPhase.Downloading, version, Previous: previous));
            await RunCoreAsync(product, version, reinstall, previous, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var stopping = _stop.IsCancellationRequested;
            _log($"install: {product.Name} {version} {(stopping ? "stopped: the tray is closing" : "cancelled")}");
            // Back to what the page showed before the click (a failed upgrade still offers its reinstall).
            if (!stopping) Set(product, previous is null ? InstallProgress.Idle : before);
        }
        catch (Exception ex)
        {
            _log($"install: {product.Name} {version} failed unexpectedly: {ex}");
            Set(product, new InstallProgress(InstallPhase.Failed, version, InstallProblem.InstallFailed, Previous: previous));
        }
        finally
        {
            lock (_gate) _running.Remove(product.Id);
            cts.Dispose();
        }
    }

    private async Task RunCoreAsync(ProductInfo product, ReleaseVersion version, bool reinstall, ReleaseVersion? previous, CancellationToken cancel)
    {
        // 1. What is installed now.
        var found = ProductDetection.Query(_installed, product, _log);
        if (found is null)
        {
            Refuse(product, version, InstallProblem.DetectionFailed, "Windows Installer could not be read", previous);
            return;
        }
        if (found.Any(p => p.Context == InstallContext.Machine) || (product.IsRst && _self.PerMachineTray))
        {
            Refuse(product, version, InstallProblem.PerMachine, $"{product.Name} is installed for everyone on this PC; a per-user tray does not update it", previous);
            return;
        }
        var current = found.OrderByDescending(p => p.Version ?? new ReleaseVersion(-1, 0, 0)).FirstOrDefault();
        if (current is not null && current.Version is null)
        {
            Refuse(product, version, InstallProblem.DetectionFailed, $"the installed version '{current.VersionText}' is not a.b.c", previous);
            return;
        }
        if (reinstall && current is not null)
        {
            // A reinstall is only for a product the failed upgrade left uninstalled; it is back, so nothing to do.
            _log($"install: {product.Name} is installed again ({current.VersionText}); no reinstall needed");
            InstallCache.ClearFailedUpgrade(_paths.CacheRoot, product.Id, _log);
            Set(product, InstallProgress.Idle);
            return;
        }

        // 2. The four checks (a reinstall uses the cached previous MSI when it is still the verified one).
        VerifyResult verified;
        var cached = reinstall ? InstallCache.InstalledMsi(_paths.CacheRoot, product.Id, version, _log) : null;
        if (cached is not null)
        {
            _log($"install: reinstalling {product.Name} {version} from the cache ({cached})");
            verified = _download.Recheck(product, version, cached);
            if (verified.Ok && !InstallChecks.Newer(version, current?.Version).Passed)
                verified = VerifyResult.Refused(InstallChecks.Newer(version, current?.Version));
        }
        else
        {
            verified = await _download.RunAsync(product, version, current?.Version, cancel).ConfigureAwait(false);
        }
        cancel.ThrowIfCancellationRequested();
        if (!verified.Ok)
        {
            Set(product, new InstallProgress(InstallPhase.Refused, version, verified.Problem, verified.Net, verified.Status, Previous: previous));
            return;
        }
        var msi = verified.Msi!;

        for (var attempt = 1; ; attempt++)
        {
            // 3. Never while Revit runs.
            if (RevitRunning)
            {
                _log($"install: Revit is running; {product.Name} {version} installs when it closes");
                Set(product, new InstallProgress(InstallPhase.WaitingForRevit, version, Previous: previous));
                await _revit.WaitUntilNoneAsync(cancel).ConfigureAwait(false);
                _log($"install: Revit has closed; installing {product.Name} {version}");
            }

            // 4a. RST: hand over to the updater copy, then quit.
            if (product.IsRst)
            {
                Set(product, new InstallProgress(InstallPhase.HandingOff, version));
                var why = _self.Start(msi.Path);
                if (why is null)
                {
                    _log($"install: the updater is installing RST {version}; the tray quits");
                    _quitForSelfUpdate();
                    return;
                }
                _log($"install: the updater could not start: {why}");
                Set(product, new InstallProgress(InstallPhase.Failed, version, InstallProblem.UpdaterFailed));
                return;
            }

            // 4b. A companion: msiexec, one at a time.
            var logPath = _paths.LogFor(product.Id, _time.GetUtcNow());
            int code;
            await _msiexecGate.WaitAsync(cancel).ConfigureAwait(false);
            try
            {
                if (RevitRunning) continue; // Revit started while waiting for the gate: wait for it again
                Set(product, new InstallProgress(InstallPhase.Installing, version, Previous: previous));
                Directory.CreateDirectory(_paths.LogsDir);
                _log($"install: msiexec /i \"{msi.Path}\" /qn /norestart /l*v \"{logPath}\"");
                // Not cancellable once started: msiexec runs to its end and its result is recorded.
                code = await _msiexec.InstallAsync(msi.Path, logPath, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _msiexecGate.Release();
            }
            _log($"install: msiexec exited {code} for {product.Name} {version} (log {logPath})");

            switch (code)
            {
                case MsiExit.Success:
                case MsiExit.RestartRequired:
                case MsiExit.RestartInitiated:
                    InstallCache.RecordInstalled(_paths.CacheRoot, msi.Record(_time.GetUtcNow()), _log);
                    Set(product, new InstallProgress(InstallPhase.Installed, version, ExitCode: code, LogPath: logPath,
                                                     RestartNeeded: code != MsiExit.Success));
                    _installsChanged();
                    return;

                case MsiExit.AnotherInstallRunning when attempt < BusyAttempts:
                    _log($"install: another installation is running; trying {product.Name} {version} again in {BusyRetryDelay.TotalMinutes:0} minutes");
                    Set(product, new InstallProgress(InstallPhase.RetryLater, version, ExitCode: code, LogPath: logPath, Previous: previous));
                    await Task.Delay(BusyRetryDelay, _time, cancel).ConfigureAwait(false);
                    continue;

                case MsiExit.AnotherInstallRunning:
                    Set(product, new InstallProgress(InstallPhase.Failed, version, InstallProblem.InstallerBusy, ExitCode: code,
                                                     LogPath: logPath, Previous: previous));
                    return;

                default:
                    Failed(product, version, current, code, logPath, previous);
                    return;
            }
        }
    }

    /// <summary>msiexec failed. After a failed UPGRADE that left nothing installed, the previous version is offered.</summary>
    private void Failed(ProductInfo product, ReleaseVersion version, InstalledProduct? before, int code, string logPath, ReleaseVersion? previous)
    {
        var after = ProductDetection.Query(_installed, product, _log);
        var gone = after is { Count: 0 };
        if (before?.Version is { } from && gone)
        {
            _log($"install: the {product.Name} upgrade {from} -> {version} failed and left it uninstalled; offering to reinstall {from}");
            InstallCache.RecordFailedUpgrade(_paths.CacheRoot, product.Id,
                new FailedUpgrade(from.ToString(), version.ToString(), code, logPath, _time.GetUtcNow()), _log);
            previous = from;
        }
        else if (!gone)
        {
            previous = null; // something is installed: nothing to reinstall
        }
        Set(product, new InstallProgress(InstallPhase.Failed, version, InstallProblem.InstallFailed, ExitCode: code,
                                         LogPath: logPath, Previous: previous));
        _installsChanged();
    }

    private void Refuse(ProductInfo product, ReleaseVersion version, InstallProblem problem, string detail, ReleaseVersion? previous)
    {
        _log($"refused {product.Name} {version}: {problem}: {detail}");
        Set(product, new InstallProgress(InstallPhase.Refused, version, problem, Previous: previous));
    }

    private void Set(ProductInfo product, InstallProgress progress)
    {
        lock (_gate)
        {
            var next = new Dictionary<string, InstallProgress>(_progress, StringComparer.Ordinal) { [product.Id] = progress };
            _progress = next;
        }
        if (!_disposed) _post(() => Changed?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>
    /// Looks for the updater's result again: when Windows Installer's Restart Manager started this tray before the
    /// updater had written it, the updater's own launch of the tray only brings this one forward (the
    /// single-instance hand-off), so the result is picked up then. Cheap: one File.Exists.
    /// </summary>
    public void RefreshSelfUpdateResult()
    {
        if (_disposed || !File.Exists(_paths.SelfUpdateResultFile)) return;
        lock (_gate)
        {
            var next = new Dictionary<string, InstallProgress>(_progress, StringComparer.Ordinal);
            _progress = next;
            ReadSelfUpdateResult();
        }
        _post(() => Changed?.Invoke(this, EventArgs.Empty));
        _installsChanged();
    }

    /// <summary>The updater's result from the last self-update, shown once on RST's row, then removed.</summary>
    private void ReadSelfUpdateResult()
    {
        string text;
        try
        {
            if (!File.Exists(_paths.SelfUpdateResultFile)) return;
            text = TrayAtomicFile.ReadAllText(_paths.SelfUpdateResultFile);
            File.Delete(_paths.SelfUpdateResultFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log("install: the updater's result could not be read: " + ex.Message);
            return;
        }
        var result = SelfUpdateResult.TryParse(text);
        if (result is null || ReleaseVersion.TryParseTag("v" + result.Version) is not { } version)
        {
            _log("install: the updater's result is damaged; ignored");
            return;
        }
        var age = _time.GetUtcNow() - result.AtUtc;
        if (age > TimeSpan.FromDays(1) || age < TimeSpan.FromMinutes(-5)) return;
        var ok = result.Problem == InstallProblem.None && result.ExitCode is MsiExit.Success or MsiExit.RestartRequired or MsiExit.RestartInitiated;
        _progress[ProductCatalog.RstId] = ok
            ? new InstallProgress(InstallPhase.Installed, version, ExitCode: result.ExitCode, LogPath: result.Log,
                                  RestartNeeded: result.ExitCode != MsiExit.Success)
            : new InstallProgress(InstallPhase.Failed, version,
                                  result.Problem == InstallProblem.None ? InstallProblem.InstallFailed : result.Problem,
                                  ExitCode: result.ExitCode, LogPath: result.Log);
        _log($"install: the updater reported RST {version}: {(ok ? "installed" : "failed")} (exit {result.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "none"}, {result.Problem})");
    }
}
