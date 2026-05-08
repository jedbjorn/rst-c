// RstifyToggle.cs — apply, lift, and toggle the active profile's
// `hidden_tabs` rule against the live AdWindows ribbon, plus keep the
// RSTify ribbon button's icon in sync with the current state.
//
// Mirrors the pyRevit RST behaviour at /home/jedi/RST/startup.py
// (lines 821-870) and Minify.panel/RSTify.pushbutton/script.py:
//   - On Revit start, if the active profile has hidden_tabs, hide them
//     and flip the RSTify icon to "on" (icon_minify_on.png).
//   - When the user clicks the RSTify button, toggle visibility for the
//     configured tabs and swap the icon to match.
//   - The button is identified by the Cookie / Id we stamp at OnStartup
//     (see RibbonBuilder + RstifyCommand).
//
// Tab visibility is mutated via Autodesk.Windows.RibbonTab.IsVisible —
// the same property pyRevit's RSTify uses. AdWindows is unsupported but
// de-facto stable, same caveat as ProfileTabBuilder.
//
// Thread model: every method here mutates ComponentManager.Ribbon and
// must run on the Revit UI thread. RstifyCommand runs on UI thread by
// virtue of being an IExternalCommand; OnStartup-time application uses
// ApplicationInitialized which also fires on the UI thread.

using System;
using System.Collections.Generic;
using System.Windows.Media;
using AwComponentManager = Autodesk.Windows.ComponentManager;
using AwRibbonItem = Autodesk.Windows.RibbonItem;
using Serilog;

namespace RST.Engine.Ribbon;

internal static class RstifyToggle
{
    /// <summary>
    /// Cookie stamped on the RSTify <c>PushButtonData</c> at OnStartup so
    /// we can find the button at runtime and swap its icon. Cookie is
    /// the only identifier that survives the official-API → AdWindows
    /// boundary, since PushButtonData is wrapped before reaching
    /// ComponentManager.Ribbon.
    /// </summary>
    public const string RstifyButtonCookie = "RST_Rstify";

    /// <summary>
    /// Apply the hide rule (set IsVisible=false on every tab in
    /// <paramref name="hiddenTabTitles"/>). No-op when the list is empty.
    /// Returns the count of tabs actually hidden.
    /// </summary>
    public static int Hide(IReadOnlyCollection<string> hiddenTabTitles)
    {
        if (hiddenTabTitles is null || hiddenTabTitles.Count == 0) return 0;
        return SetVisibility(hiddenTabTitles, visible: false);
    }

    /// <summary>
    /// Lift the hide rule (set IsVisible=true on every tab in
    /// <paramref name="hiddenTabTitles"/>). No-op when the list is empty.
    /// Returns the count of tabs actually shown.
    /// </summary>
    public static int Show(IReadOnlyCollection<string> hiddenTabTitles)
    {
        if (hiddenTabTitles is null || hiddenTabTitles.Count == 0) return 0;
        return SetVisibility(hiddenTabTitles, visible: true);
    }

    /// <summary>
    /// Returns true when at least one of <paramref name="hiddenTabTitles"/>
    /// is currently invisible — i.e. the hide rule is currently in effect.
    /// </summary>
    public static bool IsCurrentlyHidden(IReadOnlyCollection<string> hiddenTabTitles)
    {
        if (hiddenTabTitles is null || hiddenTabTitles.Count == 0) return false;
        var ribbon = AwComponentManager.Ribbon;
        if (ribbon is null) return false;
        var titles = new HashSet<string>(hiddenTabTitles, StringComparer.Ordinal);
        foreach (var tab in ribbon.Tabs)
        {
            if (tab is null) continue;
            var title = tab.Title ?? "";
            if (titles.Contains(title) && !tab.IsVisible) return true;
        }
        return false;
    }

    /// <summary>
    /// Walk the live ribbon and update the RSTify button's Image and
    /// LargeImage to match <paramref name="active"/>. Silently skips
    /// when the button or the ribbon isn't ready.
    /// </summary>
    public static void RefreshIcon(bool active)
    {
        var icon = active ? IconAssets.RstifyIconOn : IconAssets.RstifyIconOff;
        if (icon is null) return;

        var ribbon = AwComponentManager.Ribbon;
        if (ribbon is null) return;

        foreach (var tab in ribbon.Tabs)
        {
            if (tab is null) continue;
            foreach (var panel in tab.Panels)
            {
                if (panel?.Source is null) continue;
                foreach (var item in panel.Source.Items)
                {
                    if (TrySetIfRstify(item, icon)) return;
                }
            }
        }
    }

    private static bool TrySetIfRstify(AwRibbonItem? item, ImageSource icon)
    {
        if (item is null) return false;
        // Cookie or Id can carry our marker; check both.
        var cookie = item.Cookie ?? "";
        var id = item.Id ?? "";
        if (!cookie.Contains(RstifyButtonCookie, StringComparison.Ordinal) &&
            !id.Contains(RstifyButtonCookie, StringComparison.Ordinal))
            return false;
        try
        {
            item.LargeImage = icon;
            item.Image = icon;
            Log.Debug("RstifyToggle.RefreshIcon: updated RSTify button icon (cookie={Cookie}, id={Id})", cookie, id);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "RstifyToggle.RefreshIcon: setting icon failed (cookie={Cookie})", cookie);
            return false;
        }
    }

    private static int SetVisibility(IReadOnlyCollection<string> titles, bool visible)
    {
        var ribbon = AwComponentManager.Ribbon;
        if (ribbon is null) return 0;
        var titleSet = new HashSet<string>(titles, StringComparer.Ordinal);
        int count = 0;
        foreach (var tab in ribbon.Tabs)
        {
            if (tab is null) continue;
            var title = tab.Title ?? "";
            if (!titleSet.Contains(title)) continue;
            try
            {
                tab.IsVisible = visible;
                count++;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "RstifyToggle.SetVisibility: failed to set IsVisible={Visible} on tab '{Title}'", visible, title);
            }
        }
        Log.Information("RstifyToggle: visible={Visible}, affected {Count}/{Asked} tabs", visible, count, titles.Count);
        return count;
    }
}
