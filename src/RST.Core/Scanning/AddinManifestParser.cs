// AddinManifestParser.cs — read .addin XML files into AddinManifest records.
//
// Robust by design: a single malformed .addin must not abort a scan. Parse
// failures are surfaced via the optional onSkip callback and the file is
// dropped from the result set.

using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;

namespace RST.Core.Scanning;

public static class AddinManifestParser
{
    private const string DisabledSuffix = ".RSTdisabled";

    /// <summary>
    /// Recursively scan <paramref name="root"/> for *.addin and *.addin.RSTdisabled
    /// files. Returns one AddinManifest per file successfully parsed.
    /// </summary>
    public static IEnumerable<AddinManifest> ParseDirectory(
        string root,
        Action<string, Exception>? onSkip = null)
    {
        if (!Directory.Exists(root)) yield break;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*.addin*", SearchOption.AllDirectories);
        }
        catch (Exception ex)
        {
            onSkip?.Invoke(root, ex);
            yield break;
        }

        foreach (var path in files)
        {
            if (!IsAddinFile(path)) continue;
            AddinManifest? m = null;
            try { m = ParseFile(path); }
            catch (Exception ex) { onSkip?.Invoke(path, ex); }
            if (m is not null) yield return m;
        }
    }

    public static AddinManifest? ParseFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Parse(path, XDocument.Load(stream));
    }

    public static AddinManifest? Parse(string path, XDocument doc)
    {
        var root = doc.Root;
        if (root is null) return null;

        var entries = new List<AddinEntry>();
        foreach (var addin in root.Elements("AddIn"))
        {
            entries.Add(new AddinEntry(
                Type: (string?)addin.Attribute("Type") ?? "Application",
                AssemblyPath: TrimQuotes((string?)addin.Element("Assembly")),
                // Revit accepts either <AddInId> (legacy) or <ClientId>
                // (current manifests, incl. RST's own) for the add-in GUID.
                AddinId: ((string?)addin.Element("AddInId"))?.Trim()
                         ?? ((string?)addin.Element("ClientId"))?.Trim(),
                Name: ((string?)addin.Element("Name"))?.Trim(),
                VendorId: ((string?)addin.Element("VendorId"))?.Trim(),
                VendorDescription: ((string?)addin.Element("VendorDescription"))?.Trim()));
        }

        var fileName = Path.GetFileName(path);
        var isDisabled = fileName.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase);
        var canonical = isDisabled
            ? fileName.Substring(0, fileName.Length - DisabledSuffix.Length)
            : fileName;

        return new AddinManifest(path, canonical, isDisabled, entries);
    }

    private static bool IsAddinFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".addin", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".addin" + DisabledSuffix, StringComparison.OrdinalIgnoreCase);
    }

    private static string? TrimQuotes(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim().Trim('"', '\'');
        return t.Length == 0 ? null : t;
    }
}
