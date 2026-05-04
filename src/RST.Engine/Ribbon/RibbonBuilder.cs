// RibbonBuilder.cs — translates an ActiveProfile into Revit ribbon panels.
//
// Two phases:
//   1. Always create the RST tab + a "Loader" pushbutton. This is the
//      entry point users always have available, even with no profile
//      applied.
//   2. If a profile is active, build its panels under the tab named in
//      Profile.Tab (creating that tab if it doesn't exist). Each
//      Panel.Slot[type=tool] claims a SlotRegistry index and gets wired
//      to a generated Slot### IExternalCommand via PushButtonData. Stack
//      slots and URL slots (URL: prefix) are supported on the same path.
//
// Tab semantics: profiles may name the same tab as RST ("RST"), in
// which case panels are added alongside the Loader on the existing
// tab. Otherwise a new tab is created with the profile's name —
// matches how users describe profiles ("my Drafting tab", "my QA tab").
//
// Revit can't tear down ribbon panels mid-session via UIControlledApplication,
// so this runs once per Revit launch from RstApplication.OnStartup. Profile
// changes require a Revit restart today — RST-020 will lift that restriction
// by switching the profile-tab path to AdWindows-direct construction.
//
// RST-008: after each profile panel is created via app.CreateRibbonPanel,
// we look up the underlying Autodesk.Windows.RibbonPanel via PanelStyling
// .FindAwPanel and apply color + opacity + rounded corners. A leftmost
// branding panel (logo + URL) is built directly via AdWindows and inserted
// at index 0 of the profile tab — same pattern pyRevit's startup.py uses,
// because Revit's UIControlledApplication has no equivalent of "create a
// panel with no IExternalCommand backing".

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Autodesk.Revit.UI;
using RST.Core.Configuration;
using RST.Core.Profiles;
using Serilog;

namespace RST.Engine.Ribbon;

internal static class RibbonBuilder
{
    private const string RstTabName = "RST";
    private const string LoaderClassName = "RST.Engine.Commands.LoaderCommand";

    public static void Build(UIControlledApplication app)
    {
        try { app.CreateRibbonTab(RstTabName); }
        catch (Autodesk.Revit.Exceptions.ArgumentException) { /* tab exists — addin reload */ }

        // Always register the RST tab itself so the catalog scanner
        // doesn't surface our own Loader button (or any future RST-tab
        // panels) as profile-buildable commands.
        RstManagedTabs.Add(RstTabName);

        var assemblyPath = typeof(RibbonBuilder).Assembly.Location;

        // Loader button — always present.
        var loaderPanel = app.CreateRibbonPanel(RstTabName, "RST");
        var loaderBtn = new PushButtonData(
            name: "RST_Loader",
            text: "Loader",
            assemblyName: assemblyPath,
            className: LoaderClassName)
        {
            ToolTip = "Open the RST profile selector.",
        };
        loaderPanel.AddItem(loaderBtn);

        // Profile panels — only if a real profile is active and resolves cleanly.
        var active = ActiveProfile.Read();
        if (active.IsBlank)
        {
            Log.Information("No active RST profile; ribbon shows Loader only.");
            return;
        }

        var entry = ProfileStore.Resolve(active.ProfileName, active.ProfileId);
        if (entry is null)
        {
            Log.Warning("Active profile {Name} ({Id}) not found on disk; ribbon shows Loader only.",
                        active.ProfileName, active.ProfileId);
            return;
        }

        SlotRegistry.Clear();
        BuildProfilePanels(app, assemblyPath, entry.Profile);
    }

    private static void BuildProfilePanels(UIControlledApplication app, string assemblyPath, Profile profile)
    {
        // Profile-defined tab. Empty string falls back to the always-present
        // RST tab (defensive — bridge SaveProfile rejects blank tabs, but if
        // a hand-edited profile slips through we don't want to throw).
        var tabName = string.IsNullOrWhiteSpace(profile.Tab) ? RstTabName : profile.Tab;
        if (!string.Equals(tabName, RstTabName, StringComparison.Ordinal))
        {
            try
            {
                app.CreateRibbonTab(tabName);
                Log.Information("Created profile tab '{TabName}'", tabName);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
                // Tab already exists — fine, addin reload or another addin
                // claimed the same name. AddItem below will still attach.
                Log.Debug("Profile tab '{TabName}' already exists", tabName);
            }
        }
        // Register so RibbonScanner skips the profile tab — same reasoning
        // as the RST tab: if RST built it, it shouldn't appear as catalog
        // input on the next Loader open.
        RstManagedTabs.Add(tabName);

        int slotIdx = 0;
        var skippedTooMany = new List<string>();
        var alpha = Math.Max(10, Math.Min(100, profile.PanelOpacity)) / 100.0;

        foreach (var panelDef in profile.Panels)
        {
            RibbonPanel panel;
            try { panel = app.CreateRibbonPanel(tabName, panelDef.Name); }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to create panel '{PanelName}' on tab '{TabName}' — skipping.",
                            panelDef.Name, tabName);
                continue;
            }

