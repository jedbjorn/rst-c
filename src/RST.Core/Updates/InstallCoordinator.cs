// rst-c's own (not vendored): installs from the RST tray (spec #20 §5, §6; research Q5; decisions #15, #34; F17 WU4).
// One click on Install or Update runs, for that product:
//
//   1. what is installed now (Windows Installer, ProductDetection.Query): unreadable is refused ("couldn't tell");
//      a product installed for everyone on this PC (or a tray that runs from a per-machine install) is never
//      installed over: the page says who can update it (decision #34)
//   2. VerifiedDownload: the four checks; any refusal stops here, with a logged reason. RST is updated the way it
//      was installed (decision #34): RST-Setup.exe when the bundle's per-user registration is present, else RST.msi
//   3. The pages advise closing Revit; installs do not inspect or wait for Revit (decision #73).
//   4. ONE queue for every install (review B1): a companion's msiexec waits its turn ("Queued"); RST's self-update
//      waits until no other product's install is running, waiting or queued, so every install the user asked for
//      runs, and its result is recorded, before the tray hands over and quits. Nothing is dropped.
//   5. right before msiexec the MSI is held against writes, re-hashed and re-identified, and what is installed is
//      looked at again (check 4 once more; review N1)
//   6. RST itself: ISelfUpdater copies the tray to the updater folder and starts it with --apply; the tray then
//      quits and the copy installs (SelfUpdateApply), then starts the tray to show the result.
//      a companion: msiexec /i <msi> /qn /norestart MSIRESTARTMANAGERCONTROL=Disable /l*v <log> (IMsiexec):
//        0     installed; the cache keeps this MSI as the product's "previous version" from now on
//        3010  installed, Windows needs a restart: the user is told; the tray never restarts the PC
//        1618  another installation is running: tried again after 3 minutes, three runs in all
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
using RST.Core.Logging;
using RST.Core.Tray;

namespace RST.Core.Updates;

/// <summary>Where one product's install is.</summary>
public enum InstallPhase
{
    Idle,
    Downloading,

    /// <summary>Waiting its turn: another install runs (or, for RST's self-update, another is still under way).</summary>
    Queued,

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
    public bool IsBusy => Phase is InstallPhase.Downloading or InstallPhase.Queued
                          or InstallPhase.Installing or InstallPhase.RetryLater or InstallPhase.HandingOff;
}

/// <summary>
/// Runs Windows Installer (RST.Tray: System32's msiexec.exe) or a setup bundle, waited on by its own process handle.
/// Returns the exit code.
/// </summary>
public interface IMsiexec
{
    Task<int> InstallAsync(string msiPath, string logPath, CancellationToken cancel);

    /// <summary>RST-Setup.exe /quiet /norestart RstRestartManagerControl=Disable /log &lt;log&gt;: per-user, no companion switches (decision #34).</summary>
    Task<int> RunBundleAsync(string exePath, string logPath, CancellationToken cancel);
}

/// <summary>RST updating itself (RST.Tray: the updater copy).</summary>
public interface ISelfUpdater
{
    /// <summary>This tray runs from a per-machine install (Program Files): it never installs RST.</summary>
    bool PerMachineTray { get; }

    /// <summary>
    /// RST was installed by RST-Setup.exe: the bundle's per-user registration (its UpgradeCode under HKCU's
    /// Uninstall key) is present, so RST is updated with the new RST-Setup.exe (decision #34).
    /// </summary>
    bool InstalledByBundle { get; }

    /// <summary>
    /// Copies the tray to the updater folder under another exe name and starts it with --apply
    /// <paramref name="packagePath"/> --wait-pid &lt;this process&gt;. Null when it started; otherwise why not (for the log).
    /// </summary>
    string? Start(string packagePath);
}

/// <param name="CacheRoot">%LocalAppData%\RST\Tray\cache.</param>
/// <param name="LogsDir">%LocalAppData%\RST\Tray\logs (msiexec's logs).</param>
/// <param name="SelfUpdateResultFile">%LocalAppData%\RST\Tray\selfupdate.json (written by the updater).</param>
public sealed record InstallPaths(string CacheRoot, string LogsDir, string SelfUpdateResultFile)
{
    /// <summary>
    /// <c>install-&lt;product&gt;-&lt;yyyyMMdd-HHmmss&gt;.log</c>; a second install in the same second (a quick reinstall
    /// after a failure) gets <c>…-2.log</c>, so msiexec never overwrites the failed install's log.
    /// </summary>
    public string LogFor(string productId, DateTimeOffset at)
    {
        var stem = string.Create(CultureInfo.InvariantCulture, $"install-{productId}-{at.ToLocalTime():yyyyMMdd-HHmmss}");
        var path = Path.Combine(LogsDir, stem + ".log");
        for (var n = 2; File.Exists(path) && n < 100; n++)
            path = Path.Combine(LogsDir, string.Create(CultureInfo.InvariantCulture, $"{stem}-{n}.log"));
        return path;
    }
}

