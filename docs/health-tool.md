---
title: Health Tool
tags: [rst-c, health, diagnostics, cleanup, activity, telemetry]
date: 2026-06-24
project: rst-c
purpose: Guide to the Health system snapshot and junk file cleanup
---

# Health Tool

[![Open in md-converter](https://img.shields.io/badge/Open%20in-md--converter-6b46c1?style=flat-square)](https://md-converter.designs-os.com/?url=https://github.com/jedbjorn/rst-c/blob/main/docs/health-tool.md)

## Overview

Health is a one-click workstation and Revit-session snapshot. It collects hardware readings, OS details, the active Revit model, and session warnings, then offers a targeted junk file cleanup for common Revit bloat directories. Everything runs locally — nothing is uploaded or sent anywhere.

The intent is to give a user something concrete to share with a BIM lead when Revit is misbehaving, without asking them to open Task Manager, dig through Windows settings, or know what a journal file is.

The window has two tabs:

- **Health** — the system snapshot and junk file cleanup (below).
- **Activity** — session timing and per-file history charts ([Activity tab](#activity-tab)).

https://github.com/user-attachments/assets/01fb84a3-a340-4a91-bba7-7936da277014

## System snapshot

### What is captured

Clicking **Scan System** in the Health tool captures a point-in-time snapshot of the workstation and the running Revit session:

```stats
:::class1
value: CPU
label: Processor
description: Name, logical/physical cores, current usage %
:::class2
value: RAM
label: Memory
description: Total, used, available MB and usage %
:::class1
value: GPU
label: Graphics
description: Name, driver version, VRAM total
:::class2
value: Disk
label: Storage
description: Total, used, available GB, type, bus type
:::class3
value: Display
label: Monitors
description: Monitor count, primary resolution
:::class4
value: Network
label: Connection
description: Adapter name, type, speed Mbps
```

In addition to hardware, the snapshot includes:

- **OS** — Windows version, release, and build number
- **Revit session** — Revit version and build, Windows username, hardware acceleration state
- **Active model** — file name, path, and size on disk
- **Warnings** — total warning count and breakdown by severity level

### Sharing a snapshot

The snapshot is displayed in the Health viewer. It can be copied or exported and sent to a BIM lead or support contact. Because the snapshot includes the model path and Revit build, the recipient has enough context to reproduce the environment without a back-and-forth.

## Junk file cleanup

### What can be cleaned

Health offers cleanup for directories that Revit and Windows accumulate over time and that rarely need to be kept:

| Target | Path | What is removed |
|---|---|---|
| Temp files | `%LocalAppData%\Temp` | All files and subdirectories |
| Revit package cache | `%LocalAppData%\Autodesk\Revit\PacCache` | All files and subdirectories |
| Journal files | `%ProgramData%\Autodesk\Revit\YYYY\Journals\` | Per Revit major version |
| Collaboration cache | `%ProgramData%\Autodesk\Revit\YYYY\CollaborationCache\` | Per Revit major version |
| Recent file list | `%AppData%\Autodesk\Revit\Autodesk Revit YYYY\Revit.ini` | Strips `FileN=` entries from `[Recent File List]` |

The cleanup is selective — only the targets you confirm are cleaned. Targets are shown with a file count and on-disk size before you run the cleanup, so the user knows what will be removed.

### Per-profile cleanup targets

The cleanup target list is configurable per profile. An admin can enable or disable individual targets in the Builder, and can add custom directory paths. If the active profile has zero enabled targets, the cleanup section shows nothing. Profiles created before RST-031 use the built-in defaults.

> [!class4]
> Locked files (in use by a running Revit or another process) are skipped individually. A single locked file does not abort the rest of the cleanup. The count of skipped files is reported after the run.

### How the cleanup runs

```linear
Select targets to clean :::class1 -> Confirm :::class2 -> Files deleted, locked files skipped :::class3 -> Summary of deleted and skipped counts :::class4
```

For directory targets, rst-c walks the directory tree and deletes files and subdirectories. For the recent file list, rst-c reads `Revit.ini` (preserving its UTF-16 LE encoding), strips `FileN=` lines from the `[Recent File List]` section, and rewrites the file. Revit must not be holding the ini file open, or the write is skipped.

## Activity tab

The **Activity** tab turns the passive session into something you can look at: how long you have been working, how long the current model has been open, and how the current file has behaved over time. It reads entirely from data recorded locally on this machine — see [Data & privacy](#data--privacy).

### Current session

At the top of the tab, a live readout ticks every second:

- **Session time** — elapsed time since Revit started this rst-c session, shown as `HH:MM:SS`.
- **This file open** — how long the currently active model has been open, updated as you switch documents.

The timer runs only while the Activity tab is open; leaving the tab stops it.

### Per-file history charts

Below the live readout, three charts plot the **currently open model's** history. A range selector — **7D · 1M · 3M · 6M** — sets the window for all three at once (7 days by default).

```stats
:::class1
value: Session time
label: this file
description: Hours the model was open, one point per calendar day across the range
:::class2
value: Opening time
label: this file
description: How many seconds each open of the model took, one point per open
:::class3
value: Sync history
label: this file
description: Duration of each synchronize-with-central, workshared and cloud models only
```

Chart behaviour:

- **Session time** plots one point per calendar day, including days with zero recorded time, so a gap reads as a gap rather than a flat line. If the range holds no recorded time, the chart shows an empty state instead of a line at zero.
- **Opening time** and **Sync history** plot one point per event. With a single data point they show a dot and a "not enough history yet" hint; with two or more they draw a smoothed curve. Hovering any point shows its exact timestamp and value.
- **Sync history** only applies to workshared and cloud models. For a single-user local file it shows "Not a workshared model".
- Charts key off the model's identity. A brand-new model that has never been saved has no usable identity key yet — save it once and its history begins to accumulate.

### Data & privacy

The Activity tab is a view onto rst-c's local session telemetry. Session, open, and sync events are written to a local outbox under `%AppData%\RST\` on this machine; the charts aggregate those files. **Nothing is uploaded or sent anywhere** — the same local-only guarantee as the rest of the Health tool.

A **Collection** toggle in the tab footer controls whether these events are recorded at all. Next to it, a status line reports the local outbox state — file count, on-disk size, and the date of the oldest recorded event. Turning collection off stops new events from being recorded; existing history remains until it ages out.

## Notes & limits

- The snapshot is point-in-time. Values like RAM usage and CPU usage reflect the moment Scan System was clicked; they are not live readings.
- Activity charts cover only the **currently open model**. Close it or open a different one and the charts follow the active file; with no model open, the charts show a "No model open" state.
- Activity history is per-machine and local. It does not follow a user to another workstation, and it is not shared between machines.
- Journal paths and collaboration cache paths are resolved per installed Revit major version (2025, 2026, 2027). Only majors that exist on the machine are checked.
- Cleaning Temp (`%LocalAppData%\Temp`) deletes all files in that directory, not just Revit files. This is the same directory Windows uses for all application temp files; the cleanup mirrors what a manual `%temp%` cleanup would do.
- The recent file list cleanup removes recent file entries from Revit.ini. Revit will rebuild the list as you open files; the cleanup just clears the current history.
- Nothing in the Health tool uploads data, phones home, or requires network access.
