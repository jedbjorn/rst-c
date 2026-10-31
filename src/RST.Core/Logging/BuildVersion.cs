// rst-c's own (not vendored): the build's version as the startup log lines print it (SC-054, SC-015).
//
// Directory.Build.props stamps every RST assembly from one Version: FileVersion a.b.c.0 and
// InformationalVersion a.b.c+<short commit> (0.0.0-dev+<commit> in a dev or PR build).
// AssemblyVersion stays 0.1.0.0 for the bootstrap's resolver, so it never says which build this
// is: the startup lines read the informational version instead.

using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace RST.Core.Logging;

public static class BuildVersion
{
    /// <summary>
    /// <paramref name="assembly"/>'s informational version (<c>3.2.0+abc1234</c>); its assembly version when it has
    /// none; <c>0.0.0</c> when neither can be read. Never throws.
    /// </summary>
    public static string Informational(Assembly assembly)
    {
        try
        {
            var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info)) return info.Trim();
            return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }
        catch
        {
            return "0.0.0";
        }
    }

    /// <summary>The product version: the informational version without its <c>+commit</c> (<c>3.2.0</c>).</summary>
    public static string Product(string informational)
    {
        if (string.IsNullOrWhiteSpace(informational)) return "0.0.0";
        var plus = informational.IndexOf('+');
        return plus >= 0 ? informational[..plus] : informational;
    }

    /// <summary>The runtime, as .NET names itself (<c>.NET 8.0.11</c>).</summary>
    public static string Runtime()
    {
        try
        {
            return RuntimeInformation.FrameworkDescription;
        }
        catch
        {
            return ".NET " + Environment.Version;
        }
    }

    /// <summary>
    /// The engine's session log header: <c>RST 3.2.0 (3.2.0+abc1234) starting in Revit 2026.4 (26.4.10.32), .NET 8.0.11</c>.
    /// <paramref name="revitSubVersion"/> (<c>Application.SubVersionNumber</c>) names the update and falls back to
    /// <paramref name="revitVersion"/> (the year); the build is left out when Revit gives none.
    /// </summary>
    public static string EngineHeader(string informational, string revitVersion, string? revitSubVersion, string? revitBuild, string runtime)
    {
        var revit = string.IsNullOrWhiteSpace(revitSubVersion) ? revitVersion : revitSubVersion!.Trim();
        var build = string.IsNullOrWhiteSpace(revitBuild) ? "" : $" ({revitBuild!.Trim()})";
        return $"RST {Product(informational)} ({informational}) starting in Revit {revit}{build}, {runtime}";
    }

    /// <summary>The engine's session log header for <paramref name="assembly"/>, read from its informational version.</summary>
    public static string EngineHeader(Assembly assembly, string revitVersion, string? revitSubVersion, string? revitBuild) =>
        EngineHeader(Informational(assembly), revitVersion, revitSubVersion, revitBuild, Runtime());
}
