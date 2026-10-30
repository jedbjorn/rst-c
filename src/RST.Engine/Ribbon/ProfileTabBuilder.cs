// ProfileTabBuilder.cs — single AdWindows-direct path for the profile
// tab. Used by:
//   - RstApplication.OnStartup → ApplicationInitialized (first build of
//     the active profile post-Revit-launch).
//   - ProfileLoadService (the Loader's Load Profile →, the Builder's
//     auto-reload) → ProfileSwitchScheduler (live rebuild).
//
// Why AdWindows-direct: UIControlledApplication.CreateRibbonTab/Panel
// only works during IExternalApplication.OnStartup. To rebuild the
// profile tab mid-session (without a Revit restart) we bypass the
// official wrapper and mutate Autodesk.Windows.ComponentManager.Ribbon
// directly — the same internal API pyRevit uses for its hot-reload.
//
// Scope: this file owns *only* the profile tab and its branding panel.
// The "RST" tab and its Loader panel are built once at OnStartup via
// UIControlledApplication (RibbonBuilder.cs) and are never touched
// here, even when profile.Tab == "RST" (we add panels alongside the
// Loader panel without removing it).
//
// Dispatch: AdWindows-built buttons can't go through Revit's
// IExternalCommand pipeline (which requires PushButtonData wiring at
// OnStartup). We use AwRibbonButton.CommandHandler = SlotInvokeCommand
// (WPF ICommand) to route URL slots to Process.Start and Command slots
// to UIApplication.PostCommand, one per button (large or stacked).
//
// Layout (feature #15, spec #17): RST.Core.Ribbon.RibbonLayoutPlanner
// decides what each panel shows: large buttons, stacks of 2-3 small
// buttons (an AdWindows RibbonRowPanel, see RibbonItemFactory: the
// equivalent of AddStackedItems, which only works during OnStartup), and
// the slots it skips. Every skip is logged with its reason. Row panels
// live inside the managed panels, so Teardown removes them with them.
//
// Thread model: every method here mutates ComponentManager.Ribbon and
// must run on the Revit UI thread. Callers are responsible for that —
// both the OnStartup path (ApplicationInitialized fires on UI thread)
// and the live-switch path (ProfileSwitchScheduler routes through
// ExternalEvent) satisfy it.

using System;
using System.Collections.Generic;
using Autodesk.Revit.UI;
using RST.Core.Configuration;
using RST.Core.Profiles;
using RST.Core.Ribbon;
using Serilog;
using AwComponentManager = Autodesk.Windows.ComponentManager;
using AwRibbonTab = Autodesk.Windows.RibbonTab;
using AwRibbonPanel = Autodesk.Windows.RibbonPanel;
using AwRibbonPanelSource = Autodesk.Windows.RibbonPanelSource;

namespace RST.Engine.Ribbon;

internal static class ProfileTabBuilder
{
    private const string RstTabName = "RST";
    private const string ManagedTabIdPrefix = "RST_ProfileTab_";
    private const string ManagedPanelIdPrefix = "RST_ProfilePanel_";
    private const string BrandingPanelIdPrefix = "RST_ProfileBranding_";

    /// <summary>
    /// Panels we have created (profile panels + branding panel). Used
    /// by Teardown() to remove only what we own — the Loader panel
    /// (created via UIControlledApplication at OnStartup) is never in
    /// this list and so survives every rebuild.
    /// </summary>
    private static readonly List<AwRibbonPanel> _managedPanels = new();

