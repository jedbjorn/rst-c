// rst-c's own (not vendored): a .bundle's PackageContents.xml, which is how Revit decides which of
// a bundle's manifests load in which year (fixes SC-006: v3.0 treated every Contents\<year>\X.addin
// copy as a manifest of the running year). Shape (Autodesk's ApplicationPlugins format):
//   <ApplicationPackage …>
//     <Components Description="…">
//       <RuntimeRequirements OS="Win64" Platform="Revit" SeriesMin="R2026" SeriesMax="R2027" />
//       <ComponentEntry AppName="…" ModuleName="./Contents/2026/X.addin" />
//     </Components>
//   </ApplicationPackage>
// A Components element's RuntimeRequirements apply to its entries; a missing SeriesMin or
// SeriesMax leaves that end open. Only .addin modules matter here.

using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RST.Core.Activation;

/// <summary>One manifest a bundle declares, with the Revit years it is for.</summary>
public sealed record BundleComponent(string ManifestPath, int? SeriesMin, int? SeriesMax)
{
    public bool AppliesTo(int year) => (SeriesMin is null || year >= SeriesMin) && (SeriesMax is null || year <= SeriesMax);

    /// <summary>"2026–2027", "2026+", or "any year".</summary>
    public string Range => (SeriesMin, SeriesMax) switch
    {
        (null, null) => "any year",
        ({ } a, null) => a + "+",
        (null, { } b) => "up to " + b,
        ({ } a, { } b) when a == b => a.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ({ } a, { } b) => a + "–" + b,
    };
}

public static partial class PackageContents
{
    public const string FileName = "PackageContents.xml";

    /// <summary>The .addin components of the bundle at <paramref name="bundleDir"/>.</summary>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="System.Xml.XmlException">The file is not XML.</exception>
    public static IReadOnlyList<BundleComponent> Read(string bundleDir)
    {
        var doc = XDocument.Load(Path.Combine(bundleDir, FileName));
        return Parse(bundleDir, doc);
    }

    public static IReadOnlyList<BundleComponent> Parse(string bundleDir, XDocument doc)
    {
        var result = new List<BundleComponent>();
        var root = doc.Root;
        if (root is null) return result;
        foreach (var components in root.Elements().Where(e => e.Name.LocalName == "Components"))
        {
            var req = components.Elements().FirstOrDefault(e => e.Name.LocalName == "RuntimeRequirements");
            if (req is not null && !IsRevit((string?)req.Attribute("Platform"))) continue;
            var min = Series((string?)req?.Attribute("SeriesMin"));
            var max = Series((string?)req?.Attribute("SeriesMax"));
            foreach (var entry in components.Elements().Where(e => e.Name.LocalName == "ComponentEntry"))
            {
                var module = ((string?)entry.Attribute("ModuleName"))?.Trim();
                if (string.IsNullOrEmpty(module) || !module.EndsWith(".addin", StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(new BundleComponent(Resolve(bundleDir, module), min, max));
            }
        }
        return result;
    }

    private static bool IsRevit(string? platform) =>
        string.IsNullOrWhiteSpace(platform) || platform.Contains("Revit", StringComparison.OrdinalIgnoreCase);

    /// <summary>"R2026" or "R2026.1" → 2026.</summary>
    internal static int? Series(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var m = YearPattern().Match(value);
        return m.Success ? int.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    private static string Resolve(string bundleDir, string module)
    {
        var relative = module.Replace('\\', '/').TrimStart('.').TrimStart('/');
        var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return Path.GetFullPath(Path.Combine([bundleDir, .. parts]));
    }

    [GeneratedRegex(@"\d{4}")]
    private static partial Regex YearPattern();
}
