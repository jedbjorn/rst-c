// rst-c's own (not vendored): the RST tray's download cache (spec #20 §5; F17 WU4), %LocalAppData%\RST\Tray\cache\:
//
//   cache\<product>\<version>\<asset>          a downloaded MSI (GanttIt-1.2.0.msi, RST.msi)
//   cache\<product>\<version>\verified.json    written only once all four checks passed: its sha256 and version
//   cache\<product>\state.json                 the last MSI the tray installed (kept for "Reinstall previous
//                                              version") and the last failed upgrade, if any
//
// After a successful install only that version's folder is kept: "the last installed MSI" of each product. RST.msi
// removes the cache on a real uninstall and keeps it through upgrades (WU1: RemoveFolderEx, NOT UPGRADINGPRODUCTCODE).
// Every read is fail-safe (a damaged file is "nothing recorded"); writes are atomic (TrayAtomicFile).

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using RST.Core.Tray;

namespace RST.Core.Updates;

/// <summary>An MSI that passed every check, as recorded beside it in <c>verified.json</c>.</summary>
public sealed record VerifiedRecord(
    [property: JsonPropertyName("product")] string Product,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("asset")] string Asset,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("verifiedUtc")] DateTimeOffset VerifiedUtc);

/// <summary>The last upgrade that failed, so the Apps page can offer "Reinstall previous version".</summary>
public sealed record FailedUpgrade(
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("exitCode")] int ExitCode,
    [property: JsonPropertyName("log")] string? Log,
    [property: JsonPropertyName("atUtc")] DateTimeOffset AtUtc);

/// <summary>What the cache remembers about one product.</summary>
public sealed record ProductCacheState(
    [property: JsonPropertyName("installed")] VerifiedRecord? Installed = null,
    [property: JsonPropertyName("failedUpgrade")] FailedUpgrade? FailedUpgrade = null);

public static class InstallCache
{
    public const string VerifiedFileName = "verified.json";
    public const string StateFileName = "state.json";

