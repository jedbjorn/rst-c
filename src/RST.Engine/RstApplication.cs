// RstApplication.cs — Revit IExternalApplication entry point.
//
// Lifecycle:
//   OnStartup  — construct ribbon (branding panel + active profile), wire
//                handlers, kick off the scanner pre-warm.
//   OnShutdown — flush logs, dispose handlers, persist any pending state.

using System;
using System.IO;
using Autodesk.Revit.UI;
using Serilog;

namespace RST.Engine;

[Autodesk.Revit.Attributes.Regeneration(Autodesk.Revit.Attributes.RegenerationOption.Manual)]
[Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]
public sealed class RstApplication : IExternalApplication
{
    private const string TabName = "RST";

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            ConfigureLogging();
            Log.Information("RST {Version} starting on Revit {RevitVersion}",
                            ThisVersion, application.ControlledApplication.VersionNumber);

            BuildRstTab(application);
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "RST.OnStartup failed");
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        Log.Information("RST shutting down");
        Log.CloseAndFlush();
        return Result.Succeeded;
    }

    private static void BuildRstTab(UIControlledApplication app)
    {
        try { app.CreateRibbonTab(TabName); }
        catch (Autodesk.Revit.Exceptions.ArgumentException)
        {
            // Tab already exists (e.g. on add-in reload).
        }

        // Real ribbon assembly lands in RST-004 (Loader) and RST-005 (switcher).
        // For RST-002 we only prove the IExternalApplication wiring compiles
        // and gets called.
    }

    private static void ConfigureLogging()
    {
        var logsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RST", "logs");
        Directory.CreateDirectory(logsDir);

        var sessionLog = Path.Combine(
            logsDir,
            $"rst_{DateTime.Now:yyyy-MM-dd_HHmmss}.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.WithProperty("Component", "RST.Engine")
            .WriteTo.File(sessionLog,
                          rollingInterval: RollingInterval.Infinite,
                          retainedFileCountLimit: 20)
            .CreateLogger();
    }

    private static string ThisVersion =>
        typeof(RstApplication).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}
