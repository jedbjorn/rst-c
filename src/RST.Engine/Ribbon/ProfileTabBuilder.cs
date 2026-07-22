// ProfileTabBuilder.cs — single AdWindows-direct path for the profile
// tab. Used by:
//   - RstApplication.OnStartup → ApplicationInitialized (first build of
//     the active profile post-Revit-launch).
//   - LoaderBridge.LoadProfile → ProfileSwitchScheduler (live rebuild
//     when the user clicks Apply).
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
// to UIApplication.PostCommand. Same effect as Slot###/SlotRegistry,
// constructed per-button instead of via a static index pool.
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
using Serilog;
using AwComponentManager = Autodesk.Windows.ComponentManager;
using AwRibbonTab = Autodesk.Windows.RibbonTab;
using AwRibbonPanel = Autodesk.Windows.RibbonPanel;
using AwRibbonPanelSource = Autodesk.Windows.RibbonPanelSource;
using AwRibbonButton = Autodesk.Windows.RibbonButton;
using AwRibbonItemSize = Autodesk.Windows.RibbonItemSize;

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
        int panelIndex = 0;
        int slotCount = 0;

        foreach (var panelDef in profile.Panels)
        {
            var panel = BuildPanel(uiApp, panelDef, panelIndex);
            tab.Panels.Add(panel);
            _managedPanels.Add(panel);
            PanelStyling.ApplyColor(panel, panelDef.Color, alpha);
            slotCount += panel.Source?.Items.Count ?? 0;
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

        Log.Information("ProfileTabBuilder built: profile={Name}, tab={Tab}, panels={PanelCount}, slots={Slots}",
                        profile.ProfileName, tabName, profile.Panels.Count, slotCount);
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

    private static AwRibbonPanel BuildPanel(UIApplication uiApp, Panel panelDef, int panelIndex)
    {
        var source = new AwRibbonPanelSource
        {
            Title = panelDef.Name ?? "",
            Id = ManagedPanelIdPrefix + panelIndex.ToString("D3"),
        };
        var panel = new AwRibbonPanel { Source = source };

        int slotInPanel = 0;
        foreach (var slot in panelDef.Slots)
        {
            if (slot.SlotType != "tool") continue;
            if (string.IsNullOrEmpty(slot.CommandId)) continue;

            var target = ParseTarget(slot.CommandId!, slot.Name);
            // Precedence: explicit pack icon (admin override) → branded
            // icon for RST native tools → generic default.
            var icon = IconAssets.ResolveSlotIcon(slot.IconFile)
                    ?? IconAssets.ResolveNativeToolIcon(slot.CommandId)
                    ?? IconAssets.Default32;
            var btn = new AwRibbonButton
            {
                Text = RST.Core.Ribbon.ButtonLabel.Wrap(slot.Name),
                Id = $"RST_ProfileBtn_{panelIndex:D3}_{slotInPanel:D3}",
                ShowText = true,
                Size = AwRibbonItemSize.Large,
                Image = icon,
                LargeImage = icon,
                Orientation = System.Windows.Controls.Orientation.Vertical,
                ToolTip = target.Kind == SlotKind.Url
                    ? "Open: " + target.Payload
                    : "Posts: " + target.Payload,
                CommandHandler = new SlotInvokeCommand(uiApp, target),
            };
            source.Items.Add(btn);
            slotInPanel++;
        }

        return panel;
    }

    private static SlotTarget ParseTarget(string commandId, string displayName)
    {
        if (commandId.StartsWith("URL:", StringComparison.OrdinalIgnoreCase))
            return new SlotTarget(SlotKind.Url, commandId.Substring(4), displayName);
        return new SlotTarget(SlotKind.Command, commandId, displayName);
    }
}
