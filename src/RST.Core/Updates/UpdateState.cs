// rst-c's own (not vendored): what the RST tray remembers between checks (spec #20 §4; F17 WU3),
// %AppData%\RST\tray_updates.json, beside tray_settings.json (user data; it survives an uninstall):
//
//   { "lastCheckedUtc": "2026-10-30T09:14:02+00:00",
//     "products": { "rst": { "latest": "v3.1.1", "notified": "v3.1.1", "noRelease": false } } }
//
//   lastCheckedUtc  the last SUCCESSFUL check (every product answered); a failed check never moves it
//   latest          the latest full release the last good check found, so the pages show it before the next check
//   notified        the newest version a notification was SHOWN for: each version is announced ONCE, not every day
//   noRelease       the last good check found no full release ("No release yet", not "Not checked yet")
//
// A missing or damaged file is an empty state (the worst case is one repeated notification). Written atomically
// (TrayAtomicFile). The rules that turn statuses into notifications are pure, for RST.Tests.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using RST.Core.Tray;

namespace RST.Core.Updates;

public sealed class UpdateState
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly Dictionary<string, ProductMemory> _products = new(StringComparer.Ordinal);

    /// <summary>When the last successful check finished (every product answered). A failed check leaves it alone.</summary>
    public DateTimeOffset? LastCheckedUtc { get; set; }

    /// <summary>The last good check found no full release for <paramref name="productId"/>.</summary>
    public bool NoRelease(string productId) =>
        _products.TryGetValue(productId, out var m) && m.NoRelease;

    public void SetNoRelease(string productId, bool noRelease) => Memory(productId).NoRelease = noRelease;

    /// <summary>The last known latest full release of <paramref name="productId"/>.</summary>
    public ReleaseVersion? Latest(string productId) =>
        _products.TryGetValue(productId, out var m) ? m.Latest : null;

    /// <summary>The newest version of <paramref name="productId"/> a notification was shown for.</summary>
    public ReleaseVersion? Notified(string productId) =>
        _products.TryGetValue(productId, out var m) ? m.Notified : null;

    public void SetLatest(string productId, ReleaseVersion? latest) => Memory(productId).Latest = latest;

    public void SetNotified(string productId, ReleaseVersion version) => Memory(productId).Notified = version;

    /// <summary>
    /// The products to announce now: an update is available, and no notification was shown yet for that
    /// version or a newer one. Manual installs and products that are not installed are never announced.
    /// </summary>
    public IReadOnlyList<ProductStatus> ToNotify(IEnumerable<ProductStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        return statuses
            .Where(s => s.State == ProductState.UpdateAvailable && s.Latest is { } latest
                        && !(Notified(s.Product.Id) is { } shown && shown >= latest))
            .ToList();
    }

    /// <summary>Reads <paramref name="path"/>; an empty state when it is missing or damaged (logged). Never throws.</summary>
    public static UpdateState Read(string path, Action<string>? log = null)
    {
        var state = new UpdateState();
        string text;
        try
        {
            text = TrayAtomicFile.ReadAllText(path);
        }
        catch (FileNotFoundException)
        {
            return state;
        }
        catch (DirectoryNotFoundException)
        {
            return state;
        }
        catch (Exception ex)
        {
            Log(log, "update state unreadable, starting empty: " + ex.Message);
            return state;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<StateDto>(text);
            if (dto is null) return state;
            state.LastCheckedUtc = dto.LastCheckedUtc;
            foreach (var (id, product) in dto.Products ?? new Dictionary<string, ProductDto>())
            {
                if (string.IsNullOrEmpty(id) || product is null) continue;
                var memory = state.Memory(id);
                memory.Latest = ReleaseVersion.TryParseTag(product.Latest);
                memory.Notified = ReleaseVersion.TryParseTag(product.Notified);
                memory.NoRelease = product.NoRelease == true && memory.Latest is null;
            }
        }
        catch (Exception ex)
        {
            Log(log, "update state damaged, starting empty: " + ex.Message);
            return new UpdateState();
        }
        return state;
    }

    /// <summary>Writes the state atomically. False (logged) when it could not be written.</summary>
    public bool Write(string path, Action<string>? log = null)
    {
        try
        {
            TrayAtomicFile.WriteAllText(path, ToJson(), log);
            return true;
        }
        catch (Exception ex)
        {
            Log(log, "update state not saved: " + ex.Message);
            return false;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(new StateDto
    {
        LastCheckedUtc = LastCheckedUtc,
        Products = _products.ToDictionary(
            p => p.Key,
            p => new ProductDto
            {
                Latest = p.Value.Latest?.Tag,
                Notified = p.Value.Notified?.Tag,
                NoRelease = p.Value.NoRelease ? true : null,
            },
            StringComparer.Ordinal),
    }, JsonOptions);

    private ProductMemory Memory(string productId)
    {
        ArgumentException.ThrowIfNullOrEmpty(productId);
        if (!_products.TryGetValue(productId, out var m)) _products[productId] = m = new ProductMemory();
        return m;
    }

    private static void Log(Action<string>? log, string message)
    {
        try { log?.Invoke(message); }
        catch { /* logging must never throw */ }
    }

    private sealed class ProductMemory
    {
        public ReleaseVersion? Latest { get; set; }

        public ReleaseVersion? Notified { get; set; }

        public bool NoRelease { get; set; }
    }

    private sealed class StateDto
    {
        [JsonPropertyName("lastCheckedUtc")]
        public DateTimeOffset? LastCheckedUtc { get; set; }

        [JsonPropertyName("products")]
        public Dictionary<string, ProductDto>? Products { get; set; }
    }

    private sealed class ProductDto
    {
        [JsonPropertyName("latest")]
        public string? Latest { get; set; }

        [JsonPropertyName("notified")]
        public string? Notified { get; set; }

        [JsonPropertyName("noRelease")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? NoRelease { get; set; }
    }
}

/// <summary>Everything the Updates and Apps pages show, at one moment. Immutable.</summary>
/// <param name="Products">RST first, then the companions.</param>
/// <param name="LastCheckedUtc">When the last SUCCESSFUL check finished (null: never). A failed check never moves it.</param>
/// <param name="Checking">A check is running.</param>
/// <param name="AutomaticChecks">Automatic checks are on (the setting and the preset).</param>
/// <param name="Detected">Installed products have been detected at least once (false: still looking).</param>
/// <param name="FailureReason">Why the last check failed (null: it did not), the raw detail for the log.</param>
/// <param name="Failure">The kind of that failure, for the page's plain "Couldn't check: …" (WU3 review D3).</param>
/// <param name="FailureStatus">Its HTTP status, when it is one.</param>
public sealed record UpdateSnapshot(
    IReadOnlyList<ProductStatus> Products,
    DateTimeOffset? LastCheckedUtc,
    bool Checking,
    bool AutomaticChecks,
    bool Detected,
    string? FailureReason = null,
    NetFailure Failure = NetFailure.Unexpected,
    int? FailureStatus = null)
{
    public ProductStatus? Rst => Products.FirstOrDefault(p => p.Product.IsRst);

    public IEnumerable<ProductStatus> Companions => Products.Where(p => !p.Product.IsRst);

    /// <summary>The last check failed for at least one product.</summary>
    public bool LastCheckFailed => FailureReason is not null || Products.Any(p => p.LatestFailed);

    /// <summary>Windows Installer could not be read for at least one product.</summary>
    public bool DetectionFailed => Products.Any(p => p.Installation.Kind == InstallKind.Unknown);

    /// <summary>A snapshot before anything is known: every product not installed, never checked.</summary>
    public static UpdateSnapshot Empty(ProductCatalog catalog, bool automaticChecks)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return new UpdateSnapshot(
            catalog.Products.Select(p => new ProductStatus(p, ProductInstallation.None, null, false)).ToList(),
            null, Checking: false, automaticChecks, Detected: false);
    }
}
