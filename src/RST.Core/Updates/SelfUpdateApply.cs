// rst-c's own (not vendored): the updater copy's job when RST updates itself (spec #20 §5; research Q5; F17 WU4).
// RST.msi contains the running tray, so the tray copies itself to %LocalAppData%\RST\Tray\updater\ under ANOTHER exe
// name (RST.TrayUpdater.exe: WU1 review N2, util:CloseApplication targets RST.Tray.exe by name), starts it with
//
//   RST.TrayUpdater.exe --apply <cache>\rst\<version>\RST.msi --wait-pid <tray pid>
//
// and quits. The copy then:
//   1. accepts only RST.msi in the tray's own cache that the tray verified (verified.json), whose SHA256 is still the
//      verified one and whose Property table is still RST's UpgradeCode, that version, per user: --apply installs
//      nothing else, whoever starts it
//   2. waits (up to a minute) for that pid to exit, if it is an RST tray
//   3. runs msiexec /i … /qn /norestart /l*v (System32's, waited on by its own handle)
//   4. records the result in selfupdate.json (the new tray shows it once) and, on success, keeps the MSI as RST's
//      cached "last installed"
//   5. starts the installed tray with no switch: its window opens on the result. If Windows Installer's Restart
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
}

public static class SelfUpdateApply
{
    /// <summary>How long the updater waits for the old tray to exit before it runs msiexec anyway.</summary>
    public static readonly TimeSpan TrayExitTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Exit codes of the updater process.</summary>
    public const int Installed = 0, InstallFailed = 1, Refused = 2;

    /// <summary>Runs the update. Never throws.</summary>
    public static async Task<int> RunAsync(
        string msiPath, int? waitPid, InstallPaths paths, IMsiReader msi, IMsiexec msiexec, ISelfUpdateHost host,
        TimeProvider time, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(msi);
        ArgumentNullException.ThrowIfNull(msiexec);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(time);
        log ??= _ => { };
        var product = ProductCatalog.RstProduct;
        ReleaseVersion? version = null;
        try
        {
            var (path, verified, refusal) = Accept(msiPath, paths, msi, log);
            if (path is null)
            {
                log("updater: refused: " + refusal);
                Finish(paths, new SelfUpdateResult("0.0.0", null, InstallProblem.UpdaterFailed, null, time.GetUtcNow()), host, log, writeResult: false);
                return Refused;
            }
            version = ReleaseVersion.TryParseTag("v" + verified!.Version);

            if (waitPid is { } pid)
            {
                log($"updater: waiting for the tray (pid {pid}) to exit");
                if (!host.WaitForTrayExit(pid, TrayExitTimeout))
                    log($"updater: the tray (pid {pid}) is still running after {TrayExitTimeout.TotalSeconds:0} s; installing anyway (Windows Installer closes it)");
            }

            Directory.CreateDirectory(paths.LogsDir);
            var logPath = paths.LogFor(product.Id, time.GetUtcNow());
            log($"updater: msiexec /i \"{path}\" /qn /norestart /l*v \"{logPath}\"");
            var code = await msiexec.InstallAsync(path, logPath, CancellationToken.None).ConfigureAwait(false);
            var ok = code is MsiExit.Success or MsiExit.RestartRequired or MsiExit.RestartInitiated;
            log($"updater: msiexec exited {code} ({(ok ? "installed" : "failed")})");
            if (ok) InstallCache.RecordInstalled(paths.CacheRoot, verified, log);
            Finish(paths, new SelfUpdateResult(verified.Version, code, ok ? InstallProblem.None : InstallProblem.InstallFailed, logPath,
                                               time.GetUtcNow()), host, log, writeResult: true);
            return ok ? Installed : InstallFailed;
        }
        catch (Exception ex)
        {
            log("updater: failed: " + ex);
            Finish(paths, new SelfUpdateResult(version?.ToString() ?? "0.0.0", null, InstallProblem.UpdaterFailed, null, time.GetUtcNow()),
                   host, log, writeResult: version is not null);
            return InstallFailed;
        }
    }

    /// <summary>The MSI, if it is RST.msi in RST's cache folder, verified and still intact.</summary>
    private static (string? Path, VerifiedRecord? Record, string? Refusal) Accept(string msiPath, InstallPaths paths, IMsiReader msi, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(msiPath)) return (null, null, "no MSI given");
        string full, rstCache;
        try
        {
            full = Path.GetFullPath(msiPath);
            rstCache = Path.GetFullPath(InstallCache.ProductDir(paths.CacheRoot, ProductCatalog.RstId)) + Path.DirectorySeparatorChar;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, null, $"'{msiPath}' is not a path: {ex.Message}");
        }
        if (!full.StartsWith(rstCache, StringComparison.OrdinalIgnoreCase))
            return (null, null, $"{full} is not in RST's download cache {rstCache}");
        if (!string.Equals(Path.GetFileName(full), ProductCatalog.RstAsset, StringComparison.OrdinalIgnoreCase))
            return (null, null, $"{full} is not {ProductCatalog.RstAsset}");
        var record = InstallCache.ReadVerified(full, log);
        if (record is null || record.Product != ProductCatalog.RstId || ReleaseVersion.TryParseTag("v" + record.Version) is not { } version)
            return (null, null, $"{full} was not verified by the tray, or changed since");
        if (!string.Equals(Path.GetFileName(Path.GetDirectoryName(full)), version.ToString(), StringComparison.Ordinal))
            return (null, null, $"{full} is not in its version's folder ({version})");
        MsiIdentity identity;
        try
        {
            identity = msi.Read(full);
        }
        catch (Exception ex)
        {
            return (null, null, $"{full}: its Property table is unreadable: {ex.Message}");
        }
        var check = InstallChecks.Identity(ProductCatalog.RstProduct, version, identity);
        return check.Passed ? (full, record, null) : (null, null, $"{check.Problem}: {check.Detail}");
    }

    private static void Finish(InstallPaths paths, SelfUpdateResult result, ISelfUpdateHost host, Action<string> log, bool writeResult)
    {
        if (writeResult)
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
