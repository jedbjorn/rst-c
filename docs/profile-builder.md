---
title: Profile Builder
tags: [rst-c, builder, profiles, admin, export, import]
date: 2026-06-24
project: rst-c
purpose: Admin guide to building, managing, exporting, and importing profiles
---

# Profile Builder

[![Open in md-converter](https://img.shields.io/badge/Open%20in-md--converter-6b46c1?style=flat-square)](https://md-converter.designs-os.com/?url=https://github.com/jedbjorn/rst-c/blob/main/docs/profile-builder.md)

## Overview

The Builder is the admin counterpart to the Loader. It scans the live Revit session for every command on every tab — native Revit commands, add-in commands, pyRevit buttons, anything Revit's ribbon API knows about — and presents them as a searchable catalogue you can drag into panels on a new profile tab. The finished profile exports as a self-contained zip that any rst-c install can import.

Live profile switching (no Revit restart on apply) is part of the same system — the profile the Builder produces is what the Loader applies live. Export and import are the distribution mechanism.

## Building a profile

### Setting the profile name and tab title

Every profile has a **profile name** (shown in the Loader's picker) and a **tab title** (the ribbon tab label). These are set at the top of the Builder. If you rename a profile while editing an existing one, rst-c saves it as a new copy — the original is unchanged. This is a deliberate "fork" mechanism for creating variants of an existing profile.

### Assembling the command catalogue

When the Builder opens, rst-c scans every tab and panel in the live Revit session and indexes all commands. The catalogue is searchable by name. Commands from all sources appear together — Revit built-ins, installed add-ins, pyRevit buttons.

Drag any command from the catalogue into a panel slot on the right. Each tool in a panel gets:

- **Name** — displayed on the ribbon button; defaults to the source command's name, editable.
- **Icon** — chosen from a vendored 48-icon pack, or auto-derived from the source command's own icon.
- **Panel assignment** — which panel on the profile tab the tool appears in.

### Naming and colouring panels

Panels are named individually. Each panel can also carry a hex colour and an opacity between 10% and 100% — see [Appearance](appearance.md) for how colours render on the ribbon.

### Setting profile defaults (presets)

The Builder lets admins set default behaviours that the Loader uses when a user first applies the profile:

- **RSTify** — whether tab-hiding is on by default and which tabs to hide. Users can override this in the Loader. See [Ribbon Tools](ribbon-tools.md).
- **Disable non-required add-ins** — whether add-ins not listed as required are disabled on Apply.

These defaults are stored in the profile's `presets` field. The user's own override (stored in `active_profile.json`) takes precedence once they have loaded the profile at least once.

### Required add-ins

Profiles can declare the add-ins they depend on. rst-c checks these on Apply and either auto-enables ones that are installed but disabled, or surfaces a download link for ones not found on the machine. See [Ribbon Tools](ribbon-tools.md) for how the matching and auto-enable work.

### Branding

A logo image can be set for the machine — it appears in the branding panel at the leading edge of every profile tab. Set it once in the Builder; it applies to all profiles created or edited after that point. See [Appearance](appearance.md).

## Managing profiles

```linear
Create new profile :::class1 -> Name + assemble panels :::class2 -> Set presets + required add-ins :::class2 -> Export zip :::class3 -> Distribute to users :::class4
```

### Editing an existing profile

Open the Builder and choose a profile from the drop-down at the top left. The profile's panels, tools, and settings load into the Builder. Edit as needed. **Saving with the same name overwrites the profile in place; renaming it saves a new copy.** This lets you maintain flavours of the same profile side by side.

### Deleting a profile

Profile deletion is done from the Loader — select the profile and click **Delete profile**. There is no delete action in the Builder itself.

## Export and import

### Exporting a profile

Click **Export** in the Builder. rst-c packages the profile as a `.rstprofile` zip containing:

- `profile.json` — the full profile definition
- `assets/branding.png` — the resolved branding logo (per-profile override if set, otherwise the machine-wide default)

The exported zip is self-contained. The recipient does not need to set up branding separately — the logo travels with the profile.

### Importing a profile

Import is done through the Loader. Open the Loader, click **Import**, and select the `.rstprofile` file. rst-c extracts the profile JSON and branding assets into `%AppData%\RST\profiles\` and `%AppData%\RST\<profile-id>\` respectively. The profile then appears in the Loader's list ready to apply.

> [!class2]
> Profiles are saved to `%AppData%\RST\profiles\` on creation. Imported zips are extracted there automatically — you do not need to manually place or rename any files.

## Notes & limits

- The Builder requires a live Revit session to scan commands. Open a project (even a blank one) before building a profile if you want add-in commands to appear in the catalogue.
- A profile's `schemaVersion` field ensures forward compatibility. Older pre-port pyRevit profiles (no `schemaVersion` field, treated as v0) can be loaded and re-exported.
- The `min_version` field on a profile records the minimum rst-c runtime required to apply it. Profiles built with newer features may not load on older installs.
- Stacks (grouped button stacks on a panel) are supported in the profile schema; the Builder produces them when tools are grouped.
