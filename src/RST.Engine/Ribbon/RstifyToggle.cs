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
    /// Tabs the most-recent Hide call hid. Used by
    /// <see cref="ApplyForActiveProfile"/> to lift the prior set before
    /// applying the new one — without this, switching from a profile
    /// that hid {Architecture, Annotate} to one that hides {View} would
    /// leave Architecture and Annotate stranded-hidden.
    /// </summary>
    private static IReadOnlyCollection<string> _lastAppliedHidden = Array.Empty<string>();

    /// <summary>
    /// Apply the hide rule (set IsVisible=false on every tab in
    /// <paramref name="hiddenTabTitles"/>). No-op when the list is empty.
    /// Returns the count of tabs actually hidden. Updates the
    /// last-applied tracking so a subsequent
    /// <see cref="ApplyForActiveProfile"/> can lift exactly this set.
    /// </summary>
    public static int Hide(IReadOnlyCollection<string> hiddenTabTitles)
    {
        if (hiddenTabTitles is null || hiddenTabTitles.Count == 0) return 0;
        var count = SetVisibility(hiddenTabTitles, visible: false);
        _lastAppliedHidden = SnapshotCopy(hiddenTabTitles);
        return count;
    }

    /// <summary>
    /// Lift the hide rule (set IsVisible=true on every tab in
    /// <paramref name="hiddenTabTitles"/>). No-op when the list is empty.
    /// Returns the count of tabs actually shown. Clears the
    /// last-applied tracking — nothing is hidden after this returns.
    /// </summary>
    public static int Show(IReadOnlyCollection<string> hiddenTabTitles)
    {
        if (hiddenTabTitles is null || hiddenTabTitles.Count == 0) return 0;
        var count = SetVisibility(hiddenTabTitles, visible: true);
        _lastAppliedHidden = Array.Empty<string>();
        return count;
    }

    /// <summary>
    /// Apply the hide rule for the active profile end-to-end:
    /// lift any previous hides this session set, hide the new set, and
    /// flip the RSTify button icon to match. Idempotent and safe to
    /// call with an empty/null list (clears all RST hides + icon→off).
    ///
    /// This is the right entry point for "apply the active profile's
    /// RSTify state on the live ribbon" — used by both startup
    /// (ApplicationInitialized) and live profile switching
    /// (ProfileSwitchScheduler.Execute) so the two paths can't drift.
    /// </summary>
    public static void ApplyForActiveProfile(IReadOnlyCollection<string>? newHiddenTabs)
    {
        // Lift the previous set first, so a profile switch from
        // {Architecture, Annotate} → {View} doesn't leave the first
        // two stranded-hidden.
        if (_lastAppliedHidden.Count > 0)
        {
            SetVisibility(_lastAppliedHidden, visible: true);
            _lastAppliedHidden = Array.Empty<string>();
        }

        var hasNew = newHiddenTabs is { Count: > 0 };
        if (hasNew)
        {
            SetVisibility(newHiddenTabs!, visible: false);
            _lastAppliedHidden = SnapshotCopy(newHiddenTabs!);
        }

        RefreshIcon(active: hasNew);
    }

    /// <summary>
    /// True while a hide rule is in force this session (tabs were hidden
    /// and not toggled back). Lets callers skip queueing a re-assert when
    /// there is nothing to protect.
    /// </summary>
    public static bool HasActiveHideRule => _lastAppliedHidden.Count > 0;

    /// <summary>
    /// Re-apply the hide rule if Revit un-hid any of our tabs behind our
    /// back. Revit rebuilds the ribbon tab set when the first document
    /// of the session opens, resetting IsVisible on the tabs we hid at
    /// ApplicationInitialized (doc #4 addendum, flag #15 secondary).
    ///
    /// Only acts while a hide rule is in force (_lastAppliedHidden
    /// non-empty) AND at least one of its tabs is visible again — so a
    /// user who toggled RSTify off (Show clears the tracking) is never
    /// fought. Returns the number of tabs re-hidden, 0 for no drift.
    /// </summary>
    public static int ReassertIfDrifted()
    {
        if (_lastAppliedHidden.Count == 0) return 0;
        var ribbon = AwComponentManager.Ribbon;
        if (ribbon is null) return 0;

        var titles = new HashSet<string>(_lastAppliedHidden, StringComparer.Ordinal);
        bool drifted = false;
        foreach (var tab in ribbon.Tabs)
        {
            if (tab is null) continue;
            if (titles.Contains(tab.Title ?? "") && tab.IsVisible) { drifted = true; break; }
        }
        if (!drifted) return 0;

        var count = SetVisibility(_lastAppliedHidden, visible: false);
        Log.Information("RstifyToggle: re-asserted hide rule after ribbon rebuild ({Count} tabs)", count);
        return count;
    }

    private static IReadOnlyCollection<string> SnapshotCopy(IReadOnlyCollection<string> source)
    {
        var copy = new string[source.Count];
        var i = 0;
        foreach (var s in source) copy[i++] = s;
        return copy;
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
        var large = active ? IconAssets.RstifyIconOn : IconAssets.RstifyIconOff;
        if (large is null) return;
        // Small (16x16) variant for QAT — falls back to the 32 if missing
        // so the on/off swap still happens even when the small isn't bundled.
        var small = (active ? IconAssets.RstifyIconOn16 : IconAssets.RstifyIconOff16) ?? large;

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
                    if (TrySetIfRstify(item, large, small)) return;
                }
            }
        }
    }

    private static bool TrySetIfRstify(AwRibbonItem? item, ImageSource large, ImageSource small)
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
            item.LargeImage = large;
            item.Image = small;
            Log.Debug("RstifyToggle.RefreshIcon: updated RSTify button icon (cookie={Cookie}, id={Id})", cookie, id);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "RstifyToggle.RefreshIcon: setting icon failed (cookie={Cookie})", cookie);
            return false;
        }
    }

    // Title of the RST tools panel (Loader/Builder/RSTify/Health). Mirrors
    // RibbonBuilder.RstPanelTitle — used to locate the host tab to protect.
    private const string RstToolsPanelTitle = "RST";

    /// <summary>
    /// Title of the tab hosting the RST tools panel — the user's only escape
    /// hatch (Loader/RSTify live here). The host tab is Revit's built-in
    /// Add-Ins tab, whose title is locale-dependent, so we find it by the
    /// panel it contains rather than by a hard-coded name. Null if not found.
    /// </summary>
    private static string? HostTabTitle()
    {
        var ribbon = AwComponentManager.Ribbon;
        if (ribbon is null) return null;
        foreach (var tab in ribbon.Tabs)
        {
            if (tab is null) continue;
            foreach (var panel in tab.Panels)
            {
                if (panel?.Source is null) continue;
                if (string.Equals(panel.Source.Title, RstToolsPanelTitle, StringComparison.Ordinal))
                    return tab.Title;
            }
        }
        return null;
    }

    private static int SetVisibility(IReadOnlyCollection<string> titles, bool visible)
    {
        var ribbon = AwComponentManager.Ribbon;
        if (ribbon is null) return 0;
        var titleSet = new HashSet<string>(titles, StringComparer.Ordinal);

        // Never hide the escape-hatch tab (the one hosting the RST tools panel)
        // or any tab RST itself manages. Hiding the Add-Ins host tab would
        // strip the only UI to switch profiles or toggle RSTify back on — a
        // soft-lockout. A profile listing "Add-Ins" in hidden_tabs is refused.
        if (!visible)
        {
            var host = HostTabTitle();
            if (host is not null && titleSet.Remove(host))
                Log.Information("RstifyToggle: refused to hide host tab '{Host}' (escape hatch)", host);
            foreach (var managed in RstManagedTabs.Names) titleSet.Remove(managed);
        }

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
