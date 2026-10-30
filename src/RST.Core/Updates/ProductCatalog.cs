// rst-c's own (not vendored): the products the RST tray knows (spec #20 §4, §6; F17 WU3): RST itself, then each
// shippable companion from companions/companions.json, which RST.Core embeds AT BUILD TIME
// (RST.Core.csproj, LogicalName RST.Core.Updates.companions.json). Only the identity fields are read: id, name,
// description, repo, release.upgradeCode and addinClientId. The bundle's pins (tag, asset, sha256, version) stay
// build-time only: the bundle is the offline baseline, and the tray follows each repo's latest full release.
//
// RST's own identity is fixed here: UpgradeCode AF0331FC-5280-492A-94BD-95EDF108BE74 (RST.msi, installer/Product.wxs).
// WU5 adds RST-Machine.msi's code to the same entry.
//
// A companion that is not shippable, or whose identity fields are missing or malformed, is left out (logged): the
// tray must never look up, or later install, something it cannot identify. Nothing here throws.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RST.Core.Updates;

/// <summary>One product the tray detects and checks.</summary>
/// <param name="Id">Stable id: <c>rst</c>, or the companion's id (<c>gantt-it</c>).</param>
/// <param name="Name">Display name: RST, Gantt-It, Splash-It.</param>
/// <param name="Description">One line for the Apps page.</param>
/// <param name="Repo">GitHub <c>owner/name</c>; the feed and downloads are built from it.</param>
/// <param name="UpgradeCodes">The Windows Installer UpgradeCodes that identify it (upper-case, braces stripped).</param>
/// <param name="AddinClientId">The ClientId in its .addin (lower-case), for the "installed manually" signal; null for RST.</param>
public sealed record ProductInfo(
    string Id,
    string Name,
    string Description,
    string Repo,
    IReadOnlyList<string> UpgradeCodes,
    string? AddinClientId)
{
    public bool IsRst => string.Equals(Id, ProductCatalog.RstId, StringComparison.Ordinal);
}

public sealed class ProductCatalog
{
    public const string RstId = "rst";

    /// <summary>RST.msi's UpgradeCode (installer/Product.wxs).</summary>
    public const string RstUpgradeCode = "AF0331FC-5280-492A-94BD-95EDF108BE74";

    public const string RstRepo = "jedbjorn/rst-c";

    /// <summary>The embedded companions.json's resource name.</summary>
    public const string CompanionsResource = "RST.Core.Updates.companions.json";

    private static readonly Regex IdPattern = new(@"^[a-z0-9]+(?:-[a-z0-9]+)*\z", RegexOptions.CultureInvariant);

    private static readonly Lazy<ProductCatalog> Embedded = new(() => FromEmbedded(log: null));

    public ProductCatalog(IReadOnlyList<ProductInfo> products) =>
        Products = products ?? throw new ArgumentNullException(nameof(products));

    /// <summary>RST first, then the companions in companions.json order.</summary>
    public IReadOnlyList<ProductInfo> Products { get; }

    public ProductInfo Rst => Products.First(p => p.IsRst);

    public IEnumerable<ProductInfo> Companions => Products.Where(p => !p.IsRst);

    public ProductInfo? Find(string id) => Products.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));

    /// <summary>The catalog built from the companions.json embedded in this build.</summary>
    public static ProductCatalog Default => Embedded.Value;

    /// <summary>RST's own entry.</summary>
    public static ProductInfo RstProduct { get; } = new(
        RstId, "RST", "Ribbon profiles for Revit", RstRepo, new[] { RstUpgradeCode }, AddinClientId: null);

    /// <summary>Reads the embedded companions.json (RST alone if it is missing or unreadable).</summary>
    public static ProductCatalog FromEmbedded(Action<string>? log)
    {
        try
        {
            using var stream = typeof(ProductCatalog).Assembly.GetManifestResourceStream(CompanionsResource);
            if (stream is null)
            {
                Log(log, "product catalog: companions.json is not embedded; only RST is known");
                return new ProductCatalog(new[] { RstProduct });
            }
            using var reader = new StreamReader(stream);
            return Parse(reader.ReadToEnd(), log);
        }
        catch (Exception ex)
        {
            Log(log, "product catalog: companions.json unreadable (" + ex.Message + "); only RST is known");
            return new ProductCatalog(new[] { RstProduct });
        }
    }

    /// <summary>RST plus each shippable, well-identified companion in <paramref name="companionsJson"/>.</summary>
    public static ProductCatalog Parse(string companionsJson, Action<string>? log = null)
    {
        var products = new List<ProductInfo> { RstProduct };
        try
        {
            using var doc = JsonDocument.Parse(companionsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("companions", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                Log(log, "product catalog: companions.json has no companions list");
                return new ProductCatalog(products);
            }
            foreach (var entry in list.EnumerateArray())
            {
                var companion = ReadCompanion(entry, log);
                if (companion is null) continue;
                if (products.Any(p => p.Id == companion.Id || p.UpgradeCodes.Intersect(companion.UpgradeCodes).Any()
                                      || (p.AddinClientId is not null && p.AddinClientId == companion.AddinClientId)))
                {
                    Log(log, $"product catalog: {companion.Id} repeats another product's identity; left out");
                    continue;
                }
                products.Add(companion);
            }
        }
        catch (JsonException ex)
        {
            Log(log, "product catalog: companions.json is not valid JSON (" + ex.Message + "); only RST is known");
            return new ProductCatalog(new[] { RstProduct });
        }
        catch (Exception ex)
        {
            Log(log, "product catalog: companions.json unreadable (" + ex.Message + "); only RST is known");
            return new ProductCatalog(new[] { RstProduct });
        }
        return new ProductCatalog(products);
    }

    /// <summary>An upper-case GUID without braces, or null.</summary>
    public static string? NormalizeGuid(string? value) =>
        Guid.TryParseExact(value?.Trim().Trim('{', '}') ?? "", "D", out var guid) ? guid.ToString("D").ToUpperInvariant() : null;

    private static ProductInfo? ReadCompanion(JsonElement entry, Action<string>? log)
    {
        if (entry.ValueKind != JsonValueKind.Object) return null;
        var id = Text(entry, "id");
        if (id is null || !IdPattern.IsMatch(id))
        {
            Log(log, "product catalog: a companion without a valid id; left out");
            return null;
        }
        if (Text(entry, "status") != "shippable") return null; // in_development / retired: not offered.

        var name = Text(entry, "name");
        var repo = Text(entry, "repo");
        var upgradeCode = entry.TryGetProperty("release", out var release) && release.ValueKind == JsonValueKind.Object
            ? NormalizeGuid(Text(release, "upgradeCode"))
            : null;
        var clientId = NormalizeGuid(Text(entry, "addinClientId"))?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(name) || !ReleaseFeed.IsRepo(repo) || upgradeCode is null || clientId is null)
        {
            Log(log, $"product catalog: {id} lacks a name, repo, UpgradeCode or ClientId; left out");
            return null;
        }
        if (upgradeCode == RstUpgradeCode)
        {
            Log(log, $"product catalog: {id} claims RST's UpgradeCode; left out");
            return null;
        }
        return new ProductInfo(id, name!.Trim(), Text(entry, "description")?.Trim() ?? "", repo!, new[] { upgradeCode }, clientId);
    }

    private static string? Text(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void Log(Action<string>? log, string message)
    {
        try { log?.Invoke(message); }
        catch { /* logging must never throw */ }
    }
}
