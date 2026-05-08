// RibbonBuilder.cs — OnStartup-only construction of the RST tab + Loader.
//
// The profile tab is built post-startup by ProfileTabBuilder
// (AdWindows-direct), so it can be torn down and rebuilt at runtime
// without a Revit restart (RST-020). RibbonBuilder owns only the parts
// that *can't* move to AdWindows: UIControlledApplication is the only
// API that wires a PushButtonData to an IExternalCommand class
// (LoaderCommand), and that wiring has to happen during OnStartup.
//
// The Loader button + RST tab are the user's permanent entry point and
// stay static across profile switches. RstManagedTabs.Add(RstTabName)
// is called so the catalog scanner skips this tab when enumerating
// commands.

using Autodesk.Revit.UI;

namespace RST.Engine.Ribbon;

internal static class RibbonBuilder
{
    private const string RstTabName = "RST";
    private const string LoaderClassName = "RST.Engine.Commands.LoaderCommand";
    private const string BuilderClassName = "RST.Engine.Commands.BuilderCommand";

    public static void Build(UIControlledApplication app)
    {
        try { app.CreateRibbonTab(RstTabName); }
        catch (Autodesk.Revit.Exceptions.ArgumentException) { /* tab exists — addin reload */ }

        // Always register the RST tab so the catalog scanner doesn't
        // surface our own buttons (or any future RST-tab panels) as
        // profile-buildable commands.
        RstManagedTabs.Add(RstTabName);

        var assemblyPath = typeof(RibbonBuilder).Assembly.Location;

        // Single panel, two side-by-side Large buttons: Loader + Builder.
        // Order matches upstream pyRevit RST.
        var rstPanel = app.CreateRibbonPanel(RstTabName, "RST");

        var loaderBtn = new PushButtonData(
            name: "RST_Loader",
            text: "Loader",
            assemblyName: assemblyPath,
            className: LoaderClassName)
        {
            ToolTip = "Open the RST profile selector.",
            Image = IconAssets.LoaderIcon ?? IconAssets.Default32,
            LargeImage = IconAssets.LoaderIcon ?? IconAssets.Default32,
        };

        var builderBtn = new PushButtonData(
            name: "RST_Builder",
            text: "Builder",
            assemblyName: assemblyPath,
            className: BuilderClassName)
        {
            ToolTip = "Edit profiles or create a new one.",
            Image = IconAssets.BuilderIcon ?? IconAssets.Default32,
            LargeImage = IconAssets.BuilderIcon ?? IconAssets.Default32,
        };

        rstPanel.AddItem(loaderBtn);
        rstPanel.AddItem(builderBtn);
    }
}
