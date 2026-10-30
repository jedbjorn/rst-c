---
title: RST tray
tags: [rst-c, tray, startup, updates, installer]
date: 2026-10-30
project: rst-c
purpose: What the RST tray app is, where it lives, and how to quit it, stop it starting, or preset update checks off
---

# RST tray

[![Open in md-converter](https://img.shields.io/badge/Open%20in-md--converter-6b46c1?style=flat-square)](https://md-converter.designs-os.com/?url=https://github.com/jedbjorn/rst-c/blob/main/docs/tray.md)

## Overview

From v3.1, `RST.msi` also installs **RST.Tray.exe**, a small app that sits in the Windows notification area (the icons by the clock). It is the one program rst-c runs outside Revit. There is still no second runtime: it is a .NET app like the add-in, it uses the .NET runtime that Revit already installed, and it runs only while you are signed in.

The tray will show which versions of RST, Gantt-It and Splash-It are installed, tell you when a newer one is out, install it when you click, and switch Revit add-ins on or off before you start Revit. In this first build its window has four pages. **Updates**, **Apps** and **Add-ins** say "Coming in a later update". **Settings** is complete.

- **Theme:** System (follows your Windows app theme), Light or Dark.
- **Check for updates automatically:** on by default. The first time the tray starts, a notice explains what this does.
- **About:** the version and a link to the log folder.

Click the icon to open the window. Right-click it for the menu: **Open RST**, **Check for updates** (greyed out until update checks arrive), **Settings** and **Quit**. Closing the window leaves the tray running.

## Where it lives

| What | Where |
|---|---|
| The app | `%LocalAppData%\Programs\RST\Tray\RST.Tray.exe` |
| Start at sign-in | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `RST` = `"…\RST.Tray.exe" --startup` |
| Settings | `%AppData%\RST\tray_settings.json` |
| Log | `%AppData%\RST\logs\tray_<yyyyMMdd>.log` (the newest 10 files are kept) |
| Download cache (later updates) | `%LocalAppData%\RST\Tray\cache\` |

Each Windows user has their own tray, settings and log. Only one tray runs per user and sign-in session: starting `RST.Tray.exe` again brings the running tray's window to the front.

The tray's log sits beside Revit's `rst_*.log` session logs. Each keeps its own ten files, and neither one's automatic clean-up deletes the other's. Health's **RST Logs** clean-up, which you start yourself, clears both, except the log the tray has open.

## Quitting it

Right-click the RST icon in the notification area and choose **Quit**. It starts again the next time you sign in.

Nothing in Revit depends on the tray. RST in Revit, including the Profile Loader, works the same with the tray closed.

## Stopping it starting at sign-in

Open **Task Manager**, go to **Startup apps**, select **RST** and choose **Disable**. Windows remembers that choice. Choose **Enable** to undo it.

## Turning update checks off for everyone (IT)

Set this DWORD to `0` and the tray never checks for updates by itself. In Settings, "Check for updates automatically" shows off and greyed out, with a note that it was turned off on this computer:

```text
HKLM\Software\Designs-OS\RST\Tray   UpdateChecks = 0   (every user)
HKCU\Software\Designs-OS\RST\Tray   UpdateChecks = 0   (one user)
```

```powershell
New-Item -Path 'HKLM:\Software\Designs-OS\RST\Tray' -Force | Out-Null
New-ItemProperty -Path 'HKLM:\Software\Designs-OS\RST\Tray' -Name UpdateChecks -PropertyType DWord -Value 0 -Force | Out-Null
```

The tray reads both keys when it starts, and a `0` in either one wins. Any other value, or no value, leaves the choice to each user. With checks off, the tray makes no network calls of its own.

## Uninstalling

Uninstalling RST from **Settings → Apps** closes a running tray first, then removes the app, the `RST` Run value and the download cache. The tray's settings and log stay under `%AppData%\RST\` with the rest of your RST data, as profiles and logs always have.

A repair or an upgrade also closes a running tray while its files are replaced. If Windows shows a **Files in Use** dialog, the default choice closes the tray, and Windows starts it again, hidden in the notification area, when the install finishes. An installer only ever asks the tray to close. It never forces it to end, so another user's tray on the same computer is left alone.

## For developers

The installer's `util:CloseApplication` finds `RST.Tray.exe` by file name in every session. The updater copy that later versions run from `%LocalAppData%\RST\Tray\updater\` therefore has to use a different exe name, for example `RST.Tray.Updater.exe`. Otherwise the install it starts asks it to close part-way through.
