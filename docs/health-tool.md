---
title: Health Tool
tags: [rst-c, health, diagnostics, cleanup, activity, telemetry, privacy]
date: 2026-10-31
project: rst-c
purpose: Guide to the Health system snapshot, junk file cleanup, and the Activity tab with what activity data RST records
---

# Health Tool

[![Open in md-converter](https://img.shields.io/badge/Open%20in-md--converter-6b46c1?style=flat-square)](https://md-converter.designs-os.com/?url=https://github.com/jedbjorn/rst-c/blob/main/docs/health-tool.md)

## Overview

Health is a one-click workstation and Revit-session snapshot. It collects hardware readings, OS details, the active Revit model and its warnings, then offers a targeted junk file cleanup for common Revit bloat. Everything runs locally: nothing is uploaded or sent anywhere.

The intent is to give a user something concrete to share with a BIM lead when Revit is misbehaving, without asking them to open Task Manager, dig through Windows settings, or know what a journal file is.

Click **Health** on the **RST** panel of the **Add-Ins** tab. The window, titled **RST — Health**, opens centred over Revit. It belongs to Revit (no taskbar button of its own; Revit waits until you close it) and uses Revit's light or dark theme as it was when the window opened. **Escape** closes it, as **Close** does, except while a scan or a clean is running or a dialog is open over it.

The header reads "RST · Health" and holds two tabs, plus the **Device** name and the time the snapshot was **Captured**:

- **Health:** the system snapshot and junk file cleanup (below).
- **Activity:** the current model's active time, open and sync times and health over time ([Activity tab](#activity-tab)).

## System snapshot

![The Health tab after a scan, with all four sections opened: Hardware (user, CPU, RAM, GPUs, disk, the junk rows and Junk total, with a red dot for high RAM use), Display & Network, Revit (version, username, hardware acceleration, model, warnings) and Operating System, above Clean Junk Files, Close and Scan System](https://raw.githubusercontent.com/jedbjorn/rst-c/main/docs/images/health-system-snapshot.png)

### Scanning

The Health tab shows four sections: **Hardware**, **Display & Network**, **Revit** and **Operating System**. They start collapsed; click one to open it. The coloured dot beside the Hardware, Display & Network and Revit headings sums up that section's readings: green when they are fine, amber or red when something needs attention (low free disk space, high RAM or CPU use, a large model, many warnings).

Click **Scan System** to take a new snapshot. While the scan runs, the button shows a spinner and "Scanning…", and the other buttons and the tabs are disabled. The scan runs in the background, so Revit and the window stay responsive. The last snapshot is saved to `%AppData%\RST\health_scan.json` and shown again the next time Health opens, with its Captured time. Before the first scan, every value reads "—". If Health is closed while a scan or a clean is running, that work still finishes in the background but nothing more is shown for it; a Health window opened meanwhile keeps **Scan System** and **Clean Junk Files** disabled until it ends.

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

- **Junk:** the size and file count of each cleanup target, and a **Junk total** of them (under Hardware). Journals, Collaboration Cache and Recent File List show the folder of the Revit year you are running, for example `Autodesk Revit 2027`. Clean Junk Files empties those targets for every installed Revit year, so it can reclaim more than the total shows.
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

The **Activity** tab shows how the **model that is active in Revit** has been used and how it behaves: how long you have actively worked on it, how long it takes to open and to synchronize, and whether its health is getting better or worse. Everything on the tab is about that one model; only the Collection switch and the outbox figures in the footer cover all of your models. It reads only data recorded locally on this PC; see [Data & privacy](#data--privacy).

> [!class2]
> **All your models:** the tray's [Health page](tray.md#health-page) is the all-models view. Its **Models** sub-tab lists every model you have worked on on this PC, in every Revit version, with search, totals and health colours, and opens any of them, without Revit, on these same cards and charts (with a 1Y range as well).

![The Health tool's Activity tab: six stat cards (Activity (Session), Activity (Total), Longest Sync, Longest Open Time, Warning Count, File Size), the 7D / 1M / 3M / 6M range switch, the active-time, opening-time, sync-history and health charts for the current model, and the Collection switch with its status line](https://raw.githubusercontent.com/jedbjorn/rst-c/main/docs/images/health-activity-tab.png)

### Active time

rst-c measures **active time**: the time you spend changing the model, not the time it sits open. While a model is open, Revit tells rst-c whenever the model changes, and rst-c notes that at most once a minute. Each of those minutes counts as active, and so does the time between two of them up to **10 minutes** apart. A longer pause is not counted, and nothing is counted across closing the model or turning collection off. A model left open overnight with nobody working on it adds nothing.

So an hour of work with one 15-minute coffee break in the middle counts about 45 minutes. Changes Revit makes for you also count (a synchronize reloading other people's work, another add-in changing the model): the rule is fixed, so the same work always gives the same number. Active time is split at midnight into calendar days.

### Stat cards

| Card | Shows |
|---|---|
| **Activity (Session)** | Active time on this model since you opened it in this Revit session; below it, the time it was opened. "—" when collection was off |
| **Activity (Total)** | This model's active time over the selected range, today's session included |
| **Longest Sync** | The longest synchronize-with-central over the range |
| **Longest Open Time** | The longest time the model took to open over the range |
| **Warning Count** | The model's warnings when the window opened |
| **File Size** | The model's size on disk when the window opened |

The values are read when you open the Activity tab, when you change the range and after **Scan System**; they do not tick while you watch. To bring them up to date, switch to the Health tab and back. With no model open, every card reads "—".

### Charts

Below the cards, four charts plot the **current model's** history. The range switch, **7D · 1M · 3M · 6M**, sets the period for the charts and for the Activity (Total) and Longest cards together (7 days by default).

```stats
:::class1
value: Active time
label: this file
description: Active time per calendar day; per week at 6M
:::class2
value: Opening time
label: this file
description: How long each open took; the weekly average at 6M
:::class3
value: Sync history
label: this file
description: How long each synchronize took, workshared and cloud models only; the weekly average at 6M
:::class4
value: Health
label: this file
description: The model's health colour for each day used; per week at 6M
```

- **Active time** plots one point per calendar day, including days with no active time, so a gap reads as a gap rather than a flat line. If the range holds no active time, the chart shows "No time recorded in this range" instead of a line at zero.
- **Opening time** and **Sync history** plot one point per open or synchronize. With a single point they show a dot and "not enough history yet"; with two or more they draw a smooth curve.
- **6M plots weeks.** At 6M every chart has one point per week (Monday to Sunday) over the last 26 weeks, this week included. Active time is the week's total; Opening time and Sync history are the week's average (the total time divided by the number of opens or syncs); the Longest cards are still the single longest. The hover reads "Week of 2026-10-26 — …".
- **Sync history** applies only to workshared and cloud models. For a local file it shows "Not a workshared model".
- Hover over any point to see its date and value, for example "2026-10-30 — 45m". The axis labels always differ from each other: for very short times the charts show more precision or a smaller unit.
- Charts follow the model's identity. A new model that has never been saved has no identity yet ("save it first"); save it once and its history starts to accumulate. All local copies of one central model share one history.

### Health chart

**Health — this file** shows whether the model is getting healthier or not. It has one dot per day you used the model (at 6M, one per week, for the last day used that week), placed higher the healthier the model was and coloured by its band:

| Colour | Means |
|---|---|
| **Green** | Healthy |
| **Yellow** | Worth watching |
| **Amber** | Needs attention |
| **Red** | Poor |
| **Grey** | Not enough data yet |

The colour comes from four things rst-c records about the model, each weighed equally: its **file size** (no concern up to 500 MB, the most at 4 GB), its **warnings per MB** (no concern up to 1 warning per 2 MB, the most at 2 per MB), and the **typical time to open** and **to synchronize** (the middle value of the last 10 of each; no concern up to 30 seconds, the most at 5 minutes). Anything not known for a model is left out rather than counted against it: a local model has no syncs, a cloud model has no local file size, so it has no warnings per MB either, and a model that has only been opened, never saved or synced, has no warning count yet (warnings are counted after a save or sync, not after an open). The chart has no numbers on purpose; hover over a dot to see its date and colour, for example "2026-10-30 — Yellow". The newest dot is the model as it is now.

### Data & privacy

rst-c records model activity on this PC so the Activity tab, and the RST tray's Health page, can show it. **Nothing is uploaded or sent anywhere.** There is no first-run notice; this section and the Collection switch are where you see and control it.

**What is recorded**, as small events, one file per Revit session:

- Revit starting and closing, and a heartbeat every 2 minutes while it runs: the Revit version, build and minor version, the RST version, the PC's name, your Windows and Autodesk user names, and a random id for this RST install.
- Opening, closing, saving and Save As of a model, and how long the open took. Each carries the model's identity: its title, its local and central paths, the ids Revit keeps for it (creation, central and cloud ids), whether it is workshared, cloud, a family or detached.
- Each synchronize with central: its start, its end, and the text of the optional comment you typed in Revit's Synchronize with Central dialog.
- That the model changed, at most once a minute per model, and that you switched to another open model, at most once a minute. Only the time and which model; never what changed.
- The model's file size on disk after it opens, and its warning count and file size after it saves or syncs, at most once every 5 minutes. Warnings are not counted after an open, so opening a model is never slowed down; the last count recorded is kept until the next save or sync.
- Collection being turned off and on.

**What is never recorded:** the model's content (elements, views, sheets, parameters, geometry, names inside the model), your keystrokes and clicks (the only text you type that is recorded is the sync comment above), the commands you ran, or anything from other programs. Linked models are not tracked.

**Where it lives**, in your Windows profile on this PC (it does not roam to other PCs):

| Folder or file | Holds |
|---|---|
| `%LOCALAPPDATA%\RST\telemetry\outbox\` | The raw event files, one per Revit session |
| `%LOCALAPPDATA%\RST\telemetry\rollups\` | Per-model totals the RST tray builds from those files |
| `%AppData%\RST\telemetry_prefs.json` | The Collection setting |

**How long it is kept.** After Revit closes, the RST tray folds each session's raw file into the model's rollup, usually within 15 minutes. Once a file's data is safely in the rollup (and in its backup copy), the raw file is deleted, except the 3 newest. The rollups keep one row of totals per model per day used, with no end date, and each individual open and sync time for 90 days (always at least the last 10). If the tray never runs, raw files are still deleted once they are 180 days old. The Health window reads files the tray has not folded yet, so its numbers are up to date either way. The file formats are documented in [Activity rollups](activity-rollups.md).

**Turning it off.** The **Collection** switch in the Activity tab's footer turns recording on or off; it is on by default. The setting is saved for your Windows account. It applies to the Revit session you change it in immediately; other Revit windows already open keep their previous setting until you close and reopen them. Off, nothing new is recorded; what was already recorded stays and is still shown. To remove everything recorded, close Revit and the RST tray and delete `%LOCALAPPDATA%\RST\telemetry\`.

Beside the switch, a status line reports the raw event files: on or off, how many there are and their size on disk, and since when the current model has history (for example "collection on · 8 files · 424 KB · since 10/22/2026").

## Notes & limits

- The snapshot is point-in-time. RAM and CPU usage are the values at the moment of the scan, not live readings.
- Activity covers only the **model active in Revit**. Open a different model and reopen Health to see its history; with no model open, the charts show "No model open". The RST tray's [Health page](tray.md#health-page) lists every model and shows any one's history without Revit.
- Activity history is per-machine and local. It does not follow a user to another workstation and is not shared between machines.
- The same model open in two Revit sessions at once counts its active time in both, so an overlap can count twice (a day never counts more than 24 hours).
- Journal, collaboration cache and recent-file paths are found per Revit year (folders named `Autodesk Revit <year>`). Only the years that exist on the machine are checked.
- Cleaning Temp (`%LocalAppData%\Temp`) deletes every file in that folder, not just Revit's. Windows uses it for all programs' temporary files; the cleanup does what a manual `%temp%` cleanup would.
- The recent file list cleanup clears Revit's list of recent files. Revit rebuilds it as you open files.
- Nothing in the Health tool uploads data, phones home, or needs network access.
