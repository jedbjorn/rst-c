# RST Installer

The MSIs for RST, built with [WiX Toolset v6](https://wixtoolset.org/).

One MSI project lives in this repo. It builds two packages from the same sources (feature #17, decision #15):

| Project | Output | Scope | Covers |
|---|---|---|---|
| `installer/RST.Installer.wixproj` | `RST.msi` | per user, no administrator | Revit 2025, 2026 and 2027 |
| `installer/RST.Installer.wixproj -p:RstScope=machine` | `RST-Machine.msi` ("RST (all users)") | per machine, needs an administrator | Revit 2025, 2026 and 2027 |

Pick one per PC: [Per-machine install (RST-Machine.msi)](#per-machine-install-rst-machinemsi) explains the second one.

`RST-Setup.exe`, the setup bundle that chains these MSIs with the opt-in companion add-ins, is built from [`installer-bundle/`](../installer-bundle/README.md) and published beside them. Its first page asks "Just for me (recommended)" or "Everyone on this PC".

Releases v1.0.0 to v2.2.0 also shipped a standalone Revit 2027 preview MSI ("RST (Revit 2027 preview)"). From v3.0.0 Revit 2027 is folded into `RST.msi` and that project is retired; see [Migrating from the Revit 2027 preview](#migrating-from-the-revit-2027-preview).

## Dependencies

**Build-time:**

- **.NET SDK 8.0** — required for the R25/R26 build path (`net8.0-windows` engine)
- **.NET SDK 10.0** — required to stage R27 (`net10.0-windows` engine). The WiX build itself needs only SDK 8: the MSI just carries the staged files. CI installs SDK 10 via `actions/setup-dotnet` on the R27 legs.
- **WixToolset.Sdk 6.0.2** — pulled automatically when the wixproj restores. Version is pinned: the `<Files>` harvest element used in `Product.wxs` was introduced in WiX v5, so v4.x trips `WIX0005` regardless of where the element is parented (RST-040). v6.0.2 is the latest patch on the v6 line; bumping to v7 is a one-line SDK change if/when needed.
- **Windows** for production-grade emission. WiX runs on Linux but the `WIX0000` warning ("behavior is undefined") and the absence of ICE validation make it suitable for syntax iteration only.

**Pre-build artefacts** — `Product.wxs` uses `<Files Include="..\build\R<NN>\…">` to harvest the staged engine bundle. Stage all three Revit majors before building:

```powershell
# from repo root
bash build/stage.sh R25 Release    # populates build/R25/{addins,app}/
bash build/stage.sh R26 Release    # populates build/R26/{addins,app}/
bash build/stage.sh R27 Release    # populates build/R27/{addins,app}/  (needs .NET 10 SDK)
```

`stage.sh` runs the full solution build per config and copies outputs into the layout `Product.wxs` expects. Re-running is idempotent.

**Suppressed ICE rules** — the wixproj sets `<SuppressIces>ICE38;ICE64;ICE91</SuppressIces>`. These three rules fire spuriously on every per-user MSI; the WiX team has acknowledged the gap (`wixtoolset/issues#8633`). Combined with the repo-wide `TreatWarningsAsErrors=true`, leaving them on blocks the build despite being non-issues for `Scope="perUser"`.

## Build

```powershell
# After staging (above), from repo root:
dotnet build installer\RST.Installer.wixproj -c Release
# → installer\bin\Release\RST.msi  (Revit 2025 + 2026 + 2027)
```

Local and push/PR builds without `-p:RstMsiVersion=` are stamped with the `.wixproj` fallback version (`3.0.99`), which sits between the latest release (v3.0.0) and the next (v3.1.0) so a dev build upgrades a released install in place.

**Generated per-year components** — `<Files Include>` harvesting cannot carry a component `Condition`, so the `.wixproj` target `RstGenerateRevitYears` runs `installer/gen-revit-years.ps1` before compiling. It writes `installer/obj/RevitYears.g.wxs` (`installer/obj/machine/RevitYears.g.wxs` for `RST-Machine.msi`): component groups `Revit2025`/`Revit2026`/`Revit2027`, with one component per staged file and each component carrying its year's condition and `Transitive="yes"`. The file is regenerated on every build and never committed. The package is **x64**, as in gantt-it and splash-it, because the Revit detection reads the 64-bit registry (ICE80).

**CI checks** — `installer/ci-smoke.ps1` runs on `windows-latest` in `_build.yml`.
- **Every mode** first reads the built package and checks:
  - every component belongs to one Revit year and carries `REVIT<year>FOUND OR REMOVE~="ALL"` and Transitive;
  - the per-year counts equal the staged files;
  - the launch condition exists;
  - `RemoveExistingProducts` is sequenced inside the install transaction.
- **`-Mode Install`** runs on every build. Stub `Revit.exe` files in `C:\Program Files\Autodesk\Revit <year>\` drive the detection through these cases:
  - no Revit: the install is refused;
  - 2026 + 2027 only: only those years are installed;
  - 2025 added, then Repair: 2025 is installed;
  - 2026 removed, then Repair: 2026 is dropped;
  - all three, same-version rebuild: one product;
  - uninstall: every tree is gone, and user data survives.
  - the tray (SC-044): the first install leaves exactly one tray running, `--startup`, as the runner's user, in its session, not elevated (like the user's Explorer), no window, started by `RstStartTray`; a refused install starts none; a repair or upgrade with a tray running leaves exactly one, started by `RstStartTray` (its log names it; not a child of `msiexec`), and `MSIDISABLERMRESTART=1` is in the package; a no-op reinstall leaves the running one alone (`RstStartTray`: "already holds"); an uninstall starts none and runs no `RstStartTray`. `ci-machine-smoke.ps1`, `ci-tray-selfupdate.ps1` and `installer-bundle/ci-bundle-smoke.ps1` check their own paths (shared checks: `ci-tray-start.ps1`).
- **`-Mode Migrate`** (job `msi-migrate`) upgrades from the public v2.2.0 preview MSI, from v2.2.0 `RST.msi` and from v3.0.0 `RST.msi`. Each upgrade leaves exactly one tray running, hidden, as the runner's user, not elevated.

## Setup UI & branding

The MSI references the `WixUI_Minimal` dialog set (from `WixToolset.UI.wixext`, pinned centrally in `Directory.Packages.props`). The interactive flow is welcome + license → progress → finish; a silent `/qn` install skips it entirely. Per-user installs have nothing to configure, so the minimal flow is deliberate — no install-dir or feature-selection dialogs.

Artwork and the license live in `installer/branding/`:

| Asset | Bound to | Size / format | Shown on |
|---|---|---|---|
| `banner.bmp` | `WixUIBannerBmp` | 493×58 BMP | top of every dialog |
| `dialog.bmp` | `WixUIDialogBmp` | 493×312 BMP | welcome + exit pages |
| `License.rtf` | `WixUILicenseRtf` | RTF | license page (MIT) |
| `rst.ico` | `ARPPRODUCTICON` | 48×48 ICO | Add/Remove Programs |

The bitmaps are generated from the bundled `src/RST.Engine/Assets/branding.png` mark (the blue "RST" monogram) — a brand sidebar with a vector "RST" wordmark on the welcome panel, the mark in the banner corner. WiX requires **BMP** for the banner/dialog and **RTF** for the license; PNG/Markdown are not accepted there.

**Documentation links** surface in Add/Remove Programs (Settings → Apps → RST → Advanced options):

- `ARPHELPLINK` → `https://github.com/jedbjorn/rst-c/tree/main/docs` (the repo's `/docs`)
- `ARPURLINFOABOUT` → the repo
- `ARPURLUPDATEINFO` → the latest release

## What the MSI installs

`RST.msi` uses the RST-033 layout: a tiny bootstrap thunk in Revit's add-in directory, the engine + transitives in a per-major subdir under `%AppData%\RST\`. The bootstrap derives `R<major>` from `application.ControlledApplication.VersionNumber` at `OnStartup` (RST-037) and loads the matching engine.

```
%AppData%\Autodesk\Revit\Addins\<ver>\          ← .addin manifest target
  RST.addin
  RST.Bootstrap.dll
  RST.Bootstrap.pdb

%AppData%\RST\R<NN>\app\                         ← engine target
  RST.Engine.dll
  RST.UI.dll
  RST.Core.dll
  RST.Engine.deps.json
  Assets\**\*
  *.pdb
```

No third-party DLL ships in either tree (decision #33): RST runs in Revit's shared load context,
where another add-in's copy of a same-named DLL can bind in place of RST's. CI's
`build/check-shipped-dlls.sh` fails the Stage jobs on any DLL that is not `RST.*` and not on
`build/third-party-allowlist.txt` (empty), and `ci-smoke.ps1` checks the installed trees.

`RST.msi` carries three pairs of trees: Addins\2025 + RST\R25\app, Addins\2026 + RST\R26\app, and Addins\2027 + RST\R27\app. R25 and R26 carry the `net8.0-windows` engine. R27 carries the `net10.0-windows` engine, with its own `RST.Engine.deps.json`.

**Only the Revit years installed on the computer get their trees.** The detection is the same as gantt-it and splash-it. A year counts as installed when `Revit.exe` is found in one of two places:
- the `InstallLocation` of Revit's uninstall entry `{7346B4A0-<yy>00-0510-0000-705C0D862004}`, read from the 64-bit registry;
- the default folder, `C:\Program Files\Autodesk\Revit <year>\`.

What follows from that:
- **No supported Revit:** a first install is refused with "RST needs Revit 2025, 2026 or 2027, and none was found on this computer." Repair and uninstall are never blocked.
- **Revit year installed later:** run the same `RST.msi` again and choose **Repair** (or `msiexec /fa RST.msi`). The repair adds the new year's trees. It also drops a year whose Revit has since been uninstalled. The next RST upgrade does the same.
- **Upgrade:** a newer `RST.msi` removes the old version completely, every year included, then installs fresh for the years it detects. Files an older RST put in a year whose Revit is absent are removed.
- **Uninstall:** always removes every year.

**User data** (`%AppData%\RST\profiles\`, `\logs\`, `\branding.png`, `\active_profile.json`, `\user_profile_prefs.json`, etc.) lives outside the install trees and is preserved across reinstalls and uninstalls.

## Install / uninstall

Per-user MSI — no admin elevation needed.

```powershell
# Install (silent)
msiexec /i RST.msi /qn

# Install (interactive, with progress bar)
msiexec /i RST.msi

# Uninstall (silent)
msiexec /x RST.msi /qn

# Or uninstall via Settings → Apps → "RST"
```

### When the tray starts

`RST.Tray.exe` is started in these ways, always with `--startup` (hidden in the notification area, no window), except the self-update's start:

| Start | Who starts it | Notes |
|---|---|---|
| Sign-in | The `RST` Run value (`HKCU` for `RST.msi`, `HKLM` for `RST-Machine.msi`) | Task Manager's Startup toggle is respected. |
| Revit | RST's add-in (`src/RST.Engine/Tray/TrayLauncher.cs`) | Only when no tray runs, and never from an elevated Revit. |
| After an install, repair or upgrade (SC-044, decision #45) | The installer's `RstStartTray` custom action (`Tray.wxs`, `helper\StartTray.cs`), for the user who ran it | See below. |
| After an installer closed a running tray | Nobody else. | Restart Manager still closes a running tray for a repair or upgrade, but never restarts it: `RST.msi` sets `MSIDISABLERMRESTART=1`, and `RST-Machine.msi` turns Restart Manager off. Its restart would come from `msiexec`, elevated when `msiexec` is (SC-044 review B1). `RstStartTray` starts the tray instead. |
| After a self-update | The tray's updater copy (`RST.TrayUpdater.exe`) | No switch: the window opens with the result. |

**`RstStartTray`** is an immediate custom action after `InstallFinalize`, run from the MSI's Binary table (it is never installed), with `Return="ignore"`. The install is already committed when it runs, so it can never fail or roll an install back. It does not run on an uninstall, or when an upgrade removes the old version (`REMOVE=ALL` in both). In order, it:

1. starts none when it runs as a service account (SYSTEM, LocalService, NetworkService) or in session 0. That is a deployment tool such as SCCM or Intune. Each user's tray then starts at their sign-in.
2. starts none when the tray's mutex `Local\RST.Tray.<UserSid>` exists. A tray already runs (a reinstall over a running tray), or RST's self-updater holds it while its `msiexec` runs, and then starts the tray itself. So a self-update still ends with the updater's one tray, window shown, and the updater needs no property to turn this off.
3. finds this user's desktop shell: the Explorer that owns the desktop's shell window, or else an `explorer.exe` in this session running as the same user. An Explorer of another user (as with `runas`) is never used, nor one elevated through UAC (a full split token, such as an Explorer started "as administrator"). The tray is created with that Explorer as its parent (`PROC_THREAD_ATTRIBUTE_PARENT_PROCESS`), so Windows gives it Explorer's token, session and job, and Explorer's environment. It runs as the user, unelevated, outside `msiexec`'s process tree, exactly as at sign-in. The process handle opened for this is checked again (user, session, elevation, `explorer.exe` image), so a reused process id is never used.
4. with no such shell, starts none if this process is elevated (or its token cannot be read), because that tray would run elevated and lose its icon after an Explorer restart (WU1 review N8). Otherwise, or with UAC off (`EnableLUA=0`, where every administrator's process has the full token), it starts the tray directly on the interactive desktop (`winsta0\default`). **CI never takes this direct route** (every runner has an Explorer), so it is checked on the VM only, like the split-token claim below.
5. waits up to 20 seconds until the tray holds its mutex, or exits because another launch holds it (SC-022).

Each run also deletes this helper's own logs older than 7 days.

Each run writes `RST-StartTray-<yyyyMMdd-HHmmss>-<pid>.log` in the installer's `%TEMP%` (as SYSTEM: `C:\Windows\Temp\` or `C:\Windows\SystemTemp\`). It says which user and token it ran with, and what it started or why it started none.

| Path | What starts the tray |
|---|---|
| `RST.msi`, with the UI or `/qn` | `RstStartTray`, as the user who runs it (elevated only when started from an elevated prompt), through that user's Explorer, so the tray is not elevated. |
| `RST-Machine.msi` from an elevated prompt, or double-clicked (UAC) | `RstStartTray`, through the installing user's own unelevated Explorer, so the tray is not elevated. When a standard user types an administrator's credentials, the tray either starts unelevated for the signed-in user or, if no suitable Explorer is found, arrives at the next sign-in. It never starts elevated. |
| `RST-Machine.msi` as SYSTEM (deployment tools) | Nothing. Each user's tray starts at their sign-in. |
| `RST-Setup.exe`, "Just for me" | `RST.msi`'s `RstStartTray`, in Burn's per-user engine. |
| `RST-Setup.exe`, "Everyone on this PC" | The bundle's `RemoveUserRst` helper, in the bundle's own process, after it has removed the user's own RST. `RST-Machine.msi` runs in Burn's elevated engine with `RSTFROMSETUP=1`, and skips its own start: removing the user's `RST.msi` closes every `RST.Tray.exe` on the desktop by name (its `util:CloseApplication`), so a tray started before it would not survive. The helper compiles in the same `StartTray.cs`. |
| Self-update (the tray's **Update** button) | The updater, as before. `RST.msi`'s `RstStartTray` finds the updater holding the mutex and starts none. |
| Any uninstall | Nothing. |

The tray's one-time update-check notice opens on the tray's first start. With `RstStartTray` that is now right after the first install, not at the next sign-in (kept on purpose: the Planner's call, review N5). The tray itself opens no window.

## Per-machine install (RST-Machine.msi)

`RST-Machine.msi` installs RST once for everyone on the PC. It needs an administrator, and it is for PCs where IT installs software. Most people should use `RST.msi`, or `RST-Setup.exe`'s "Just for me". It is the same RST: the same engine, tray and Revit years, built from the same `Product.wxs` and `Tray.wxs` with the preprocessor variable `Scope=machine`.

```powershell
dotnet build installer\RST.Installer.wixproj -c Release -p:RstScope=machine
# → installer\bin\Release\RST-Machine.msi  (obj\machine\ keeps it apart from RST.msi's build)
```

**What it installs:**

```
C:\Program Files\RST\R<NN>\RST.Bootstrap.dll         ← bootstrap (+ .pdb), one per Revit year
C:\Program Files\RST\R<NN>\app\                      ← engine, as in RST.msi
%ProgramData%\Autodesk\Revit\Addins\2025\RST.addin   ← Revit 2025 and 2026 read all-users manifests here
%ProgramData%\Autodesk\Revit\Addins\2026\RST.addin
C:\Program Files\Autodesk\Revit\Addins\2027\RST.addin ← Revit 2027 reads them from Program Files only
C:\Program Files\RST\Tray\RST.Tray.exe               ← the tray
HKLM\Software\Microsoft\Windows\CurrentVersion\Run   RST = "C:\Program Files\RST\Tray\RST.Tray.exe" --startup
```

- **Manifests:** each one's `<Assembly>` is set at install time to the absolute path of its year's bootstrap (`util:XmlFile`, generated by `gen-revit-years.ps1 -Scope machine`). The bootstrap loads the engine from the `app\` folder beside it (`RstBootstrap.cs`, `EngineLocation`).
- **Rules shared with RST.msi:** the same Revit detection, launch condition, Repair-adds-a-year behaviour and `MajorUpgrade` settings. Only detected years are installed.
- **Grants nothing:** there is no permission change, no service, and nothing elevated is left behind. Every folder and file inherits its parent's permissions. Add-in switching stays per user, exactly as with `RST.msi` (research §Q1, recommendation A). The custom actions are WiX's own `XmlFile`, `CloseApplication` and WixUI actions, and two of RST's own: `RstEndTrays` (`helper\EndTrays.cs`, SC-028; see "Signed-in users" below) and `RstStartTray` (`helper\StartTray.cs`, SC-044). They run only while the package installs or uninstalls and are never installed.
- **The tray:** a tray started by the elevated installer itself would run elevated and lose its icon after an Explorer restart (WU1 review N8). So Restart Manager is off (`MSIRESTARTMANAGERCONTROL=Disable`), and nothing restarts a closed tray from `msiexec`. Under `/qn`, Restart Manager would close the tray and restart it from the elevated `msiexec` (CI showed that `DisableShutdown` is not honoured there), and it would also close other users' applications, Revit included. `util:CloseApplication` asks a running tray on the installer's desktop to exit, and `RstEndTrays` ends this package's trays in every other session; see "Signed-in users" below. After a successful install, repair or upgrade, `RstStartTray` starts the installing administrator's tray through their own Explorer, so it is not elevated (SC-044; see [When the tray starts](#when-the-tray-starts)). As SYSTEM it starts none. Every other user's tray starts at their next sign-in from the HKLM Run value, or when Revit starts it.
- **Components:** every component has its own GUID. The generated ones differ because their paths differ, and `TrayStartup` has a fixed GUID of its own (WU1 review N6). No ComponentId is shared with `RST.msi`.
- **No download cache clean-up:** the tray's cache is per user (`%LocalAppData%\RST\Tray\cache\`), so one administrator's uninstall cannot reach every user's. It is user data, like `%AppData%\RST\`.

**Both installed:** Revit lets a per-user manifest of the same name win, so a user who still has `RST.msi` runs their own copy, not the shared one.
- **The installing user:** a first install of `RST-Machine.msi` is refused while that user has `RST.msi` installed. The message is: "RST is already installed just for you (<path of the per-user RST.addin>) and would be used instead of this copy. Remove "RST" in Settings > Apps (or delete that file), then try again. RST-Setup.exe does this for you: choose "Everyone on this PC"." Repair and uninstall are never blocked. Through `RST-Setup.exe` (`RSTFROMSETUP=1`) the check is skipped: setup installs RST-Machine beside the user's copy, then removes that copy (rolling RST-Machine back if it cannot), and a leftover hand-copied manifest is treated as a shadow.
- **Other users:** an administrator's install cannot remove their per-user copies. For them, RST logs it once at every Revit start, and the tray tells them once: "RST is installed twice … To use the shared one, remove "RST" in Settings > Apps, and keep "RST (all users)"." Their profiles and settings stay.

**Silent and IT use** (from an elevated prompt, or SCCM/Intune as SYSTEM):

```powershell
msiexec /i RST-Machine.msi /qn /norestart /l*v "%TEMP%\RST-Machine.log"
msiexec /x RST-Machine.msi /qn /norestart
```

Deploy `RST-Machine.msi` on its own as SYSTEM. `RST-Setup.exe` with `InstallScope=machine` is for an administrator signed in at the PC: as SYSTEM it would register itself, and install companions, for no real user.

- **Signed-in users:** an uninstall, repair or upgrade ends every signed-in user's tray before it removes the files, in every session, also when it runs as SYSTEM (SCCM, Intune), which has no user's desktop (SC-028). There is nothing to do first. It works in two steps:
  1. `CloseApplication` asks a tray on the installer's own desktop to exit (it closes cleanly on `WM_CLOSE`). It waits up to 10 seconds for it.
  2. `RstEndTrays` then runs as SYSTEM, just before the files are removed. It ends every process whose program is exactly `C:\Program Files\RST\Tray\RST.Tray.exe`, in any session. A user's own per-user tray (`%LocalAppData%\Programs\RST\Tray\RST.Tray.exe`) has the same name but is left alone. The tray holds no unsaved data. Each one comes back at that user's next sign-in, or when Revit starts it. Until then its icon may linger until the pointer passes over it.

  On a full uninstall, `RstEndTrays` then asks Restart Manager whether anything else still uses a file under `C:\Program Files\RST\`, typically Revit with RST loaded in some session. If so, `msiexec` returns `3010` (restart needed) instead of `0`. Windows Installer has already removed or renamed those files, and the renamed copies go at the next restart. CI proves both results as SYSTEM and from an administrator's own session. An install, repair or upgrade skips this check: Windows Installer itself returns `3010` when it must replace a file in use, and a repair that replaces nothing returns `0` even while Revit runs.
  - **How long the restart flag lasts:** `RstEndTrays` uses WiX's own restart flag, the same one `util:CloseApplication`'s reboot prompt uses. It lasts until the restart. Run as SYSTEM, that covers every later install by SYSTEM, not just RST's: any other WiX-built MSI that checks the flag also returns `3010` until the PC restarts. From an administrator's session, it covers that session's later installs. A restart is genuinely pending in both cases, and the restart clears it.
  - **If the uninstall rolls back** after `RstEndTrays` ran, the trays it ended stay ended. Each comes back at that user's next sign-in, or when Revit starts it. A restart flag it set also stays.
  - **Log:** each run writes its own `RST-Machine-EndTrays-<yyyyMMdd-HHmmss>-<pid>.log` in the installer's `%TEMP%` (as SYSTEM: `C:\Windows\Temp\`, or `C:\Windows\SystemTemp\`). It lists each tray it ended, each one it left alone, and each process still using the files. In the MSI log, `RstEndTrays returned actual error code 3010` means a restart is needed.
- **Updates:** updating a per-machine RST is an administrator's job. A user's tray only reports a newer release. It does not install it, because that would need an administrator (spec #20 §5). Deploy the new `RST-Machine.msi` the same way: it upgrades in place.
- **Update checks:** `HKLM\Software\Designs-OS\RST\Tray` `UpdateChecks` (DWORD) = `0` turns the tray's update checks off for every user, and locks the setting ([docs/tray.md](../docs/tray.md)).
- **Switching back to per user** is uninstall, then install: remove "RST (all users)", then install `RST.msi` (or `RST-Setup.exe`, "Just for me"). Nothing switches by itself. User data under `%AppData%\RST\` is untouched either way.

**CI checks:** `installer/ci-machine-smoke.ps1` runs in job `msi-machine` on a `windows-latest` runner, which runs as an administrator.
- **Package:** the identity (UpgradeCode, per machine, name), the shared sequencing and launch conditions, the per-year component conditions and counts, and no ComponentId shared with `RST.msi`. It also checks there are no service, permission, user or group tables, that every custom action is WiX's own, `RstEndTrays` (deferred, as SYSTEM, `Return="ignore"`, before `RemoveFiles`, aimed at `[TrayDir]RST.Tray.exe`) or `RstStartTray` (immediate, `Return="ignore"`, after `InstallFinalize`, never on `REMOVE=ALL` or under `RST-Setup.exe`), that WiX's `CheckRebootRequired` also runs on uninstall, and that the only registry row is the Run value.
- **Install:** refusal while `RST.msi` is installed for the user; refusal with no Revit; 2026 + 2027 only, then Repair adds 2025; folder and Run-key permissions compared before and after, everything created only inherits, and no service or scheduled task is added. The install from the runner's elevated session leaves exactly one tray: `--startup`, as the runner's user, in its session, not elevated (like the user's Explorer), not `msiexec`'s child, no window, started by `RstStartTray` through Explorer. The repair closes it and starts exactly one new one. An upgrade from a 1.0.0 build of the same commit with a tray running: closed by WM_CLOSE, never restarted by Restart Manager, and exactly one new, unelevated tray. Each uninstall leaves no tray and runs no `RstStartTray`.
- **Install and repair as SYSTEM (SC-044):** `msiexec /i` as SYSTEM from a scheduled task starts no tray, and `RstStartTray`'s log says it ran as a service account. A repair as SYSTEM ends the running tray and starts none.
- **Uninstall as SYSTEM (SC-028):** `msiexec /x` runs as SYSTEM in session 0 from a scheduled task, with the tray running in the runner's own session. The tray is ended (exit code 1, logged by `RstEndTrays`), a stand-in for a per-user tray named `RST.Tray.exe` in `%LocalAppData%\Programs\RST\Tray\` keeps running, nothing is left, and the exit code is `0`. With another process holding `R26\app\RST.Core.dll` open, a repair as SYSTEM that replaces nothing (`REINSTALLMODE=omus`) returns `0`, and the uninstall returns `3010` with that process still running. The same uninstall from the runner's elevated session, file held, also returns `3010`.

### Migrating from the Revit 2027 preview

Installing `RST.msi` (v3.0.0 or later) removes an installed "RST (Revit 2027 preview)" automatically — no manual uninstall, and it works under `msiexec /qn`. `Product.wxs` carries an `<Upgrade>` row on the preview's retired `UpgradeCode` (any version, `OnlyDetect="no"`), so the `RemoveExistingProducts` action that `MajorUpgrade` schedules removes the preview before the new files install. `MajorUpgrade` is scheduled `afterInstallInitialize`, so that removal is part of the install transaction: if the new install fails, the rollback reinstalls the preview (or the previous `RST`). Afterwards Settings → Apps shows a single "RST". Removal is per-user: other Windows users on the same machine migrate when they run the new MSI. User data under `%AppData%\RST\` is not touched.

The row stays indefinitely, so if someone installs an old `RST-R27.msi` from a past release after the fold, the next `RST.msi` install removes it again.

## Versioning

**Source of truth:** the release **tag**. `release.yml`'s `prepare` job strips the leading `v` and any `-prerelease` suffix from the tag down to a numeric `a.b.c` and passes it to `_build.yml`, which stamps `RST.msi` via `-p:RstMsiVersion=a.b.c`. The WXS consumes it as `$(var.RstMsiVersion)`; the `.wixproj` carries a fallback used only for local builds without an explicit `-p:RstMsiVersion=`. The stamped version is what Add/Remove Programs displays and what the MSI engine uses to decide whether an installer is an upgrade, downgrade, or sidegrade vs. the currently installed product.

**Release flow:**

1. Tag from `main` HEAD: `git tag -a v1.0.0 -m "…" && git push origin v1.0.0`. No version-bump commit is needed — the tag carries the version.
2. `.github/workflows/release.yml` fires on the `v*` tag, runs the matrix in `_build.yml`, creates a GitHub Release at `releases/tag/v1.0.0`, and attaches `RST.msi` (Revit 2025 + 2026 + 2027) by name, with `RST-Machine.msi`, `RST-Setup.exe` and `SHA256SUMS.txt`. Both MSIs carry the same version. Every major, R27 included, is a hard gate: an R27 build failure fails the workflow and blocks the release.
3. The installed ProductVersion always matches the numeric part of the tag — driving `MajorUpgrade` / `RemoveExistingProducts` on in-place upgrade. A new release raises it, **except** that `v3.0.0-rc.N` and `v3.0.0` all stamp `3.0.0`: an rc → rc → final install is a same-version upgrade, which works only because `MajorUpgrade` sets `AllowSameVersionUpgrades="yes"`. Do not remove that attribute; without it the final installs side by side with the rc as a second "RST".

**Pre-release tags:** any tag with a hyphen (`v1.0.0-rc.1`, `v0.2.0-alpha`) is flagged as pre-release on the GitHub Release.

**`UpgradeCode` is immutable.** Each RST product carries a fixed GUID:

| MSI | UpgradeCode | Status |
|---|---|---|
| `RST.msi` | `AF0331FC-5280-492A-94BD-95EDF108BE74` | Current |
| `RST-Machine.msi` ("RST (all users)", from v3.1.0) | `79C25959-C28A-4ED8-8984-85ED525476C3` | Current. A different product from `RST.msi` by design: Windows Installer never upgrades across install contexts |
| `RST-R27.msi` ("RST (Revit 2027 preview)", v1.0.0–v2.2.0) | `02BE4399-A563-4B15-BD12-ECF5D8486FB3` | **Retired, never reuse.** Referenced only by the migration `<Upgrade>` row in `Product.wxs` |

**Never change a shipped `UpgradeCode`.** It's how Windows Installer recognises a new MSI as an upgrade of a prior install rather than a parallel product. Changing it leaves orphaned installs in Add/Remove Programs.

`<MajorUpgrade>` handles the upgrade in place: installing v1.0.1 on a machine with v1.0.0 silently uninstalls the old and installs the new without losing user data.

## Code signing

Currently unsigned. `build/sign.targets` is a placeholder with `signtool` hooks scaffolded but the cert source (`SignAssemblies=true`, `SignCertThumbprint=…`) is not yet wired into CI. Tracked under RST-042; once that lands, the unsigned line in the project's top-level README intro can also be reverted.
