// ScannerProbe.cs — verification scaffolding for catalog-API round-trip.
// Walks the live Revit ribbon (RibbonScanner only — no catalog merge or
// filter pipeline) and verifies that RevitCommandId.LookupCommandId(id)
// returns non-null for each Id. Useful when bumping Revit majors to
// confirm the Id-as-opaque assumption still holds. Not shipped.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;

namespace RST.Engine.Scanning;

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class ScannerProbe : IExternalCommand
{
    public Result Execute(
        ExternalCommandData commandData,
        ref string message,
        Autodesk.Revit.DB.ElementSet elements)
    {
        try
        {
            var scanned = RibbonScanner.Enumerate().ToList();

            var probed = scanned
                .Select(c => new ProbedCommand(
                    Command: c,
                    LookupOk: TryLookup(c.Id)))
                .ToList();

            var outDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RST");
            Directory.CreateDirectory(outDir);
            var outPath = Path.Combine(outDir, "scan-output.json");

            var app = commandData.Application.Application;
            var lookupOk = probed.Count(p => p.LookupOk);
            var envelope = new ScanEnvelope(
                CapturedAt: DateTime.UtcNow.ToString("O"),
                RevitVersion: app.VersionNumber,
                RevitBuild: app.VersionBuild,
                CommandCount: probed.Count,
                LookupOkCount: lookupOk,
                LookupFailCount: probed.Count - lookupOk,
                Commands: probed);

            var json = JsonSerializer.Serialize(
                envelope,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(outPath, json);

            TaskDialog.Show(
                "RST Scanner Probe",
                $"Enumerated {probed.Count} ribbon commands.\n" +
                $"LookupCommandId: {lookupOk} ok / {probed.Count - lookupOk} failed.\n" +
                $"Wrote: {outPath}");
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.ToString();
            return Result.Failed;
        }
    }

    private static bool TryLookup(string id)
    {
        try { return RevitCommandId.LookupCommandId(id) is not null; }
        catch { return false; }
    }

    private sealed record ProbedCommand(
        RST.Core.Scanning.ScannedCommand Command,
        bool LookupOk);

    private sealed record ScanEnvelope(
        string CapturedAt,
        string RevitVersion,
        string RevitBuild,
        int CommandCount,
        int LookupOkCount,
        int LookupFailCount,
        IReadOnlyList<ProbedCommand> Commands);
}