    /// <summary>
    /// Tear down the previous profile tab (if any) and build the new
    /// one. Pass null/blank to tear down without rebuilding (unload).
    /// </summary>
    public static void BuildOrRebuild(UIApplication uiApp, Profile? profile)
    {
        if (uiApp is null) throw new ArgumentNullException(nameof(uiApp));

        Teardown();

        if (profile is null)
        {
            Log.Information("ProfileTabBuilder: no profile — teardown only.");
            return;
        }

        var tabName = string.IsNullOrWhiteSpace(profile.Tab) ? RstTabName : profile.Tab!;
        var tab = LocateOrCreateTab(tabName);
        if (tab is null)
        {
            Log.Warning("ProfileTabBuilder: ribbon not ready — skipping build for profile={Name}", profile.ProfileName);
            return;
        }

        // RST-046: per-user, per-profile panel-opacity override REPLACES
        // profile.PanelOpacity when set. Sticky across sessions; admin
        // profile updates do not clear the user's override.
        int? userOverride = !string.IsNullOrEmpty(profile.Id)
            ? UserProfilePrefs.Read().Get(profile.Id!)?.PanelOpacityOverride
            : null;
        int effectiveOpacity = userOverride ?? profile.PanelOpacity;
        var alpha = Math.Max(10, Math.Min(100, effectiveOpacity)) / 100.0;
        if (userOverride.HasValue)
        {
            Log.Information("ProfileTabBuilder: opacity override active for profile={Id} admin={Admin} user={User}",
                            profile.Id, profile.PanelOpacity, userOverride.Value);
        }
        // One SlotInvokeCommand per button, large or stacked.
        Func<SlotTarget, System.Windows.Input.ICommand> handler = target => new SlotInvokeCommand(uiApp, target);
        int panelIndex = 0;
        int slotCount = 0;
        int buttonCount = 0;
        int stackCount = 0;
        int skippedCount = 0;
        int failedCount = 0;

        // Spec #17 (review N5): problems that do not stop a profile loading, such as a stack no
        // panel places (it is not drawn), are logged when the profile is built.
        foreach (var warning in ProfileSerializer.Warnings(profile))
            Log.Warning("ProfileTabBuilder: profile={Name} warning: {Warning}", profile.ProfileName, warning);

        foreach (var panelDef in profile.Panels)
        {
            var layout = RibbonLayoutPlanner.PlanPanel(profile, panelDef, panelIndex);
            var panel = BuildPanel(layout, handler, _ => failedCount++);
            tab.Panels.Add(panel);
            _managedPanels.Add(panel);
            PanelStyling.ApplyColor(panel, panelDef.Color, alpha, RibbonLayoutPlanner.EstimatePanelWidth(layout));
            slotCount += panel.Source?.Items.Count ?? 0;
            buttonCount += layout.ButtonCount;
            stackCount += layout.StackCount;
            skippedCount += layout.Skipped.Count();
            panelIndex++;
        }

        // Branding panel — leftmost on the profile tab. Same precedent as
        // OnStartup: pyRevit and RST-008 both insert at index 0 directly.
        // When profile.Tab == "RST" this pushes the Loader panel one slot
        // right; that matches the previous behaviour exactly.
        var (logoPath, _) = BrandingDefaults.Resolve(profile);
        var brandingPanel = PanelStyling.BuildBrandingPanel(logoPath, profile.ProfileName);
        if (brandingPanel is not null)
        {
            // Tag the source id so teardown can identify it as ours even
            // though PanelStyling.BuildBrandingPanel stamps a default id.
            if (brandingPanel.Source is not null)
                brandingPanel.Source.Id = BrandingPanelIdPrefix + tabName;

            try
            {
                tab.Panels.Insert(0, brandingPanel);
                _managedPanels.Add(brandingPanel);
                Log.Information("ProfileTabBuilder: branding panel inserted at index 0 of tab '{Tab}' (logo={Logo}, profile={Profile})",
                                tabName, logoPath, profile.ProfileName);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "ProfileTabBuilder: failed to insert branding panel on tab '{Tab}'", tabName);
            }
        }

        // Keep RstManagedTabs in sync so the catalog scanner skips the
        // profile tab on the next Loader open.
        RstManagedTabs.Add(tabName);

