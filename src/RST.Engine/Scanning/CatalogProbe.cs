// CatalogProbe.cs — verification scaffolding for RST-014.
// Builds the live CommandCatalog and dumps it to %AppData%\RST\catalog-output.json
// so the filter pipeline (ModeRestrictedCommandIds + BanList) can be verified
// against a real Revit session. Not shipped.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using RST.Core.Configuration;
using RST.Core.Scanning;

namespace RST.Engine.Scanning;

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class CatalogProbe : IExternalCommand
{
    public Result Execute(
        ExternalCommandData commandData,
        ref string message,
        Autodesk.Revit.DB.ElementSet elements)
    {
        try
        {
            var app = commandData.Application.Application;
            var catalog = CommandCatalog.Build(app.VersionNumber);

            // Audit the filter — these should all be absent.
            var leakedModeRestricted = catalog.Commands
                .Where(c => ModeRestrictedCommandIds.Contains(c.Id))
                .Select(c => c.Id)
                .ToList();

            var bans = BanList.Load(BanList.DefaultPath);
            var leakedBans = catalog.Commands
                .Where(c => bans.IsBanned(c.Id))
                .Select(c => c.Id)
                .ToList();

            var outDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RST");
            Directory.CreateDirectory(outDir);
            var outPath = Path.Combine(outDir, "catalog-output.json");

            var envelope = new CatalogEnvelope(
                CapturedAt: DateTime.UtcNow.ToString("O"),
                RevitVersion: app.VersionNumber,
                RevitBuild: app.VersionBuild,
                CatalogCount: catalog.Commands.Count,
                BanListCount: bans.Count,
                BanListPath: BanList.DefaultPath,
                LeakedModeRestricted: leakedModeRestricted,
                LeakedBans: leakedBans,
                Commands: catalog.Commands);

            var json = JsonSerializer.Serialize(
                envelope,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(outPath, json);

            TaskDialog.Show(
                "RST Catalog Probe",
                $"Catalog: {catalog.Commands.Count} commands.\n" +
                $"BanList: {bans.Count} entries (from {BanList.DefaultPath}).\n" +
                $"Leaks: mode-restricted={leakedModeRestricted.Count}, banned={leakedBans.Count}.\n" +
                $"Wrote: {outPath}");
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.ToString();
            return Result.Failed;
        }
    }

    private sealed record CatalogEnvelope(
        string CapturedAt,
        string RevitVersion,
        string RevitBuild,
        int CatalogCount,
        int BanListCount,
        string BanListPath,
        IReadOnlyList<string> LeakedModeRestricted,
        IReadOnlyList<string> LeakedBans,
        IReadOnlyList<ScannedCommand> Commands);
}
