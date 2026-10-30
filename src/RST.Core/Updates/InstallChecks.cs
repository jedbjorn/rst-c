// rst-c's own (not vendored): the checks a release MSI must pass before the RST tray runs msiexec on it (spec #20 §5;
// research Q5; decision #15; F17 WU4). Pure, so RST.Tests proves every refusal on Linux:
//
//   1. the URL is BUILT from constants: https://github.com/<fixed repo>/releases/download/<tag>/<asset>, where the
//      tag is a full release (v<a>.<b>.<c>) and the asset name matches the product's hard-coded pattern;
//   2. the file's SHA256 equals its line in that release's SHA256SUMS.txt AND the asset's `digest` from one
//      api.github.com/repos/<repo>/releases/tags/<tag> call (ReleaseApi.FindDigest);
//   3. the MSI's own Property table: the product's hard-coded UpgradeCode, ProductVersion equal to the tag, and a
//      per-user package (ALLUSERS absent, or 2 with MSIINSTALLPERUSER=1, as companions/README.md C1 states);
//   4. the version is newer than the installed one: no downgrades, no reinstall of the same version.
//
// Each refusal is an InstallProblem plus a detail for the log; the pages show only a plain phrase for the problem.

using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RST.Core.Updates;

/// <summary>Why the tray did not install something, or why an install did not finish.</summary>
public enum InstallProblem
{
    None,

    /// <summary>The catalog has no asset name for this product.</summary>
    NotInstallable,

    /// <summary>The built asset name does not match the product's hard-coded pattern.</summary>
    AssetNameBad,

    /// <summary>Not newer than what is installed (a downgrade or the same version).</summary>
    NotNewer,

    /// <summary>GitHub could not be asked (see <see cref="NetFailure"/>).</summary>
    Network,

    /// <summary>A redirect led off the release-asset host allowlist.</summary>
    RedirectNotAllowed,

    /// <summary>The release has no SHA256SUMS.txt.</summary>
    SumsMissing,

    /// <summary>The release's SHA256SUMS.txt is not in the expected form.</summary>
    SumsUnreadable,

    /// <summary>SHA256SUMS.txt has no line for the asset.</summary>
    NotInSums,

    /// <summary>The GitHub API has no such full release (missing, a draft, a pre-release, another tag).</summary>
    ReleaseNotFound,

    /// <summary>The GitHub API lists no asset of that name, or no digest for it.</summary>
    DigestMissing,

    /// <summary>SHA256SUMS.txt and GitHub's digest disagree.</summary>
    DigestMismatch,

    /// <summary>The downloaded file's SHA256 is not the one both sources vouch for.</summary>
    HashMismatch,

    /// <summary>A download was larger than the tray accepts.</summary>
    TooLarge,

    /// <summary>The MSI's Property table could not be read.</summary>
    MsiUnreadable,

    /// <summary>The MSI is another product (its UpgradeCode is not this product's).</summary>
    WrongUpgradeCode,

    /// <summary>The MSI's ProductVersion is not the release's version.</summary>
    WrongVersion,

    /// <summary>The MSI installs for everyone on the PC (ALLUSERS=1), not for this user.</summary>
    WrongScope,

    /// <summary>The download could not be saved (disk full, a locked file).</summary>
    DiskError,

    /// <summary>Windows Installer could not tell what is installed.</summary>
    DetectionFailed,

    /// <summary>The product is installed for everyone on this PC: a per-user tray does not update it.</summary>
    PerMachine,

    /// <summary>msiexec ran and failed (see the exit code and the log).</summary>
    InstallFailed,

    /// <summary>Another installation kept running (msiexec 1618) after the retries.</summary>
    InstallerBusy,

    /// <summary>The updater copy could not be prepared or started (RST updating itself).</summary>
    UpdaterFailed,

    /// <summary>The tray was closing, or the user cancelled.</summary>
    Cancelled,
}

/// <summary>What an MSI's Property table says about itself.</summary>
public sealed record MsiIdentity(
    string? UpgradeCode, string? ProductVersion, string? ProductCode, string? AllUsers, string? InstallPerUser);

