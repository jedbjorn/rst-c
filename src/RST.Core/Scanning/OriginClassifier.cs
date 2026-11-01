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
        string? programFilesAutodesk = null,
        int? revitYear = null)
    {
        var hasPublisher = !string.IsNullOrWhiteSpace(publisher);

        if (hasPublisher && !ContainsAutodesk(publisher!))
            return CommandOrigin.ThirdParty;

        if (BuiltinTabs.Contains(tabName, revitYear))
            return CommandOrigin.Native;

        if (hasPublisher && ContainsAutodesk(publisher!))
            return CommandOrigin.Autodesk;

        if (IsAutodeskDll(assemblyPath, programFilesAutodesk))
            return CommandOrigin.Autodesk;

        return CommandOrigin.Custom;
    }

    /// <summary>The prefix Revit gives the ids of buttons an add-in creates through the Revit API.</summary>
    public const string AddinButtonIdPrefix = "CustomCtrl_";

    /// <summary>
    /// The origin of a button found by walking the live ribbon (RibbonScanner), which has no
    /// assembly or publisher to go on (spec #18, SC-007):
    ///   - an add-in's own button (id <c>CustomCtrl_…</c>) is <see cref="CommandOrigin.Custom"/>,
    ///     on any tab, so an add-in panel on a built-in tab still counts as an add-in;
    ///   - any other button on one of Revit's own tabs (<see cref="BuiltinTabs"/>) is Revit's:
    ///     <see cref="CommandOrigin.Native"/> (v3.0 left these Unknown, and the Builder then recorded
    ///     their panels, "Build", "Select", "Model", as required add-ins);
    ///   - anything else stays <see cref="CommandOrigin.Unknown"/>.
    /// </summary>
    public static CommandOrigin ClassifyRibbonButton(string? tabTitle, string? commandId, int? revitYear = null)
    {
        if (commandId is not null && commandId.StartsWith(AddinButtonIdPrefix, StringComparison.Ordinal))
            return CommandOrigin.Custom;
        if (BuiltinTabs.Contains(tabTitle, revitYear))
            return CommandOrigin.Native;
        return CommandOrigin.Unknown;
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
