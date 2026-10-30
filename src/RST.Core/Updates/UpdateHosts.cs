// rst-c's own (not vendored): the ONE place that lists the hosts the RST tray may talk to (spec #20 §4, §5; F17
// WU3). Every URL the tray requests is built from a fixed repo and checked here first, and a redirect is followed
// only to a host this list allows for that purpose (WU4's downloads). Nothing else is ever contacted.
//
//   github.com                           the releases/latest redirect (§4) and release downloads (§5)
//   api.github.com                       one releases/tags/<tag> call per update found, for the asset digest (§5)
//   release-assets.githubusercontent.com where github.com redirects a release download (checked 2026-10-30)
//   objects.githubusercontent.com        GitHub's previous release-asset host, still seen on older assets
//
// Exact host names only (no wildcards, no subdomains), HTTPS on the default port, no user info.

using System;
using System.Collections.Generic;
using System.Linq;

namespace RST.Core.Updates;

/// <summary>What a request is for, which decides the hosts it may reach.</summary>
public enum UpdateHostUse
{
    /// <summary>The <c>releases/latest</c> HEAD, and the Location it answers with.</summary>
    Feed,

    /// <summary>The one GitHub REST call per update found (WU4).</summary>
    Api,

    /// <summary>A release asset download's first request: github.com (WU4).</summary>
    Download,

    /// <summary>
    /// Where a download may be redirected: GitHub's release-asset hosts only, never github.com again (spec #20 §5;
    /// WU4 review N2).
    /// </summary>
    DownloadRedirect,
}

public static class UpdateHosts
{
    public const string GitHub = "github.com";
    public const string GitHubApi = "api.github.com";

    /// <summary>GitHub's release-asset hosts: where a <c>releases/download</c> URL redirects.</summary>
    public static IReadOnlyList<string> ReleaseAssetHosts { get; } =
        new[] { "release-assets.githubusercontent.com", "objects.githubusercontent.com" };

    /// <summary>The hosts allowed for <paramref name="use"/>.</summary>
    public static IReadOnlyList<string> For(UpdateHostUse use) => use switch
    {
        UpdateHostUse.Feed => new[] { GitHub },
        UpdateHostUse.Api => new[] { GitHubApi },
        UpdateHostUse.Download => new[] { GitHub },
        UpdateHostUse.DownloadRedirect => ReleaseAssetHosts,
        _ => Array.Empty<string>(),
    };

    /// <summary>Every host the tray may contact, for the docs and the IT allowlist.</summary>
    public static IReadOnlyList<string> All { get; } =
        new[] { GitHub, GitHubApi }.Concat(ReleaseAssetHosts).ToArray();

    /// <summary>True when <paramref name="uri"/> is absolute HTTPS on the default port, with no user info, to a host allowed for <paramref name="use"/>.</summary>
    public static bool IsAllowed(Uri? uri, UpdateHostUse use)
    {
        if (uri is null || !uri.IsAbsoluteUri) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)) return false;
        if (!uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        var host = uri.IdnHost;
        foreach (var allowed in For(use))
            if (string.Equals(host, allowed, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
