// rst-c's own: a fake GitHub for the RST tray's verified installs (F17 WU4, AC6). It serves, per release, exactly
// what github.com and api.github.com do: SHA256SUMS.txt and the MSI under releases/download/<tag>/ (each redirected,
// as GitHub does, to the release-asset host, or to any host a test names), and the releases/tags/<tag> JSON with each
// asset's digest. Every request is recorded. Shared as source with RST.RuntimeTests (linked there), where it serves
// the real fixture MSIs to the real msiexec.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RST.Core.Updates;

namespace RST.Tests.Updates;

/// <summary>One release the fake serves.</summary>
internal sealed class FakeReleaseData
{
    public FakeReleaseData(string repo, string tag, string asset, byte[] bytes)
    {
        Repo = repo;
        Tag = tag;
        Asset = asset;
        Bytes = bytes;
        Sums = $"{Sha256(bytes)}  {asset}\n";
        Digest = "sha256:" + Sha256(bytes);
    }

    public string Repo { get; }

    public string Tag { get; set; }

    /// <summary>The asset name the release lists and serves.</summary>
    public string Asset { get; set; }

    /// <summary>The MSI's bytes as served (a test may serve other bytes than the sums and digest vouch for).</summary>
    public byte[] Bytes { get; set; }

    /// <summary>SHA256SUMS.txt's text; null: the release has none (404).</summary>
    public string? Sums { get; set; }

    /// <summary>The asset's digest in the API answer; null: none.</summary>
    public string? Digest { get; set; }

    /// <summary>The API's tag_name (default: the tag).</summary>
    public string? ApiTag { get; set; }

    public bool Draft { get; set; }

    public bool Prerelease { get; set; }

    /// <summary>Where releases/download/… redirects (GitHub's release-asset host by default); null: served at github.com.</summary>
    public string? RedirectHost { get; set; } = "release-assets.githubusercontent.com";

    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public string ApiJson() => JsonSerializer.Serialize(new
    {
        tag_name = ApiTag ?? Tag,
        draft = Draft,
        prerelease = Prerelease,
        assets = new object[]
        {
            new { name = Asset, digest = Digest },
            new { name = "SHA256SUMS.txt", digest = Sums is null ? null : "sha256:" + Sha256(Encoding.UTF8.GetBytes(Sums)) },
        },
    });
}

/// <summary>github.com, api.github.com and the release-asset hosts, in memory.</summary>
internal sealed class FakeGitHub : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly List<FakeReleaseData> _releases = new();

    public List<Uri> Requests { get; } = new();

    /// <summary>Thrown for every request (null: answer normally), e.g. an HttpRequestException for "offline".</summary>
    public Exception? Fail { get; set; }

    /// <summary>The status every request answers (null: normal), e.g. 407 from a proxy.</summary>
    public HttpStatusCode? Status { get; set; }

    public int Count
    {
        get { lock (_gate) return Requests.Count; }
    }

    public FakeReleaseData Add(string repo, string tag, string asset, byte[] bytes)
    {
        var release = new FakeReleaseData(repo, tag, asset, bytes);
        lock (_gate) _releases.Add(release);
        return release;
    }

    public FakeReleaseData Release(string tag) { lock (_gate) return _releases.Single(r => r.Tag == tag); }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        lock (_gate) Requests.Add(uri);
        if (Fail is { } fail) throw fail;
        if (Status is { } status) return Task.FromResult(new HttpResponseMessage(status));
        return Task.FromResult(Answer(uri));
    }

    private HttpResponseMessage Answer(Uri uri)
    {
        var path = uri.AbsolutePath;
        lock (_gate)
        {
            foreach (var r in _releases)
            {
                var download = $"/{r.Repo}/releases/download/{r.Tag}/";
                if (uri.Host == "github.com" && path.StartsWith(download, StringComparison.Ordinal))
                {
                    var file = path[download.Length..];
                    if (file != "SHA256SUMS.txt" && file != r.Asset) return new HttpResponseMessage(HttpStatusCode.NotFound);
                    if (file == "SHA256SUMS.txt" && r.Sums is null) return new HttpResponseMessage(HttpStatusCode.NotFound);
                    if (r.RedirectHost is { } host)
                    {
                        var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                        redirect.Headers.Location = new Uri($"https://{host}/github-production-release-asset/{r.Repo.Replace('/', '-')}/{r.Tag}/{file}?sig=x");
                        return redirect;
                    }
                    return Body(r, file);
                }
                var assetPath = $"/github-production-release-asset/{r.Repo.Replace('/', '-')}/{r.Tag}/";
                if (uri.Host != "github.com" && uri.Host != "api.github.com" && path.StartsWith(assetPath, StringComparison.Ordinal))
                    return Body(r, path[assetPath.Length..]);
                if (uri.Host == "api.github.com" && path == $"/repos/{r.Repo}/releases/tags/{r.Tag}")
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(r.ApiJson(), Encoding.UTF8, "application/json") };
            }
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Body(FakeReleaseData r, string file) => new(HttpStatusCode.OK)
    {
        Content = file == "SHA256SUMS.txt" ? new StringContent(r.Sums!, Encoding.UTF8) : new ByteArrayContent(r.Bytes),
    };
}

