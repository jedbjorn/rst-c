---
title: RST tray
tags: [rst-c, tray, startup, updates, installer]
date: 2026-10-30
project: rst-c
purpose: What the RST tray app is, where it lives, how its update checks work and what they send, and how to quit it, stop it starting, or preset update checks off
---

# RST tray

[![Open in md-converter](https://img.shields.io/badge/Open%20in-md--converter-6b46c1?style=flat-square)](https://md-converter.designs-os.com/?url=https://github.com/jedbjorn/rst-c/blob/main/docs/tray.md)

## Overview

From v3.1, `RST.msi` also installs **RST.Tray.exe**, a small app that sits in the Windows notification area (the icons by the clock). It is the one program rst-c runs outside Revit. There is still no second runtime: it is a .NET app like the add-in, it uses the .NET runtime that Revit already installed, and it runs only while you are signed in.

The tray shows which versions of RST, Gantt-It and Splash-It are installed and tells you when a newer one is out. Later v3.1 builds also install updates when you click, and switch Revit add-ins on or off before you start Revit. Its window has four pages:

- **Updates:** RST's installed and latest versions, **Check now**, and when it last checked; then Gantt-It and Splash-It. See [Updates](#updates).
- **Apps:** one row each for RST, Gantt-It and Splash-It. Each row shows the installed version, the latest version and a state: **Up to date**, **Update available**, **Not installed** or **Installed manually**. The **Install** and **Update** buttons say "Coming soon" and are greyed out for now. Until they work, **Release page** opens the app's latest release on GitHub.
- **Add-ins:** says "Coming in a later update".
- **Settings:**
  - **Theme:** System (follows your Windows app theme), Light or Dark.
  - **Check for updates automatically:** on by default. The first time the tray starts, a notice explains what this does.
  - **About:** the version and a link to the log folder.

Click the icon to open the window. Right-click it for the menu: **Open RST**, **Check for updates** (checks now and opens the Updates page), **Settings** and **Quit**. Closing the window leaves the tray running.

## Where it lives

| What | Where |
|---|---|
| The app | `%LocalAppData%\Programs\RST\Tray\RST.Tray.exe` |
| Start at sign-in | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `RST` = `"…\RST.Tray.exe" --startup` |
| Settings | `%AppData%\RST\tray_settings.json` |
| Log | `%AppData%\RST\logs\tray_<yyyyMMdd>.log` (the newest 10 files are kept) |
| Last update check | `%AppData%\RST\tray_updates.json`: the latest versions found and the ones already announced |
| Download cache (later updates) | `%LocalAppData%\RST\Tray\cache\` |

Each Windows user has their own tray, settings and log. Only one tray runs per user and sign-in session: starting `RST.Tray.exe` again brings the running tray's window to the front.

The tray's log sits beside Revit's `rst_*.log` session logs. Each keeps its own ten files, and neither one's automatic clean-up deletes the other's. Health's **RST Logs** clean-up, which you start yourself, clears both, except the log the tray has open.

## Updates

### When it checks

- Once, 2 to 10 minutes after the tray starts. The delay is random, so a whole office signing in at 9 am doesn't ask GitHub at the same moment.
- Then once every 24 hours while the tray runs. If the PC was asleep when a check was due, the tray checks a few minutes after it wakes.
- Whenever you click **Check now** on the Updates page, or **Check for updates** in the icon's menu.

If a check fails (no network, a proxy that refuses, GitHub down), the tray writes one line to its log and tries again at the next daily check. It never shows an error dialog or a notification about it; the Updates page says "Couldn't check for updates".

### What it tells you

When a check finds a newer version of an app you have installed, the tray shows one Windows notification, for example **RST 3.1.1 is available**. Click it to open the Updates page. Each version is announced once: you won't see the same notification again the next day, or after you restart. A later version gets its own notification. If one check finds several new versions, they share one notification.

The tray never announces an app you haven't installed, or one you installed by hand (below). It installs nothing by itself. In this build the **Install** and **Update** buttons are not active yet, so update from the app's release page or with `RST-Setup.exe`.

Only full releases count. A release candidate such as `v3.2.0-rc.1` is never offered.

### How it knows what is installed

- **Windows Installer:** the tray asks it, by each app's UpgradeCode, which version is installed and whether it is installed for you or for everyone on this PC. This needs no administrator rights. An app another Windows user installed for themselves isn't visible to you, and isn't shown.
- **Installed manually:** if an app isn't known to Windows Installer but its `.addin` file is in one of Revit's add-in folders (for Revit 2025 or later), the Apps page shows **Installed manually: updates not managed by RST**. That happens when someone copied the add-in in by hand, for example from a zip.

### Privacy: what is sent, and to whom

- **Only GitHub.** For each of RST, Gantt-It and Splash-It, the tray sends one request to `https://github.com/<owner>/<repo>/releases/latest`, which answers with the name of the latest release. That is three small requests per check.
- **Nothing about you.** The requests carry no account, no ID, no version, nothing about your PC, your models or your projects: only the address above and the User-Agent `RST-Tray`. GitHub sees your network's IP address, as it does for any web request. The tray has no telemetry.
- **No sign-in.** The tray never sends credentials to GitHub and keeps no cookies. If your network uses a proxy, the tray uses the Windows proxy settings (as Edge does), and your Windows sign-in only to authenticate to that proxy.
- **Later builds** also download an update you click Install on, from `github.com` and GitHub's release-asset hosts, and make one `api.github.com` call to read its checksum.

The hosts, for an allowlist: `github.com`, `api.github.com`, `release-assets.githubusercontent.com`, `objects.githubusercontent.com`. With checks off, none of them is contacted unless someone clicks Check now.

### Switching checks off

- **For yourself:** Settings, then turn off **Check for updates automatically**. The Updates page then says "Automatic checks are off".
- **For everyone (IT):** set the `UpdateChecks` preset, below.

### Release checksums

From v3.1 each rst-c release on GitHub includes `SHA256SUMS.txt`, one line per file, in the format `sha256sum` writes:

```text
<64 hex digits: the SHA256 of RST.msi>  RST.msi
<64 hex digits: the SHA256 of RST-Setup.exe>  RST-Setup.exe
```

To check a download by hand, compare the line with `Get-FileHash .\RST.msi -Algorithm SHA256` (PowerShell) or `sha256sum -c SHA256SUMS.txt`. The release workflow checks after publishing that every line matches GitHub's own digest of that file.

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

The tray reads both keys when it starts and again before every daily check, and a `0` in either one wins. Any other value, or no value, leaves the choice to each user. With checks off, the tray makes no network calls of its own. **Check now** still works, because the user asked for it: one check, three requests, then nothing again.

## Silent installs and scripts (IT)

If the tray is running when `RST.msi` repairs or upgrades RST, Windows closes it and starts it again when the install finishes. The restarted tray is a child process of `msiexec`.

Script waits that follow the whole process tree, such as PowerShell's `Start-Process -Wait`, therefore do not return until the user quits the tray or signs out. Wait on msiexec's own exit instead:

```powershell
$p = Start-Process msiexec.exe -ArgumentList '/i RST.msi /qn /norestart' -PassThru
$p.WaitForExit()
$p.ExitCode
```

From `cmd`, use `start /wait msiexec /i RST.msi /qn /norestart`.

A deployment tool that runs the install inside a job object that ends its processes when the job closes will end the restarted tray too. The tray then comes back at the user's next sign-in.

Run per-user `RST.msi` as the user, not from an elevated prompt, so that the restarted tray runs with the user's normal rights.

## Uninstalling

Uninstalling RST from **Settings → Apps** closes a running tray first, then removes the app, the `RST` Run value and the download cache. The tray's settings and log stay under `%AppData%\RST\` with the rest of your RST data, as profiles and logs always have.

A repair or an upgrade also closes a running tray while its files are replaced. If Windows shows a **Files in Use** dialog, the default choice closes the tray, and Windows starts it again, hidden in the notification area, when the install finishes. An installer only ever asks the tray to close. It never forces it to end, so another user's tray on the same computer is left alone.

## For developers

The installer's `util:CloseApplication` finds `RST.Tray.exe` by file name in every session. The updater copy that later versions run from `%LocalAppData%\RST\Tray\updater\` therefore has to use a different exe name, for example `RST.Tray.Updater.exe`. Otherwise the install it starts asks it to close part-way through.
