---
title: Profile Loader
tags: [rst-c, loader, profiles, end-user]
date: 2026-06-24
project: rst-c
purpose: End-user guide to loading and managing profiles
---

# Profile Loader

[![Open in md-converter](https://img.shields.io/badge/Open%20in-md--converter-6b46c1?style=flat-square)](https://md-converter.designs-os.com/?url=https://github.com/jedbjorn/rst-c/blob/main/docs/profile-loader.md)

## Overview

The Loader is the end-user face of rst-c. It opens as a WebView2 window listing every profile installed on the machine, shows what is in each one before you commit, and applies the chosen profile to the ribbon with a single click. Most users will only ever see this window — loading a profile is the whole job.

https://github.com/user-attachments/assets/7ac96408-d531-4930-a1ad-446b2994739c

## Using it

![The Profile Selector window — the installed profiles list on the left, and for the selected profile a preview of its ribbon tab, the RSTify tab-hiding toggles, and the required add-ins with their load status](https://raw.githubusercontent.com/jedbjorn/rst-c/main/docs/images/loader-profile-selector.png)

### Opening the Loader

The Loader button lives on the Add-Ins tab under the RST panel. Click it to open the profile picker.

### Browsing and previewing

Every profile stored under `%AppData%\RST\profiles\` appears in the list. Selecting a profile shows its panels and tools before you commit — you can see exactly what will appear on the ribbon without applying anything.

### Applying a profile

Click **Apply** to load the selected profile. rst-c rebuilds the profile tab in place on Revit's next idle event — no restart required. The ribbon updates with the profile's panels and tools; nothing outside the profile tab is touched.

The active profile is remembered in `%AppData%\RST\active_profile.json` and restored automatically when Revit starts.

### Switching profiles

To switch to a different profile, open the Loader again and apply another one. Switching is live — rst-c lifts the previous profile's tab, builds the new one in its place, and updates the RSTify state in a single idle-event pass. You do not need to close the model.

### Deleting a profile

Select the profile in the Loader and click **Delete profile**. The profile file is removed from `%AppData%\RST\profiles\`. Deleting the active profile unloads it from the ribbon.

## How it works

```mermaid
graph LR
  A["Open Loader"]:::class1 --> B["Browse profiles"]:::class2 --> C["Click Apply"]:::class1 --> D["Ribbon rebuilds on idle"]:::class3
```

The Loader runs as a WebView2 window hosted inside Revit. The JavaScript UI talks to the C# host through a COM-visible bridge (`LoaderBridge`). When Apply is called, the bridge schedules a profile switch on the next Revit idle event — Revit's ribbon and AdWindows APIs require UI-thread access, and the idle event is the safe delivery point.

The bridge reads profiles from `%AppData%\RST\profiles\` and writes the active selection to `%AppData%\RST\active_profile.json`. Both locations are per-user; no admin rights are required.

## Notes & limits

- Profiles are per-user. Each Windows user on the machine has their own `%AppData%\RST\` directory and sees only their own profiles.
- Only one profile tab is active at a time. Applying a profile replaces the previous one.
- The Loader requires WebView2 Runtime to be installed. The MSI installer includes this dependency.
- Profiles shared from another machine (via the Builder's export) are imported through the Loader — see [Profile Builder](profile-builder.md) for export and import details.
