// PanelStyling.cs — paint background colors and the leftmost branding
// panel onto Autodesk.Windows.RibbonPanel instances.
//
// Mirrors the visible behavior of pyRevit's startup.py port at
// /home/jedi/RST/startup.py (_make_brush, _build_ribbon branding-panel
// section). Two halves:
//
//   ColorBrush(hex, alpha, width, height) — returns a DrawingBrush that
//     paints a rounded rectangle. Falls back to SolidColorBrush if the
//     rounded path throws (e.g. unparseable hex).
//
//   ApplyColor(panel, hex, alpha) — sets the panel's
//     CustomPanelBackground + CustomPanelTitleBarBackground.
//
// Note on corner radius: AdWindows.RibbonPanel inherits from
// System.Object (verified via metadata-reader probe of 2025.4.41 +
// 2026.4.0), so it has no SizeChanged event and no ActualWidth/Height.
// pyRevit's startup.py wraps a SizeChanged subscription in try/except
// — IronPython's late binding swallows the AttributeError silently,
// which means pyRevit's runtime radii are also just the initial relative
// ratios. We do better here: estimate panel dimensions from item count
// (each Large item ≈ 96px wide) and emit two brushes (body + title bar)
// with absolute-pixel-targeted relative radii. Result: ~5px corners on
// every panel regardless of item count, matching pyRevit's intended
// PANEL_CORNER_RADIUS_PX = 5 — which pyRevit silently fails to apply.
//
// Branding panel construction lives in BuildBrandingPanel() — square
// 85×85 logo with rounded corners, no button (a zero-content RibbonLabel
// holds the layout slot AdWindows requires). Title strip is hidden;
// Source.Name carries the active profile name for diagnostics. Caller
// (ProfileTabBuilder) inserts at index 0 of the profile tab.
//
// Thread model: every method must be called on the UI thread (Revit's
// ribbon and WPF have the usual STA constraint). Initial color
// application happens in OnStartup, which is UI-thread.

using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AwComponentManager = Autodesk.Windows.ComponentManager;
using AwRibbonTab = Autodesk.Windows.RibbonTab;
using AwRibbonPanel = Autodesk.Windows.RibbonPanel;
using AwRibbonPanelSource = Autodesk.Windows.RibbonPanelSource;
using AwRibbonLabel = Autodesk.Windows.RibbonLabel;
using RST.Core.Configuration;
using Serilog;

namespace RST.Engine.Ribbon;

internal static class PanelStyling
{
    /// <summary>Target absolute corner radius in pixels (matches pyRevit's PANEL_CORNER_RADIUS_PX).</summary>
    private const double TargetRadiusPx = 5.0;

