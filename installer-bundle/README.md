# RST Setup bundle (`RST-Setup.exe`)

`RST-Setup.exe` is a [WiX v6](https://wixtoolset.org/) Burn bundle (feature #13, spec #15). Its first page asks who RST is for (feature #17, spec #20 §7): **Just for me (recommended)** installs `RST.msi`, and **Everyone on this PC** installs `RST-Machine.msi` (see [Install scope](#install-scope)). It offers each **shippable** companion add-in from [`companions/companions.json`](../companions/companions.json) as an opt-in checkbox. Ticking one installs that companion's own MSI silently and unmodified. After setup every product is a stock install:

- rst-c sits behind the bundle's **RST** entry in Settings → Apps.
- Each companion has its **own** entry, upgrades through its own MSI, and has no runtime link to rst-c.
- **Uninstalling RST does not remove the companions.** They are permanent: remove each from Settings → Apps. The setup's finish page says so.

The bare `RST.msi` and `RST-Machine.msi` are still published beside it, for IT deployments that want only rst-c.

## Identity

| | Value |
|---|---|
| Bundle `UpgradeCode` | **`2BE2BE5D-9203-4344-A70D-E735D3B041A3`**: never change it once shipped |
| Name in Settings → Apps | `RST` (Designs/OS) |
| Version | the numeric `a.b.c` of the rst-c tag, the same value stamped into `RST.msi` (`v3.0.0-rc.1` → `3.0.0`); untagged builds `3.0.99` |
| Scope | per-user: the bundle is registered in HKCU, for the user who ran it, on both install-scope paths. "Just for me" chains only per-user packages and never asks for elevation; "Everyone on this PC" elevates once, for `RST-Machine.msi` |
| Engine | x64, `WixStandardBootstrapperApplication` (`WixToolset.BootstrapperApplications.wixext` 6.0.2), RTF license theme |

**Never change the `UpgradeCode`.** It is how a newer `RST-Setup.exe` finds and replaces an installed older one. It is written as a literal GUID in `Bundle.wxs`; the companion registry test (`src/RST.Tests/Companions/CompanionRegistryTests.cs`) reads it so no companion can reuse it, and fails if it changes or stops being a literal.

A same-version bundle (an rc and its final, both `3.0.0`) replaces the installed one: Burn plans an equal version as an upgrade.

## What it installs

| Package | When | Burn attributes | Add/Remove Programs |
|---|---|---|---|
| `RST-Machine.msi` (`RSTMachine`), first in the chain | "Everyone on this PC" only | per machine, vital, `Visible="yes"`, `RSTFROMSETUP=1` | its own `RST (all users)` entry, which every administrator sees |
| `RST.RemoveUserRst.exe` (`RemoveUserRst`, from `helper\RemoveUserRst.cs`), after `RST-Machine.msi` | "Everyone on this PC" only: checks Revit is closed and every copy can be removed, then removes the user's own `RST.msi` (any version) and the retired 2027 preview, unelevated. A stop rolls `RST-Machine.msi` back | `PerMachine="no"`, `Permanent="yes"`, `Vital="yes"`, no detection, `Cache="remove"` | none |
| `RST.msi` (`RST`) | "Just for me" (the default). On "Everyone on this PC", Burn removes it if this version is installed for the user (the helper already has) | vital (a failure fails the setup and rolls back) | hidden behind the bundle's `RST` entry |
| each shippable companion (`GanttIt-1.1.0.msi`, `SplashIt-1.1.0.msi`) | only when its box is ticked, on either path, for the installing user | `Permanent="yes"`, `Visible="yes"`, `Vital="no"`, `InstallCondition="Install<Id> = 1"` | its own entry |

Everything is embedded: `RST-Setup.exe` is one offline file, and nothing is downloaded at install time.

`RST.msi`'s Revit check still applies: a first install fails when no Revit 2025, 2026 or 2027 is found, and so does the setup. Both current companions carry the same check. A companion is non-vital, so its failure is reported in the log but never rolls back rst-c.

"Unmodified" means the companion's released MSI file is embedded byte for byte (sha256-pinned) and run with no transform and no properties of ours. Burn itself passes its usual properties to every MSI it runs (for example `REBOOT=ReallySuppress`, `MSIFASTINSTALL`), as it does for `RST.msi`.

## Install scope

The first page has two radio buttons, worded as spec #20 §7 words them:

- **Just for me (recommended):** "Installs for your Windows account only. No administrator needed. RST can switch off any add-in for you, including ones installed for everyone."
- **Everyone on this PC:** "Installs once for all users. Windows will ask for an administrator. Each user still chooses their own add-ins. Gantt-It and Splash-It install for your account; other users can add them from the RST tray in one click."

On the "Everyone on this PC" path, the companions' header says the same: they install for your Windows account only, and other users add them from the RST tray. WixStdBA 6.0.2's theme radio buttons (`RadioButtons Name="InstallScope"`) bind to a Burn variable, so the two-button fallback was not needed. The theme uses Windows' classic system colours: it has no dark variant.

They set the Burn variable `InstallScope` to `user` or `machine`. **The user always ends with RST, in one form or the other.** In chain order, the "Everyone on this PC" path runs these steps (review D1):
1. **`RST-Machine.msi` installs first.** It is the one per-machine package, so Windows asks for an administrator once. Burn shows the prompt when the install starts, before any package runs: declining it changes nothing.
   - Setup passes `RSTFROMSETUP=1`, so `RST-Machine.msi` installs beside the user's own per-user RST, which is still there at this point. The two share no component, folder or Run key.
   - **If this step fails,** nothing per-user has been touched; the user keeps their RST.
2. **Then the user's own RST is removed, unelevated, as that user,** by `RemoveUserRst`. Burn cannot remove an older version through a package it does not carry, hence the helper.
   - **What it removes:** every per-user product of that user with `RST.msi`'s UpgradeCode, at any version (installed bare or by an older setup), and the retired Revit 2027 preview `RST-R27.msi` (`02BE4399-…`), the preview first.
   - **What it checks first, before anything is removed:**
     - Revit must be closed in the user's session. With the full UI it asks "Close Revit, then click Retry"; otherwise it stops with exit 32.
     - Each product must be removable, with its cached installer present; otherwise it stops with exit 1612.
   - **After a stop or a failure:** the helper is vital, so Burn rolls `RST-Machine.msi` back. The user keeps their own RST, and the setup reports the failure. A removal that fails midway (the preview gone, then `RST.msi` fails) still leaves `RST.msi`.
   - **A leftover per-user `RST.addin` that no installer can remove** (a hand copy) stays. It is reported like any shadow: the engine logs it, and the tray tells the user once. A bare `RST-Machine.msi` refuses it instead and names the file.
   - **Burn's own `RST` package,** whose condition is false on this path, then finds the bundled version already gone. Burn logs that and carries on.
3. **Ticked companions install for the installing user.** Companions stay per user (contract C1). Every other user adds them from their RST tray.

**The scope is found again on every run.** While `RST-Machine.msi` is installed, the scope is `machine`. "Just for me" is then disabled, and its text says to uninstall "RST (all users)" first. So a repair, an upgrade or a re-run never silently switches scope, and never quietly needs an administrator. Otherwise the scope comes from the radio buttons or the command line, and anything but `machine` (in any case) is `user`.

**Switching back** from per machine to per user is uninstall, then install. Remove the setup's "RST" entry (or "RST (all users)"), then run `RST-Setup.exe` again and choose "Just for me".

**Where the entries are:** the bundle's "RST" entry is the installing user's (the bundle is registered per user). "RST (all users)" is `RST-Machine.msi`'s own entry, visible to every user. Uninstalling the bundle removes `RST-Machine.msi` too, and asks for an administrator. Removing only "RST (all users)" leaves the installing user's bundle entry with nothing under it, as with a bare `RST.msi` (AC5 below). Burn does not ref-count a per-machine package for a per-user bundle (warning `WIX1140`, suppressed in `RST.Setup.wixproj`).

## Silent install (IT)

Each checkbox is a Burn variable named after the companion's registry id: `gantt-it` → `InstallGanttIt`, `splash-it` → `InstallSplashIt`. `1` ticks it; anything else (or leaving it out) does not. Variable names are case-insensitive.

```powershell
# rst-c + both companions, no UI
RST-Setup.exe /quiet /norestart InstallGanttIt=1 InstallSplashIt=1

# rst-c only (same as the bare RST.msi)
RST-Setup.exe /quiet /norestart

# rst-c for everyone on this PC, by an administrator signed in at the PC
# (elevated prompt), plus Gantt-It for that administrator's account.
# For SCCM/Intune (SYSTEM), deploy RST-Machine.msi on its own instead.
RST-Setup.exe /quiet /norestart InstallScope=machine InstallGanttIt=1

# with a log
RST-Setup.exe /quiet /norestart InstallGanttIt=1 InstallSplashIt=1 /log "%TEMP%\RST-Setup.log"

# uninstall rst-c (companions stay)
RST-Setup.exe /uninstall /quiet /norestart
```

**Always pass `/norestart` with `/quiet` or `/passive`.** Without it, the standard bootstrapper restarts the computer by itself, with no prompt, if any package asks for a restart, and in quiet mode it asks Windows Installer to close the applications holding files. With `/norestart` nothing restarts: a needed restart is reported as exit code `3010`, to be done later. Exit code `0` is success. No current package asks for a restart.

**Run the setup with Revit closed.** rst-c's and the companions' files are loaded by Revit while it runs.

`/passive` shows progress without prompts; it takes the same `/norestart`.

`InstallScope` is `user` by default. With `InstallScope=machine` the companions install for the account that runs the setup, and the setup registers itself for that account. Don't run the setup as SYSTEM: it would do both for no real user. For SCCM or Intune, deploy `RST-Machine.msi` on its own ([installer/README.md](../installer/README.md#per-machine-install-rst-machinemsi)). Each user then adds companions from their tray.

**Adding a companion later:** re-running the setup's UI on a computer where it is installed shows Repair/Uninstall, not the checkboxes. Run the companion's own MSI, or `RST-Setup.exe /quiet /norestart InstallGanttIt=1`.

## Install-order behavior

What the bundle smoke (`ci-bundle-smoke.ps1 -Mode Chain`, with the test-only fixture companion) proves on every build:

| Situation | Result | Smoke step |
|---|---|---|
| Box ticked, companion not installed | installed, with its own Add/Remove Programs entry | A |
| Same command run again | no change | B |
| Companion already installed, same version, box ticked | detected present and skipped | H |
| Companion installed at an older version, box ticked | upgraded in place | F |
| Companion installed at a newer version, box ticked | left alone; the setup still succeeds | G |
| Box unticked (left out, or `=0`) | companion untouched, whatever its state | D, E |
| Companion installed later from its own MSI | normal install; rst-c unaffected | D |
| `RST-Setup.exe` uninstalled | rst-c removed; companions stay | C, D, F, G, H |
| Bare `RST.msi` installed first, then `RST-Setup.exe` | see below (AC5) | `-Mode BareMsiFirst` |

### Bare `RST.msi` first, then the setup (AC5)

Observed on CI (`ci-bundle-smoke.ps1 -Mode BareMsiFirst`, first in PR #134, run 40999336203). The leg now asserts each of these, so a change fails the build:

1. `msiexec /i RST.msi /qn`: one "RST" product with its own visible Settings → Apps entry.
2. `RST-Setup.exe /quiet /norestart` (the same `RST.msi` inside, same ProductCode):
   - Burn detects `RST` as `state: Present, install registration state: Ignored` (installed, but not by this bundle).
   - It plans `execute: None` (nothing reinstalled, no files touched) with `dependency: Register, expected install registration state: Present`. **The bundle adopts the existing RST.msi as its own.**
   - There is still exactly one RST product. Because the MSI is not re-run, its own ARP entry stays visible, so **Settings → Apps shows two "RST" entries**: the bundle's and the bare MSI's.
   - Ticked companions install normally.
3. Uninstalling the bundle (Settings → Apps → the bundle's "RST", or `RST-Setup.exe /uninstall /quiet /norestart`): Burn plans `RST` as `execute: Uninstall`. **The adopted RST.msi is removed**: no RST product, no RST files, and no "RST" entry left. Companions stay, as always.

4. The other way out (recorded, not asserted): bare `RST.msi`, then the bundle, then the MSI's own entry removed (`msiexec /x`). rst-c is removed, but **the bundle's "RST" entry stays behind** with nothing under it. Uninstalling that leftover entry succeeds (Burn plans `RST` as `state: Absent, execute: None`) and removes it.

So after `RST.msi` then `RST-Setup.exe`, there is one product behind two entries, and removing either one removes rst-c. Removing the MSI's entry leaves an empty bundle entry to remove as well. This is Burn's default for a package it did not install. No workaround is applied; it was returned to the Planner (PLN1) as the spec asks.

## Build

Windows only. Build `RST.msi` and `RST-Machine.msi` first (see [`installer/README.md`](../installer/README.md)), then:

```powershell
# from the repo root
pwsh installer-bundle\fetch-companions.ps1           # downloads + verifies the pinned MSIs into installer-bundle\payload\ (needs gh)
dotnet build installer-bundle\RST.Setup.wixproj -c Release
# → installer-bundle\bin\Release\RST-Setup.exe
```

| File | Role |
|---|---|
| `RST.Setup.wixproj` | the bundle project; inputs `RstMsi`, `RstMachineMsi`, `CompanionRegistry`, `CompanionPayloadDir`, `RstBundleVersion`. It compiles `helper\RemoveUserRst.cs` with the .NET Framework 4 compiler that ships in Windows |
| `Bundle.wxs` | bundle identity, the BA, the scope variable and search, the chain (`RST.msi`, the helper, `RST-Machine.msi` + the generated `RstCompanions` group) |
| `helper\RemoveUserRst.cs` | the "Everyone on this PC" helper: checks, then removes the installing user's own per-user `RST.msi` (any version) and `RST-R27.msi` preview (C# 5, no dependencies) |
| `gen-companions.ps1` | runs before every compile: reads the registry, re-checks each payload's sha256, writes `obj\gen\Companions.g.wxs` (variables + `MsiPackage`s) and `obj\gen\thm.xml` (the theme with one checkbox per shippable companion). Zero shippable → empty chain, no checkboxes |
| `theme\RstTheme.xml`, `theme\RstTheme.wxl` | the BA theme template and strings, adapted from WiX's RtfTheme (MS-RL) |
| `fetch-companions.ps1` | downloads each pinned asset with `gh release download` and fails on a missing asset or a sha256, UpgradeCode or version mismatch |
| `fixture\` | the **test-only** "RST Companion Fixture" MSI (one text file under `%AppData%\RST-CompanionFixture\`). Never shipped, never in the registry |
| `ci-bundle-build.ps1`, `ci-bundle-smoke.ps1` | the CI `bundle` and `bundle-smoke` jobs |

## CI

`.github/workflows/_build.yml`:

- **`bundle`** (needs `msi-unified`): `fetch-companions.ps1`, then `ci-bundle-build.ps1`. It builds `RST-Setup.exe`, a zero-shippable variant, the fixture at 1.0.0 / 1.1.0 / 2.0.0 and the smoke's test bundle. It checks with `/layout` that each one embeds exactly `RST.msi`, the helper and `RST-Machine.msi` plus its shippable companions, and that the theme has exactly one checkbox per shippable companion. It uploads `RST-Setup.exe` (attached to releases by `release.yml`) and `bundle-test`.
- **`bundle-smoke`** (needs `bundle`; stub `Revit.exe` files stand in for Revit, as in `installer/ci-smoke.ps1`):
  - `Chain`: the install-order table above, with the fixture.
  - `BareMsiFirst`: AC5.
  - `Companions`: the real `RST-Setup.exe` with both companions ticked. Both install silently with their own entries and `.addin` files, survive the bundle's uninstall, uninstall with `msiexec /x`, and install alone without rst-c.

  - `UI`: the real `RST-Setup.exe` through its own UI. It checks the scope radio buttons' wording, that "Just for me" is the default, and the companions' header on each path, then installs per user with no elevated engine. It then installs `RST-Machine.msi` bare and checks the locked state: "Just for me" disabled, with its "uninstall RST (all users) first" text. Screenshots are uploaded.
  - `Scope` (feature #17, AC10): both install-scope paths with the test bundle. "Just for me" launches no elevated engine. "Everyone on this PC" launches exactly one, and installs `RST-Machine.msi`, then removes the user's own `RST.msi` (a 1.0.0 build of the same commit, then the same version installed bare). It also checks that re-runs keep the scope and that uninstall is clean. With v2.2.0 `RST.msi` and the `RST-R27.msi` preview installed it ends with only "RST (all users)". A hand-copied manifest does not block it. With Revit running, the helper stops before removing anything and `RST-Machine.msi` is rolled back, so the user keeps their RST.

  `Chain` and `Companions` also assert from the Burn log that the bundle registered per-user and never launched an elevated engine. The elevation check is Burn's own: it logs "Launching elevated engine process" whenever it starts its elevated engine, even on CI's already-elevated runner. Every silent run uses `/quiet /norestart`, the documented form.

None of this loads anything in Revit. That a real companion works in Revit after the setup is the one-time VM check (AC6, skill `rst_c_vm`), not CI.

## Bumping a companion

Edit `companions/companions.json` as described in [`companions/README.md`](../companions/README.md#bumping-a-pin). The bundle picks the change up on the next build. The `bundle` job fails if the new pin does not match the release asset.
