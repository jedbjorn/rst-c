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

**Topics:** `revit` · `revit-addin` · `bim` · `ribbon` · `wpf` · `dotnet` · `wix` · `autodesk`

![A loaded rst-c profile on the Revit ribbon: company branding at the leading edge of the tab, followed by coloured panels grouping the profile's tools](https://raw.githubusercontent.com/jedbjorn/rst-c/main/docs/images/ribbon-profile-loaded.png)

## Overview

rst-c (Revit Standardization Tool) is a native Revit add-in that gives admins a way to build curated ribbon toolbar profiles, and gives end users a one-click way to load them. Architects, modellers, BIM coordinators, and trainees rarely need the same ribbon — rst-c lets each role get the ribbon that fits the work, without hunting through tabs or relearning where commands live.

It ships as a single managed add-in for Revit 2025 through 2027, installs into the standard per-user add-in directory (or once for everyone on the PC, from v3.1), and runs without pyRevit, Python, or any other external runtime. Its windows are native WPF: they open over Revit, belong to it, and follow Revit's light or dark theme.

**Runtime model:** the add-in runs inside Revit, plus one small tray app (`RST.Tray.exe`, from v3.1) in the notification area for updates and add-in switching outside Revit. There is no second runtime: both use the .NET that Revit already installs. **[Tray docs →](https://github.com/jedbjorn/rst-c/blob/main/docs/tray.md)**

### Download

Latest release: **[github.com/jedbjorn/rst-c/releases/latest](https://github.com/jedbjorn/rst-c/releases/latest)**

- `RST.msi` — Revit 2025, 2026 and 2027, for your Windows account
- `RST-Setup.exe` — the same rst-c, plus optional Designs/OS add-ins ([Gantt-It](https://github.com/jedbjorn/gantt-it), [Splash-It](https://github.com/jedbjorn/splash-it)) as checkboxes
- `RST-Machine.msi` — rst-c once for everyone on the PC, for IT (from v3.1; needs an administrator)

Per-user install; no admin rights required. Run `RST.msi` or `RST-Setup.exe`, then launch Revit — the RST tools appear on the Add-Ins tab. See [Install modes](#install-modes) for the per-machine option.

### Why rst-c

Revit's native ribbon assumes every user wants every tool, and every add-in vendor assumes their tab is the most important one. The result, on a real machine with five or six add-ins installed, is a ribbon dense enough that finding the right command is itself a task.

rst-c treats the ribbon as a curated surface. An admin assembles a profile — a tab full of panels full of tools, drawn from anywhere in Revit's command catalogue — and an end user loads that profile to get exactly that surface and nothing else. Profiles are JSON, profiles are shareable, profiles can be swapped live without restarting Revit, and everything that touches Revit runs in-process inside Revit's own .NET host. There is no IPC, no GitHub clone step on the user's machine.

The port from the original pyRevit-based RST enables this: a single managed assembly can be code-signed, installed by an MSI, and shipped through normal IT channels. Since v3.0 the windows are native WPF too, with no embedded browser runtime to install or keep up to date.

## Features

### Profile Loader

The Loader is the end-user face of rst-c. It is a native window over Revit that lists every profile installed for the user, previews what is in each one before you commit, and loads the chosen profile onto the ribbon with **Load Profile →**. Before it closes, an Add-in Check reports the add-ins the profile needs. The profile tab then rebuilds with its panels and tools in place; nothing else on the ribbon is touched. Profile switching is live — no Revit restart required.

![The Profile Loader: installed profiles on the left with the loaded one ringed in green; for the selected profile, a preview of its ribbon tab, its RSTify tab-hiding switches and the add-ins it requires](https://raw.githubusercontent.com/jedbjorn/rst-c/main/docs/images/loader-profile-selector.png)

**[Profile Loader docs →](https://github.com/jedbjorn/rst-c/blob/main/docs/profile-loader.md)**

### Profile Builder, Live Switching & Export

The Builder is the admin counterpart to the Loader. It scans the live Revit session for every command on every tab and presents them as a searchable catalogue you can drag into panels on a new profile tab. Each tool gets a name, an icon from rst-c's built-in icon pack, and a panel assignment. The Builder also sets panel colours, RSTify presets, junk cleanup targets and the machine's company branding, works out the profile's required add-ins, and exports the finished profile as a self-contained `.zip` that any rst-c install can import. Editing a profile and renaming it creates a copy, enabling flavours of the same profile, and it asks before discarding unsaved changes.

![The Profile Builder: the searchable Revit command catalogue on the left, the profile's panels and their tools in the centre, properties on the right, and a live preview of the resulting tab along the bottom](https://raw.githubusercontent.com/jedbjorn/rst-c/main/docs/images/builder-overview.png)

**[Profile Builder docs →](https://github.com/jedbjorn/rst-c/blob/main/docs/profile-builder.md)**

### Custom URL Slots

A profile button can point at a URL, a `mailto:` address or a `file://` path, in addition to a Revit command. Bare hostnames and bare email addresses are normalised so they resolve cleanly through the user's default browser or mail client. This lets admins fold company resources — the wiki, the SharePoint library, the BIM standards PDF, the helpdesk inbox — into the same ribbon people already use.

**[Custom URL Slots docs →](https://github.com/jedbjorn/rst-c/blob/main/docs/custom-url-slots.md)**

### Appearance — Branding & Coloured Panels

Every profile tab can display an 85×85 branding image with rounded corners at the leading edge — typically a company logo, set once per machine in the Builder and shown on every profile tab. Each panel can carry its own colour, drawn at the profile's opacity (10% to 100%) with ~5px rounded corners over Revit's native panel chrome. Logos travel inside the profile zip so a shared profile keeps its branding on any machine.

**[Appearance docs →](https://github.com/jedbjorn/rst-c/blob/main/docs/appearance.md)**

### Ribbon Tools — RSTify & Required Add-ins

RSTify is a tab-hiding mode: when enabled, rst-c hides the Revit and add-in tabs chosen for the active profile, from those it does not draw commands from, so the ribbon narrows to the profile's tab and the tabs it needs. It is per-profile and per-user — the admin sets defaults in the Builder, the user adopts or changes them in the Loader, and the ribbon icon shows the current state. Required Add-ins lets profiles declare the add-ins they depend on; when a profile is loaded, rst-c re-enables disabled ones and shows download links for missing ones.

**[Ribbon Tools docs →](https://github.com/jedbjorn/rst-c/blob/main/docs/ribbon-tools.md)**

### Health Tool

Health is a one-click workstation and Revit-session snapshot: CPU, RAM, GPU, disk, display, network, OS, the active model and its size, and any warnings the session has accumulated. It also offers a targeted junk file cleanup for Temp, the package cache, journal files, the collaboration cache, the recent-file list and rst-c's logs: fixed preset locations, which each profile can switch on or off. A second **Activity** tab tracks how long the current session has run, how long the open model has been open, and per-file charts of daily session time, model open times, and sync durations. Everything runs locally; nothing is uploaded.

**[Health Tool docs →](https://github.com/jedbjorn/rst-c/blob/main/docs/health-tool.md)**

### Logging

rst-c writes a Serilog session log under `%AppData%\RST\logs\` for every Revit launch. The engine and rst-c's windows run in the same process and log to the same file, so a single log captures the full picture when something misbehaves. The latest log is the first place to look when a profile behaves unexpectedly.

**[Logging docs →](https://github.com/jedbjorn/rst-c/blob/main/docs/logging.md)**

## Install

`RST.msi` ships as a GitHub Release asset and covers Revit 2025, 2026 and 2027 in one installer. It installs RST only for the Revit years it finds on the computer; if you install another Revit year later, run the same `RST.msi` again and choose **Repair**.

1. Grab the latest from the [Releases](https://github.com/jedbjorn/rst-c/releases) page.
2. Close Revit (the engine DLLs are locked while it is running).
3. Run the MSI — double-click or `msiexec /i RST.msi`.
4. Launch Revit. The RST tools (Builder, Loader, RSTify and Health) appear on the Add-Ins tab.

**Or use `RST-Setup.exe`**, the same release's setup, which installs rst-c and offers the Designs/OS companion add-ins:

1. Close Revit.
2. Run `RST-Setup.exe`. Choose **Just for me (recommended)** (the default) or **Everyone on this PC** ([Install modes](#install-modes)), accept the license, and tick any companion add-ins you want. They are unticked by default.
3. Launch Revit.

| Checkbox | Add-in | Silent variable |
|---|---|---|
| Gantt-It 1.1.0 | [Gantt-It](https://github.com/jedbjorn/gantt-it): Gantt charts as native Revit legend views | `InstallGanttIt=1` |
| Splash-It 1.1.0 | [Splash-It](https://github.com/jedbjorn/splash-it): colors Revit view tabs by file | `InstallSplashIt=1` |

A ticked add-in is installed from its own released MSI, unchanged. It is a separate product: it gets its own entry in Settings → Apps and upgrades through its own installer. An add-in that is already installed at the same or a newer version is left as it is; an older one is upgraded.

**Companions are permanent.** Uninstalling RST (the "RST" entry in Settings → Apps, or `RST-Setup.exe /uninstall /quiet /norestart`) leaves them installed; remove each one from Settings → Apps.

Silent install for IT, rst-c plus both add-ins (leave a variable out for an add-in you do not want). Always pass `/norestart`; without it the setup may restart the computer by itself, with no prompt. Run it with Revit closed:

```powershell
RST-Setup.exe /quiet /norestart InstallGanttIt=1 InstallSplashIt=1
```

Use either `RST.msi` or `RST-Setup.exe` on a computer, not both. If the **same version** of `RST.msi` is already installed, the setup adopts it: Settings → Apps shows two "RST" entries, and removing either one removes rst-c. An older `RST.msi` (for example v2.2.0) is upgraded in place, leaving one entry. Details: [`installer-bundle/README.md`](installer-bundle/README.md).

The installer is per-user — no admin elevation required. User data (profiles, logs, branding, active-profile state) lives at `%AppData%\RST\` and is preserved across reinstalls.

### Install modes

From v3.1 rst-c installs one of two ways. Both are the same RST, with the same tray and the same per-user add-in switching. Choose one per PC.

| | Just for me (recommended) | Everyone on this PC |
|---|---|---|
| Installer | `RST.msi`, or `RST-Setup.exe` "Just for me" | `RST-Machine.msi`, or `RST-Setup.exe` "Everyone on this PC" |
| Administrator | never | once, at install |
| Program files | `%AppData%\RST\`, `%LocalAppData%\Programs\RST\` | `C:\Program Files\RST\` |
| Settings → Apps | "RST" | "RST (all users)" |
| Updates | each user, from the tray | an administrator deploys the new `RST-Machine.msi` |

Each user still has their own profiles, settings, tray and add-in choices. A per-machine install shares only the program files and grants nothing extra: no permission changes and no service. Gantt-It and Splash-It always install per user; on a per-machine PC, each user adds them from the RST tray.

```powershell
# IT: for everyone, silently (elevated)
msiexec /i RST-Machine.msi /qn /norestart
RST-Setup.exe /quiet /norestart InstallScope=machine InstallGanttIt=1
```

**Both installed:** if a user also has their own `RST.msi`, Revit loads their own copy. "Everyone on this PC" in `RST-Setup.exe` removes the installing user's own copy first. A bare `RST-Machine.msi` refuses to install until that copy is removed. Any other user is told once by their tray how to remove theirs.

**Switching back** to just for me is uninstall, then install: remove "RST (all users)", then install `RST.msi`. Details: [`installer/README.md`](installer/README.md#per-machine-install-rst-machinemsi) and [docs/tray.md](docs/tray.md#installed-for-everyone-on-this-pc-it).

| Revit version | Installer | TFM |
|---|---|---|
| 2025 | `RST.msi` | `net8.0-windows` |
| 2026 | `RST.msi` | `net8.0-windows` |
| 2027 | `RST.msi` | `net10.0-windows` |

**If you installed the Revit 2027 preview:** releases v1.0.0 to v2.2.0 shipped Revit 2027 as a separate preview MSI. Revit 2027 is now included in `RST.msi`, and installing it removes the "RST (Revit 2027 preview)" entry from Settings → Apps automatically, including under `msiexec /qn`. Your profiles and settings are kept.

**If you previously installed an older layout:**

Pre-RST-033 installs bundled the engine in the addins directory; pre-RST-037 used a flat `app\` layout. Clear the leftovers once:

```powershell
Remove-Item -Recurse -Force "$env:APPDATA\Autodesk\Revit\Addins\2025\RST" -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force "$env:APPDATA\RST\app" -ErrorAction SilentlyContinue
```

## Compatibility

rst-c supports **every update** of Revit 2025, 2026 and 2027. Each year is built against that year's earliest update (the "API floor"), so it uses no Revit API that a later update added, and it runs on the first update as well as the latest.

| Revit year | Supported updates | Built against | Support |
|---|---|---|---|
| 2025 | every update | 2025.0.2 | **Partial.** Assumed to behave like 2026 and takes the same code paths; not on the test machine, so it is verified by hand after each public release |
| 2026 | every update | 2026.0.4 | Full |
| 2027 | every update | 2027.0.0 | Full |

Years before 2025 are not supported.

Revit's journal file logs `Assembly version conflict in some references in RST.Bootstrap.dll` at every start, naming RevitAPI and RevitAPIUI. This is Revit's normal notice for any add-in built against a different update than the one running. It is not an error, and rst-c loads normally.

**Raising the floor** is a deliberate change: CI (`build/check-revit-api-floor.py`) fails any build that references a newer Revit update than the floor. If a feature needs newer API, raise the pins and the floor in the same pull request and update this table, so dropping the older updates is a visible decision. The steps are in [docs/DEVELOPING.md](docs/DEVELOPING.md#revit-api-floor).

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

For an MSI build, stage all three majors (`build/stage.sh R25`, `R26`, `R27`), then run `dotnet build installer\RST.Installer.wixproj` on Windows. It produces one `RST.msi` covering Revit 2025–2027. Add `-p:RstScope=machine` for `RST-Machine.msi`.

See [docs/DEVELOPING.md](docs/DEVELOPING.md) for the WPF architecture, the vendored family UI kit, and the screenshot tests.

## License

MIT
