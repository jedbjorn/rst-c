// rst-c's own (not vendored): what kind of network failure happened, so the tray's pages say it in plain words and
// the raw detail stays in the log (F17 WU4; WU3 review D3). Used by the feed (ReleaseFeed) and the verified download
// (VerifiedDownload). The pages map each kind to one short phrase (TrayStrings); nothing here is shown to the user.

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace RST.Core.Updates;

/// <summary>Why a request to GitHub failed, in the few kinds the pages tell apart.</summary>
public enum NetFailure
{
    /// <summary>An answer RST did not expect (a changed page, a bad redirect, a malformed file).</summary>
    Unexpected,

    /// <summary>GitHub could not be reached: no network, no DNS, a refused or broken connection, a TLS failure.</summary>
    Offline,

    /// <summary>GitHub did not answer in time.</summary>
    TimedOut,

    /// <summary>The proxy refused the request (HTTP 407, or a refused proxy tunnel).</summary>
    ProxyRefused,

    /// <summary>GitHub asked RST to wait (HTTP 403 or 429 from the API: its hourly limit for this network).</summary>
    RateLimited,

    /// <summary>GitHub answered with another HTTP error (see the status code).</summary>
    HttpStatus,

    /// <summary>The tray was closing.</summary>
    Cancelled,
}

public static class NetFailures
{
    /// <summary>The kind for an exception from <see cref="HttpMessageInvoker.SendAsync"/> or from reading a body.</summary>
    public static NetFailure FromException(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.ProxyAuthenticationRequired } => NetFailure.ProxyRefused,
        HttpRequestException { HttpRequestError: HttpRequestError.ProxyTunnelError } => NetFailure.ProxyRefused,
        HttpRequestException => NetFailure.Offline,
        SocketException or IOException => NetFailure.Offline,
        TimeoutException => NetFailure.TimedOut,
        _ => NetFailure.Unexpected,
    };

    /// <summary>The kind for an HTTP error status.</summary>
    public static NetFailure FromStatus(int status) => status switch
    {
        407 => NetFailure.ProxyRefused,
        403 or 429 => NetFailure.RateLimited,
        _ => NetFailure.HttpStatus,
    };
}
