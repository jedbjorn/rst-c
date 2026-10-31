// rst-c's own (not vendored): the tray icon's menu (spec #20 §1; F17 WU1): Open RST, Check for updates (checks
// now and opens the Updates page, F17 WU3), Settings, Quit. A WPF ContextMenu in the family look: each open builds it afresh on a new
// Theme.xaml, so it always matches the current theme. Opened at the icon's anchor point; it takes the
// foreground (the classic notification-menu rule) so a click anywhere else dismisses it.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using RST.Core.Logging;
using RST.Tray.Resources;

namespace RST.Tray.Windows;

/// <summary>What the menu's items do.</summary>
internal sealed record TrayMenuActions(Action Open, Action CheckForUpdates, Action Settings, Action Quit)
{
    /// <summary>Check for updates is enabled (F17 WU3); false shows it disabled.</summary>
    public bool CanCheckForUpdates { get; init; } = true;
}

internal static class TrayMenu
{
    public const string OpenId = "Tray.Menu.Open";
    public const string CheckForUpdatesId = "Tray.Menu.CheckForUpdates";
    public const string SettingsId = "Tray.Menu.Settings";
    public const string QuitId = "Tray.Menu.Quit";

    /// <summary>The menu, styled by a fresh Theme.xaml for the current theme.</summary>
    public static ContextMenu Build(TrayMenuActions actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        var theme = new ResourceDictionary { Source = new Uri("/RST.UI;component/Theme.xaml", UriKind.Relative) };
        var menu = new ContextMenu { StaysOpen = false };
        menu.Resources.MergedDictionaries.Add(theme);
        // An element's own Resources do not supply its implicit style, so the menu's is set directly; its items
        // and separators find theirs in the menu's Resources.
        if (theme[typeof(ContextMenu)] is Style menuStyle) menu.Style = menuStyle;
        AutomationIds(menu, "Tray.Menu");

        menu.Items.Add(Item(TrayStrings.Menu_Open, OpenId, actions.Open, isDefault: true));
        menu.Items.Add(Item(TrayStrings.Menu_CheckForUpdates, CheckForUpdatesId, actions.CheckForUpdates, enabled: actions.CanCheckForUpdates));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(TrayStrings.Menu_Settings, SettingsId, actions.Settings));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(TrayStrings.Menu_Quit, QuitId, actions.Quit));
        return menu;
    }

    /// <summary>Opens <paramref name="menu"/> at <paramref name="screenPixels"/> (the icon's anchor), converted through <paramref name="anchor"/>.</summary>
    public static void Open(ContextMenu menu, Point screenPixels, HwndSource anchor)
    {
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(anchor);
        var dips = anchor.CompositionTarget?.TransformFromDevice.Transform(screenPixels) ?? screenPixels;
        menu.Placement = PlacementMode.AbsolutePoint;
        menu.HorizontalOffset = dips.X;
        menu.VerticalOffset = dips.Y;
        // The notification-menu rule: the owner takes the foreground first, so the menu gets the input and a click
        // anywhere else dismisses it. Explorer lets the icon's process do this while it handles the icon's message.
        var anchorForeground = SetForegroundWindow(anchor.Handle);
        var shown = Stopwatch.StartNew();
        menu.Opened += (_, _) =>
        {
            var popupForeground = PresentationSource.FromVisual(menu) is HwndSource popup && SetForegroundWindow(popup.Handle);
            menu.Focus();
            // SC-020: with the "menu requested" line, the tray log tells a menu that opened from one that never did.
            Log.Information("tray menu opened at {X},{Y} (foreground: owner {Owner}, menu {Menu})",
                Math.Round(dips.X), Math.Round(dips.Y), anchorForeground, popupForeground);
            shown.Restart();
        };
        menu.Closed += (_, _) => Log.Information("tray menu closed after {Ms} ms", shown.ElapsedMilliseconds);
        menu.IsOpen = true;
    }

    private static MenuItem Item(string header, string automationId, Action action, bool enabled = true, bool isDefault = false)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        if (isDefault) item.FontWeight = FontWeights.SemiBold;
        AutomationIds(item, automationId);
        item.Click += (_, _) => action();
        return item;
    }

    private static void AutomationIds(DependencyObject element, string id) =>
        System.Windows.Automation.AutomationProperties.SetAutomationId(element, id);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
