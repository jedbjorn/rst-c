---
title: Profile Loader
tags: [rst-c, loader, profiles, end-user]
date: 2026-10-30
project: rst-c
purpose: End-user guide to loading and managing profiles
---

# Profile Loader

[![Open in md-converter](https://img.shields.io/badge/Open%20in-md--converter-6b46c1?style=flat-square)](https://md-converter.designs-os.com/?url=https://github.com/jedbjorn/rst-c/blob/main/docs/profile-loader.md)

## Overview

The Loader is the end-user face of rst-c. It is a native Revit window that lists every profile installed for you, shows what is in each one before you commit, and loads the chosen profile onto the ribbon with one click. Most users only ever see this window: loading a profile is the whole job.

## Using it

![The Profile Loader window: the Profiles list on the left with the loaded profile ringed in green, and for the selected profile its ribbon tab preview with the opacity slider, the RSTify "Hide These Tabs" switches and the required add-ins with their status pills, above the footer buttons](https://raw.githubusercontent.com/jedbjorn/rst-c/main/docs/images/loader-profile-selector.png)

### Opening the Loader

Click **Loader** on the **RST** panel of the **Add-Ins** tab. The window, titled **RST — Profile Loader**, opens centred over Revit:

- It belongs to Revit. It has no taskbar button of its own, and Revit waits until you close it.
- It uses Revit's light or dark theme, as set in Revit under Options → Colors → UI active theme (including "Use system setting"). The theme is read when the window opens; if you change Revit's theme, close and reopen the Loader to pick it up.

### The window

- **Header:** "Profile Selector", the running Revit version, and **+ Import from File**.
- **Profiles (left):** one card per profile, with its name, Tab, Min Revit and panel count, and a count of profiles at the top. The loaded profile has a green ring and a **● Loaded** badge. With no profiles installed, the list reads "No profiles found" and offers **+ Import Profile from File**.
- **Details (right):** the selected profile's name (with **● Loaded** when it is the loaded one), its Tab, Min Revit and Updated date, and three sections:
  - **Tab Preview:** a read-only picture of the ribbon tab the profile builds: its panels, their colours and the tools' icons and names.
  - **RSTify: Hide These Tabs:** one switch per ribbon tab the profile can hide. See [Ribbon Tools](ribbon-tools.md).
  - **Required Add-ins for this profile:** each add-in the profile depends on, with its file name, an **info ↗** link where the profile has one, and a status pill: **Native** (built into Revit), **Loaded**, **Installed**, **Disabled** or **Not Found**.
- **Footer:** **Unload Profile** and **Delete Profile** on the left; **Restore All Add-ins** and the **Disable unused add-ins** switch in the middle; **Load Profile →** and **Close** on the right.

### Browsing and previewing

Every profile stored under `%AppData%\RST\profiles\` has a card. Click a card to see its details; nothing changes on the ribbon until you click **Load Profile →**. When the Loader opens, the loaded profile is selected.

**Override Panel Opacity** (above the preview) sets how strongly this profile's panel colours are drawn for you, from 10 to 100. The preview follows the slider. Your value is saved for this profile when you let go of the slider, and it is used the next time the profile is loaded.

### Admin presets

A profile can carry defaults set by its admin: tabs to hide and whether to disable unused add-ins. The first time you select such a profile, the Loader asks **Adopt RSTify Presets?** and summarises the defaults. **Continue** turns them on; **Cancel** starts with nothing hidden and the switch off. Either way you are asked only once per profile, and you can change the switches at any time.

### Loading a profile

Click **Load Profile →**. The Loader then:

1. If **Disable unused add-ins** is on, shows **Confirm Add-in Changes**: what stays active, what will be disabled, and what it will try to disable, plus a note that Revit must restart for the changes to take effect. **Cancel** is the default button, so pressing Enter cancels. **Confirm & Load** goes ahead.
2. Shows **Loading <profile>** while it works. Add-in files are renamed in the background, so the window stays responsive.
3. Shows the **Add-in Check**: either "All required add-ins are installed and active", or the add-ins it re-enabled (**Auto-Restored (Was Disabled)**) and the ones still missing (**Missing — Not Installed**), each with a **download ↗** link or "(no link)". Click **Continue**.
4. If some add-ins could not be disabled (for example, add-ins installed for all users need admin rights), lists them under **Some Add-ins Were Not Disabled**.
5. Shows a short summary for about 3 seconds, then closes: "Tab "<tab>" will be built.", a restart note when add-ins were changed, and a line for each required add-in that is not installed for this Revit version. A missing add-in never stops the load.

rst-c rebuilds the profile tab when Revit is next idle; no restart is needed. Your RSTify switches and the **Disable unused add-ins** setting are remembered for this profile in `%AppData%\RST\user_profile_prefs.json`. The loaded profile is recorded in `%AppData%\RST\active_profile.json` and loaded again automatically when Revit starts.

### Switching profiles

Open the Loader again, select another profile and click **Load Profile →**. rst-c removes the previous profile's tab and hidden-tab rules and builds the new one in a single pass. You do not need to close the model.

### Unloading

**Unload Profile** removes the loaded profile: "Unloading — A blank RST tab will replace the current one." shows for about 3 seconds and the window closes. With nothing loaded, it reports "No profile is currently loaded."

### Deleting a profile

Select the profile and click **Delete Profile**. The confirmation, **Delete "<profile>"?**, explains that this deletes your local copy and that the file shared by your admin is not affected. Its **Continue** button is red and **Cancel** is the default. Deleting the loaded profile unloads it first, then the window closes.

### Importing a profile

Click **+ Import from File** and choose a profile package (`.zip`) exported from the [Profile Builder](profile-builder.md). The profile appears as a new card. Importing a profile you already have replaces its card and refreshes its details. A file that is not a valid profile package shows **Import Failed** with the reason.

### Restoring add-ins

**Restore All Add-ins** re-enables every add-in rst-c has disabled (any `.addin.RSTdisabled` file is renamed back to `.addin`). It reports which add-ins will load after Revit restarts, or "No disabled add-ins found to restore."

## How it works

```mermaid
graph LR
  A["Open Loader"]:::class1 --> B["Select a profile"]:::class2 --> C["Load Profile →"]:::class1 --> D["Add-in Check"]:::class2 --> E["Ribbon rebuilds on idle"]:::class3
```

The Loader is a WPF window inside Revit's own process. The **Loader** button reads what the window needs from Revit first (the ribbon's tabs, the add-ins that are loaded, the running version), then opens it owned by Revit's main window. Loading a profile hands the switch to Revit's next idle event, because Revit's ribbon can only be changed from Revit's own UI thread at a safe moment.

Profiles are read from `%AppData%\RST\profiles\`; the loaded profile and your per-profile choices are written under `%AppData%\RST\`. Everything is per-user, and no admin rights are needed.

## Notes & limits

- Profiles are per-user. Each Windows user has their own `%AppData%\RST\` folder and sees only their own profiles.
- Only one profile tab is active at a time. Loading a profile replaces the previous one.
- **info ↗** and **download ↗** open only `http://` and `https://` links, in your default browser. A profile shared with you is not trusted to open anything else, so other links are hidden (the Add-in Check shows "(no link)").
- Disabling and re-enabling add-ins takes effect the next time Revit starts. Add-ins installed for all users (under `%ProgramData%`) usually need admin rights to rename, so rst-c may not be able to disable them.
- Upgrading from v2.x: earlier versions kept a browser cache in `%LocalAppData%\RST\WebView2`. rst-c no longer uses it, and the installer and the add-in leave it in place; you can delete the folder.
- Profiles shared from another machine (via the Builder's **Export ZIP**) are imported here. See [Profile Builder](profile-builder.md).
