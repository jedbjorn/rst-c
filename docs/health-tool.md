---
title: Health Tool
tags: [rst-c, health, diagnostics, cleanup]
date: 2026-06-24
project: rst-c
purpose: Guide to the Health system snapshot and junk file cleanup
---

# Health Tool

[![Open in md-converter](https://img.shields.io/badge/Open%20in-md--converter-6b46c1?style=flat-square)](https://md-converter.designs-os.com/?url=https://github.com/jedbjorn/rst-c/blob/main/docs/health-tool.md)

## Overview

Health is a one-click workstation and Revit-session snapshot. It collects hardware readings, OS details, the active Revit model, and session warnings, then offers a targeted junk file cleanup for common Revit bloat directories. Everything runs locally — nothing is uploaded or sent anywhere.

The intent is to give a user something concrete to share with a BIM lead when Revit is misbehaving, without asking them to open Task Manager, dig through Windows settings, or know what a journal file is.

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

## Notes & limits

- The snapshot is point-in-time. Values like RAM usage and CPU usage reflect the moment Scan System was clicked; they are not live readings.
- Journal paths and collaboration cache paths are resolved per installed Revit major version (2025, 2026, 2027). Only majors that exist on the machine are checked.
- Cleaning Temp (`%LocalAppData%\Temp`) deletes all files in that directory, not just Revit files. This is the same directory Windows uses for all application temp files; the cleanup mirrors what a manual `%temp%` cleanup would do.
- The recent file list cleanup removes recent file entries from Revit.ini. Revit will rebuild the list as you open files; the cleanup just clears the current history.
- Nothing in the Health tool uploads data, phones home, or requires network access.
