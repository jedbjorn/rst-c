// rst-c's own (not vendored): the RST tray's one HTTP client (spec #20 §4; F17 WU3).
//
//   - redirects are NOT followed (AllowAutoRedirect = false): the feed reads releases/latest's Location itself,
//     and WU4 follows a download's redirect only to a host on RST.Core's UpdateHosts list;
//   - the system proxy (UseProxy with no Proxy set means HttpClient.DefaultProxy: the Windows/IE proxy settings,
//     a PAC script included), and the signed-in user's default credentials for that proxy only. No credentials
//     are ever sent to GitHub, no cookies are kept;
//   - a plain User-Agent, "RST-Tray", with no version or anything else about the PC or the user.

using System.Net;
using System.Net.Http;

namespace RST.Tray.Updates;

internal static class TrayHttp
{
    public const string UserAgent = "RST-Tray";

    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = true,
        DefaultProxyCredentials = CredentialCache.DefaultCredentials,
        UseCookies = false,
        Credentials = null,
        PreAuthenticate = false,
        ConnectTimeout = TimeSpan.FromSeconds(20),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
    };

    /// <summary>A client over <paramref name="handler"/> (the real one, or a test's counting handler).</summary>
    public static HttpClient CreateClient(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }
}
