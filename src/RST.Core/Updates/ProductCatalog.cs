// rst-c's own (not vendored): the products the RST tray knows (spec #20 §4, §6; F17 WU3): RST itself, then each
// shippable companion from companions/companions.json, which RST.Core embeds AT BUILD TIME
// (RST.Core.csproj, LogicalName RST.Core.Updates.companions.json). Only the identity fields are read: id, name,
// description, repo, release.upgradeCode and addinClientId, plus (F17 WU4) the FORM of the pinned asset's name
// (GanttIt-1.1.0.msi gives GanttIt-{0}.msi), which must also match this file's hard-coded pattern for that id. The
// pins themselves (tag, sha256, version) stay build-time only: the bundle is the offline baseline, and the tray
// follows each repo's latest full release.
//
// RST's own identity is fixed here: UpgradeCode AF0331FC-5280-492A-94BD-95EDF108BE74 (RST.msi, per user) and
// 79C25959-C28A-4ED8-8984-85ED525476C3 (RST-Machine.msi, per machine; F17 WU5), both in installer/Product.wxs. One
// entry with both codes: Windows Installer reports the context, so the tray tells the two apart.
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
/// <param name="AssetTemplate">
/// Its MSI's release-asset name with <c>{0}</c> for the a.b.c version (<c>GanttIt-{0}.msi</c>; RST: <c>RST.msi</c>), from
/// the build-time catalog (F17 WU4). Null: the tray offers no Install or Update for it.
/// </param>
/// <param name="AssetPattern">
/// The hard-coded pattern that name must match (spec #20 §5; <see cref="ProductCatalog.AssetPatterns"/>). Null: not installable.
/// </param>
public sealed record ProductInfo(
    string Id,
    string Name,
    string Description,
    string Repo,
    IReadOnlyList<string> UpgradeCodes,
    string? AddinClientId,
    string? AssetTemplate = null,
    string? AssetPattern = null)
{
    public bool IsRst => string.Equals(Id, ProductCatalog.RstId, StringComparison.Ordinal);

    /// <summary>The tray can install it: it has an asset name and a pattern to check that name against.</summary>
    public bool IsInstallable => AssetTemplate is not null && AssetPattern is not null;
}

public sealed class ProductCatalog
{
    public const string RstId = "rst";

    /// <summary>RST.msi's UpgradeCode (installer/Product.wxs).</summary>
    public const string RstUpgradeCode = "AF0331FC-5280-492A-94BD-95EDF108BE74";

    /// <summary>RST-Machine.msi's UpgradeCode (installer/Product.wxs, Scope=machine; spec #20 §7).</summary>
    public const string RstMachineUpgradeCode = "79C25959-C28A-4ED8-8984-85ED525476C3";

    public const string RstRepo = "jedbjorn/rst-c";

    /// <summary>RST.msi's release-asset name (release.yml attaches it under this exact name).</summary>
    public const string RstAsset = "RST.msi";

    /// <summary>
    /// The setup bundle's release-asset name (decision #34: an RST that RST-Setup.exe installed is updated by the
    /// new release's RST-Setup.exe, so Add/Remove Programs keeps its one entry and the bundle's uninstall keeps
    /// working).
    /// </summary>
    public const string RstBundleAsset = "RST-Setup.exe";

    /// <summary>The hard-coded pattern the bundle's asset name must match (exactly <c>RST-Setup.exe</c>).</summary>
    public const string RstBundlePattern = @"^RST-Setup\.exe\z";

    /// <summary>RST-Setup.exe's bundle UpgradeCode (installer-bundle/Bundle.wxs): how the tray recognises a bundle install.</summary>
    public const string RstBundleUpgradeCode = "2BE2BE5D-9203-4344-A70D-E735D3B041A3";

    /// <summary>
    /// RST as RST.msi updates it: the PER-USER UpgradeCode only. Self-update never accepts another RST
    /// UpgradeCode (the per-machine RST-Machine.msi's, from WU5), whatever RstProduct lists for detection.
    /// </summary>
    public static ProductInfo RstPerUserUpdate => RstProduct with { UpgradeCodes = new[] { RstUpgradeCode } };

    /// <summary>
    /// The asset-name patterns, hard-coded per product id (spec #20 §5: ^RST\.msi$, ^GanttIt-\d+\.\d+\.\d+\.msi$,
    /// ^SplashIt-\d+\.\d+\.\d+\.msi$, written with ASCII [0-9] and \z as in ReleaseVersion). A companion's asset name,
    /// built from its pinned asset in companions.json, must match its pattern here or it is never installed.
    /// </summary>
    public static IReadOnlyDictionary<string, string> AssetPatterns { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [RstId] = @"^RST\.msi\z",
        ["gantt-it"] = @"^GanttIt-[0-9]{1,9}\.[0-9]{1,9}\.[0-9]{1,9}\.msi\z",
        ["splash-it"] = @"^SplashIt-[0-9]{1,9}\.[0-9]{1,9}\.[0-9]{1,9}\.msi\z",
    };

    private static readonly Regex AssetStemPattern = new(@"^[A-Za-z][A-Za-z0-9]*(?:-[A-Za-z][A-Za-z0-9]*)*\z",
        RegexOptions.CultureInvariant);

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
        RstId, "RST", "Ribbon profiles for Revit", RstRepo, new[] { RstUpgradeCode, RstMachineUpgradeCode }, AddinClientId: null,
        AssetTemplate: RstAsset, AssetPattern: AssetPatterns[RstId]);

    /// <summary>
    /// The asset template from a pinned asset name and its version: <c>GanttIt-1.1.0.msi</c> with <c>1.1.0</c> gives
    /// <c>GanttIt-{0}.msi</c>. Null when the name is not <c>&lt;Stem&gt;-&lt;version&gt;.msi</c>.
    /// </summary>
    public static string? AssetTemplateFrom(string? pinnedAsset, string? pinnedVersion)
    {
        if (string.IsNullOrEmpty(pinnedAsset) || ReleaseVersion.TryParseTag("v" + pinnedVersion) is not { } version) return null;
        var suffix = "-" + version + ".msi";
        if (!pinnedAsset.EndsWith(suffix, StringComparison.Ordinal)) return null;
        var stem = pinnedAsset[..^suffix.Length];
        return AssetStemPattern.IsMatch(stem) ? stem + "-{0}.msi" : null;
    }

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
                if (!companion.IsInstallable)
                    Log(log, $"product catalog: {companion.Id} has no known asset name; it is detected and checked, never installed");
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
        if (upgradeCode is RstUpgradeCode or RstMachineUpgradeCode)
        {
            Log(log, $"product catalog: {id} claims RST's UpgradeCode; left out");
            return null;
        }
        // The install identity (F17 WU4): the asset name the pin uses, checked at install time against the
        // hard-coded pattern for this id. Without both, the companion is detected and checked, never installed.
        var template = AssetTemplateFrom(Text(release, "asset"), Text(release, "version"));
        AssetPatterns.TryGetValue(id, out var pattern);
        return new ProductInfo(id, name!.Trim(), Text(entry, "description")?.Trim() ?? "", repo!, new[] { upgradeCode }, clientId,
                               template, pattern);
    }

    private static string? Text(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void Log(Action<string>? log, string message)
    {
        try { log?.Invoke(message); }
        catch { /* logging must never throw */ }
    }
}
