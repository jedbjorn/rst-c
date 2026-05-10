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
using Serilog;

namespace RST.Engine.Ribbon;

internal static class RibbonBuilder
{
    private const string RstPanelTitle = "RST";

    /// <summary>
    /// RST tools panel backdrop. Paired with the #d9d9d9 chip background
    /// baked into the brand icon PNGs (RST-043) so the four buttons stay
    /// visible against both Revit's light and dark themes.
    /// </summary>
    private const string RstPanelColor = "#c5d8d8";
    private const string LoaderClassName = "RST.Engine.Commands.LoaderCommand";
    private const string BuilderClassName = "RST.Engine.Commands.BuilderCommand";
    private const string RstifyClassName = "RST.Engine.Commands.RstifyCommand";
    private const string HealthClassName = "RST.Engine.Commands.HealthCommand";

    public static void Build(UIControlledApplication app)
    {
        // Register the panel title so the catalog scanner skips it —
        // otherwise our own three buttons would surface as catalog
        // entries on every Loader open. RstManagedTabs is too coarse on
        // a shared tab (Add-Ins hosts every addin's buttons), so we
        // filter at the panel-title level instead.
        RstManagedPanels.Add(RstPanelTitle);

        var assemblyPath = typeof(RibbonBuilder).Assembly.Location;

        // Single panel, four side-by-side Large buttons:
        // Builder | Loader | RSTify | Health. Lives on Revit's built-in
        // Add-Ins tab (single-arg CreateRibbonPanel overload). No custom
        // RST tab — RST tools share the External Tools area with every
        // other add-in.
        var rstPanel = app.CreateRibbonPanel(RstPanelTitle);

        var builderBtn = new PushButtonData(
            name: "RST_Builder",
            text: "Builder",
            assemblyName: assemblyPath,
            className: BuilderClassName)
        {
            ToolTip = "Edit profiles or create a new one.",
            // Image (small, 16x16) is what Quick Access Toolbar reads.
            // LargeImage (32x32) is what the panel renders. Shipping
            // hand-sized 16s avoids Revit's naive downscale of the 32.
            Image = IconAssets.BuilderIcon16 ?? IconAssets.Default16,
            LargeImage = IconAssets.BuilderIcon ?? IconAssets.Default32,
        };

        var loaderBtn = new PushButtonData(
            name: "RST_Loader",
            text: "Loader",
            assemblyName: assemblyPath,
            className: LoaderClassName)
        {
            ToolTip = "Open the RST profile selector.",
            Image = IconAssets.LoaderIcon16 ?? IconAssets.Default16,
            LargeImage = IconAssets.LoaderIcon ?? IconAssets.Default32,
        };

        // Name doubles as the cookie/id marker RstifyToggle.RefreshIcon
        // looks up at runtime to swap the on/off icon.
        var rstifyBtn = new PushButtonData(
            name: RstifyToggle.RstifyButtonCookie,
            text: "RSTify",
            assemblyName: assemblyPath,
            className: RstifyClassName)
        {
            ToolTip = "Toggle hide-rules from the active profile (hide configured tabs / show all).",
            Image = IconAssets.RstifyIconOff16 ?? IconAssets.Default16,
            LargeImage = IconAssets.RstifyIconOff ?? IconAssets.Default32,
        };

        var healthBtn = new PushButtonData(
            name: "RST_Health",
            text: "Health",
            assemblyName: assemblyPath,
            className: HealthClassName)
        {
            ToolTip = "System health snapshot, Revit context, and cache cleanup.",
            Image = IconAssets.HealthIcon16 ?? IconAssets.Default16,
            LargeImage = IconAssets.HealthIcon ?? IconAssets.Default32,
        };

        rstPanel.AddItem(builderBtn);
        rstPanel.AddItem(loaderBtn);
        rstPanel.AddItem(rstifyBtn);
        rstPanel.AddItem(healthBtn);
    }

    /// <summary>
    /// Apply the RST tools panel backdrop. Must be called after the ribbon
    /// is fully constructed (ApplicationInitialized timing) — at OnStartup
    /// AwComponentManager.Ribbon hasn't materialised the new panel yet.
    /// Lookup is title-only (cross-tab) because the host Add-Ins tab title
    /// is locale-dependent.
    /// </summary>
    public static void ApplyToolsPanelStyling()
    {
        var panel = PanelStyling.FindAwPanelInAnyTab(RstPanelTitle);
        if (panel is null)
        {
            Log.Debug("RibbonBuilder: RST tools panel not found at styling time — skipping ApplyColor");
            return;
        }
        PanelStyling.ApplyColor(panel, RstPanelColor, alpha: 1.0);
    }
}