    private static readonly Regex IdPattern = new(@"^[a-z0-9]+(?:-[a-z0-9]+)*\z", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary><c>cache\&lt;product&gt;\</c>.</summary>
    public static string ProductDir(string cacheRoot, string productId)
    {
        ArgumentException.ThrowIfNullOrEmpty(cacheRoot);
        if (string.IsNullOrEmpty(productId) || !IdPattern.IsMatch(productId))
            throw new ArgumentException($"'{productId}' is not a product id.", nameof(productId));
        return Path.Combine(cacheRoot, productId);
    }

    /// <summary><c>cache\&lt;product&gt;\&lt;version&gt;\</c>.</summary>
    public static string VersionDir(string cacheRoot, string productId, ReleaseVersion version) =>
        Path.Combine(ProductDir(cacheRoot, productId), version.ToString());

    /// <summary>The lower-case hex SHA256 of a file.</summary>
    public static string Sha256Of(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>Writes <c>verified.json</c> beside <paramref name="msiPath"/>.</summary>
    public static void WriteVerified(string msiPath, VerifiedRecord record, Action<string>? log = null) =>
        TrayAtomicFile.WriteAllText(Path.Combine(Path.GetDirectoryName(msiPath)!, VerifiedFileName),
                                    JsonSerializer.Serialize(record, Json), log);

    /// <summary>
    /// The <c>verified.json</c> beside <paramref name="msiPath"/>, or null when it is missing, damaged, for another
    /// file, or the file's SHA256 is no longer the recorded one (it changed since it was checked).
    /// </summary>
    public static VerifiedRecord? ReadVerified(string msiPath, Action<string>? log = null)
    {
        try
        {
            if (!File.Exists(msiPath)) return null;
            var text = TrayAtomicFile.ReadAllText(Path.Combine(Path.GetDirectoryName(msiPath)!, VerifiedFileName));
            var record = JsonSerializer.Deserialize<VerifiedRecord>(text);
            if (record is null || !string.Equals(record.Asset, Path.GetFileName(msiPath), StringComparison.Ordinal)) return null;
            var actual = Sha256Of(msiPath);
            if (!string.Equals(actual, record.Sha256, StringComparison.Ordinal))
            {
                Log(log, $"cache: {msiPath} changed since it was verified (sha256 {actual}, recorded {record.Sha256})");
                return null;
            }
            return record;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Log(log, $"cache: no usable verification record for {msiPath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>The product's <c>state.json</c>; empty when missing or damaged. Never throws.</summary>
    public static ProductCacheState ReadState(string cacheRoot, string productId, Action<string>? log = null)
    {
        try
        {
            var text = TrayAtomicFile.ReadAllText(Path.Combine(ProductDir(cacheRoot, productId), StateFileName));
            return JsonSerializer.Deserialize<ProductCacheState>(text) ?? new ProductCacheState();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new ProductCacheState();
        }
        catch (Exception ex)
        {
            Log(log, $"cache: {productId} state unreadable, starting empty: {ex.Message}");
            return new ProductCacheState();
        }
    }

    /// <summary>Writes the product's <c>state.json</c>. False (logged) when it could not.</summary>
    public static bool WriteState(string cacheRoot, string productId, ProductCacheState state, Action<string>? log = null)
    {
        try
        {
            var dir = ProductDir(cacheRoot, productId);
            Directory.CreateDirectory(dir);
            TrayAtomicFile.WriteAllText(Path.Combine(dir, StateFileName), JsonSerializer.Serialize(state, Json), log);
            return true;
        }
        catch (Exception ex)
        {
            Log(log, $"cache: {productId} state not saved: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The MSI installed last, if its file and verification record are still there and intact: the "previous
    /// version" a failed upgrade can go back to without a download.
    /// </summary>
    public static string? InstalledMsi(string cacheRoot, string productId, ReleaseVersion version, Action<string>? log = null)
    {
        var state = ReadState(cacheRoot, productId, log);
        if (state.Installed is not { } installed || installed.Version != version.ToString()) return null;
        var path = Path.Combine(VersionDir(cacheRoot, productId, version), installed.Asset);
        var record = ReadVerified(path, log);
        return record is not null && record.Sha256 == installed.Sha256 ? path : null;
    }

    /// <summary>Records a successful install of <paramref name="record"/> and keeps only its version's folder.</summary>
    public static void RecordInstalled(string cacheRoot, VerifiedRecord record, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        WriteState(cacheRoot, record.Product, new ProductCacheState(record, FailedUpgrade: null), log);
        Prune(cacheRoot, record.Product, keep: record.Version, log);
    }

    /// <summary>Records a failed upgrade (the previous version is offered again); the last installed MSI is kept.</summary>
    public static void RecordFailedUpgrade(string cacheRoot, string productId, FailedUpgrade failed, Action<string>? log = null)
    {
        var state = ReadState(cacheRoot, productId, log);
        WriteState(cacheRoot, productId, state with { FailedUpgrade = failed }, log);
    }

    /// <summary>Forgets a failed upgrade (the product is installed again).</summary>
    public static void ClearFailedUpgrade(string cacheRoot, string productId, Action<string>? log = null)
    {
        var state = ReadState(cacheRoot, productId, log);
        if (state.FailedUpgrade is not null) WriteState(cacheRoot, productId, state with { FailedUpgrade = null }, log);
    }

    /// <summary>Removes every version folder of the product except <paramref name="keep"/> (and except the installed one's). Best effort.</summary>
    public static void Prune(string cacheRoot, string productId, string? keep, Action<string>? log = null)
    {
        var dir = ProductDir(cacheRoot, productId);
        if (!Directory.Exists(dir)) return;
        var installed = ReadState(cacheRoot, productId, log).Installed?.Version;
        foreach (var versionDir in SafeDirectories(dir))
        {
            var name = Path.GetFileName(versionDir);
            if (name == keep || name == installed) continue;
            try
            {
                Directory.Delete(versionDir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log(log, $"cache: could not remove {versionDir}: {ex.Message}");
            }
        }
    }

    private static IEnumerable<string> SafeDirectories(string dir)
    {
        try
        {
            return Directory.GetDirectories(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static void Log(Action<string>? log, string message)
    {
        try { log?.Invoke(message); }
        catch { /* logging must never throw */ }
    }
}
