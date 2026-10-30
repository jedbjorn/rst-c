// rst-c's own (not vendored): the update feed (spec #20 §4; research Q5; F17 WU3). One request per product:
//
//   HEAD https://github.com/<owner>/<repo>/releases/latest      (redirects NOT followed)
//   302  Location: https://github.com/<owner>/<repo>/releases/tag/v3.1.1
//
// GitHub answers with the latest FULL release (pre-releases and drafts are never "latest"); a repo with no full
// release redirects to /releases instead. This is github.com's web front end, not api.github.com, so it is outside
// the REST API's 60-an-hour unauthenticated limit. The Location is checked, never followed: an https URL on
// github.com, for this exact repo, ending in /releases/tag/<tag> where the tag is a full release
// (ReleaseVersion.TryParseTag). Anything else fails closed ("couldn't check"). Nothing here throws.

using System;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RST.Core.Updates;

/// <summary>What the feed said about one repo.</summary>
public enum FeedOutcome
{
    /// <summary>The latest full release is <see cref="FeedResult.Version"/>.</summary>
    Latest,

    /// <summary>The repo has no full release yet (GitHub redirected to /releases).</summary>
    NoRelease,

    /// <summary>The check failed: offline, a proxy refusal, an unexpected answer. Reported as "couldn't check".</summary>
    Failed,
}

/// <param name="Outcome">What happened.</param>
/// <param name="Version">The latest full release (only for <see cref="FeedOutcome.Latest"/>).</param>
/// <param name="Reason">Why it failed, for the log only (only for <see cref="FeedOutcome.Failed"/>).</param>
/// <param name="Failure">The kind of failure, for the pages' plain wording (WU3 review D3).</param>
/// <param name="Status">The HTTP status, when the failure is one.</param>
public sealed record FeedResult(
    FeedOutcome Outcome, ReleaseVersion? Version = null, string? Reason = null,
    NetFailure Failure = NetFailure.Unexpected, int? Status = null)
{
    public static FeedResult Latest(ReleaseVersion version) => new(FeedOutcome.Latest, version);

    public static FeedResult None { get; } = new(FeedOutcome.NoRelease);

    public static FeedResult Fail(string reason, NetFailure failure = NetFailure.Unexpected, int? status = null) =>
        new(FeedOutcome.Failed, Reason: reason, Failure: failure, Status: status);
}

public static class ReleaseFeed
{
    // owner/name as GitHub allows them (owner: letters, digits, hyphens; name: also . and _).
    private static readonly Regex RepoPattern = new(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9._-]{1,100}\z",
        RegexOptions.CultureInvariant);

    /// <summary>How long one HEAD may take before it counts as failed.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>True for a well-formed <c>owner/name</c>.</summary>
    public static bool IsRepo(string? repo) =>
        !string.IsNullOrEmpty(repo) && RepoPattern.IsMatch(repo) && !repo.EndsWith("/.", StringComparison.Ordinal)
        && !repo.EndsWith("/..", StringComparison.Ordinal);

    /// <summary><c>https://github.com/&lt;repo&gt;/releases/latest</c>, built (never taken from data).</summary>
    public static Uri LatestUri(string repo)
    {
        if (!IsRepo(repo)) throw new ArgumentException($"'{repo}' is not an owner/name repo.", nameof(repo));
        return new Uri("https://" + UpdateHosts.GitHub + "/" + repo + "/releases/latest", UriKind.Absolute);
    }

    /// <summary>
    /// Reads the answer to <see cref="LatestUri"/>: <paramref name="status"/> and its Location header
    /// (<paramref name="location"/>, absolute or relative to <paramref name="requestUri"/>).
    /// </summary>
    public static FeedResult ParseLatestRedirect(string repo, HttpStatusCode status, Uri? location, Uri requestUri)
    {
        if (!IsRepo(repo)) return FeedResult.Fail("not a repo: " + repo);
        var code = (int)status;
        if (code is not (301 or 302 or 303 or 307 or 308))
            return code == 200
                ? FeedResult.Fail("HTTP 200: the redirect was followed or GitHub changed releases/latest")
                : FeedResult.Fail($"HTTP {code}", NetFailures.FromStatus(code), code);
        if (location is null) return FeedResult.Fail($"HTTP {code} without a Location");

        Uri target;
        try
        {
            target = location.IsAbsoluteUri ? location : new Uri(requestUri, location);
        }
        catch (UriFormatException)
        {
            return FeedResult.Fail("unreadable Location");
        }
        if (!UpdateHosts.IsAllowed(target, UpdateHostUse.Feed))
            return FeedResult.Fail("Location off the allowlist: " + target.GetLeftPart(UriPartial.Authority));
        if (!string.IsNullOrEmpty(target.Query) || !string.IsNullOrEmpty(target.Fragment))
            return FeedResult.Fail("unexpected Location: " + target.AbsoluteUri);

        // GitHub echoes the request's owner/name case; repo names are case-insensitive.
        var path = target.AbsolutePath.TrimEnd('/');
        var releases = "/" + repo + "/releases";
        if (string.Equals(path, releases, StringComparison.OrdinalIgnoreCase)) return FeedResult.None;
        var tagPrefix = releases + "/tag/";
        if (!path.StartsWith(tagPrefix, StringComparison.OrdinalIgnoreCase))
            return FeedResult.Fail("unexpected Location: " + target.AbsoluteUri);

        var tag = path[tagPrefix.Length..];
        return ReleaseVersion.TryParseTag(tag) is { } version
            ? FeedResult.Latest(version)
            : FeedResult.Fail("not a full-release tag: " + tag);
    }

    /// <summary>
    /// Sends the one HEAD for <paramref name="repo"/> through <paramref name="client"/> (whose handler must not
    /// follow redirects) and reads the answer. Never throws; a failure or a timeout is <see cref="FeedOutcome.Failed"/>.
    /// </summary>
    public static async Task<FeedResult> GetLatestAsync(HttpMessageInvoker client, string repo, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(client);
        Uri uri;
        try
        {
            uri = LatestUri(repo);
        }
        catch (ArgumentException ex)
        {
            return FeedResult.Fail(ex.Message);
        }
        if (!UpdateHosts.IsAllowed(uri, UpdateHostUse.Feed)) return FeedResult.Fail("off the allowlist: " + uri);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            return ParseLatestRedirect(repo, response.StatusCode, response.Headers.Location, uri);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return FeedResult.Fail("cancelled", NetFailure.Cancelled);
        }
        catch (OperationCanceledException)
        {
            return FeedResult.Fail($"no answer in {RequestTimeout.TotalSeconds:0} s", NetFailure.TimedOut);
        }
        catch (Exception ex)
        {
            return FeedResult.Fail(ex.GetType().Name + ": " + ex.Message, NetFailures.FromException(ex));
        }
    }
}
