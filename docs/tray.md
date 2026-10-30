---
title: RST tray
tags: [rst-c, tray, startup, updates, installer]
date: 2026-10-30
project: rst-c
purpose: What the RST tray app is, where it lives, how its update checks work and what they send, how to quit it, stop it starting, or preset update checks off, and what changes when RST is installed for everyone on the PC
---

# RST tray

[![Open in md-converter](https://img.shields.io/badge/Open%20in-md--converter-6b46c1?style=flat-square)](https://md-converter.designs-os.com/?url=https://github.com/jedbjorn/rst-c/blob/main/docs/tray.md)

## Overview

From v3.1, `RST.msi` and `RST-Machine.msi` also install **RST.Tray.exe**, a small app that sits in the Windows notification area (the icons by the clock). It is the one program rst-c runs outside Revit. There is still no second runtime: it is a .NET app like the add-in, it uses the .NET runtime that Revit already installed, and it runs only while you are signed in.

The tray shows which versions of RST, Gantt-It and Splash-It are installed, tells you when a newer one is out, and installs it when you click (see [Installing and updating](#installing-and-updating)). Later v3.1 builds also switch Revit add-ins on or off before you start Revit. Its window has four pages:

- **Updates:** RST's installed and latest versions, **Check now**, and when it last checked; then Gantt-It and Splash-It. See [Updates](#updates).
- **Apps:** one row each for RST, Gantt-It and Splash-It. Each row shows the installed version, the latest version and a state: **Up to date**, **Update available**, **Not installed**, **Installed manually**, or **Unknown (couldn't read installed apps)** when Windows Installer can't be asked. A repo with no full release yet says **No release yet**. **Update** (an update is out) and **Install** (Gantt-It or Splash-It not installed yet) install it for you; **Release page** opens the app's latest release on GitHub.
- **Add-ins:** says "Coming in a later update".
- **Settings:**
  - **Theme:** System (follows your Windows app theme), Light or Dark.
  - **Check for updates automatically:** on by default. The first time the tray starts, a notice explains what this does.
  - **About:** the version and a link to the log folder.

Click the icon to open the window. Right-click it for the menu: **Open RST**, **Check for updates** (checks now and opens the Updates page), **Settings** and **Quit**. Closing the window leaves the tray running.

## Where it lives

| What | Where |
|---|---|
| The app | `%LocalAppData%\Programs\RST\Tray\RST.Tray.exe` (`RST.msi`, just for you); `C:\Program Files\RST\Tray\RST.Tray.exe` (`RST-Machine.msi`, everyone on this PC) |
| Start at sign-in | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (`RST.msi`) or `HKLM\…\Run` (`RST-Machine.msi`), value `RST` = `"…\RST.Tray.exe" --startup` |
| Settings | `%AppData%\RST\tray_settings.json` |
| Log | `%AppData%\RST\logs\tray_<yyyyMMdd>.log` (the newest 10 files are kept) |
| Last update check | `%AppData%\RST\tray_updates.json`: the latest versions found and the ones already announced |
| Download cache (later updates) | `%LocalAppData%\RST\Tray\cache\` |

Each Windows user has their own tray, settings and log, whichever MSI installed it: a per-machine install shares only the program files. Only one tray runs per user and sign-in session: starting `RST.Tray.exe` again brings the running tray's window to the front.

The tray's log sits beside Revit's `rst_*.log` session logs. Each keeps its own ten files, and neither one's automatic clean-up deletes the other's. Health's **RST Logs** clean-up, which you start yourself, clears both, except the log the tray has open.

## Updates

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
- **Nothing about you.** The requests carry no account, no ID, no version, nothing about your PC, your models or your projects: only the address above and the User-Agent `RST-Tray`. GitHub sees your network's IP address, as it does for any web request. The tray has no telemetry.
- **No sign-in.** The tray never sends credentials to GitHub and keeps no cookies. If your network uses a proxy, the tray uses the Windows proxy settings (as Edge does), and your Windows sign-in only to authenticate to that proxy.
- **Installs:** when you click Install or Update, the tray downloads that release's `SHA256SUMS.txt` and the installer from `github.com` (redirected to GitHub's release-asset host), and makes one `api.github.com` call to read GitHub's checksum. Nothing else.

The hosts, for an allowlist: `github.com`, `api.github.com`, `release-assets.githubusercontent.com`, `objects.githubusercontent.com`. With checks off, none of them is contacted unless someone clicks Check now, Install or Update.

### Switching checks off

- **For yourself:** Settings, then turn off **Check for updates automatically**. The Updates page then says "Automatic checks are off".
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

## Installed for everyone on this PC (IT)

`RST-Machine.msi` (or `RST-Setup.exe`, "Everyone on this PC") installs the tray once, in `C:\Program Files\RST\Tray\`, and the `HKLM` Run value starts it for every user at sign-in. Each user still gets their own tray process, settings, log and add-in choices. Details of the package are in [installer/README.md](../installer/README.md#per-machine-install-rst-machinemsi).

```powershell
# a deployment tool (SCCM, Intune: runs as SYSTEM) or an elevated prompt
Stop-Process -Name RST.Tray -Force -ErrorAction SilentlyContinue   # upgrades only: see below
msiexec /i RST-Machine.msi /qn /norestart /l*v "%TEMP%\RST-Machine.log"

# an administrator signed in at the PC (also installs ticked companions for that account)
RST-Setup.exe /quiet /norestart InstallScope=machine
```

Deploy `RST-Machine.msi` on its own as SYSTEM. Run as SYSTEM, `RST-Setup.exe` would register itself, and install companions, for no real user.

- **When the tray starts:** the installer never starts it. A tray started by an elevated installer would run elevated, and its icon would not come back after Explorer restarts. Each user's tray starts at their next sign-in, or when Revit starts it. A repair or upgrade asks a running tray on the installer's desktop to exit, and does not restart it, for the same reason. It comes back at the next sign-in, or with Revit.
- **Updates are an administrator's job.** A user's tray tells them a newer RST is out, but it does not install a per-machine RST, because that needs an administrator. Deploy the new `RST-Machine.msi`; it upgrades in place. Companions stay per user, so each user's tray keeps them up to date for that user.
- **Update checks:** to turn them off for every user, set `HKLM\Software\Designs-OS\RST\Tray` `UpdateChecks` = `0` ([below](#turning-update-checks-off-for-everyone-it)).
- **Signed-in users during an upgrade:** the installer asks only trays on its own desktop to exit. A deployment tool running as SYSTEM has no user's desktop, so it closes no tray at all. Any signed-in user's tray keeps its files in `C:\Program Files\RST\Tray\` open, and the upgrade then returns `3010` (restart needed). The installer may also wait up to 10 seconds for each tray. To avoid that, end every user's tray first; as SYSTEM this reaches every session:

  ```powershell
  Stop-Process -Name RST.Tray -Force -ErrorAction SilentlyContinue   # or: taskkill /f /im RST.Tray.exe
  ```

  Each tray comes back at that user's next sign-in, or when Revit starts it. Until then the icon may linger until the pointer passes over it. Revit open in any session still holds RST's engine files, and needs the restart as any add-in does.
- **Installed twice:** a user who also has their own `RST.msi` runs their own copy. Revit lets a per-user add-in of the same name win. RST logs it at every Revit start, and the tray tells that user once: "RST is installed twice … To use the shared one, remove "RST" in Settings > Apps, and keep "RST (all users)"." Their profiles and settings stay. A bare `RST-Machine.msi` refuses to install while the installing administrator has their own copy. `RST-Setup.exe`'s "Everyone on this PC" installs RST for everyone, then removes that copy. If Revit is open or a copy can't be removed, it rolls back, and the administrator keeps their own RST.
- **Going back to per user** is uninstall, then install: remove "RST (all users)", then install `RST.msi`.

## Allowlisting (AppLocker, antivirus)

RST's files are not code-signed yet, so allow them by path or by hash.

| Install | Paths |
|---|---|
| `RST.msi` (per user) | `%LocalAppData%\Programs\RST\Tray\`, `%AppData%\RST\R<NN>\app\`, `%AppData%\Autodesk\Revit\Addins\<year>\RST.Bootstrap.dll` |
| `RST-Machine.msi` (per machine) | `C:\Program Files\RST\` (tray, bootstraps and engines) |

Per-machine mode puts every RST program file under Program Files. AppLocker's default rules already allow that path, and users cannot write there. Each release's `SHA256SUMS.txt` gives the installers' hashes ([Release checksums](#release-checksums)). The tray only ever starts `msiexec.exe` from System32 and its own updater copy (later builds), and it talks only to the GitHub hosts listed under [Privacy](#privacy-what-is-sent-and-to-whom).

## Uninstalling

Uninstalling RST from **Settings → Apps** closes a running tray first, then removes the app and the `RST` Run value. `RST.msi` also removes `%LocalAppData%\RST\Tray\` (the download cache, the updater copy and the install logs). `RST-Machine.msi` leaves each user's `%LocalAppData%\RST\Tray\`, which is per-user data an administrator's uninstall cannot reach and must not delete. The tray's settings and log stay under `%AppData%\RST\` with the rest of your RST data, as profiles and logs always have.

A repair or an upgrade also closes a running tray while its files are replaced. If Windows shows a **Files in Use** dialog, the default choice closes the tray, and Windows starts it again, hidden in the notification area, when the install finishes. An installer only ever asks the tray to close. It never forces it to end, so another user's tray on the same computer is left alone.

## For developers

The installer's `util:CloseApplication` finds `RST.Tray.exe` by file name in every session. The updater copy that runs from `%LocalAppData%\RST\Tray\updater\` is therefore `RST.TrayUpdater.exe` (the apphost renamed; it still loads `RST.Tray.dll` beside it). Otherwise the install it starts would ask it to close part-way through. `--apply` is honoured only by that copy in that folder, and only for an `RST.msi` the tray verified into its cache (`verified.json`, SHA256 and Property table checked again).
