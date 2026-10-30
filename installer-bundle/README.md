# RST Setup bundle (`RST-Setup.exe`)

`RST-Setup.exe` is a [WiX v6](https://wixtoolset.org/) Burn bundle (feature #13, spec #15). It always installs `RST.msi`, and offers each **shippable** companion add-in from [`companions/companions.json`](../companions/companions.json) as an opt-in checkbox. Ticking one installs that companion's own MSI silently and unmodified. After setup every product is a stock install:

- rst-c sits behind the bundle's **RST** entry in Settings → Apps.
- Each companion has its **own** entry, upgrades through its own MSI, and has no runtime link to rst-c.
- **Uninstalling RST does not remove the companions.** They are permanent: remove each from Settings → Apps. The setup's finish page says so.

The bare `RST.msi` is still published beside it, for IT deployments that want only rst-c.

## Identity

| | Value |
|---|---|
| Bundle `UpgradeCode` | **`2BE2BE5D-9203-4344-A70D-E735D3B041A3`**: never change it once shipped |
| Name in Settings → Apps | `RST` (Designs/OS) |
| Version | the numeric `a.b.c` of the rst-c tag, the same value stamped into `RST.msi` (`v3.0.0-rc.1` → `3.0.0`); untagged builds `2.99.0` |
| Scope | per-user: every chained package is a per-user MSI, so Burn registers the bundle in HKCU and never asks for elevation |
| Engine | x64, `WixStandardBootstrapperApplication` (`WixToolset.BootstrapperApplications.wixext` 6.0.2), RTF license theme |

**Never change the `UpgradeCode`.** It is how a newer `RST-Setup.exe` finds and replaces an installed older one. It is written as a literal GUID in `Bundle.wxs`; the companion registry test (`src/RST.Tests/Companions/CompanionRegistryTests.cs`) reads it so no companion can reuse it, and fails if it changes or stops being a literal.

A same-version bundle (an rc and its final, both `3.0.0`) replaces the installed one: Burn plans an equal version as an upgrade.

## What it installs

| Package | When | Burn attributes | Add/Remove Programs |
|---|---|---|---|
| `RST.msi` | always | vital (a failure fails the setup and rolls back) | hidden behind the bundle's `RST` entry |
| each shippable companion (`GanttIt-1.1.0.msi`, `SplashIt-1.1.0.msi`) | only when its box is ticked | `Permanent="yes"`, `Visible="yes"`, `Vital="no"`, `InstallCondition="Install<Id> = 1"` | its own entry |

Everything is embedded: `RST-Setup.exe` is one offline file, and nothing is downloaded at install time.

`RST.msi`'s Revit check still applies: a first install fails when no Revit 2025, 2026 or 2027 is found, and so does the setup. Both current companions carry the same check. A companion is non-vital, so its failure is reported in the log but never rolls back rst-c.

"Unmodified" means the companion's released MSI file is embedded byte for byte (sha256-pinned) and run with no transform and no properties of ours. Burn itself passes its usual properties to every MSI it runs (for example `REBOOT=ReallySuppress`, `MSIFASTINSTALL`), as it does for `RST.msi`.

## Silent install (IT)

Each checkbox is a Burn variable named after the companion's registry id: `gantt-it` → `InstallGanttIt`, `splash-it` → `InstallSplashIt`. `1` ticks it; anything else (or leaving it out) does not. Variable names are case-insensitive.

```powershell
# rst-c + both companions, no UI
RST-Setup.exe /quiet InstallGanttIt=1 InstallSplashIt=1

# rst-c only (same as the bare RST.msi)
RST-Setup.exe /quiet

# with a log
RST-Setup.exe /quiet InstallGanttIt=1 InstallSplashIt=1 /log "%TEMP%\RST-Setup.log"

# uninstall rst-c (companions stay)
RST-Setup.exe /uninstall /quiet
```

`/passive` shows progress without prompts. Exit code `0` is success; `3010` means a restart is needed (no current package asks for one).

**Adding a companion later:** re-running the setup's UI on a computer where it is installed shows Repair/Uninstall, not the checkboxes. Run the companion's own MSI, or `RST-Setup.exe /quiet InstallGanttIt=1`.

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
2. `RST-Setup.exe /quiet` (the same `RST.msi` inside, same ProductCode):
   - Burn detects `RST` as `state: Present, install registration state: Ignored` (installed, but not by this bundle).
   - It plans `execute: None` (nothing reinstalled, no files touched) with `dependency: Register, expected install registration state: Present`. **The bundle adopts the existing RST.msi as its own.**
   - There is still exactly one RST product. Because the MSI is not re-run, its own ARP entry stays visible, so **Settings → Apps shows two "RST" entries**: the bundle's and the bare MSI's.
   - Ticked companions install normally.
3. Uninstalling the bundle (Settings → Apps → the bundle's "RST", or `RST-Setup.exe /uninstall /quiet`): Burn plans `RST` as `execute: Uninstall`. **The adopted RST.msi is removed**: no RST product, no RST files, and no "RST" entry left. Companions stay, as always.

So after `RST.msi` then `RST-Setup.exe`, there is one product behind two entries, and removing either one removes rst-c. Uninstalling the MSI entry directly leaves the bundle's entry behind, pointing at an rst-c that is gone; that path is not covered by the smoke. This is Burn's default for a package it did not install. No workaround is applied; it was returned to the Planner (PLN1) as the spec asks.

## Build

Windows only. Build `RST.msi` first (see [`installer/README.md`](../installer/README.md)), then:

```powershell
# from the repo root
pwsh installer-bundle\fetch-companions.ps1           # downloads + verifies the pinned MSIs into installer-bundle\payload\ (needs gh)
dotnet build installer-bundle\RST.Setup.wixproj -c Release
# → installer-bundle\bin\Release\RST-Setup.exe
```

| File | Role |
|---|---|
| `RST.Setup.wixproj` | the bundle project; inputs `RstMsi`, `CompanionRegistry`, `CompanionPayloadDir`, `RstBundleVersion` |
| `Bundle.wxs` | bundle identity, the BA, the chain (`RST.msi` + the generated `RstCompanions` group) |
| `gen-companions.ps1` | runs before every compile: reads the registry, re-checks each payload's sha256, writes `obj\gen\Companions.g.wxs` (variables + `MsiPackage`s) and `obj\gen\thm.xml` (the theme with one checkbox per shippable companion). Zero shippable → empty chain, no checkboxes |
| `theme\RstTheme.xml`, `theme\RstTheme.wxl` | the BA theme template and strings, adapted from WiX's RtfTheme (MS-RL) |
| `fetch-companions.ps1` | downloads each pinned asset with `gh release download` and fails on a missing asset or a sha256, UpgradeCode or version mismatch |
| `fixture\` | the **test-only** "RST Companion Fixture" MSI (one text file under `%AppData%\RST-CompanionFixture\`). Never shipped, never in the registry |
| `ci-bundle-build.ps1`, `ci-bundle-smoke.ps1` | the CI `bundle` and `bundle-smoke` jobs |

## CI

`.github/workflows/_build.yml`:

- **`bundle`** (needs `msi-unified`): `fetch-companions.ps1`, then `ci-bundle-build.ps1`. It builds `RST-Setup.exe`, a zero-shippable variant, the fixture at 1.0.0 / 1.1.0 / 2.0.0 and the smoke's test bundle. It checks with `/layout` that each one embeds exactly `RST.msi` plus its shippable companions, and that the theme has exactly one checkbox per shippable companion. It uploads `RST-Setup.exe` (attached to releases by `release.yml`) and `bundle-test`.
- **`bundle-smoke`** (needs `bundle`; stub `Revit.exe` files stand in for Revit, as in `installer/ci-smoke.ps1`):
  - `Chain`: the install-order table above, with the fixture.
  - `BareMsiFirst`: AC5.
  - `Companions`: the real `RST-Setup.exe` with both companions ticked. Both install silently with their own entries and `.addin` files, survive the bundle's uninstall, uninstall with `msiexec /x`, and install alone without rst-c.

  Every leg also asserts the bundle registered per-user and never launched an elevated engine.

None of this loads anything in Revit. That a real companion works in Revit after the setup is the one-time VM check (AC6, skill `rst_c_vm`), not CI.

## Bumping a companion

Edit `companions/companions.json` as described in [`companions/README.md`](../companions/README.md#bumping-a-pin). The bundle picks the change up on the next build. The `bundle` job fails if the new pin does not match the release asset.
