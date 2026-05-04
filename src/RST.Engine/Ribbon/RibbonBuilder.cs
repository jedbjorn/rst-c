// RibbonBuilder.cs — translates an ActiveProfile into Revit ribbon panels.
//
// Two phases:
//   1. Always create the RST tab + a "Loader" pushbutton. This is the
//      entry point users always have available, even with no profile
//      applied.
//   2. If a profile is active, walk Profile.Panels and build one
//      RibbonPanel per Profile.Panel — flat, no AdWindows colored
//      surfaces (those land in RST-008). Each Panel.Slot[type=tool]
//      claims a SlotRegistry index and gets wired to a generated
//      Slot### IExternalCommand class via PushButtonData. Stack slots
//      and URL slots (URL: prefix) are supported on the same path.
//
// Revit can't tear down ribbon panels mid-session, so this runs once
// per Revit launch from RstApplication.OnStartup. Profile changes
// require a Revit restart — the Loader UI tells users this on Apply.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Autodesk.Revit.UI;
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
        int slotIdx = 0;
        var skippedTooMany = new List<string>();

        foreach (var panelDef in profile.Panels)
        {
            RibbonPanel panel;
            try { panel = app.CreateRibbonPanel(RstTabName, panelDef.Name); }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to create RST panel '{PanelName}' — skipping.", panelDef.Name);
                continue;
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
                    text: slot.Name,
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

        Log.Information("RST ribbon built: profile={Name}, panels={PanelCount}, slots={SlotCount}{TooMany}",
                        profile.ProfileName, profile.Panels.Count, slotIdx,
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
}
