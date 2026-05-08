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
// Note on corner radius: pyRevit's startup.py wraps SizeChanged-aware
// brush recalc in a try/except that silently swallows the
// AttributeError — Autodesk.Windows.RibbonPanel inherits from
// System.Object, not from a WPF FrameworkElement, so it has no
// SizeChanged event, no ActualWidth/Height. Verified via metadata-
// reader probe of AdWindows 2026.4.0 (events are PropertyChanged /
// HostEvent / IsVisibleChanged only). We use default RadiusX/Y ratios
// (0.03 / 0.08) for the rounded rectangle — visually equivalent to
// pyRevit's actual runtime output. RST-020 may revisit if a route to
// the underlying visual surfaces.
//
// Branding panel construction lives in BuildBrandingPanel(); the
// caller (RibbonBuilder) is responsible for inserting it at index 0
// of the profile tab's Panels collection.
//
// Thread model: every method must be called on the UI thread (Revit's
// ribbon and WPF have the usual STA constraint). Initial color
// application happens in OnStartup, which is UI-thread.

using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AwComponentManager = Autodesk.Windows.ComponentManager;
using AwRibbonTab = Autodesk.Windows.RibbonTab;
using AwRibbonPanel = Autodesk.Windows.RibbonPanel;
using AwRibbonPanelSource = Autodesk.Windows.RibbonPanelSource;
using AwRibbonButton = Autodesk.Windows.RibbonButton;
using AwRibbonItemSize = Autodesk.Windows.RibbonItemSize;
using RST.Core.Ribbon;
using Serilog;

namespace RST.Engine.Ribbon;

internal static class PanelStyling
{
    /// <summary>
    /// Apply a colored, rounded background to <paramref name="panel"/>.
    /// Sets both the panel body and title-bar brushes to the same
    /// DrawingBrush.
    /// </summary>
    public static void ApplyColor(AwRibbonPanel panel, string hexColor, double alpha)
    {
        if (panel is null) return;
        var clamped = Math.Max(0.0, Math.Min(1.0, alpha));
        try
        {
            var brush = ColorBrush(hexColor, clamped);
            if (brush is null) return;
            panel.CustomPanelBackground = brush;
            panel.CustomPanelTitleBarBackground = brush;
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
    /// Build the leftmost branding panel — title is whitespace (panels
    /// without a title get auto-laid-out with a bare label, which is
    /// what we want for a logo-only panel), background is the company
    /// logo via ImageBrush, and the panel hosts a single transparent
    /// large button so the panel takes up the standard Large width;
    /// clicking the button opens the configured branding URL (or the
    /// RST GitHub repo if no URL is set).
    /// Returns null if no logo is available — caller skips the insert.
    /// </summary>
    public static AwRibbonPanel? BuildBrandingPanel(string? logoAbsolutePath, string? url)
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
                // Whitespace title leaves a bare strip; matches pyRevit's
                // 12-space placeholder so the layout reserves height for the
                // logo without showing any text.
                Title = "            ",
                Id = "REST_Branding",
            };
            panel.Source = source;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(Path.GetFullPath(logoAbsolutePath), UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            // Freeze is best-effort — failure makes the bitmap mutable, which
            // costs us cross-thread sharing and (more importantly) leaves
            // PropertyChanged subscribers attached. Surface the cause if it
            // ever fires so we can correlate against memory growth on rapid
            // profile switches.
            try { bmp.Freeze(); }
            catch (Exception ex) { Log.Debug(ex, "PanelStyling: BitmapImage.Freeze failed for logo={Logo}", logoAbsolutePath); }

            var imgBrush = new ImageBrush(bmp) { Stretch = Stretch.Uniform };
            panel.CustomPanelBackground = imgBrush;

            var btn = new AwRibbonButton
            {
                Text = " ",
                Id = "REST_Branding_Btn",
                ShowText = false,
                Size = AwRibbonItemSize.Large,
                CommandHandler = new UrlClickCommand(string.IsNullOrWhiteSpace(url)
                    ? "https://github.com/jedbjorn/RST"
                    : url!.Trim()),
            };
            source.Items.Add(btn);

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
    /// requested color. RadiusX/Y are relative-to-bounding-box ratios
    /// (0.03 / 0.08) — see header comment on why we don't size-track
    /// for fixed-pixel corners. Falls back to a SolidColorBrush if the
    /// rounded path throws.
    /// </summary>
    public static Brush? ColorBrush(string hexColor, double alpha)
    {
        var color = ParseHex(hexColor, alpha);
        if (color is null) return null;

        try
        {
            var fill = new SolidColorBrush(color.Value);
            try { fill.Freeze(); }
            catch (Exception ex) { Log.Debug(ex, "PanelStyling: SolidColorBrush.Freeze failed for hex={Hex}", hexColor); }

            var rect = new RectangleGeometry(new Rect(0, 0, 1, 1))
            {
                RadiusX = 0.03,
                RadiusY = 0.08,
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

    /// <summary>
    /// ICommand wrapper that opens a URL via the system shell when the
    /// branding-panel button is clicked. AdWindows.RibbonButton wires
    /// click to ICommand (not IExternalCommand), so the URL handler
    /// runs entirely WPF-side without a Revit transaction.
    /// </summary>
    private sealed class UrlClickCommand : ICommand
    {
        private readonly string _url;
        public UrlClickCommand(string url) { _url = UrlNormalizer.Normalize(url); }
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter)
        {
            try
            {
                Process.Start(new ProcessStartInfo(_url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Branding URL open failed for {Url}", _url);
            }
        }
    }
}