/// <summary>What the updater copy did, for the new tray to show (selfupdate.json).</summary>
/// <param name="Version">The RST version it was to install.</param>
/// <param name="ExitCode">msiexec's or the bundle's exit code (null when it never ran).</param>
/// <param name="Problem">
/// None or InstallFailed (it ran); anything
/// else is a refusal (the package changed, or is not RST's) or UpdaterFailed.
/// </param>
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

    /// <summary>Installed (maybe with a restart to come).</summary>
    public static bool Installed(int code) => code is Success or RestartRequired or RestartInitiated;
}

public sealed class InstallCoordinator : IDisposable
{
    /// <summary>The wait before trying again after msiexec 1618.</summary>
    public static readonly TimeSpan BusyRetryDelay = TimeSpan.FromMinutes(3);

    /// <summary>How many times msiexec is run when it keeps answering 1618.</summary>
    public const int BusyAttempts = 3;

    private enum RunMode
    {
        Install,
        Reinstall,
    }

    private readonly VerifiedDownload _download;
    private readonly IInstalledProducts _installed;
    private readonly IMsiReader _msi;
    private readonly IBundleReader? _bundles;
    private readonly IMsiexec _msiexec;
    private readonly ISelfUpdater _self;
    private readonly InstallPaths _paths;
    private readonly TimeProvider _time;
    private readonly Action<Action> _post;
    private readonly Action<string> _log;
    private readonly Action _quitForSelfUpdate;
    private readonly Action _installsChanged;
    private readonly object _gate = new();

