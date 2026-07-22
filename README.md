---
title: rst-c
tags: [rst-c, revit, ribbon, profiles]
date: 2026-06-24
project: rst-c
purpose: Native Revit add-in for curated ribbon profiles
---

# rst-c

**Curated Revit ribbon profiles — one native add-in, no pyRevit, no external runtime.**

[![Latest release](https://img.shields.io/github/v/release/jedbjorn/rst-c?sort=semver&display_name=tag&label=latest%20release&color=2ea44f&style=flat-square)](https://github.com/jedbjorn/rst-c/releases/latest)
[![Release date](https://img.shields.io/github/release-date/jedbjorn/rst-c?style=flat-square&color=2ea44f)](https://github.com/jedbjorn/rst-c/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/jedbjorn/rst-c/total?style=flat-square&color=6b46c1)](https://github.com/jedbjorn/rst-c/releases)
[![License: MIT](https://img.shields.io/github/license/jedbjorn/rst-c?style=flat-square&color=blue)](https://github.com/jedbjorn/rst-c/blob/main/LICENSE)

[![Revit 2025 · 2026 · 2027](https://img.shields.io/badge/Revit-2025%20%C2%B7%202026%20%C2%B7%202027-0696D7?style=flat-square&logo=autodeskrevit&logoColor=white)](#install)
[![.NET 8 · 10](https://img.shields.io/badge/.NET-8.0%20%C2%B7%2010.0-512BD4?style=flat-square&logo=dotnet&logoColor=white)](#build)
[![Platform: Windows](https://img.shields.io/badge/platform-Windows-0078D6?style=flat-square&logo=windows&logoColor=white)](#install)
[![Open in md-converter](https://img.shields.io/badge/Open%20in-md--converter-6b46c1?style=flat-square)](https://md-converter.designs-os.com/?url=https://github.com/jedbjorn/rst-c/blob/main/README.md)

**Topics:** `revit` · `revit-addin` · `bim` · `ribbon` · `webview2` · `dotnet` · `wix` · `autodesk`

## Overview

rst-c (Revit Standardization Tool) is a native Revit add-in that gives admins a way to build curated ribbon toolbar profiles, and gives end users a one-click way to load them. Architects, modellers, BIM coordinators, and trainees rarely need the same ribbon — rst-c lets each role get the ribbon that fits the work, without hunting through tabs or relearning where commands live.

It ships as a single managed add-in for Revit 2025 through 2027, installs into the standard per-user add-in directory, and runs without pyRevit, Python, or any other external runtime.

### Download

Latest release: **[github.com/jedbjorn/rst-c/releases/latest](https://github.com/jedbjorn/rst-c/releases/latest)**

- `RST.msi` — Revit 2025 + 2026
- `RST-R27.msi` — Revit 2027

Per-user install; no admin rights required. Run the matching MSI, then launch Revit — the rst-c tab appears on the ribbon.

### Why rst-c

Revit's native ribbon assumes every user wants every tool, and every add-in vendor assumes their tab is the most important one. The result, on a real machine with five or six add-ins installed, is a ribbon dense enough that finding the right command is itself a task.

rst-c treats the ribbon as a curated surface. An admin assembles a profile — a tab full of panels full of tools, drawn from anywhere in Revit's command catalogue — and an end user loads that profile to get exactly that surface and nothing else. Profiles are JSON, profiles are shareable, profiles can be swapped live without restarting Revit, and everything that runs runs in-process inside Revit's own .NET host. There is no IPC, no second runtime, no GitHub clone step on the user's machine.

The port from the original pyRevit-based RST enables this: a single managed assembly can be code-signed, installed by an MSI, and shipped through normal IT channels.

## Features

### Profile Loader

The Loader is the end-user face of rst-c. It opens as a WebView2 window listing every profile installed on the machine, shows what is in each one before you commit, and applies the chosen profile to the ribbon with a single click. After Apply, the rst-c tab rebuilds with the profile's panels and tools in place; nothing else on the ribbon is touched. Profile switching is live — no Revit restart required.

**[Profile Loader docs →](https://github.com/jedbjorn/rst-c/blob/main/docs/profile-loader.md)**

### Profile Builder, Live Switching & Export

The Builder is the admin counterpart to the Loader. It scans the live Revit session for every command on every tab and presents them as a searchable catalogue you can drag into panels on a new profile tab. Each tool gets a name, an icon, and a panel assignment. The Builder also handles per-profile metadata — branding, colours, cleanup targets, required add-ins — and exports the finished profile as a self-contained zip that any rst-c install can import. Editing a profile and renaming it creates a copy, enabling flavours of the same profile.

**[Profile Builder docs →](https://github.com/jedbjorn/rst-c/blob/main/docs/profile-builder.md)**

### Custom URL Slots

A profile button can point at a URL, a `mailto:` address, a file path, or a UNC share, in addition to a Revit command. Bare hostnames and bare email addresses are normalised so they resolve cleanly through the user's default browser or mail client. This lets admins fold company resources — the wiki, the SharePoint library, the BIM standards PDF, the helpdesk inbox — into the same ribbon people already use.

**[Custom URL Slots docs →](https://github.com/jedbjorn/rst-c/blob/main/docs/custom-url-slots.md)**

### Appearance — Branding & Coloured Panels

Every profile tab can display an 85×85 branding image with rounded corners at the leading edge — typically a company logo, set once in the Builder and applied to all profiles thereafter. Each panel can carry a custom hex colour and opacity between 10% and 100%, drawn with ~5px rounded corners over Revit's native panel chrome. Logos travel inside the profile zip so a shared profile keeps its branding on any machine.

**[Appearance docs →](https://github.com/jedbjorn/rst-c/blob/main/docs/appearance.md)**

### Ribbon Tools — RSTify & Required Add-ins

RSTify is a tab-hiding mode: when enabled, rst-c hides every Revit and add-in tab the active profile did not draw from, leaving just the rst-c tab and any preserved tabs. It is per-profile and per-user — the admin sets a default in the Builder, the user can override from the Loader, and the ribbon icon shows the current state. Required Add-ins lets profiles declare the add-ins they depend on; rst-c auto-enables disabled ones and surfaces download links for missing ones on Apply.

**[Ribbon Tools docs →](https://github.com/jedbjorn/rst-c/blob/main/docs/ribbon-tools.md)**

### Health Tool

Health is a one-click workstation and Revit-session snapshot: CPU, RAM, GPU, disk, display, network, OS, the active model and its size, and any warnings the session has accumulated. It also offers a targeted junk file cleanup for Temp folders, the package cache, journal files, and the collaboration cache, with cleanup targets configurable per profile. A second **Activity** tab tracks how long the current session has run, how long the open model has been open, and per-file charts of daily session time, model open times, and sync durations. Everything runs locally; nothing is uploaded.

**[Health Tool docs →](https://github.com/jedbjorn/rst-c/blob/main/docs/health-tool.md)**

### Logging

rst-c writes Serilog rolling files under `%AppData%\RST\logs\`. Both C#-side and WebView2-side events land in the same file via a `log_event` bridge, so a single log captures the full picture when something misbehaves. The active log is the first place to look when a profile behaves unexpectedly.

**[Logging docs →](https://github.com/jedbjorn/rst-c/blob/main/docs/logging.md)**

## Install

MSIs ship as GitHub Release assets. The unified `RST.msi` covers Revit 2025 + 2026; `RST-R27.msi` covers Revit 2027.

1. Grab the latest from the [Releases](https://github.com/jedbjorn/rst-c/releases) page.
2. Close Revit (the engine DLLs are locked while it is running).
3. Run the MSI — double-click or `msiexec /i RST.msi`.
4. Launch Revit. The RST tools appear on the Add-Ins tab.

Both installers are per-user — no admin elevation required. User data (profiles, logs, branding, active-profile state) lives at `%AppData%\RST\` and is preserved across reinstalls.

| Revit version | Installer | TFM |
|---|---|---|
| 2025 | `RST.msi` | `net8.0-windows` |
| 2026 | `RST.msi` | `net8.0-windows` |
| 2027 | `RST-R27.msi` | `net10.0-windows` |

**If you previously installed an older layout:**

Pre-RST-033 installs bundled the engine in the addins directory; pre-RST-037 used a flat `app\` layout. Clear the leftovers once:

```powershell
Remove-Item -Recurse -Force "$env:APPDATA\Autodesk\Revit\Addins\2025\RST" -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force "$env:APPDATA\RST\app" -ErrorAction SilentlyContinue
```

## Build

Local builds need the .NET 8 SDK. Revit 2027 additionally requires the .NET 10 SDK. Revit API references resolve from NuGet.

```bash
dotnet restore
dotnet build -c "Debug R25"     # Revit 2025
dotnet build -c "Debug R26"     # Revit 2026
dotnet build -c "Debug R27"     # Revit 2027 — needs .NET 10 SDK
```

To produce a runnable bundle for a single Revit major:

```bash
build/stage.sh R25              # Release config by default → build/R25/{addins,app}
build/stage.sh R26 Debug        # Debug build for VM diagnostics
```

For an MSI build, run `dotnet build installer\RST.Installer.wixproj` (unified R25 + R26) or `dotnet build installer-r27\RST.Installer.R27.wixproj` on Windows after staging.

## License

MIT
