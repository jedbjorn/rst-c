---
title: Health Tool
tags: [rst-c, health, diagnostics, cleanup, activity, telemetry]
date: 2026-10-31
project: rst-c
purpose: Guide to the Health system snapshot and junk file cleanup
---

# Health Tool

[![Open in md-converter](https://img.shields.io/badge/Open%20in-md--converter-6b46c1?style=flat-square)](https://md-converter.designs-os.com/?url=https://github.com/jedbjorn/rst-c/blob/main/docs/health-tool.md)

## Overview

Health is a one-click workstation and Revit-session snapshot. It collects hardware readings, OS details, the active Revit model and its warnings, then offers a targeted junk file cleanup for common Revit bloat. Everything runs locally: nothing is uploaded or sent anywhere.

The intent is to give a user something concrete to share with a BIM lead when Revit is misbehaving, without asking them to open Task Manager, dig through Windows settings, or know what a journal file is.

Click **Health** on the **RST** panel of the **Add-Ins** tab. The window, titled **RST — Health**, opens centred over Revit. It belongs to Revit (no taskbar button of its own; Revit waits until you close it) and uses Revit's light or dark theme as it was when the window opened. **Escape** closes it, as **Close** does, except while a scan or a clean is running or a dialog is open over it.

The header reads "RST · Health" and holds two tabs, plus the **Device** name and the time the snapshot was **Captured**:

- **Health:** the system snapshot and junk file cleanup (below).
- **Activity:** session timing and per-file history charts ([Activity tab](#activity-tab)).

## System snapshot

![The Health tab after a scan, with all four sections opened: Hardware (user, CPU, RAM, GPUs, disk, the junk rows and Junk total, with a red dot for high RAM use), Display & Network, Revit (version, username, hardware acceleration, model, warnings) and Operating System, above Clean Junk Files, Close and Scan System](https://raw.githubusercontent.com/jedbjorn/rst-c/main/docs/images/health-system-snapshot.png)

### Scanning

The Health tab shows four sections: **Hardware**, **Display & Network**, **Revit** and **Operating System**. They start collapsed; click one to open it. The coloured dot beside the Hardware, Display & Network and Revit headings sums up that section's readings: green when they are fine, amber or red when something needs attention (low free disk space, high RAM or CPU use, a large model, many warnings).

Click **Scan System** to take a new snapshot. While the scan runs, the button shows a spinner and "Scanning…", and the other buttons and the tabs are disabled. The scan runs in the background, so Revit and the window stay responsive. The last snapshot is saved to `%AppData%\RST\health_scan.json` and shown again the next time Health opens, with its Captured time. Before the first scan, every value reads "—".

### What is captured

```stats
:::class1
value: CPU
label: Processor
description: Name, physical/logical cores, current usage %
:::class2
value: RAM
label: Memory
description: Total, used and available, usage %
:::class1
value: GPU
label: Graphics
description: Name, driver version, VRAM
:::class2
value: Disk
label: Storage
description: Total, used and free space on C:, type, bus type
:::class3
value: Display
label: Monitors
description: Monitors on the desktop, primary resolution
:::class4
value: Network
label: Connection
description: Adapter name, type, speed
```

In addition to hardware, the snapshot includes:

- **Junk:** the size and file count of each cleanup target, and a **Junk total** of what Clean Junk Files would reclaim (under Hardware).
- **Revit:** the Revit version and build, the Windows username, and whether hardware acceleration is on.
- **Model:** the active model's name and size on disk.
- **Warnings:** the model's warning count, with a breakdown by severity.
- **Operating System:** the Windows name and build.

### Sharing a snapshot

Health has no export button. To share a snapshot with a BIM lead or support contact, send a screenshot of the window or the `%AppData%\RST\health_scan.json` file. Because the snapshot includes the model path and the Revit build, the recipient has enough context to reproduce the environment without a back-and-forth.

## Junk file cleanup

### What can be cleaned

The cleanup targets are fixed presets. A profile can turn them on or off or remove them (in the Profile Builder, on the Profile pane's [Junk cleanup](profile-builder.md#junk-cleanup) tab), but no profile can point the cleanup at any other folder. A profile that names a path outside this list is ignored for safety.

| Target | Path | What is removed |
|---|---|---|
| Temp | `%LocalAppData%\Temp` | All files and subfolders |
| PacCache | `%LocalAppData%\Autodesk\Revit\PacCache` | All files and subfolders |
| Journals | `%LocalAppData%\Autodesk\Revit\Autodesk Revit <year>\Journals` | All files and subfolders, for each installed Revit year |
| Collaboration Cache | `%LocalAppData%\Autodesk\Revit\Autodesk Revit <year>\CollaborationCache` | All files and subfolders, for each installed Revit year |
| Recent File List | `%AppData%\Autodesk\Revit\Autodesk Revit <year>\Revit.ini` | The `FileN=` entries of the `[Recent File List]` section, for each Revit year |
| RST Logs | `%AppData%\RST\logs` | rst-c's own log files |

### Which targets a profile offers

The targets offered come from the loaded profile: its enabled targets, in its order and with its names. With no profile loaded, or a profile made before cleanup targets existed (before RST-031), Health offers the full preset list. An admin chooses a profile's targets in the [Profile Builder](profile-builder.md#junk-cleanup).

### How the cleanup runs

```linear
Clean Junk Files :::class1 -> Choose targets, Clean Selected :::class2 -> Confirm :::class2 -> Files deleted, locked files skipped :::class3 -> Summary per target :::class4
```

1. Click **Clean Junk Files**. A dialog lists the targets, each with a switch and its path, all switched on. With none configured, it reads "No cleanup targets configured".

   ![The Clean Junk Files dialog: the six preset targets (Temp, PacCache, Journals, Collaboration Cache, Recent File List, RST Logs), each with its path and a switch turned on, above Cancel and Clean Selected](https://raw.githubusercontent.com/jedbjorn/rst-c/main/docs/images/health-junk-cleanup.png)

2. Switch off any target you want to keep and click **Clean Selected** (or **Cancel**). With every target off, rst-c tells you "No targets were chosen for cleanup."
3. Confirm **Delete selected junk?**. **Cancel** is the default; **Continue** deletes.
4. "Cleaning" shows while the files are deleted in the background.
5. A **Cleanup complete** message lists each target, for example "Journals: 24 deleted, 2 locked/in-use".

The dialog starts with every target switched on each time it opens. The junk sizes on the Health tab are not refreshed until the next **Scan System**.

> [!class4]
> Locked files (in use by Revit or another program) are skipped one by one. A single locked file does not stop the rest of the cleanup, and the number skipped is reported for each target.

For folder targets, rst-c walks the folder and deletes its files and subfolders. For the recent file list, rst-c reads `Revit.ini` (keeping its UTF-16 LE encoding), removes the `FileN=` lines from the `[Recent File List]` section and writes the file back. If Revit is holding the file open, that write is skipped.

## Activity tab

The **Activity** tab turns the passive session into something you can look at: how long you have been working, how long the current model has been open, and how the current file has behaved over time. It reads only data recorded locally on this machine; see [Data & privacy](#data--privacy).

![The Health tool's Activity tab: six stat cards (Session Timer, Session Total, Longest Sync, Longest Open Time, Warning Count, File Size), the 7D / 1M / 3M / 6M range switch, the session-time, opening-time and sync-history charts for the current model, and the Collection switch with its status line](https://raw.githubusercontent.com/jedbjorn/rst-c/main/docs/images/health-activity-tab.png)

### Stat cards

| Card | Shows |
|---|---|
| **Session Timer** | Time since this Revit session started, as `HH:MM:SS`, ticking every second; below it, "file open" and how long the current model has been open |
| **Session Total** | Hours the current model was open over the selected range |
| **Longest Sync** | The longest synchronize-with-central over the range |
| **Longest Open Time** | The longest time the model took to open over the range |
| **Warning Count** | The model's warnings when the window opened |
| **File Size** | The model's size on disk when the window opened |

The timer runs only while the Activity tab is shown. With no model open, the cards read "—".

### Per-file history charts

Below the cards, three charts plot the **currently open model's** history. The range switch, **7D · 1M · 3M · 6M**, sets the period for the charts and the cards together (7 days by default).

```stats
:::class1
value: Session time
label: this file
description: Hours the model was open, one point per calendar day across the range
:::class2
value: Opening time
label: this file
description: How long each open of the model took, one point per open
:::class3
value: Sync history
label: this file
description: Duration of each synchronize-with-central, workshared and cloud models only
```

Chart behaviour:

- **Session time** plots one point per calendar day, including days with no recorded time, so a gap reads as a gap rather than a flat line. If the range holds no recorded time, the chart shows "No time recorded in this range" instead of a line at zero.
- **Opening time** and **Sync history** plot one point per event. With a single point they show a dot and "not enough history yet"; with two or more they draw a smooth curve.
- Hover over any point to see its date and value, for example "2026-10-30 — 0.1h".
- The axis labels always differ from each other: for very short times the charts show more precision or a smaller unit.
- **Sync history** applies only to workshared and cloud models. For a local file it shows "Not a workshared model".
- Charts follow the model's identity. A new model that has never been saved has no identity yet ("save it first"); save it once and its history starts to accumulate.

### Data & privacy

The Activity tab is a view of rst-c's local session records. Session, open and sync events are written to a local outbox under `%AppData%\RST\` on this machine, and the charts are built from those files. **Nothing is uploaded or sent anywhere**: the same local-only guarantee as the rest of the Health tool.

The **Collection** switch in the Activity tab's footer controls whether these events are recorded at all. Beside it, a status line reports the outbox: on or off, the number of files, their size on disk and the date of the oldest event (for example "collection on · 8 files · 424 KB · since 10/22/2026"). Turning collection off stops new events from being recorded; existing history stays until it ages out.

## Notes & limits

- The snapshot is point-in-time. RAM and CPU usage are the values at the moment of the scan, not live readings.
- Activity charts cover only the **currently open model**. Open a different model and reopen Health to see its history; with no model open, the charts show "No model open".
- Activity history is per-machine and local. It does not follow a user to another workstation and is not shared between machines.
- Journal, collaboration cache and recent-file paths are found per Revit year (folders named `Autodesk Revit <year>`). Only the years that exist on the machine are checked.
- Cleaning Temp (`%LocalAppData%\Temp`) deletes every file in that folder, not just Revit's. Windows uses it for all programs' temporary files; the cleanup does what a manual `%temp%` cleanup would.
- The recent file list cleanup clears Revit's list of recent files. Revit rebuilds it as you open files.
- Nothing in the Health tool uploads data, phones home, or needs network access.