    /// <summary>The one install queue: one msiexec (or RST's hand-off) at a time, in the order asked (review B1).</summary>
    private readonly SemaphoreSlim _queue = new(1, 1);

    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);
    private Dictionary<string, InstallProgress> _progress = new(StringComparer.Ordinal);
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _handingOff;
    private volatile bool _disposed;

    /// <param name="products">The catalog's products: a failed upgrade recorded in the cache is offered again.</param>
    /// <param name="bundles">Reads RST-Setup.exe's version resource (decision #34).</param>
    /// <param name="quitForSelfUpdate">Quits the tray once the updater copy has started.</param>
    /// <param name="installsChanged">Something was installed or removed: detect again.</param>
    public InstallCoordinator(
        IEnumerable<ProductInfo> products,
        HttpClient http,
        IInstalledProducts installed,
        IMsiReader msi,
        IBundleReader? bundles,
        IMsiexec msiexec,
        ISelfUpdater self,
        InstallPaths paths,
        TimeProvider time,
        Action<Action> post,
        Action<string> log,
        Action quitForSelfUpdate,
        Action installsChanged)
    {
        ArgumentNullException.ThrowIfNull(products);
        var catalog = products.ToList();
        _installed = installed ?? throw new ArgumentNullException(nameof(installed));
        _msi = msi ?? throw new ArgumentNullException(nameof(msi));
        _bundles = bundles;
        _msiexec = msiexec ?? throw new ArgumentNullException(nameof(msiexec));
        _self = self ?? throw new ArgumentNullException(nameof(self));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _post = post ?? throw new ArgumentNullException(nameof(post));
        _log = log ?? (_ => { });
        _quitForSelfUpdate = quitForSelfUpdate ?? throw new ArgumentNullException(nameof(quitForSelfUpdate));
        _installsChanged = installsChanged ?? (() => { });
        _download = new VerifiedDownload(http, msi, paths.CacheRoot, _log, time, bundles);

        foreach (var product in catalog)
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

    /// <summary>Install or Update: <paramref name="version"/> of <paramref name="product"/>. Never throws.</summary>
    public Task InstallAsync(ProductInfo product, ReleaseVersion version) => RunAsync(product, version, RunMode.Install);

    /// <summary>"Reinstall previous version", after a failed upgrade left nothing installed. Never throws.</summary>
    public Task ReinstallPreviousAsync(ProductInfo product)
    {
        ArgumentNullException.ThrowIfNull(product);
        if (For(product.Id).Previous is not { } previous) return Task.CompletedTask;
        return RunAsync(product, previous, RunMode.Reinstall);
    }

    /// <summary>Stops the product's install while it downloads, waits its turn or waits to retry.</summary>
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

    private async Task RunAsync(ProductInfo product, ReleaseVersion version, RunMode mode)
    {
        ArgumentNullException.ThrowIfNull(product);
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_disposed || _running.ContainsKey(product.Id) || For(product.Id).IsBusy) return;
            if (_handingOff)
            {
                _log($"install: {product.Name} not started: RST is updating itself, and the tray starts again afterwards");
                return;
            }
            cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            _running[product.Id] = cts;
        }
        var before = For(product.Id);
        var previous = before.Previous;
        try
        {
            _log($"install: {product.Name} {version} asked for ({mode switch { RunMode.Reinstall => "reinstall previous version", _ => "Install/Update" }})");
            Set(product, new InstallProgress(InstallPhase.Downloading, version, Previous: previous));
            await RunCoreAsync(product, version, mode, previous, cts.Token).ConfigureAwait(false);
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
            TaskCompletionSource changed;
            lock (_gate)
            {
                _running.Remove(product.Id);
                changed = _changed;
                _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            changed.TrySetResult(); // wake a hand-off waiting for the registered request (SC-017 DN1)
            cts.Dispose();
        }
    }

    private async Task RunCoreAsync(ProductInfo product, ReleaseVersion version, RunMode mode, ReleaseVersion? previous, CancellationToken cancel)
    {
        var reinstall = mode == RunMode.Reinstall;

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
        var current = Highest(found);
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

        // Decision #34: RST is updated the way it was installed.
        var kind = product.IsRst && _self.InstalledByBundle ? PackageKind.Bundle : PackageKind.Msi;
        if (product.IsRst)
            _log(kind == PackageKind.Bundle
                ? "install: RST was installed by RST-Setup.exe; it is updated with the release's RST-Setup.exe"
                : "install: RST was installed by RST.msi; it is updated with the release's RST.msi");

        // 2. The four checks (a reinstall uses the cached package when it is still the verified one).
        VerifyResult verified;
        var cached = mode switch
        {
            RunMode.Reinstall => InstallCache.InstalledMsi(_paths.CacheRoot, product.Id, version, _log),
            _ => null,
        };
        if (cached is not null)
        {
            _log($"install: {product.Name} {version} from the cache ({cached})");
            verified = _download.Recheck(product, version, cached);
            var newer = InstallChecks.Newer(version, current?.Version);
            if (verified.Ok && !newer.Passed) verified = VerifyResult.Refused(newer);
        }
        else
        {
            verified = await _download.RunAsync(product, version, current?.Version, cancel, kind).ConfigureAwait(false);
        }
        cancel.ThrowIfCancellationRequested();
        if (!verified.Ok)
        {
            Set(product, new InstallProgress(InstallPhase.Refused, version, verified.Problem, verified.Net, verified.Status, Previous: previous));
            return;
        }

        if (product.IsRst) await HandOffAsync(product, verified.Msi!, cancel).ConfigureAwait(false);
        else await InstallCompanionAsync(product, version, verified.Msi!, current, reinstall, previous, cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// 4b/5. A companion's turn in the queue: held, re-checked, msiexec. 1618 tries again.
    /// </summary>
    private async Task InstallCompanionAsync(ProductInfo product, ReleaseVersion version, VerifiedMsi msi, InstalledProduct? before,
                                             bool reinstall, ReleaseVersion? previous, CancellationToken cancel)
    {
        var runs = 0;
        while (true)
        {
            if (_queue.CurrentCount == 0)
            {
                _log($"install: {product.Name} {version} waits its turn: another installation is running");
                Set(product, new InstallProgress(InstallPhase.Queued, version, Previous: previous));
            }
            await _queue.WaitAsync(cancel).ConfigureAwait(false);
            int code;
            string logPath;
            try
            {
                // 5. Right before msiexec: hold the file against writes, re-hash, re-identify, look at what is installed.
                using var hold = InstallCache.HoldVerified(msi.Path, msi.Sha256, _log);
                if (hold is null)
                {
                    Refuse(product, version, InstallProblem.HashMismatch, $"{msi.Path} changed after it was verified", previous);
                    return;
                }
                var who = InstallChecks.IdentityOf(product, version, msi.Path, _msi, _bundles);
                if (!who.Passed)
                {
                    Refuse(product, version, who.Problem, who.Detail, previous);
                    return;
                }
                var now = ProductDetection.Query(_installed, product, _log);
                if (now is null)
                {
                    Refuse(product, version, InstallProblem.DetectionFailed, "Windows Installer could not be read", previous);
                    return;
                }
                if (now.Any(p => p.Context == InstallContext.Machine))
                {
                    Refuse(product, version, InstallProblem.PerMachine, $"{product.Name} is now installed for everyone on this PC", previous);
                    return;
                }
                var nowVersion = Highest(now)?.Version;
                if (reinstall && now.Count > 0)
                {
                    // It came back while we waited: match the pre-download path (SC-017 DN6).
                    _log($"install: {product.Name} is installed again ({Highest(now)!.VersionText}); no reinstall needed");
                    InstallCache.ClearFailedUpgrade(_paths.CacheRoot, product.Id, _log);
                    Set(product, InstallProgress.Idle);
                    return;
                }
                var newer = InstallChecks.Newer(version, nowVersion);
                if (!newer.Passed)
                {
                    Refuse(product, version, newer.Problem, newer.Detail, previous);
                    return;
                }

                Set(product, new InstallProgress(InstallPhase.Installing, version, Previous: previous));
                Directory.CreateDirectory(_paths.LogsDir);
                logPath = _paths.LogFor(product.Id, _time.GetUtcNow());
                _log($"install: msiexec /i \"{msi.Path}\" /qn /norestart MSIRESTARTMANAGERCONTROL=Disable /l*v \"{logPath}\"");
                // Not cancellable once started: msiexec runs to its end and its result is recorded.
                code = await _msiexec.InstallAsync(msi.Path, logPath, CancellationToken.None).ConfigureAwait(false);
                runs++;
            }
            finally
            {
                _queue.Release();
            }
            _log($"install: msiexec exited {code} for {product.Name} {version} (log {logPath})");

            if (MsiExit.Installed(code))
            {
                InstallCache.RecordInstalled(_paths.CacheRoot, msi.Record(_time.GetUtcNow()), _log);
                Set(product, new InstallProgress(InstallPhase.Installed, version, ExitCode: code, LogPath: logPath,
                                                 RestartNeeded: code != MsiExit.Success));
                _installsChanged();
                return;
            }
            if (code == MsiExit.AnotherInstallRunning)
            {
                if (runs < BusyAttempts)
                {
                    _log($"install: another installation is running; trying {product.Name} {version} again in {BusyRetryDelay.TotalMinutes:0} minutes");
                    Set(product, new InstallProgress(InstallPhase.RetryLater, version, ExitCode: code, LogPath: logPath, Previous: previous));
                    await Task.Delay(BusyRetryDelay, _time, cancel).ConfigureAwait(false);
                    continue;
                }
                Set(product, new InstallProgress(InstallPhase.Failed, version, InstallProblem.InstallerBusy, ExitCode: code,
                                                 LogPath: logPath, Previous: previous));
                return;
            }
            Failed(product, version, before, code, logPath, previous);
            return;
        }
    }

    /// <summary>
    /// RST: hands over to the updater copy, then quits, but only once no other product's install is running, waiting
    /// or queued (review B1): those run first and their results are recorded. Holds the queue while it hands over, so
    /// no msiexec of the tray's can start meanwhile.
    /// </summary>
    private async Task HandOffAsync(ProductInfo product, VerifiedMsi package, CancellationToken cancel)
    {
        var version = package.Version;
        while (true)
        {
            Task changed;
            bool othersBusy;
            lock (_gate)
            {
                othersBusy = OthersBusyLocked(product.Id);
                changed = _changed.Task;
            }
            if (othersBusy)
            {
                if (For(product.Id).Phase != InstallPhase.Queued)
                {
                    _log($"install: RST {version} waits until the other installs have finished");
                    Set(product, new InstallProgress(InstallPhase.Queued, version));
                }
                await changed.WaitAsync(cancel).ConfigureAwait(false);
                continue;
            }

            await _queue.WaitAsync(cancel).ConfigureAwait(false);
            var handedOff = false;
            try
            {
                lock (_gate)
                {
                    // Admit the hand-off atomically with companion requests (SC-017 DN1).
                    if (OthersBusyLocked(product.Id)) continue;
                    _handingOff = true; // RunAsync refuses new requests under this same gate
                }
                Set(product, new InstallProgress(InstallPhase.HandingOff, version));
                var why = _self.Start(package.Path);
                if (why is null)
                {
                    handedOff = true;
                    _log($"install: the updater is installing RST {version} with {package.Asset}; the tray quits");
                    _quitForSelfUpdate();
                    return;
                }
                lock (_gate) _handingOff = false;
                _log($"install: the updater could not start: {why}");
                Set(product, new InstallProgress(InstallPhase.Failed, version, InstallProblem.UpdaterFailed));
                return;
            }
            finally
            {
                if (!handedOff) _queue.Release();
            }
        }
    }

    /// <summary>msiexec failed. After a failed UPGRADE that left nothing installed, the previous version is offered.</summary>
    private void Failed(ProductInfo product, ReleaseVersion version, InstalledProduct? before, int code, string logPath, ReleaseVersion? previous)
    {
        var after = ProductDetection.Query(_installed, product, _log);
        // Gone, or Windows Installer cannot say (review N6): an upgrade's previous version stays on offer; the page
        // shows Reinstall only while the product is not installed.
        var maybeGone = after is null || after.Count == 0;
        if (before?.Version is { } from && maybeGone)
        {
            _log($"install: the {product.Name} upgrade {from} -> {version} failed{(after is null ? " (Windows Installer unreadable)" : " and left it uninstalled")}; offering to reinstall {from}");
            InstallCache.RecordFailedUpgrade(_paths.CacheRoot, product.Id,
                new FailedUpgrade(from.ToString(), version.ToString(), code, logPath, _time.GetUtcNow()), _log);
            previous = from;
        }
        else if (!maybeGone)
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
        TaskCompletionSource changed;
        lock (_gate)
        {
            _progress = new Dictionary<string, InstallProgress>(_progress, StringComparer.Ordinal) { [product.Id] = progress };
            changed = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        changed.TrySetResult();
        if (!_disposed) _post(() => Changed?.Invoke(this, EventArgs.Empty));
    }

    private bool OthersBusyLocked(string productId) =>
        // Include the interval between registration and the first progress notification (SC-017 DN1).
        _running.Keys.Any(id => id != productId) || _progress.Any(p => p.Key != productId && p.Value.IsBusy);

    private static InstalledProduct? Highest(IReadOnlyList<InstalledProduct> found) =>
        found.OrderByDescending(p => p.Version ?? new ReleaseVersion(-1, 0, 0)).FirstOrDefault();

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
            _progress = new Dictionary<string, InstallProgress>(_progress, StringComparer.Ordinal);
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
        var code = result.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "none";
        switch (result.Problem)
        {
            case InstallProblem.None or InstallProblem.InstallFailed when result.ExitCode is { } exit && MsiExit.Installed(exit):
                _progress[ProductCatalog.RstId] = new InstallProgress(InstallPhase.Installed, version, ExitCode: exit, LogPath: result.Log,
                                                                       RestartNeeded: exit != MsiExit.Success);
                _log($"install: the updater reported RST {version}: installed (exit {code}, {result.Problem})");
                return;
            case InstallProblem.None or InstallProblem.InstallFailed when result.ExitCode == MsiExit.AnotherInstallRunning:
                _progress[ProductCatalog.RstId] = new InstallProgress(InstallPhase.Failed, version, InstallProblem.InstallerBusy,
                                                                       ExitCode: result.ExitCode, LogPath: result.Log);
                break;
            case InstallProblem.None or InstallProblem.InstallFailed or InstallProblem.UpdaterFailed:
                _progress[ProductCatalog.RstId] = new InstallProgress(InstallPhase.Failed, version,
                    result.Problem == InstallProblem.None ? InstallProblem.InstallFailed : result.Problem,
                    ExitCode: result.ExitCode, LogPath: result.Log);
                break;
            default:
                // Review N4: the updater refused the package (it changed, or is not RST's); never silent.
                _progress[ProductCatalog.RstId] = new InstallProgress(InstallPhase.Refused, version, result.Problem);
                break;
        }
        _log($"install: the updater reported RST {version}: {result.Problem} (exit {code})");
    }
}
