# RST Installer

Per-user MSI for RST, built with [WiX Toolset v6](https://wixtoolset.org/).

One MSI project lives in this repo:

| Project | Output | Covers |
|---|---|---|
| `installer/RST.Installer.wixproj` | `RST.msi` | Revit 2025, 2026 and 2027 |

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

Local and push/PR builds without `-p:RstMsiVersion=` are stamped with the `.wixproj` fallback version (`2.99.0`), which sits between the latest release (v2.2.0) and the next (v3.0.0) so a dev build upgrades a released install in place.

**CI checks** — `installer/ci-smoke.ps1` runs on `windows-latest` in `_build.yml`: `-Mode Install` (every build) installs `RST.msi` silently, checks all six trees, uninstalls and checks they are gone while user data survives; `-Mode Migrate` (job `msi-migrate`) upgrades from the public v2.2.0 preview MSI and v2.2.0 `RST.msi`.

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
  Serilog*.dll
  Microsoft.Web.WebView2.*.dll
  RST.Engine.deps.json
  Assets\**\*
  runtimes\<rid>\native\WebView2Loader.dll
  runtimes\win\lib\net8.0\System.Management.dll  (RST-038: stub at bin
                                                  root removed at stage
                                                  time)
  *.pdb
```

`RST.msi` writes all six trees: Addins\2025, Addins\2026, Addins\2027 and RST\R25\app, RST\R26\app, RST\R27\app. R25 and R26 carry the `net8.0-windows` engine; R27 carries the `net10.0-windows` engine with its own `RST.Engine.deps.json`. Every major is installed whether or not that Revit is present; an unused major's files sit idle.

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

### Migrating from the Revit 2027 preview

Installing `RST.msi` (v3.0.0 or later) removes an installed "RST (Revit 2027 preview)" automatically — no manual uninstall, and it works under `msiexec /qn`. `Product.wxs` carries an `<Upgrade>` row on the preview's retired `UpgradeCode` (any version, `OnlyDetect="no"`), so the `RemoveExistingProducts` action that `MajorUpgrade` schedules removes the preview before the new files install, inside the same transaction: a failed install rolls back and restores the preview. Afterwards Settings → Apps shows a single "RST". Removal is per-user: other Windows users on the same machine migrate when they run the new MSI. User data under `%AppData%\RST\` is not touched.

The row stays indefinitely, so if someone installs an old `RST-R27.msi` from a past release after the fold, the next `RST.msi` install removes it again.

## Versioning

**Source of truth:** the release **tag**. `release.yml`'s `prepare` job strips the leading `v` and any `-prerelease` suffix from the tag down to a numeric `a.b.c` and passes it to `_build.yml`, which stamps `RST.msi` via `-p:RstMsiVersion=a.b.c`. The WXS consumes it as `$(var.RstMsiVersion)`; the `.wixproj` carries a fallback used only for local builds without an explicit `-p:RstMsiVersion=`. The stamped version is what Add/Remove Programs displays and what the MSI engine uses to decide whether an installer is an upgrade, downgrade, or sidegrade vs. the currently installed product.

**Release flow:**

1. Tag from `main` HEAD: `git tag -a v1.0.0 -m "…" && git push origin v1.0.0`. No version-bump commit is needed — the tag carries the version.
2. `.github/workflows/release.yml` fires on the `v*` tag, runs the matrix in `_build.yml`, creates a GitHub Release at `releases/tag/v1.0.0`, and attaches `RST.msi` (Revit 2025 + 2026 + 2027) by name. Every major, R27 included, is a hard gate: an R27 build failure fails the workflow and blocks the release.
3. The installed ProductVersion always matches the tag, and a new tag always raises it — driving `MajorUpgrade` / `RemoveExistingProducts` on in-place upgrade.

**Pre-release tags:** any tag with a hyphen (`v1.0.0-rc.1`, `v0.2.0-alpha`) is flagged as pre-release on the GitHub Release.

**`UpgradeCode` is immutable.** Each RST product carries a fixed GUID:

| MSI | UpgradeCode | Status |
|---|---|---|
| `RST.msi` | `AF0331FC-5280-492A-94BD-95EDF108BE74` | Current |
| `RST-R27.msi` ("RST (Revit 2027 preview)", v1.0.0–v2.2.0) | `02BE4399-A563-4B15-BD12-ECF5D8486FB3` | **Retired, never reuse.** Referenced only by the migration `<Upgrade>` row in `Product.wxs` |

**Never change a shipped `UpgradeCode`.** It's how Windows Installer recognises a new MSI as an upgrade of a prior install rather than a parallel product. Changing it leaves orphaned installs in Add/Remove Programs.

`<MajorUpgrade>` handles the upgrade in place: installing v1.0.1 on a machine with v1.0.0 silently uninstalls the old and installs the new without losing user data.

## Code signing

Currently unsigned. `build/sign.targets` is a placeholder with `signtool` hooks scaffolded but the cert source (`SignAssemblies=true`, `SignCertThumbprint=…`) is not yet wired into CI. Tracked under RST-042; once that lands, the unsigned line in the project's top-level README intro can also be reverted.