            // Reach past the Revit wrapper to colour the underlying AdWindows
            // panel. Match by Source.Title — Revit doesn't expose the wrapped
            // instance directly, but order is preserved so the panel we just
            // created is now in the tab's Panels collection.
            var awPanel = PanelStyling.FindAwPanel(tabName, panelDef.Name);
            if (awPanel is not null)
            {
                PanelStyling.ApplyColor(awPanel, panelDef.Color, alpha);
            }
            else
            {
                Log.Warning("Could not locate AdWindows panel for '{PanelName}' on tab '{TabName}' — color skipped.",
                            panelDef.Name, tabName);
            }

            foreach (var slot in panelDef.Slots)
            {
                // Stacks resolve into 2-3 buttons each; deferred — RST-006 owns the
                // expansion logic + drag-reorder UI. For now, render flat tool slots
                // only and skip stacks with a log.
                if (slot.SlotType != "tool")
                {
                    Log.Debug("Skipping non-tool slot in panel '{PanelName}': type={Type}",
                              panelDef.Name, slot.SlotType);
                    continue;
                }
                if (string.IsNullOrEmpty(slot.CommandId))
                {
                    Log.Debug("Skipping tool slot '{Name}' in panel '{PanelName}': no commandId.",
                              slot.Name, panelDef.Name);
                    continue;
                }
                if (slotIdx >= SlotRegistry.Capacity)
                {
                    skippedTooMany.Add(slot.Name);
                    continue;
                }

                var target = ParseTarget(slot.CommandId!, slot.Name);
                SlotRegistry.Set(slotIdx, target);

                var slotClass = $"RST.Engine.Ribbon.Slots.Slot{slotIdx:D3}";
                var btnName = $"RST_Slot{slotIdx:D3}";
                var pbd = new PushButtonData(
                    name: btnName,
                    text: WrapButtonText(slot.Name),
                    assemblyName: assemblyPath,
                    className: slotClass)
                {
                    ToolTip = target.Kind == SlotKind.Url
                        ? "Open: " + target.Payload
                        : "Posts: " + target.Payload,
                };

                try { panel.AddItem(pbd); }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to add slot {Index} '{Name}' to panel '{PanelName}'.",
                                slotIdx, slot.Name, panelDef.Name);
                    // Index already burned; keep counter advancing to preserve mapping.
                }
                slotIdx++;
            }
        }

        // Branding panel — leftmost on the profile tab. Uses the per-machine
        // default branding (RST-017) resolved against the loaded profile.
        // Built directly via AdWindows because UIControlledApplication has
        // no API for a non-IExternalCommand-backed panel; pyRevit's
        // startup.py uses the same approach.
        var (logoPath, brandingUrl) = BrandingDefaults.Resolve(profile);
        var brandingPanel = PanelStyling.BuildBrandingPanel(logoPath, brandingUrl);
        if (brandingPanel is not null)
        {
            var awTab = PanelStyling.FindAwTab(tabName);
            if (awTab is not null)
            {
                try
                {
                    awTab.Panels.Insert(0, brandingPanel);
                    Log.Information("Branding panel inserted at index 0 of tab '{TabName}' (logo={Logo}, urlSet={UrlSet})",
                                    tabName, logoPath, !string.IsNullOrEmpty(brandingUrl));
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to insert branding panel on tab '{TabName}'", tabName);
                }
            }
            else
            {
                Log.Warning("Could not locate AdWindows tab '{TabName}' to insert branding panel", tabName);
            }
        }
        else
        {
            Log.Debug("No branding logo available — skipping branding panel for tab '{TabName}'", tabName);
        }

        Log.Information("RST ribbon built: profile={Name}, tab={Tab}, panels={PanelCount}, slots={SlotCount}{TooMany}",
                        profile.ProfileName, tabName, profile.Panels.Count, slotIdx,
                        skippedTooMany.Count > 0 ? $" (skipped {skippedTooMany.Count} over capacity)" : "");

        if (skippedTooMany.Count > 0)
            Log.Warning("Profile '{Name}' has more buttons than SlotRegistry.Capacity={Cap}; skipped: {Skipped}",
                        profile.ProfileName, SlotRegistry.Capacity, string.Join(", ", skippedTooMany));
    }

    private static SlotTarget ParseTarget(string commandId, string displayName)
    {
        if (commandId.StartsWith("URL:", StringComparison.OrdinalIgnoreCase))
            return new SlotTarget(SlotKind.Url, commandId.Substring(4), displayName);
        return new SlotTarget(SlotKind.Command, commandId, displayName);
    }

    private static string WrapButtonText(string? text) =>
        RST.Core.Ribbon.ButtonLabel.Wrap(text);
}
