---
title: RST tray
tags: [rst-c, tray, startup, updates, installer]
date: 2026-10-31
project: rst-c
purpose: What the RST tray app is, where it lives, its Health page, how its update checks work and what they send, how to quit it, stop it starting, or preset update checks off, and what changes when RST is installed for everyone on the PC
---

# RST tray

[![Open in md-converter](https://img.shields.io/badge/Open%20in-md--converter-6b46c1?style=flat-square)](https://md-converter.designs-os.com/?url=https://github.com/jedbjorn/rst-c/blob/main/docs/tray.md)

## Overview

From v3.1, `RST.msi` and `RST-Machine.msi` also install **RST.Tray.exe**, a small app that sits in the Windows notification area (the icons by the clock). It is the one program rst-c runs outside Revit. There is still no second runtime: it is a .NET app like the add-in, it uses the .NET runtime that Revit already installed, and it runs only while you are signed in.

The tray shows which versions of RST, Gantt-It and Splash-It are installed, tells you when a newer one is out, and installs it when you click (see [Installing and updating](#installing-and-updating)). It also switches your profile and Revit add-ins before you start Revit (see [Add-ins page](#add-ins-page)), and shows this PC's health and every model you have worked on (see [Health page](#health-page)). Its window has five pages:

- **Updates:** RST's installed and latest versions, **Check now**, and when it last checked; then Gantt-It and Splash-It. See [Updates](#updates).
- **Apps:** one row each for RST, Gantt-It and Splash-It. Each row shows the installed version, the latest version and a state: **Up to date**, **Update available**, **Not installed**, **Installed manually**, or **Unknown (couldn't read installed apps)** when Windows Installer can't be asked. A repo with no full release yet says **No release yet**. **Update** (an update is out) and **Install** (Gantt-It or Splash-It not installed yet) install it for you; **Release page** opens the app's latest release on GitHub.
- **Add-ins:** the active profile, **Apply profile…** and **Restore All Add-ins**, with the Profile Loader's own switching, and per installed Revit year what RST switched off and what it can't switch back on. See [Add-ins page](#add-ins-page).
- **Health:** **Device**, this PC's snapshot and junk file cleanup without Revit, and **Models**, every model you have worked on on this PC, across Revit versions, with each model's activity and health. See [Health page](#health-page).
- **Settings:**
  - **Theme:** System (follows your Windows app theme), Light or Dark.
  - **Check for updates automatically:** on by default. The tray then asks GitHub once a day whether a newer RST, Gantt-It or Splash-It is out. Turn it off to stop those checks. See [Updates](#updates).
  - **About:** the version and a link to the log folder.

Click the icon to open the window. Right-click it for the menu: **Open RST**, **Check for updates** (checks now and opens the Updates page), **Settings** and **Quit**. Closing the window leaves the tray running.

## Where it lives

| What | Where |
|---|---|
| The app | `%LocalAppData%\Programs\RST\Tray\RST.Tray.exe` (`RST.msi`, just for you); `C:\Program Files\RST\Tray\RST.Tray.exe` (`RST-Machine.msi`, everyone on this PC) |
| Start at sign-in | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (`RST.msi`) or `HKLM\…\Run` (`RST-Machine.msi`), value `RST` = `"…\RST.Tray.exe" --startup` |
| Start after installing | The installer starts it, hidden, for the user who ran the install, repair or upgrade (not as SYSTEM). Its `RST-StartTray-<time>-<pid>.log` in that user's `%TEMP%` says what it did. See [installer/README.md](../installer/README.md#when-the-tray-starts). |
| Settings | `%AppData%\RST\tray_settings.json` |
| Log | `%AppData%\RST\logs\tray_<yyyyMMdd>.log` (the newest 10 files are kept) |
| Last update check | `%AppData%\RST\tray_updates.json`: the latest versions found and the ones already announced |
| Download cache (later updates) | `%LocalAppData%\RST\Tray\cache\` |

Each Windows user has their own tray, settings and log, whichever MSI installed it: a per-machine install shares only the program files. Only one tray runs per user and sign-in session: starting `RST.Tray.exe` again brings the running tray's window to the front.

Restart Manager may restart the tray hidden after a third-party installer closes it; a `--startup` start elevated through UAC or running as SYSTEM logs one line and exits until the next sign-in or Revit launch (SC-047 R3, decision #40). An account with no split token (UAC off, or the built-in Administrator with Admin Approval Mode off, as on the CI runners) runs the tray with the same full token it gets at sign-in; double-clicked starts are unchanged.

The tray's log sits beside Revit's `rst_*.log` session logs. Each keeps its own ten files, and neither one's automatic clean-up deletes the other's. Health's **RST Logs** clean-up, which you start yourself, clears both, except the log the tray has open.

## Updates

In short: once a day, the tray asks GitHub's releases feed whether a newer RST, Gantt-It or Splash-It is out, and shows a Windows notification when one is. Each check sends only those requests, three small web requests with nothing about you, your PC or your models ([Privacy](#privacy-what-is-sent-and-to-whom)). It installs nothing until you click **Update** or **Install**. To stop the checks, turn off **Check for updates automatically** in the tray's Settings ([Switching checks off](#switching-checks-off)). The tray shows no message about this when it is installed or first starts: this page is where it is explained.

### When it checks

- Once, 2 to 10 minutes after the tray starts. The delay is random, so a whole office signing in at 9 am doesn't ask GitHub at the same moment.
- Then once every 24 hours while the tray runs. If the PC was asleep when a check was due, the tray checks a few minutes after it wakes.
- Whenever you click **Check now** on the Updates page, or **Check for updates** in the icon's menu. Within 30 seconds of the last check, Check now asks nothing and the page keeps showing that check's result.

If a check fails (no network, a proxy that refuses, GitHub down), the tray writes one line to its log and tries again at the next daily check. It never shows an error dialog or a notification about it. The Updates page says "Couldn't check: <the reason>" and shows when the last *successful* check was, and it never calls an app "Up to date" on the strength of an older check. A newer version an earlier check found is still shown as available.

### What it tells you

When a check finds a newer version of an app you have installed, the tray shows one Windows notification, for example **RST 3.1.1 is available**. Click it to open the Updates page. Each version is announced once: you won't see the same notification again the next day, or after you restart. A version counts as announced only once its notification has actually been shown; if the tray's icon isn't in the notification area yet (Windows still starting), the notification waits for it. A later version gets its own notification. If one check finds several new versions, they share one notification.

The tray never announces an app you haven't installed, or one you installed by hand (below). It installs nothing by itself: only when you click **Update** or **Install**.

Only full releases count. A release candidate such as `v3.2.0-rc.1` is never offered.

### How it knows what is installed

- **Windows Installer:** the tray asks it, by each app's UpgradeCode, which version is installed and whether it is installed for you or for everyone on this PC. This needs no administrator rights. An app another Windows user installed for themselves isn't visible to you, and isn't shown.
- **Installed manually:** if an app isn't known to Windows Installer but its `.addin` file is in one of Revit's add-in folders (for Revit 2025 or later), the Apps page shows **Installed manually: updates not managed by RST**. That happens when someone copied the add-in in by hand, for example from a zip.
- **Unknown:** if Windows Installer itself can't be read, the pages say **Unknown (couldn't read installed apps)** rather than guess "not installed" or "installed manually", and the tray announces nothing for those apps.

### Privacy: what is sent, and to whom

- **Only GitHub.** For each of RST, Gantt-It and Splash-It, the tray sends one request to `https://github.com/<owner>/<repo>/releases/latest`, which answers with the name of the latest release. That is three small requests per check.
- **Nothing about you.** The requests carry no account, no ID, no version, nothing about your PC, your models or your projects: only the address above and the User-Agent `RST-Tray`. GitHub sees your network's IP address, as it does for any web request. The tray sends no telemetry: it reads and folds the add-in's local activity records on this PC into rollups ([Activity rollups](activity-rollups.md)) and sends nothing. What the add-in records, and how to turn it off, is in [Health: Data & privacy](health-tool.md#data--privacy).
- **No sign-in.** The tray never sends credentials to GitHub and keeps no cookies. If your network uses a proxy, the tray uses the Windows proxy settings (as Edge does), and your Windows sign-in only to authenticate to that proxy.
- **Installs:** when you click Install or Update, the tray downloads that release's `SHA256SUMS.txt` and the installer from `github.com` (redirected to GitHub's release-asset host), and makes one `api.github.com` call to read GitHub's checksum. Nothing else.

The hosts, for an allowlist: `github.com`, `api.github.com`, `release-assets.githubusercontent.com`, `objects.githubusercontent.com`. With checks off, none of them is contacted unless someone clicks Check now, Install or Update.

### Switching checks off

- **For yourself:** Settings, then turn off **Check for updates automatically**. The Updates page then says "Automatic checks are off". The tray then makes no network calls of its own; **Check now**, **Install** and **Update** still work when you click them. Turn the switch back on to resume the daily checks.
- **For everyone (IT):** set the `UpdateChecks` preset, below.

### Release checksums

From v3.1 each rst-c release on GitHub includes `SHA256SUMS.txt`, one line per file, in the format `sha256sum` writes:

```text
<64 hex digits: the SHA256 of RST.msi>  RST.msi
<64 hex digits: the SHA256 of RST-Machine.msi>  RST-Machine.msi
<64 hex digits: the SHA256 of RST-Setup.exe>  RST-Setup.exe
```

To check a download by hand, compare the line with `Get-FileHash .\RST.msi -Algorithm SHA256` (PowerShell) or `sha256sum -c SHA256SUMS.txt`. The release workflow creates each release as a draft, checks that every line matches GitHub's own digest of that upload, and only then publishes it. A re-run of a failed release job first deletes the draft the failed run left for that tag (it never touches a published release), so there is only ever one draft per tag (`build/release-draft.sh`).

## Installing and updating

Click **Update** or **Install** on the Updates or Apps page. Installs are for your Windows account only and need no administrator.

### What is checked first

Before anything runs, the tray checks the download four ways, and installs nothing unless all four pass:

1. **The address is built, never followed from a page:** `https://github.com/<the app's repo>/releases/download/<tag>/<file>`, for a full release tag (`v3.1.1`, never a pre-release) and the app's own file name (`RST.msi`, `GanttIt-<version>.msi`, `SplashIt-<version>.msi`). GitHub redirects downloads to its release-asset host; the tray follows a redirect only to `release-assets.githubusercontent.com` or `objects.githubusercontent.com`.
2. **The file's SHA256** must equal its line in that release's `SHA256SUMS.txt` **and** the digest GitHub itself computed for the upload (one call to `api.github.com/repos/<repo>/releases/tags/<tag>`).
3. **The installer says what it is:** its UpgradeCode is that app's, its version is the release's, and it installs for you, not for everyone on the PC.
4. **It is newer** than what you have. The tray never downgrades.

If a check fails, the row says so in plain words, for example "RST didn't install Gantt-It 1.2.0: the download doesn't match its checksum. Nothing was changed." The details are in the tray log.

### Never while Revit is open

While Revit is running, the button reads **Install when Revit closes** (or **Update when Revit closes**). Click it and the tray downloads and checks the update, then waits; it installs as soon as the last Revit window closes. **Cancel** stops the wait.

### How it installs

Installs run one at a time: an app asked for while another installs says it is waiting its turn. Right before it runs, the tray checks the file's SHA256 and identity once more, and what is installed (no downgrade), and keeps the file open so nothing can change it until the install has finished.

The tray runs Windows' own installer, `C:\Windows\System32\msiexec.exe /i <file> /qn /norestart`, and keeps its log in `%LocalAppData%\RST\Tray\logs\install-<app>-<date>-<time>.log` (**Open log** on the row).

- **Installed:** the row says so.
- **Restart needed** (msiexec 3010): the app is installed, and Windows asks for a restart to finish. The tray never restarts the PC.
- **Another installation is running** (1618): the tray tries again after three minutes, three times in all.
- **Anything else:** the row gives the error number, and **Open log** shows the details.

The tray keeps each app's last installed file in `%LocalAppData%\RST\Tray\cache\<app>\<version>\`. Gantt-It and Splash-It remove their old version before they install the new one, so a failed upgrade can leave neither installed. The row then offers **Reinstall previous version (x.y.z)**: from the cache, or, if the tray didn't install that version itself, downloaded and checked the same four ways.

### RST updating itself

RST is updated the way it was installed (decision #34):
- **Installed with `RST-Setup.exe`** (the recommended way): the tray downloads that release's `RST-Setup.exe` and runs it with `/quiet /norestart` and no companion choices. Settings → Apps keeps its one **RST** entry, and uninstalling that entry still removes RST. The download is checked the same way: its SHA256 against `SHA256SUMS.txt` and GitHub's digest, its exact name, and its version resource equal to the release. (The bundle's UpgradeCode sits inside the bundle's attached container, which the tray does not unpack; the two hashes and immutable releases vouch for the file.)
- **Installed with `RST.msi`**: the tray updates with that release's `RST.msi`.

Either package contains the tray, which is running, so the tray copies itself to `%LocalAppData%\RST\Tray\updater\` as **RST.TrayUpdater.exe**, starts that copy, and closes. The copy checks the file again, waits for the tray to close, checks for Revit once more, runs the package, and starts the new tray, whose window says how it went. While it works it holds the tray's single-instance lock, so a tray started meanwhile (by Revit, say) exits at once instead of running from the folder being replaced; it lets go right before it starts the new tray. If Revit was opened in the meantime, the copy installs nothing and starts the tray again, which waits for Revit to close and then tries again. The installer closes running copies of `RST.Tray.exe` by name; the updater's different name keeps it out of that.

The tray hands over only when no other install is running, waiting or queued: those finish first, and their results are recorded, so closing the tray never loses one.

### Installed for everyone on this PC

If RST, or Gantt-It or Splash-It, is installed for everyone on the PC, a tray running for your account doesn't install over it. The row says, for example: "RST 3.1.1 is available. It's installed for everyone on this PC, so run RST-Setup.exe as an administrator or ask whoever manages software here," with the release page link.

## Quitting it

Right-click the RST icon in the notification area and choose **Quit**. Quitting lasts until Revit next starts, or until you next sign in: either one starts the tray again. To stop it for good, switch RST off in Task Manager (see the next section); Revit respects that switch too.

Nothing in Revit depends on the tray. RST in Revit, including the Profile Loader, works the same with the tray closed.

## Stopping it starting at sign-in

Open **Task Manager**, go to **Startup apps**, select **RST** and choose **Disable**. Windows remembers that choice, and Revit then no longer starts the tray either. Choose **Enable** to undo it.

## Revit starts the tray

When Revit 2025 or later starts with RST, RST checks once whether your tray is running and starts it if not, in the background, without waiting for it and without delaying Revit. One line in RST's session log says what happened (`Tray: …`). It does not start the tray when:

- the tray already runs for you;
- RST is switched off in Task Manager's Startup apps (the switch of the install that is there: per-user or for everyone);
- Revit was run as administrator: the tray never runs elevated;
- the tray is not installed at its fixed place, `%LocalAppData%\Programs\RST\Tray\` or `C:\Program Files\RST\Tray\`. RST never takes that path from a profile, a setting or the registry.

**IT note (job objects).** When Revit runs inside a Windows job object (a deployment launcher, an app-virtualisation wrapper, a sandbox), RST starts the tray outside that job if the job allows it, with your normal environment rather than Revit's. If the job forbids that, Explorer starts it instead. So closing the job does not end the tray. Only with neither available does the tray start inside the job, and the log line says so.

## Add-ins page

The **Add-ins** page switches your profile and add-ins before you start Revit, with the Profile Loader's own switching: the same plan in **Confirm Add-in Changes**, the same report, the same record of what RST switched off, and the Loader's **Adopt RSTify Presets?** question the first time a profile with presets is used. Per installed Revit year it lists what RST switched off and what it can't switch back on. **Restore All Add-ins** switches back on only what RST switched off. Every change applies the next time that Revit starts. If a Revit is open, the report says so: restart it to see the change.

Things to know when Revits of several years are open:

- The active profile is one file for all years. An open Revit keeps the profile tab it built until it restarts, but its RSTify button reads the hidden tabs of the profile you just applied.
- Revit writes its add-in settings file while it starts. An Apply made while a Revit of that year is starting can be overwritten by it, even though the report says it was done. Apply again once that Revit is up. The Profile Loader in another year behaves the same.

## Health page

The **Health** page is the zoom-out view of the Health tool in Revit. In Revit, Health shows this PC and the model you have open; the tray shows this PC and **every model you have worked on**, on this PC, in any Revit version, with Revit closed or open. It has two sub-tabs, **Device** and **Models**, and opens on Models. Everything it shows is read from files on this PC; nothing is sent anywhere. What rst-c records about your models, and how to turn it off, is in [Health: Data & privacy](health-tool.md#data--privacy).

### Device

The same snapshot as Health in Revit ([System snapshot](health-tool.md#system-snapshot)): the Hardware, Display & Network, Revit and Operating System sections with their coloured dots, **Scan System** and the time it was **Captured**.

- **Revit:** outside Revit there is no current model, Revit user name, hardware acceleration or warning count, so the section lists each installed Revit year with its build instead (for example Revit 2026, 26.0.4.409).
- **The snapshot** is saved to `%AppData%\RST\tray_health_scan.json`, separate from Revit's `health_scan.json`, so neither overwrites the other.
- **Clean Junk Files** opens the same dialog, with the same targets, as in Revit ([Junk file cleanup](health-tool.md#junk-file-cleanup)): your active profile's targets, or the presets. A target that is kept per Revit year (Journals, Collaboration Cache, Recent File List) is listed once for each installed year, for example "Journals — 2026" and "Journals — 2027", so one run can clean every year. Files in use, by a running Revit for example, are skipped and counted, as in Revit. The junk sizes refresh at the next Scan System.

### Models

From the top: a search box, the filters, totals over the models shown, then the model cards, grouped.

- **Time frame:** **Recent** (the last 14 days, the default), **3M**, **6M** or **1Y** (the last 13, 26 or 52 weeks, Monday to Sunday, this week included). A model shows when it was open at any time in that window.
- **Revit version:** All, or one or more Revit years. A model shows when it was opened in a chosen year within the time frame.
- **Group by:** **Major** version (2026; the default), **Minor** version (2026.4, from the version Revit reports; for tracking down a problem that came with an update) or **None** (one flat list). A model sits in the group of the newest version it was opened in within the time frame, or the newest chosen version when the Revit version filter is set. A model recorded before Revit's minor version was kept sits under "2026 — unknown minor". Click a group's header to fold it away; groups sort newest version first, and models in a group by when they were last open, newest first.
- **Search:** type part of a model's title, file name, or local or central path (including the names and paths it had before). Search covers **every model, all time**: while the box has text, the time frame and version filter are set aside, the filter row says "Searching all time", and every number on the page is all-time. Clear the box to go back to the filters.
- **Totals** over the models shown: **Models**, **Active time**, **Opens**, **Avg open time**, **Syncs** and **Avg sync time** ("—" with no syncs). The averages are the total time divided by the count.
- **Model cards:** the model's thumbnail, its title (and its file name when that differs), its health colour **with its name**, the newest Revit version it was opened in within the time frame, the day it was last open, its active time in the time frame, and **Workshared** or **Cloud** where that applies. Click a card, or press Enter on it, to open its [detail](#model-detail).
- **Health colours** are the Health chart's in Revit ([Health chart](health-tool.md#health-chart)), from the model's file size, warnings and its typical open and sync times: **Green** (healthy), **Yellow** (worth watching), **Amber** (needs attention), **Red** (poor) or **Grey** "Not enough data". There is never a number.
- **Thumbnails** are the image Windows shows for the model's file in Explorer, for a model saved on this PC's own drives. A model on a network share, a mapped drive or in the cloud shows a placeholder. RST never reads a model file itself, and never touches a file on the network. Thumbnails are kept in `%LocalAppData%\RST\Tray\thumbnails\` and made again when the model file has been saved since. A model whose file was moved or deleted keeps its last thumbnail, its card and its history.
- **When there is nothing to show:** "No model activity yet. Open a model in Revit and it appears here after you close Revit." (nothing recorded yet), "No models in this time frame" with a link to switch to 1Y, or "No models match" (search). With collection turned off, the page still shows what was recorded before.

**How fresh it is.** The tray builds each model's history from the files Revit writes, usually within 15 minutes after Revit closes. Opening Health › Models also asks for that to run at once (unless it ran in the last minute), and the list updates when it finishes. A model open in Revit right now shows its history up to its last session; the session under way is added after Revit closes.

### Model detail

Opening a card shows that model's detail in place of the list. **Back**, or **Escape**, returns to the list as you left it: the same search, filters, folded groups and scroll position.

- **At the top:** the thumbnail, the title (and file name), the health colour with its name, the Revit versions the model was opened in (for example "Opened in Revit 2027.1, 2026.4"), its local file and its central (or cloud) model, and Workshared or Cloud.
- **Cards:** **Activity (Total)**, **Longest Sync** and **Longest Open Time** over the range, and **Warning Count** and **File Size**, the last ones recorded, with the day they were recorded ("As of Oct 28, 2026"). Activity (Session) is Revit's only: it is the session under way, which only Revit sees live.
- **Range:** **7D**, **1M**, **3M**, **6M** or **1Y**. The detail opens on the range nearest the list's time frame: Recent opens 1M, 3M, 6M and 1Y open their own, and a search opens 1Y.
- **Charts:** the four of Revit's Activity tab, in the same order: Active time, Opening time, Sync history (workshared and cloud models) and Health ([Charts](health-tool.md#charts)). At 7D, 1M and 3M, one point per day or per open or sync; at 6M and 1Y, one per week.
- **The same numbers as Revit:** the detail reads the model the way Revit's Activity tab does, including the sessions the tray has not folded yet, so for the same range both show the same figures. A session still open in Revit counts up to its last recorded event (as a session that ended in a crash does until it is recovered), so it can trail Revit by the time since then; Revit's own Activity tab is the live view. The Models list adds that session only after Revit closes. Revit's Health window has no 1Y range.
- If a model's history is no longer on this PC (its files deleted, or unreadable), the detail says so.

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

When an install, repair or upgrade succeeds, the installer starts the tray for the user who ran it, hidden in the notification area. It starts it through that user's Explorer, so the tray runs with the user's normal rights, also when `msiexec` runs from an elevated prompt, and it is not part of `msiexec`'s process tree. It starts none when one already runs, and none when the installer runs as SYSTEM (SCCM, Intune): each user's tray then starts at their next sign-in.

If the tray is running when `RST.msi` repairs or upgrades RST, Windows closes it first, and the installer then starts it again as above. Windows itself does not restart it from `msiexec` (`MSIDISABLERMRESTART=1`), because that tray would run with `msiexec`'s rights. Only one tray runs.

Script waits that follow the whole process tree, such as PowerShell's `Start-Process -Wait`, can still be held by a tray: when no Explorer of the user runs, the installer starts the tray directly, as a child of the install. Wait on msiexec's own exit instead:

```powershell
$p = Start-Process msiexec.exe -ArgumentList '/i RST.msi /qn /norestart' -PassThru
$p.WaitForExit()
$p.ExitCode
```

From `cmd`, use `start /wait msiexec /i RST.msi /qn /norestart`.

A deployment tool that runs the install inside a job object that ends its processes when the job closes can end a tray started that way too. The tray then comes back at the user's next sign-in.

## Installed for everyone on this PC (IT)

`RST-Machine.msi` (or `RST-Setup.exe`, "Everyone on this PC") installs the tray once, in `C:\Program Files\RST\Tray\`, and the `HKLM` Run value starts it for every user at sign-in. Each user still gets their own tray process, settings, log and add-in choices. Details of the package are in [installer/README.md](../installer/README.md#per-machine-install-rst-machinemsi).

```powershell
# a deployment tool (SCCM, Intune: runs as SYSTEM) or an elevated prompt
msiexec /i RST-Machine.msi /qn /norestart /l*v "%TEMP%\RST-Machine.log"

# an administrator signed in at the PC (also installs ticked companions for that account)
RST-Setup.exe /quiet /norestart InstallScope=machine
```

Deploy `RST-Machine.msi` on its own as SYSTEM. Run as SYSTEM, `RST-Setup.exe` would register itself, and install companions, for no real user.

- **When the tray starts:** after a successful install, repair or upgrade, the installer starts the tray for the administrator who ran it, through their own Explorer, so it does not run elevated. A tray started by the elevated installer itself would run elevated, and its icon would not come back after Explorer restarts. Run as SYSTEM, it starts none. Every other user's tray starts at their next sign-in, or when Revit starts it. A repair or upgrade first asks a running tray on the installer's desktop to exit, and ends the other users' trays (below). Those come back at the next sign-in, or with Revit.
- **Updates are an administrator's job.** A user's tray tells them a newer RST is out, but it does not install a per-machine RST, because that needs an administrator. Deploy the new `RST-Machine.msi`; it upgrades in place. Companions stay per user, so each user's tray keeps them up to date for that user.
- **Update checks:** to turn them off for every user, set `HKLM\Software\Designs-OS\RST\Tray` `UpdateChecks` = `0` ([below](#turning-update-checks-off-for-everyone-it)).
- **Signed-in users:** an uninstall, repair or upgrade ends every signed-in user's tray before it removes the files, in every session, also when a deployment tool runs it as SYSTEM. There is nothing to end first. A tray on the installer's own desktop is asked to exit. Every other tray that runs from `C:\Program Files\RST\Tray\` is then ended. A user's own per-user tray (`RST.msi`) is left alone. Each tray comes back at that user's next sign-in, or when Revit starts it. Until then its icon may linger until the pointer passes over it. If an uninstall finds anything else still using RST's files, typically Revit with RST loaded in some session, `msiexec` returns `3010` (restart needed). Each run's `RST-Machine-EndTrays-<time>-<pid>.log` in the installer's `%TEMP%` (as SYSTEM: `C:\Windows\Temp\`) lists what it ended and what still uses the files. See [installer/README.md](../installer/README.md) for how long the restart flag lasts.
- **Installed twice:** a user who also has their own `RST.msi` runs their own copy. Revit lets a per-user add-in of the same name win. RST logs it at every Revit start, and the tray tells that user once: "RST is installed twice … To use the shared one, remove "RST" in Settings > Apps, and keep "RST (all users)"." Their profiles and settings stay. A bare `RST-Machine.msi` refuses to install while the installing administrator has their own copy. `RST-Setup.exe`'s "Everyone on this PC" installs RST for everyone, then removes that copy. If Revit is open or a copy can't be removed, it rolls back, and the administrator keeps their own RST.
- **Going back to per user** is uninstall, then install: remove "RST (all users)", then install `RST.msi`.

## Allowlisting (AppLocker, antivirus)

RST's files are not code-signed yet, so allow them by path or by hash.

| Install | Paths |
|---|---|
| `RST.msi` (per user) | `%LocalAppData%\Programs\RST\Tray\`, `%AppData%\RST\R<NN>\app\`, `%AppData%\Autodesk\Revit\Addins\<year>\RST.Bootstrap.dll` |
| `RST-Machine.msi` (per machine) | `C:\Program Files\RST\` (tray, bootstraps and engines) |

Per-machine mode puts every RST program file under Program Files. AppLocker's default rules already allow that path, and users cannot write there. Each release's `SHA256SUMS.txt` gives the installers' hashes ([Release checksums](#release-checksums)). The tray only ever starts `msiexec.exe` from System32 and its own updater copy (`RST.TrayUpdater.exe` in `%LocalAppData%\RST\Tray\updater\`), and it talks only to the GitHub hosts listed under [Privacy](#privacy-what-is-sent-and-to-whom).

## Uninstalling

Uninstalling RST from **Settings → Apps** closes a running tray first, then removes the app and the `RST` Run value. `RST.msi` also removes `%LocalAppData%\RST\Tray\` (the download cache, the updater copy and the install logs). `RST-Machine.msi` leaves each user's `%LocalAppData%\RST\Tray\`, which is per-user data an administrator's uninstall cannot reach and must not delete. The tray's settings and log stay under `%AppData%\RST\` with the rest of your RST data, as profiles and logs always have.

A repair or an upgrade also closes a running tray while its files are replaced. With `RST.msi`, if Windows shows a **Files in Use** dialog, the default choice closes the tray. When the install finishes, the installer starts the tray again, hidden in the notification area, for the user who ran it. `RST-Machine.msi` does the same for the administrator who ran it, but not as SYSTEM, and not for other signed-in users (see [Installed for everyone on this PC (IT)](#installed-for-everyone-on-this-pc-it)): theirs comes back at the next sign-in, or when Revit starts it. `RST.msi` only ever asks the tray to close. It never forces it to end, so another user's tray on the same computer is left alone. `RST-Machine.msi` also ends its own trays in other users' sessions, the ones running from `C:\Program Files\RST\Tray\`, because no one else can close them.

## For developers

The installer's `util:CloseApplication` finds `RST.Tray.exe` by file name in every session. `RST-Machine.msi`'s `RstEndTrays` matches the full path instead, so it never ends a per-user tray. The updater copy that runs from `%LocalAppData%\RST\Tray\updater\` is therefore `RST.TrayUpdater.exe` (the apphost renamed; it still loads `RST.Tray.dll` beside it). Otherwise the install it starts would ask it to close part-way through. `--apply` is honoured only by that copy in that folder, and only for an `RST.msi` the tray verified into its cache (`verified.json`, SHA256 and Property table checked again).
