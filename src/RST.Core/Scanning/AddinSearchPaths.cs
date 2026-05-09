// AddinSearchPaths.cs — locations where Revit looks for .addin manifests.
//
// Order matters: the first .addin matching a given DLL "wins" for the
// scanner's tab→addin resolution. Mirrors the Python addin_scanner search
// order: user-machine-bundle-bundle-installroot.

using System;
using System.Collections.Generic;
using System.IO;

namespace RST.Core.Scanning;

public static class AddinSearchPaths
{
    /// <summary>
    /// Return the directory roots where .addin manifests may live for
    /// <paramref name="revitVersion"/> (e.g. "2025"). Filters out paths
    /// that don't exist. Order is significant for resolution priority.
    /// </summary>
    public static IReadOnlyList<string> ForVersion(string revitVersion)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var programFiles = Environment.GetEnvironmentVariable("PROGRAMFILES")
                          ?? @"C:\Program Files";

        var candidates = new[]
        {
            Path.Combine(appData,     "Autodesk", "Revit", "Addins", revitVersion),
            Path.Combine(programData, "Autodesk", "Revit", "Addins", revitVersion),
            Path.Combine(appData,     "Autodesk", "ApplicationPlugins"),
            Path.Combine(programData, "Autodesk", "ApplicationPlugins"),
            Path.Combine(programFiles, "Autodesk", "Revit " + revitVersion),
        };

        var existing = new List<string>();
        foreach (var p in candidates)
        {
            if (Directory.Exists(p)) existing.Add(p);
        }
        return existing;
    }
}
