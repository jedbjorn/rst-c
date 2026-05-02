// OriginClassifier.cs — assign a CommandOrigin to a scanned command.
//
// Port of the Python `classify_addin_origin` rules, minus the registry
// enrichment. Without a publisher signal, we rely on (a) tab membership for
// Native, (b) DLL path under Program Files\Autodesk for Autodesk, and
// (c) fall through to Custom. ThirdParty detection requires registry
// enrichment (deferred — RST-006/RST-007 territory).

using System;
using System.IO;

namespace RST.Core.Scanning;

public static class OriginClassifier
{
    /// <summary>
    /// Classify a command. <paramref name="publisher"/> is optional — when
    /// null, the registry-dependent rules degrade gracefully.
    /// </summary>
    /// <param name="programFilesAutodesk">
    /// Override for the "Autodesk install root" used by the DLL-path rule.
    /// Pass null to use the runtime <c>%PROGRAMFILES%\Autodesk</c>. Useful
    /// for tests on non-Windows hosts.
    /// </param>
    public static CommandOrigin Classify(
        string? tabName,
        string? assemblyPath,
        string? publisher = null,
        string? programFilesAutodesk = null)
    {
        var hasPublisher = !string.IsNullOrWhiteSpace(publisher);

        if (hasPublisher && !ContainsAutodesk(publisher!))
            return CommandOrigin.ThirdParty;

        if (BuiltinTabs.Contains(tabName))
            return CommandOrigin.Native;

        if (hasPublisher && ContainsAutodesk(publisher!))
            return CommandOrigin.Autodesk;

        if (IsAutodeskDll(assemblyPath, programFilesAutodesk))
            return CommandOrigin.Autodesk;

        return CommandOrigin.Custom;
    }

    private static bool ContainsAutodesk(string publisher) =>
        publisher.IndexOf("autodesk", StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool IsAutodeskDll(string? assemblyPath, string? programFilesAutodesk)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath)) return false;

        var root = programFilesAutodesk;
        if (string.IsNullOrEmpty(root))
        {
            var pf = Environment.GetEnvironmentVariable("PROGRAMFILES");
            if (string.IsNullOrEmpty(pf)) return false;
            root = Path.Combine(pf, "Autodesk");
        }

        var normPath = NormalizePath(assemblyPath!);
        var normRoot = NormalizePath(root!);
        return normPath.StartsWith(normRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path) =>
        path.Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);
}
