// rst-c's own (not vendored): the updater copy's job when RST updates itself (spec #20 §5; research Q5; decision #34;
// F17 WU4). RST's package contains the running tray, so the tray copies itself to %LocalAppData%\RST\Tray\updater\
// under ANOTHER exe name (RST.TrayUpdater.exe: WU1 review N2, util:CloseApplication targets RST.Tray.exe by name),
// starts it with
//
//   RST.TrayUpdater.exe --apply <cache>\rst\<version>\RST.msi | RST-Setup.exe --wait-pid <tray pid>
//
// and quits. The copy then:
//   1. accepts only RST.msi or RST-Setup.exe in the tray's own cache that the tray verified (verified.json), whose
//      SHA256 is still the verified one and which still says what it is (RST's PER-USER UpgradeCode, that version,
//      per user; or the bundle's version resource): --apply installs nothing else, whoever starts it
//   2. waits (up to a minute) for that pid to exit, if it is an RST tray
//   3. never while Revit runs (review B2): if a Revit.exe runs in this session (or the check fails) it installs
//      nothing, records "Revit was open", and starts the old tray again, which waits for Revit and hands over again
//   4. holds the package against writes, re-hashes it (review N1), and runs it: msiexec /i … /qn /norestart /l*v
//      (System32's), or the bundle /quiet /norestart /log (decision #34), waited on by its own handle; 1618 is tried
//      again after 3 minutes, three runs in all, checking for Revit before each (review B1)
//   5. records the result in selfupdate.json (the new tray shows it once; a refusal too, review N4) and, on success,
//      keeps the package as RST's cached "last installed"
//   6. starts the installed tray with no switch: its window opens on the result. If Windows Installer's Restart
//      Manager has already started one, this launch only brings that tray's window forward (the single-instance
//      mutex), so there is never a second tray.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RST.Core.Tray;

namespace RST.Core.Updates;

/// <summary>The updater's view of Windows (RST.Tray's real one; tests fake it).</summary>
public interface ISelfUpdateHost
{
    /// <summary>Waits for <paramref name="pid"/> to exit, when it is an RST tray. False when it still runs after <paramref name="timeout"/>.</summary>
    bool WaitForTrayExit(int pid, TimeSpan timeout);

    /// <summary>Starts the installed tray (no switch). False when it could not.</summary>
    bool StartTray();

    /// <summary>
    /// Takes the tray's single-instance mutex (<c>Local\RST.Tray.&lt;UserSid&gt;</c>, TrayPaths.MutexName), waiting up to
    /// <paramref name="timeout"/> for the old tray to let it go, and holds it until released. While the updater holds
    /// it, a tray that Revit (WU2), the Run value or Restart Manager starts finds "a tray runs" and exits, so nothing
    /// runs from the folder being replaced. When the mutex is not free in time the claim is returned NOT held and keeps
    /// waiting in the background: it takes the mutex the moment the tray lets it go (Restart Manager or CloseApplication
    /// closing it for msiexec; SC-022). A failure to make the claim at all (no user SID) throws: the update stops.
    /// </summary>
    ITrayInstanceClaim ClaimTrayInstance(TimeSpan timeout);
}

/// <summary>What <see cref="ITrayInstanceClaim.Release"/> found.</summary>
public enum ClaimRelease
{
    /// <summary>The claim held the mutex and has released it: the tray started next can take it.</summary>
    Released,

    /// <summary>The claim never got the mutex (a tray holds it) and has stopped waiting for it.</summary>
    NotHeld,

    /// <summary>The claim's thread has not finished within the wait: the mutex may still be held. Call again to wait more.</summary>
    Unconfirmed,
}

/// <summary>The updater's claim on the tray's single-instance mutex (<see cref="ISelfUpdateHost.ClaimTrayInstance"/>).</summary>
public interface ITrayInstanceClaim : IDisposable
{
    /// <summary>The updater holds the mutex now: taken at once, or later, in the background, once the tray let it go.</summary>
    bool Held { get; }

    /// <summary>
    /// Releases the mutex, or stops waiting for it when it was never taken, and waits a bounded time until that is
    /// done (so a tray started next can take it). Calling again waits again; once confirmed it returns the same.
    /// </summary>
    ClaimRelease Release();
}

public static class SelfUpdateApply
{
    /// <summary>How long the updater waits for the old tray to exit before it goes on anyway.</summary>
    public static readonly TimeSpan TrayExitTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long the updater waits for the tray's mutex before msiexec starts (review N2). Short: by now the waited tray
    /// has exited (its mutex is free at once) or timed out; any tray still holding it is one msiexec closes, and the
    /// claim goes on waiting in the background and takes the mutex at that moment.
    /// </summary>
    public static readonly TimeSpan ClaimTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How many times Finish waits for an unconfirmed release before it gives up starting a tray (review N3).</summary>
    public const int ReleaseAttempts = 4;

    /// <summary>Exit codes of the updater process.</summary>
    public const int Installed = 0, InstallFailed = 1, Refused = 2, DeferredForRevit = 3;