/// <summary>Reads an MSI's Property table (RST.Tray's MsiDatabase over msi.dll; tests fake it). Throws when it cannot.</summary>
public interface IMsiReader
{
    MsiIdentity Read(string msiPath);
}

/// <summary>A refusal: the problem (for the pages) and a detail (for the log only).</summary>
public readonly record struct CheckResult(InstallProblem Problem, string Detail)
{
    public static CheckResult Pass { get; } = new(InstallProblem.None, "");

    public bool Passed => Problem == InstallProblem.None;
}

public static class InstallChecks
{
    private static readonly Regex Hex64 = new(@"^[0-9a-f]{64}\z", RegexOptions.CultureInvariant);
    private static readonly Regex Digest = new(@"^sha256:([0-9a-f]{64})\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// The product's asset name for <paramref name="version"/>, or null with the refusal when it has none or the
    /// built name does not match the product's hard-coded pattern.
    /// </summary>
    public static string? AssetName(ProductInfo product, ReleaseVersion version, out CheckResult refusal)
    {
        ArgumentNullException.ThrowIfNull(product);
        refusal = CheckResult.Pass;
        if (!product.IsInstallable)
        {
            refusal = new CheckResult(InstallProblem.NotInstallable, $"{product.Name}: the catalog has no asset name");
            return null;
        }
        var name = string.Format(CultureInfo.InvariantCulture, product.AssetTemplate!, version.ToString());
        bool matches;
        try
        {
            matches = Regex.IsMatch(name, product.AssetPattern!, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex)
        {
            refusal = new CheckResult(InstallProblem.AssetNameBad, $"{product.Name}: the asset pattern is not valid ({ex.Message})");
            return null;
        }
        if (!matches)
        {
            refusal = new CheckResult(InstallProblem.AssetNameBad, $"{product.Name}: asset name '{name}' does not match {product.AssetPattern}");
            return null;
        }
        return name;
    }

    /// <summary><c>https://github.com/&lt;repo&gt;/releases/download/&lt;tag&gt;/&lt;file&gt;</c>, built (never taken from data).</summary>
    public static Uri DownloadUri(string repo, ReleaseVersion version, string fileName)
    {
        if (!ReleaseFeed.IsRepo(repo)) throw new ArgumentException($"'{repo}' is not an owner/name repo.", nameof(repo));
        if (string.IsNullOrEmpty(fileName) || fileName.IndexOfAny(new[] { '/', '\\', '?', '#', '%' }) >= 0 || fileName.Contains(".."))
            throw new ArgumentException($"'{fileName}' is not a release file name.", nameof(fileName));
        return new Uri($"https://{UpdateHosts.GitHub}/{repo}/releases/download/{version.Tag}/{fileName}", UriKind.Absolute);
    }

    /// <summary><c>https://api.github.com/repos/&lt;repo&gt;/releases/tags/&lt;tag&gt;</c>, built.</summary>
    public static Uri ReleaseApiUri(string repo, ReleaseVersion version)
    {
        if (!ReleaseFeed.IsRepo(repo)) throw new ArgumentException($"'{repo}' is not an owner/name repo.", nameof(repo));
        return new Uri($"https://{UpdateHosts.GitHubApi}/repos/{repo}/releases/tags/{version.Tag}", UriKind.Absolute);
    }

    /// <summary>Check 4: <paramref name="version"/> is newer than <paramref name="installed"/> (anything is newer than nothing).</summary>
    public static CheckResult Newer(ReleaseVersion version, ReleaseVersion? installed) =>
        installed is { } current && version <= current
            ? new CheckResult(InstallProblem.NotNewer, $"{version} is not newer than the installed {current}")
            : CheckResult.Pass;

    /// <summary>
    /// Check 2's API half: the asset's digest from the GitHub API's answer for the release (its JSON). The answer
    /// must be the full release <paramref name="version"/> (its tag, not a draft or pre-release) and list exactly one
    /// asset named <paramref name="asset"/> with a <c>sha256:</c> digest. Returns the lower-case hex.
    /// </summary>
    public static string? FindDigest(string json, ReleaseVersion version, string asset, out CheckResult refusal)
    {
        refusal = CheckResult.Pass;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                refusal = new CheckResult(InstallProblem.ReleaseNotFound, "the API answer is not a release");
                return null;
            }
            if (Text(root, "tag_name") != version.Tag)
            {
                refusal = new CheckResult(InstallProblem.ReleaseNotFound, $"the API answered for tag '{Text(root, "tag_name")}', not {version.Tag}");
                return null;
            }
            if (Bool(root, "draft") || Bool(root, "prerelease"))
            {
                refusal = new CheckResult(InstallProblem.ReleaseNotFound, $"{version.Tag} is a draft or a pre-release");
                return null;
            }
            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            {
                refusal = new CheckResult(InstallProblem.DigestMissing, "the API answer lists no assets");
                return null;
            }
            string? digest = null;
            var count = 0;
            foreach (var a in assets.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Object || Text(a, "name") != asset) continue;
                count++;
                digest = Text(a, "digest");
            }
            if (count != 1)
            {
                refusal = new CheckResult(InstallProblem.DigestMissing,
                    count == 0 ? $"the release has no asset {asset}" : $"the release lists {asset} {count} times");
                return null;
            }
            var m = digest is null ? null : Digest.Match(digest);
            if (m is not { Success: true })
            {
                refusal = new CheckResult(InstallProblem.DigestMissing, $"GitHub gives no sha256 digest for {asset} ('{digest}')");
                return null;
            }
            return m.Groups[1].Value;
        }
        catch (JsonException ex)
        {
            refusal = new CheckResult(InstallProblem.ReleaseNotFound, "the API answer is not JSON: " + ex.Message);
            return null;
        }
    }

    /// <summary>Check 2: the two sources agree before anything is downloaded.</summary>
    public static CheckResult SourcesAgree(string sumsHash, string digest, string asset) =>
        string.Equals(sumsHash, digest, StringComparison.Ordinal) && Hex64.IsMatch(sumsHash)
            ? CheckResult.Pass
            : new CheckResult(InstallProblem.DigestMismatch, $"{asset}: SHA256SUMS.txt says {sumsHash}, GitHub's digest is {digest}");

    /// <summary>Check 2: the downloaded file is what both sources vouch for.</summary>
    public static CheckResult HashMatches(string actual, string expected, string asset) =>
        string.Equals(actual, expected, StringComparison.Ordinal)
            ? CheckResult.Pass
            : new CheckResult(InstallProblem.HashMismatch, $"{asset}: downloaded sha256 {actual}, expected {expected}");

    /// <summary>Check 3: the MSI's own Property table identifies <paramref name="product"/> at <paramref name="version"/>, per user.</summary>
    public static CheckResult Identity(ProductInfo product, ReleaseVersion version, MsiIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(identity);
        var code = ProductCatalog.NormalizeGuid(identity.UpgradeCode);
        if (code is null || !product.UpgradeCodes.Contains(code, StringComparer.OrdinalIgnoreCase))
            return new CheckResult(InstallProblem.WrongUpgradeCode,
                $"the MSI's UpgradeCode is '{identity.UpgradeCode}', not {product.Name}'s {string.Join("/", product.UpgradeCodes)}");
        if (ReleaseVersion.TryParseTag("v" + identity.ProductVersion) is not { } msiVersion || msiVersion != version)
            return new CheckResult(InstallProblem.WrongVersion, $"the MSI's ProductVersion is '{identity.ProductVersion}', the release is {version}");
        var allUsers = identity.AllUsers?.Trim() ?? "";
        var perUser = allUsers.Length == 0 || (allUsers == "2" && identity.InstallPerUser?.Trim() == "1");
        if (!perUser)
            return new CheckResult(InstallProblem.WrongScope,
                $"the MSI is not per-user (ALLUSERS='{identity.AllUsers}', MSIINSTALLPERUSER='{identity.InstallPerUser}')");
        return CheckResult.Pass;
    }

    private static string? Text(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Bool(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
}
