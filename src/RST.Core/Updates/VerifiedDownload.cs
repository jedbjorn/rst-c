// rst-c's own (not vendored): download a release MSI and prove it (spec #20 §5; research Q5; decision #15; F17 WU4).
// Every request is BUILT from the product's fixed repo, the full-release tag and the asset name (InstallChecks), and
// sent through the tray's one HttpClient, which never follows redirects by itself:
//
//   0. check 4 first (no downgrade), and the asset name against the hard-coded pattern: no request at all otherwise
//   1. GET  github.com/<repo>/releases/download/<tag>/SHA256SUMS.txt    redirects followed only to the release-asset
//                                                                      hosts (UpdateHosts, Download); 64 KB at most
//   2. GET  api.github.com/repos/<repo>/releases/tags/<tag>             no redirect; 4 MB at most; the asset's digest
//      the sums line and the digest must agree before anything is downloaded
//   3. GET  github.com/<repo>/releases/download/<tag>/<asset>           redirects as in 1; 512 MB at most; hashed
//      while it streams into cache\<product>\<version>\<asset>.download, then moved into place when the hash is the
//      one both sources gave
//   4. the MSI's Property table (IMsiReader): UpgradeCode, ProductVersion, per-user
//
// Only then is verified.json written beside it. Anything else is a refusal with a logged reason, the file is deleted,
// and msiexec never sees it. Nothing here throws.

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RST.Core.Updates;

/// <summary>An MSI in the cache that passed every check.</summary>
public sealed record VerifiedMsi(ProductInfo Product, ReleaseVersion Version, string Asset, string Path, string Sha256)
{
    public VerifiedRecord Record(DateTimeOffset at) => new(Product.Id, Version.ToString(), Asset, Sha256, at);
}

/// <summary>The outcome: the verified MSI, or the refusal (with a detail for the log only).</summary>
public sealed record VerifyResult(VerifiedMsi? Msi, InstallProblem Problem, string Detail, NetFailure? Net = null, int? Status = null)
{
    public bool Ok => Msi is not null;

    public static VerifyResult Refused(CheckResult check) => new(null, check.Problem, check.Detail);
}

public sealed class VerifiedDownload
{
    public const string SumsFileName = "SHA256SUMS.txt";

    /// <summary>The largest MSI the tray downloads (RST.msi is about 15 MB).</summary>
    public const long MaxMsiBytes = 512L * 1024 * 1024;

    /// <summary>The largest API answer read (a release's JSON, notes included).</summary>
    public const int MaxApiBytes = 4 * 1024 * 1024;

    public const int MaxRedirects = 5;