/// <summary>An MSI Property table per file path (by the file's bytes' first line, so a copy keeps its identity).</summary>
internal sealed class FakeMsiReader : IMsiReader
{
    private readonly Func<string, MsiIdentity> _read;

    public FakeMsiReader(Func<string, MsiIdentity> read) => _read = read;

    public int Reads { get; private set; }

    public MsiIdentity Read(string msiPath)
    {
        Reads++;
        return _read(msiPath);
    }

    /// <summary>
    /// Reads a fake MSI written by <see cref="FakeMsi"/>: its text holds the identity as
    /// <c>UpgradeCode|ProductVersion|ALLUSERS|MSIINSTALLPERUSER</c>.
    /// </summary>
    public static FakeMsiReader FromText() => new(path =>
    {
        var text = System.IO.File.ReadAllText(path);
        if (!text.StartsWith("MSI|", StringComparison.Ordinal)) throw new InvalidOperationException("MsiOpenDatabase returned 1620 (not an MSI)");
        var f = text.Split('|');
        return new MsiIdentity(f[1], f[2], "{11111111-2222-3333-4444-555555555555}", f[3].Length == 0 ? null : f[3], f[4].Length == 0 ? null : f[4]);
    });
}

/// <summary>Fake MSI bytes that FakeMsiReader.FromText understands.</summary>
internal static class FakeMsi
{
    public static byte[] Bytes(string upgradeCode, string version, string? allUsers = null, string? perUser = null, string salt = "") =>
        Encoding.UTF8.GetBytes($"MSI|{upgradeCode}|{version}|{allUsers}|{perUser}|{salt}");

    /// <summary>Fake RST-Setup.exe bytes: its four-part file version and product name.</summary>
    public static byte[] Bundle(string fileVersion, string productName = "RST") =>
        Encoding.UTF8.GetBytes($"BUNDLE|{fileVersion}|{productName}|");
}

/// <summary>msiexec, scripted: each call answers the next code (0 when the script is used up) and may change what is installed.</summary>
internal sealed class FakeMsiexec : IMsiexec
{
    private readonly Queue<int> _codes = new();

    public List<(string Msi, string Log)> Calls { get; } = new();

    /// <summary>Runs after each call with its exit code (a test updates the fake Windows Installer here).</summary>
    public Action<string, int>? OnRun { get; set; }

    public FakeMsiexec Then(params int[] codes)
    {
        foreach (var c in codes) _codes.Enqueue(c);
        return this;
    }

    /// <summary>Runs before each call (a test checks, e.g., that the package is held against writes while it runs).</summary>
    public Action<string>? BeforeRun { get; set; }

    public Task<int> InstallAsync(string msiPath, string logPath, CancellationToken cancel)
    {
        BeforeRun?.Invoke(msiPath);
        var code = _codes.Count > 0 ? _codes.Dequeue() : 0;
        Calls.Add((msiPath, logPath));
        OnRun?.Invoke(msiPath, code);
        return Task.FromResult(code);
    }

    /// <summary>RST-Setup.exe runs, recorded in Calls too (the path ends in .exe).</summary>
    public Task<int> RunBundleAsync(string exePath, string logPath, CancellationToken cancel) => InstallAsync(exePath, logPath, cancel);
}

/// <summary>A bundle's version resource, read from fake bundle bytes (<see cref="FakeMsi.Bundle"/>).</summary>
internal sealed class FakeBundleReader : IBundleReader
{
    public BundleIdentity Read(string exePath)
    {
        var text = System.IO.File.ReadAllText(exePath);
        if (!text.StartsWith("BUNDLE|", StringComparison.Ordinal)) throw new InvalidOperationException("no version resource");
        var f = text.Split('|');
        return new BundleIdentity(f[1], f[2]);
    }
}

/// <summary>Revit, scripted: Running until Close().</summary>
internal sealed class FakeRevit : IRevitWatch
{
    private TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Running { get; private set; }

    public int Waits { get; private set; }

    /// <summary>The process list cannot be read (review N3: that must count as "Revit may be running").</summary>
    public bool Throws { get; set; }

    public void Open()
    {
        Running = true;
        _closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Close()
    {
        Running = false;
        _closed.TrySetResult();
    }

    public bool AnyRunning() => Throws ? throw new InvalidOperationException("access denied") : Running;

    public async Task WaitUntilNoneAsync(CancellationToken cancel)
    {
        Waits++;
        if (Throws) throw new InvalidOperationException("access denied");
        if (!Running) return;
        await _closed.Task.WaitAsync(cancel).ConfigureAwait(false);
    }
}

/// <summary>The self-updater, scripted.</summary>
internal sealed class FakeSelfUpdater : ISelfUpdater
{
    public bool PerMachineTray { get; set; }

    public bool InstalledByBundle { get; set; }

    /// <summary>Null: it starts; otherwise this is why not.</summary>
    public string? Failure { get; set; }

    public List<string> Started { get; } = new();

    public string? Start(string msiPath)
    {
        if (Failure is null) Started.Add(msiPath);
        return Failure;
    }
}