        Log.Information("ProfileTabBuilder built: profile={Name}, tab={Tab}, panels={PanelCount}, slots={Slots}, buttons={Buttons}, stacks={Stacks}, skipped={Skipped}, failed={Failed}",
                        profile.ProfileName, tabName, profile.Panels.Count, slotCount, buttonCount, stackCount, skippedCount, failedCount);
    }

    /// <summary>
    /// Remove every panel we have created and any tabs we created that
    /// are now empty. Spares the RST tab and its Loader panel.
    /// </summary>
    public static void Teardown()
    {
        if (_managedPanels.Count == 0) return;

        var ribbon = AwComponentManager.Ribbon;
        if (ribbon is null) { _managedPanels.Clear(); return; }

        // Snapshot then clear so any exception below doesn't leave us
        // pointing at panels that have already been removed.
        var toRemove = _managedPanels.ToArray();
        _managedPanels.Clear();

        foreach (var panel in toRemove)
        {
            try
            {
                foreach (var tab in ribbon.Tabs)
                {
                    if (tab is null) continue;
                    if (tab.Panels.Contains(panel))
                    {
                        tab.Panels.Remove(panel);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "ProfileTabBuilder.Teardown: failed to remove a managed panel");
            }
        }

        // Remove any of OUR tabs that are now empty. The RST tab is
        // never one of ours (its Id doesn't carry our prefix), so it
        // stays put even if hypothetically empty.
        for (int i = ribbon.Tabs.Count - 1; i >= 0; i--)
        {
            var tab = ribbon.Tabs[i];
            if (tab is null) continue;
            var id = tab.Id ?? "";
            if (!id.StartsWith(ManagedTabIdPrefix, StringComparison.Ordinal)) continue;
            if (tab.Panels.Count > 0) continue;
            try { ribbon.Tabs.RemoveAt(i); }
            catch (Exception ex) { Log.Warning(ex, "ProfileTabBuilder.Teardown: failed to remove empty managed tab '{Title}'", tab.Title); }
        }
    }

    private static AwRibbonTab? LocateOrCreateTab(string tabName)
    {
        var ribbon = AwComponentManager.Ribbon;
        if (ribbon is null) return null;

        foreach (var existing in ribbon.Tabs)
        {
            if (existing is null) continue;
            if (string.Equals(existing.Title, tabName, StringComparison.Ordinal))
                return existing;
        }

        var tab = new AwRibbonTab
        {
            Title = tabName,
            Id = ManagedTabIdPrefix + tabName,
            IsVisible = true,
        };
        ribbon.Tabs.Add(tab);
        return tab;
    }

    /// <summary>
    /// One profile panel from its layout: large buttons and stacks in slot
    /// order, and a log line per skipped slot or stack item. Touches no
    /// Revit API type (the click handler comes from the caller), so
    /// RST.RuntimeTests builds panels with it outside Revit.
    /// </summary>
    /// <param name="layout">The panel's layout.</param>
    /// <param name="handler">A button's click handler.</param>
    /// <param name="failed">Told about each item that threw while it was built (it is skipped).</param>
    internal static AwRibbonPanel BuildPanel(PanelLayout layout, Func<SlotTarget, System.Windows.Input.ICommand> handler, Action<RibbonLayoutItem>? failed = null)
    {
        var source = new AwRibbonPanelSource
        {
            Title = layout.Name,
            Id = ManagedPanelIdPrefix + layout.PanelIndex.ToString("D3"),
        };
        var panel = new AwRibbonPanel { Source = source };

        foreach (var item in layout.Items)
        {
            // One item that fails to build (AdWindows throwing for a row panel, an icon, a handler)
            // is skipped and logged; the rest of the panel, the other panels and the branding still
            // build (spec #17, review N4). The item is added only once it is complete.
            try
            {
                switch (item)
                {
                    case LargeButtonItem large:
                        source.Items.Add(RibbonItemFactory.LargeButton(large.Button, Icon32(large.Button), handler(large.Button.Target)));
                        break;

                    case StackItem stack:
                        source.Items.Add(RibbonItemFactory.Stack(stack, Icon16, Icon32, b => handler(b.Target)));
                        Log.Information("ProfileTabBuilder: panel {Panel} '{PanelName}' slot {Slot}: stack '{Stack}' with {Count} buttons ({Id})",
                                  layout.PanelIndex, layout.Name, stack.SlotIndex, stack.StackName, stack.Buttons.Count, stack.Id);
                        break;

                    case SkippedItem skipped:
                        Log.Warning("ProfileTabBuilder: panel {Panel} '{PanelName}' slot {Slot} skipped: {Reason}",
                                    layout.PanelIndex, layout.Name, skipped.SlotIndex, skipped.Reason);
                        break;
                }
            }
            catch (Exception ex)
            {
                failed?.Invoke(item);
                Log.Warning(ex, "ProfileTabBuilder: panel {Panel} '{PanelName}' slot {Slot} skipped: building it failed",
                            layout.PanelIndex, layout.Name, item.SlotIndex);
            }
        }

        return panel;
    }

    // Precedence: explicit pack icon (admin override) → branded icon for
    // RST native tools → generic default.
    internal static System.Windows.Media.ImageSource? Icon32(PlannedButton b) =>
        IconAssets.ResolveSlotIcon(b.IconFile)
        ?? IconAssets.ResolveNativeToolIcon(b.CommandId)
        ?? IconAssets.Default32;

    internal static System.Windows.Media.ImageSource? Icon16(PlannedButton b) =>
        IconAssets.ResolveSlotIcon16(b.IconFile)
        ?? IconAssets.ResolveNativeToolIcon16(b.CommandId)
        ?? IconAssets.Default16;
}