    /// <summary>How long SHA256SUMS.txt or the API call may take.</summary>
    public static readonly TimeSpan SmallRequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long the MSI download may take in all.</summary>
    public static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);

    private readonly HttpClient _http;
    private readonly IMsiReader _msi;
    private readonly string _cacheRoot;
    private readonly Action<string> _log;
    private readonly TimeProvider _time;

    /// <param name="http">The tray's client: its handler must not follow redirects (TrayHttp).</param>
    /// <param name="msi">Reads the MSI's Property table.</param>
    /// <param name="cacheRoot">%LocalAppData%\RST\Tray\cache.</param>
    public VerifiedDownload(HttpClient http, IMsiReader msi, string cacheRoot, Action<string>? log = null, TimeProvider? time = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _msi = msi ?? throw new ArgumentNullException(nameof(msi));
        _cacheRoot = cacheRoot ?? throw new ArgumentNullException(nameof(cacheRoot));
        _log = log ?? (_ => { });
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Downloads <paramref name="product"/> <paramref name="version"/> and checks it all four ways, against what is
    /// installed now (<paramref name="installed"/>; null when it is not). Never throws.
    /// </summary>
    public async Task<VerifyResult> RunAsync(ProductInfo product, ReleaseVersion version, ReleaseVersion? installed, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(product);
        var result = await RunCoreAsync(product, version, installed, cancel).ConfigureAwait(false);
        if (result.Ok) _log($"verified {product.Name} {version}: {result.Msi!.Asset} sha256 {result.Msi.Sha256} (SHA256SUMS.txt and GitHub's digest agree; UpgradeCode, version and per-user scope match)");
        else _log($"refused {product.Name} {version}: {result.Problem}: {result.Detail}");
        return result;
    }

    private async Task<VerifyResult> RunCoreAsync(ProductInfo product, ReleaseVersion version, ReleaseVersion? installed, CancellationToken cancel)
    {
        var asset = InstallChecks.AssetName(product, version, out var nameCheck);
        if (asset is null) return VerifyResult.Refused(nameCheck);
        var newer = InstallChecks.Newer(version, installed);
        if (!newer.Passed) return VerifyResult.Refused(newer);

        Uri sumsUri, apiUri, assetUri;
        try
        {
            sumsUri = InstallChecks.DownloadUri(product.Repo, version, SumsFileName);
            apiUri = InstallChecks.ReleaseApiUri(product.Repo, version);
            assetUri = InstallChecks.DownloadUri(product.Repo, version, asset);
        }
        catch (ArgumentException ex)
        {
            return VerifyResult.Refused(new CheckResult(InstallProblem.NotInstallable, ex.Message));
        }

        // 1. SHA256SUMS.txt, from the same release.
        var sums = await FetchTextAsync(sumsUri, UpdateHostUse.Download, Sha256Sums.MaxLength, api: false, cancel).ConfigureAwait(false);
        if (sums.Failure is { } sumsFailure)
            return sumsFailure with { Problem = sums.Status == 404 ? InstallProblem.SumsMissing : sumsFailure.Problem };
        var parsed = Sha256Sums.TryParse(sums.Text, out var sumsError);
        if (parsed is null) return VerifyResult.Refused(new CheckResult(InstallProblem.SumsUnreadable, "SHA256SUMS.txt: " + sumsError));
        if (parsed.HashOf(asset) is not { } listed)
            return VerifyResult.Refused(new CheckResult(InstallProblem.NotInSums, $"SHA256SUMS.txt has no line for {asset}"));

        // 2. GitHub's own digest of the uploaded asset: one API call.
        var release = await FetchTextAsync(apiUri, UpdateHostUse.Api, MaxApiBytes, api: true, cancel).ConfigureAwait(false);
        if (release.Failure is { } apiFailure)
            return apiFailure with { Problem = release.Status == 404 ? InstallProblem.ReleaseNotFound : apiFailure.Problem };
        var digest = InstallChecks.FindDigest(release.Text!, version, asset, out var digestCheck);
        if (digest is null) return VerifyResult.Refused(digestCheck);
        var agree = InstallChecks.SourcesAgree(listed, digest, asset);
        if (!agree.Passed) return VerifyResult.Refused(agree);

        // 3. The MSI itself.
        string dir, path;
        try
        {
            dir = InstallCache.VersionDir(_cacheRoot, product.Id, version);
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, asset);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return VerifyResult.Refused(new CheckResult(InstallProblem.DiskError, "cache folder: " + ex.Message));
        }

        string actual;
        if (TryReuse(path, listed))
        {
            actual = listed;
            _log($"{asset} {version} is already in the cache with the expected sha256; not downloaded again");
        }
        else
        {
            var download = await DownloadAsync(assetUri, path, cancel).ConfigureAwait(false);
            if (download.Failure is { } downloadFailure) return downloadFailure;
            actual = download.Text!;
            var hash = InstallChecks.HashMatches(actual, listed, asset);
            if (!hash.Passed)
            {
                TryDelete(path + ".download");
                return VerifyResult.Refused(hash);
            }
            try
            {
                File.Move(path + ".download", path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(path + ".download");
                return VerifyResult.Refused(new CheckResult(InstallProblem.DiskError, $"{asset}: {ex.Message}"));
            }
        }

        // 4. The MSI says what it is.
        MsiIdentity identity;
        try
        {
            identity = _msi.Read(path);
        }
        catch (Exception ex)
        {
            TryDelete(path);
            return VerifyResult.Refused(new CheckResult(InstallProblem.MsiUnreadable, $"{asset}: {ex.Message}"));
        }
        var who = InstallChecks.Identity(product, version, identity);
        if (!who.Passed)
        {
            TryDelete(path);
            return VerifyResult.Refused(who);
        }

        var verified = new VerifiedMsi(product, version, asset, path, actual);
        try
        {
            InstallCache.WriteVerified(path, verified.Record(_time.GetUtcNow()), _log);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return VerifyResult.Refused(new CheckResult(InstallProblem.DiskError, "verified.json: " + ex.Message));
        }
        return new VerifyResult(verified, InstallProblem.None, "");
    }

    /// <summary>
    /// Re-checks an MSI already in the cache (the previous version): its file still has the recorded SHA256, and its
    /// Property table still identifies the product and version, per user. Not check 4: the caller decides that.
    /// </summary>
    public VerifyResult Recheck(ProductInfo product, ReleaseVersion version, string path)
    {
        var record = InstallCache.ReadVerified(path, _log);
        if (record is null || record.Version != version.ToString() || record.Product != product.Id)
            return VerifyResult.Refused(new CheckResult(InstallProblem.HashMismatch, $"{path}: not the verified {product.Name} {version}"));
        MsiIdentity identity;
        try
        {
            identity = _msi.Read(path);
        }
        catch (Exception ex)
        {
            return VerifyResult.Refused(new CheckResult(InstallProblem.MsiUnreadable, $"{path}: {ex.Message}"));
        }
        var who = InstallChecks.Identity(product, version, identity);
        return who.Passed
            ? new VerifyResult(new VerifiedMsi(product, version, record.Asset, path, record.Sha256), InstallProblem.None, "")
            : VerifyResult.Refused(who);
    }

    private bool TryReuse(string path, string expected)
    {
        try
        {
            return File.Exists(path) && InstallCache.Sha256Of(path) == expected;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A small text body (Failure set on any refusal; Status is the last HTTP status seen).</summary>
    private readonly record struct Fetched(string? Text, VerifyResult? Failure, int? Status);

    private async Task<Fetched> FetchTextAsync(Uri uri, UpdateHostUse use, int maxBytes, bool api, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(SmallRequestTimeout);
        try
        {
            var (response, failure) = await SendAsync(uri, use, followRedirects: !api, api, timeout.Token).ConfigureAwait(false);
            if (failure is not null) return new Fetched(null, failure, failure.Status);
            using (response)
            {
                if (response!.Content.Headers.ContentLength is { } length && length > maxBytes)
                    return new Fetched(null, VerifyResult.Refused(new CheckResult(InstallProblem.TooLarge, $"{uri.AbsolutePath}: {length} bytes")), 200);
                await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var copy = new MemoryStream();
                var buffer = new byte[16 * 1024];
                int read;
                while ((read = await body.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    if (copy.Length + read > maxBytes)
                        return new Fetched(null, VerifyResult.Refused(new CheckResult(InstallProblem.TooLarge, $"{uri.AbsolutePath}: more than {maxBytes} bytes")), 200);
                    copy.Write(buffer, 0, read);
                }
                return new Fetched(Encoding.UTF8.GetString(copy.ToArray()), null, 200);
            }
        }
        catch (Exception ex)
        {
            return new Fetched(null, NetworkFailure(ex, uri, cancel), null);
        }
    }

    /// <summary>Streams the asset into <paramref name="path"/>.download; Text is its lower-case hex SHA256.</summary>
    private async Task<Fetched> DownloadAsync(Uri uri, string path, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(DownloadTimeout);
        var partial = path + ".download";
        FileStream file;
        try
        {
            file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Fetched(null, VerifyResult.Refused(new CheckResult(InstallProblem.DiskError, $"{partial}: {ex.Message}")), null);
        }

        var complete = false;
        try
        {
            await using (file)
            {
                var (response, failure) = await SendAsync(uri, UpdateHostUse.Download, followRedirects: true, api: false, timeout.Token).ConfigureAwait(false);
                if (failure is not null) return new Fetched(null, failure, failure.Status);
                using (response)
                {
                    if (response!.Content.Headers.ContentLength is { } length && length > MaxMsiBytes)
                        return new Fetched(null, VerifyResult.Refused(new CheckResult(InstallProblem.TooLarge, $"{uri.AbsolutePath}: {length} bytes")), 200);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                    var buffer = new byte[1 << 16];
                    long total = 0;
                    int read;
                    while ((read = await body.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                    {
                        total += read;
                        if (total > MaxMsiBytes)
                            return new Fetched(null, VerifyResult.Refused(new CheckResult(InstallProblem.TooLarge, $"{uri.AbsolutePath}: more than {MaxMsiBytes} bytes")), 200);
                        hash.AppendData(buffer, 0, read);
                        try
                        {
                            await file.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            return new Fetched(null, VerifyResult.Refused(new CheckResult(InstallProblem.DiskError, $"{partial}: {ex.Message}")), 200);
                        }
                    }
                    await file.FlushAsync(timeout.Token).ConfigureAwait(false);
                    complete = true;
                    return new Fetched(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), null, 200);
                }
            }
        }
        catch (Exception ex)
        {
            return new Fetched(null, NetworkFailure(ex, uri, cancel), null);
        }
        finally
        {
            if (!complete) TryDelete(partial);
        }
    }

    /// <summary>
    /// GET <paramref name="uri"/>, following at most <see cref="MaxRedirects"/> redirects, each only to a host allowed
    /// for <paramref name="use"/> (the release-asset hosts for downloads; none for the API). Returns the final 200
    /// response, or a refusal (the response is then disposed, except that its status is reported).
    /// </summary>
    private async Task<(HttpResponseMessage? Response, VerifyResult? Failure)> SendAsync(
        Uri uri, UpdateHostUse use, bool followRedirects, bool api, CancellationToken cancel)
    {
        var target = uri;
        for (var hop = 0; ; hop++)
        {
            if (!UpdateHosts.IsAllowed(target, use) || !string.IsNullOrEmpty(target.Fragment))
                return (null, VerifyResult.Refused(new CheckResult(InstallProblem.RedirectNotAllowed,
                    $"{uri.AbsolutePath}: {(hop == 0 ? "request" : "redirect")} to {target.GetLeftPart(UriPartial.Authority)} is off the allowlist")));

            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            if (api)
            {
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            }
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            if (code is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (!followRedirects)
                    return (null, VerifyResult.Refused(new CheckResult(InstallProblem.RedirectNotAllowed, $"{uri.AbsolutePath}: the API redirected (HTTP {code})")));
                if (location is null)
                    return (null, VerifyResult.Refused(new CheckResult(InstallProblem.Network, $"{uri.AbsolutePath}: HTTP {code} without a Location")) with { Net = NetFailure.Unexpected });
                if (hop >= MaxRedirects)
                    return (null, VerifyResult.Refused(new CheckResult(InstallProblem.Network, $"{uri.AbsolutePath}: more than {MaxRedirects} redirects")) with { Net = NetFailure.Unexpected });
                try
                {
                    target = location.IsAbsoluteUri ? location : new Uri(target, location);
                }
                catch (UriFormatException)
                {
                    return (null, VerifyResult.Refused(new CheckResult(InstallProblem.RedirectNotAllowed, $"{uri.AbsolutePath}: unreadable Location")));
                }
                continue;
            }
            if (response.StatusCode != HttpStatusCode.OK)
            {
                var failure = new VerifyResult(null, InstallProblem.Network, $"{target.GetLeftPart(UriPartial.Path)}: HTTP {code}",
                                               NetFailures.FromStatus(code), code);
                response.Dispose();
                return (null, failure);
            }
            return (response, null);
        }
    }

    private VerifyResult NetworkFailure(Exception ex, Uri uri, CancellationToken cancel)
    {
        if (cancel.IsCancellationRequested)
            return new VerifyResult(null, InstallProblem.Cancelled, $"{uri.AbsolutePath}: cancelled", NetFailure.Cancelled);
        if (ex is OperationCanceledException)
            return new VerifyResult(null, InstallProblem.Network, $"{uri.AbsolutePath}: no answer in time", NetFailure.TimedOut);
        return new VerifyResult(null, InstallProblem.Network, $"{uri.AbsolutePath}: {ex.GetType().Name}: {ex.Message}", NetFailures.FromException(ex));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next prune.
        }
    }
}