    /// <summary>Runs the update. Never throws.</summary>
    public static async Task<int> RunAsync(
        string packagePath, int? waitPid, InstallPaths paths, IMsiReader msi, IBundleReader? bundles, IMsiexec runner,
        IRevitWatch revit, ISelfUpdateHost host, TimeProvider time, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(msi);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(revit);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(time);
        log ??= _ => { };
        var folderVersion = VersionFromFolder(packagePath);
        ITrayInstanceClaim? instance = null;
        try
        {
            var (path, verified, refusal) = Accept(packagePath, paths, msi, bundles, log);
            if (path is null)
            {
                log($"updater: refused: {refusal.Problem}: {refusal.Detail}");
                // Review N4: never silent. The version comes from the folder name when the record is unusable.
                Finish(paths, folderVersion is { } v ? new SelfUpdateResult(v.ToString(), null, refusal.Problem, null, time.GetUtcNow()) : null,
                       host, log, instance);
                return Refused;
            }
            var version = verified!.Version;

            if (waitPid is { } pid)
            {
                log($"updater: waiting for the tray (pid {pid}) to exit");
                if (!host.WaitForTrayExit(pid, TrayExitTimeout))
                    log($"updater: the tray (pid {pid}) is still running after {TrayExitTimeout.TotalSeconds:0} s; going on (Windows Installer closes it)");
            }

            // While files are replaced, this process holds the tray's single-instance mutex: a tray started meanwhile
            // (by Revit, the Run value, Restart Manager) exits at once instead of running from the folder being
            // replaced. It is handed over (released) right before the new tray is started, in Finish.
            // SC-022: a tray that does not let go in time (it was never asked to quit, or is slow to) is closed by
            // msiexec's Restart Manager; the claim keeps waiting and takes the mutex at that moment, so the gap between
            // the old tray's exit and the hand-over is never unguarded.
            instance = host.ClaimTrayInstance(ClaimTimeout);
            log(instance.Held
                ? "updater: holding the tray's single-instance mutex while RST is replaced"
                : $"updater: the tray's single-instance mutex was not free within {ClaimTimeout.TotalSeconds:0} s; going on, and taking it the moment the tray lets it go");

            var kind = InstallChecks.KindOf(path);
            for (var run = 1; ; run++)
            {
                // Review B2: never while Revit runs. A failing check counts as "Revit may be running" (RevitCheck).
                if (RevitCheck.MayBeRunning(revit, log))
                {
                    log($"updater: Revit is running; RST {version} is not installed now. The tray starts again and installs it when Revit closes");
                    Finish(paths, new SelfUpdateResult(version, null, InstallProblem.RevitRunning, null, time.GetUtcNow()), host, log, instance);
                    return DeferredForRevit;
                }

                Directory.CreateDirectory(paths.LogsDir);
                var logPath = paths.LogFor(ProductCatalog.RstId, time.GetUtcNow());
                int code;
                // Review N1: the package is held against writes and re-hashed through the same handle it runs from.
                using (var hold = InstallCache.HoldVerified(path, verified.Sha256, log))
                {
                    if (hold is null)
                    {
                        log("updater: refused: the package changed after it was verified");
                        Finish(paths, new SelfUpdateResult(version, null, InstallProblem.HashMismatch, null, time.GetUtcNow()), host, log, instance);
                        return Refused;
                    }
                    if (kind == PackageKind.Bundle)
                    {
                        log($"updater: \"{path}\" /quiet /norestart /log \"{logPath}\"");
                        code = await runner.RunBundleAsync(path, logPath, CancellationToken.None).ConfigureAwait(false);
                    }
                    else
                    {
                        log($"updater: msiexec /i \"{path}\" /qn /norestart /l*v \"{logPath}\"");
                        code = await runner.InstallAsync(path, logPath, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                var ok = MsiExit.Installed(code);
                log($"updater: {(kind == PackageKind.Bundle ? "RST-Setup.exe" : "msiexec")} exited {code} ({(ok ? "installed" : code == MsiExit.AnotherInstallRunning ? "another installation is running" : "failed")})");

                if (code == MsiExit.AnotherInstallRunning && run < InstallCoordinator.BusyAttempts)
                {
                    log($"updater: trying again in {InstallCoordinator.BusyRetryDelay.TotalMinutes:0} minutes (run {run} of {InstallCoordinator.BusyAttempts})");
                    await Task.Delay(InstallCoordinator.BusyRetryDelay, time).ConfigureAwait(false);
                    continue;
                }
                if (ok) InstallCache.RecordInstalled(paths.CacheRoot, verified, log);
                Finish(paths, new SelfUpdateResult(version, code, ok ? InstallProblem.None : InstallProblem.InstallFailed, logPath,
                                                   time.GetUtcNow()), host, log, instance);
                return ok ? Installed : InstallFailed;
            }
        }
        catch (Exception ex)
        {
            log("updater: failed: " + ex);
            Finish(paths, folderVersion is { } v ? new SelfUpdateResult(v.ToString(), null, InstallProblem.UpdaterFailed, null, time.GetUtcNow()) : null,
                   host, log, instance);
            return InstallFailed;
        }
    }

    /// <summary>The version folder's name (<c>cache\rst\3.1.1\RST.msi</c> gives 3.1.1), or null.</summary>
    private static ReleaseVersion? VersionFromFolder(string? packagePath)
    {
        try
        {
            return string.IsNullOrEmpty(packagePath) ? null : ReleaseVersion.TryParseTag("v" + Path.GetFileName(Path.GetDirectoryName(packagePath)));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The package, if it is RST.msi or RST-Setup.exe in RST's cache folder, verified and still intact.</summary>
    private static (string? Path, VerifiedRecord? Record, CheckResult Refusal) Accept(
        string packagePath, InstallPaths paths, IMsiReader msi, IBundleReader? bundles, Action<string> log)
    {
        static (string?, VerifiedRecord?, CheckResult) No(InstallProblem problem, string detail) => (null, null, new CheckResult(problem, detail));
        if (string.IsNullOrWhiteSpace(packagePath)) return No(InstallProblem.UpdaterFailed, "no package given");
        string full, rstCache;
        try
        {
            full = Path.GetFullPath(packagePath);
            rstCache = Path.GetFullPath(InstallCache.ProductDir(paths.CacheRoot, ProductCatalog.RstId)) + Path.DirectorySeparatorChar;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return No(InstallProblem.UpdaterFailed, $"'{packagePath}' is not a path: {ex.Message}");
        }
        if (!full.StartsWith(rstCache, StringComparison.OrdinalIgnoreCase))
            return No(InstallProblem.UpdaterFailed, $"{full} is not in RST's download cache {rstCache}");
        var name = Path.GetFileName(full);
        if (!string.Equals(name, ProductCatalog.RstAsset, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(name, ProductCatalog.RstBundleAsset, StringComparison.OrdinalIgnoreCase))
            return No(InstallProblem.AssetNameBad, $"{full} is neither {ProductCatalog.RstAsset} nor {ProductCatalog.RstBundleAsset}");
        var record = InstallCache.ReadVerified(full, log);
        if (record is null || record.Product != ProductCatalog.RstId || ReleaseVersion.TryParseTag("v" + record.Version) is not { } version)
            return No(InstallProblem.HashMismatch, $"{full} was not verified by the tray, or changed since");
        if (!string.Equals(Path.GetFileName(Path.GetDirectoryName(full)), version.ToString(), StringComparison.Ordinal))
            return No(InstallProblem.WrongVersion, $"{full} is not in its version's folder ({version})");
        // RST's PER-USER UpgradeCode only (InstallChecks.IdentityOf uses ProductCatalog.RstPerUserUpdate for RST).
        var check = InstallChecks.IdentityOf(ProductCatalog.RstProduct, version, full, msi, bundles);
        return check.Passed ? (full, record, CheckResult.Pass) : (null, null, check);
    }

    /// <summary>Records the result (null: nothing to record) and starts the installed tray, whatever happened.</summary>
    private static void Finish(InstallPaths paths, SelfUpdateResult? result, ISelfUpdateHost host, Action<string> log, ITrayInstanceClaim? instance)
    {
        if (result is not null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(paths.SelfUpdateResultFile)!);
                TrayAtomicFile.WriteAllText(paths.SelfUpdateResultFile, result.ToJson(), log);
            }
            catch (Exception ex)
            {
                log("updater: could not record the result: " + ex.Message);
            }
        }
        // Hand the single-instance mutex over: released only now, with every file in place, right before the tray that
        // runs from the new files is started (a tray starting in between runs those new files too). A claim that never
        // got the mutex stops waiting: a tray holds it, and the launch below then only brings that tray's window
        // forward (it shows this result) and exits, so the updater never adds a second tray (SC-022). A release that is
        // never confirmed starts no tray (review N3): that launch would find the mutex still held, signal a tray that is
        // not there and exit, leaving none; a tray Restart Manager started keeps running, or one starts at sign-in.
        // instance is null only on the paths that end before the claim is made (a refused package, an early failure).
        if (instance is not null)
        {
            try
            {
                var release = instance.Release();
                for (var attempt = 1; release == ClaimRelease.Unconfirmed && attempt < ReleaseAttempts; attempt++)
                {
                    log("updater: the tray's single-instance mutex is not released yet; waiting");
                    release = instance.Release();
                }
                switch (release)
                {
                    case ClaimRelease.Released:
                        log("updater: released the tray's single-instance mutex");
                        break;
                    case ClaimRelease.NotHeld:
                        log("updater: stopped waiting for the tray's single-instance mutex (a tray holds it: the launch below defers to it)");
                        break;
                    default:
                        log("updater: the tray's single-instance mutex was not confirmed released; no tray is started now " +
                            "(one Restart Manager started keeps running, or it starts at the next sign-in)");
                        return;
                }
            }
            catch (Exception ex)
            {
                log("updater: releasing the tray's single-instance mutex failed: " + ex.Message);
            }
        }
        try
        {
            log(host.StartTray() ? "updater: started the installed tray" : "updater: could not start the installed tray; it starts at the next sign-in");
        }
        catch (Exception ex)
        {
            log("updater: could not start the installed tray: " + ex.Message);
        }
    }
}