    /// <summary>
    /// Apply a colored, rounded background to <paramref name="panel"/>.
    /// Body and title-bar each get their own DrawingBrush so both render
    /// with ~5px corners regardless of item count or title-bar height
    /// (a single shared brush would give the title bar squashed Y-radii).
    /// </summary>
    public static void ApplyColor(AwRibbonPanel panel, string hexColor, double alpha)
    {
        if (panel is null) return;
        var clamped = Math.Max(0.0, Math.Min(1.0, alpha));
        try
        {
            // Estimate rendered panel width from item count — each Large
            // item is ~96px wide. AdWindows doesn't expose ActualWidth,
            // so this is the closest we can get without a visual-tree walk.
            int itemCount = panel.Source?.Items.Count ?? 1;
            double estW = Math.Max(96.0, itemCount * 96.0 + 8.0);
            const double bodyH = 96.0;
            const double titleH = 18.0;

            var bodyBrush = ColorBrush(hexColor, clamped, estW, bodyH);
            var titleBrush = ColorBrush(hexColor, clamped, estW, titleH);
            if (bodyBrush is not null) panel.CustomPanelBackground = bodyBrush;
            if (titleBrush is not null) panel.CustomPanelTitleBarBackground = titleBrush;

            Log.Debug("PanelStyling.ApplyColor: hex={Hex} alpha={Alpha} items={Items} estW={EstW}",
                      hexColor, clamped, itemCount, estW);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "PanelStyling.ApplyColor failed for hex={Hex}, alpha={Alpha}", hexColor, clamped);
        }
    }

    /// <summary>
    /// Find the Autodesk.Windows.RibbonTab whose Title matches
    /// <paramref name="tabTitle"/>. Returns null if ComponentManager.Ribbon
    /// is not yet ready (early in OnStartup) or no tab matches.
    /// </summary>
    public static AwRibbonTab? FindAwTab(string tabTitle)
    {
        var ribbon = AwComponentManager.Ribbon;
        if (ribbon is null) return null;
        foreach (var tab in ribbon.Tabs)
        {
            if (tab is null) continue;
            if (string.Equals(tab.Title, tabTitle, StringComparison.Ordinal)) return tab;
        }
        return null;
    }

    /// <summary>
    /// Find the Autodesk.Windows.RibbonPanel created (under the named
    /// tab) by a previous app.CreateRibbonPanel call. Matches on
    /// Source.Title because Revit's RibbonPanel wrapper does not expose
    /// the underlying AdWindows panel.
    /// </summary>
    public static AwRibbonPanel? FindAwPanel(string tabTitle, string panelTitle)
    {
        var tab = FindAwTab(tabTitle);
        if (tab is null) return null;
        foreach (var panel in tab.Panels)
        {
            if (panel?.Source is null) continue;
            if (string.Equals(panel.Source.Title, panelTitle, StringComparison.Ordinal))
                return panel;
        }
        return null;
    }

    /// <summary>
    /// Build the leftmost branding panel — square logo with rounded
    /// corners and no button. Title strip is suppressed visually (transparent
    /// brush + empty Title) but the source's Name carries the active profile
    /// name for diagnostics. Sizing is forced via a single zero-content
    /// RibbonLabel of Width/Height = 85 (AdWindows panels collapse without
    /// at least one item).
    /// Returns null if no logo is available — caller skips the insert.
    /// </summary>
    public static AwRibbonPanel? BuildBrandingPanel(string? logoAbsolutePath, string? profileName)
    {
        if (string.IsNullOrEmpty(logoAbsolutePath) || !File.Exists(logoAbsolutePath))
        {
            return null;
        }

        try
        {
            var panel = new AwRibbonPanel();
            var source = new AwRibbonPanelSource
            {
                Title = "",
                Name = profileName ?? "",
                Id = "REST_Branding",
            };
            panel.Source = source;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(Path.GetFullPath(logoAbsolutePath), UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            // The branding logo is user-mutable — Bridge.PickLogoFile rewrites
            // the same %AppData%\RST\branding.png on every pick. WPF caches
            // BitmapImage by URI process-wide; without IgnoreImageCache the
            // cached old bitmap survives every profile rebuild for the rest
            // of the Revit session, even though a fresh BitmapImage instance
            // is constructed each time. Bypass the cache so re-picks land.
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.EndInit();
            try { bmp.Freeze(); }
            catch (Exception ex) { Log.Debug(ex, "PanelStyling: BitmapImage.Freeze failed for logo={Logo}", logoAbsolutePath); }

            // Rounded-corner image brush: GeometryDrawing fills a rounded
            // rectangle with the bitmap. Radii are computed against the
            // square panel size so corner radius lands at TargetRadiusPx.
            double rx = TargetRadiusPx / (double)BrandingDefaults.PanelSizePx;
            double ry = rx;
            var rect = new RectangleGeometry(new Rect(0, 0, 1, 1)) { RadiusX = rx, RadiusY = ry };
            var imgBrush = new ImageBrush(bmp) { Stretch = Stretch.Fill };
            try { imgBrush.Freeze(); } catch { /* best-effort */ }
            var drawing = new GeometryDrawing(imgBrush, pen: null, geometry: rect);
            var bgBrush = new DrawingBrush(drawing)
            {
                Stretch = Stretch.Fill,
                ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
                TileMode = TileMode.None,
            };
            try { bgBrush.Freeze(); }
            catch (Exception ex) { Log.Debug(ex, "PanelStyling: branding DrawingBrush.Freeze failed"); }
            panel.CustomPanelBackground = bgBrush;

            // Suppress the title strip: transparent brush so the bar blends
            // into the ribbon canvas. Title text is empty so no label renders.
            var transparent = new SolidColorBrush(Colors.Transparent);
            try { transparent.Freeze(); } catch { /* best-effort */ }
            panel.CustomPanelTitleBarBackground = transparent;

            // Sizing spacer — AdWindows panels collapse with zero items.
            // RibbonLabel with empty Text + explicit Width/Height enforces
            // the 85×85 footprint without rendering button chrome.
            var spacer = new AwRibbonLabel
            {
                Id = "REST_Branding_Spacer",
                Text = "",
                ShowText = false,
                ShowImage = false,
                Width = BrandingDefaults.PanelSizePx,
                Height = BrandingDefaults.PanelSizePx,
                MinWidth = BrandingDefaults.PanelSizePx,
                MinHeight = BrandingDefaults.PanelSizePx,
                IsToolTipEnabled = false,
            };
            source.Items.Add(spacer);

            Log.Debug("PanelStyling.BuildBrandingPanel: profile={Profile} logo={Logo}", profileName, logoAbsolutePath);
            return panel;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "PanelStyling.BuildBrandingPanel failed for logo={Logo}", logoAbsolutePath);
            return null;
        }
    }

    /// <summary>
    /// Create a DrawingBrush that paints a rounded rectangle in the
    /// requested color. When <paramref name="targetWidth"/> and
    /// <paramref name="targetHeight"/> are both &gt; 0, RadiusX/Y are
    /// computed as <see cref="TargetRadiusPx"/> divided by the target
    /// dimension — so the brush, when stretched (Fill / RelativeToBoundingBox)
    /// to a container of that size, renders ~5px corners. Without targets,
    /// falls back to legacy ratios (0.03 / 0.08) which scale with panel
    /// width and produce inconsistent pixel-radii across panels.
    /// Falls back to a SolidColorBrush if the rounded path throws.
    /// </summary>
    public static Brush? ColorBrush(string hexColor, double alpha, double targetWidth = 0, double targetHeight = 0)
    {
        var color = ParseHex(hexColor, alpha);
        if (color is null) return null;

        try
        {
            var fill = new SolidColorBrush(color.Value);
            try { fill.Freeze(); }
            catch (Exception ex) { Log.Debug(ex, "PanelStyling: SolidColorBrush.Freeze failed for hex={Hex}", hexColor); }

            double rx, ry;
            if (targetWidth > 0 && targetHeight > 0)
            {
                rx = TargetRadiusPx / targetWidth;
                ry = TargetRadiusPx / targetHeight;
            }
            else
            {
                rx = 0.03;
                ry = 0.08;
            }
            var rect = new RectangleGeometry(new Rect(0, 0, 1, 1))
            {
                RadiusX = rx,
                RadiusY = ry,
            };
            var drawing = new GeometryDrawing(fill, pen: null, geometry: rect);

            var brush = new DrawingBrush(drawing)
            {
                Stretch = Stretch.Fill,
                ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
                TileMode = TileMode.None,
            };
            // DrawingBrush is the prime suspect for the live-switch leak —
            // un-frozen Freezables retain PropertyChanged subscribers, which
            // AdWindows' visual cache attaches to. If this Freeze silently
            // failed we'd never know. Log at Debug so stress runs surface it.
            try { brush.Freeze(); }
            catch (Exception ex) { Log.Debug(ex, "PanelStyling: DrawingBrush.Freeze failed for hex={Hex}", hexColor); }
            return brush;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "ColorBrush rounded path failed; falling back to solid for hex={Hex}", hexColor);
            return new SolidColorBrush(color.Value);
        }
    }

    private static Color? ParseHex(string? hex, double alpha)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var s = hex!.TrimStart('#');
        if (s.Length != 6) return null;
        try
        {
            var r = byte.Parse(s.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
            var g = byte.Parse(s.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
            var b = byte.Parse(s.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
            var a = (byte)(Math.Max(0.0, Math.Min(1.0, alpha)) * 255);
            return Color.FromArgb(a, r, g, b);
        }
        catch
        {
            return null;
        }
    }

}
