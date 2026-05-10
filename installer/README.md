# RST Installer

Per-user MSIs for RST, built with [WiX Toolset v6](https://wixtoolset.org/).

Two MSI projects live in this repo:

| Project | Output | Covers |
|---|---|---|
| `installer/RST.Installer.wixproj` | `RST.msi` | Revit 2025 + 2026 (unified) |
| `installer-r27/RST.Installer.R27.wixproj` | `RST-R27.msi` | Revit 2027 (preview, standalone) |

The two MSIs are independent products with distinct `UpgradeCode`s — they coexist on disk during the R27 preview window. R27 will be folded into the unified MSI once the build path stabilises in CI; at that point the satellite project is end-of-lifed and users uninstall `RST-R27.msi` before installing the unified build.

## Dependencies

**Build-time:**

- **.NET SDK 8.0** — required for the R25/R26 build path (`net8.0-windows` engine)
- **.NET SDK 10.0** — required only for the R27 build path (`net10.0-windows` engine). Skip if you're not building `RST-R27.msi`. CI installs this via `actions/setup-dotnet`.
- **WixToolset.Sdk 6.0.2** — pulled automatically when the wixproj restores. Version is pinned: the `<Files>` harvest element used in `Product.wxs` was introduced in WiX v5, so v4.x trips `WIX0005` regardless of where the element is parented (RST-040). v6.0.2 is the latest patch on the v6 line; bumping to v7 is a one-line SDK change if/when needed.
- **Windows** for production-grade emission. WiX runs on Linux but the `WIX0000` warning ("behavior is undefined") and the absence of ICE validation make it suitable for syntax iteration only.

**Pre-build artefacts** — the WXS files use `<Files Include="..\build\R<NN>\…">` to harvest the staged engine bundle. Stage the relevant Revit major(s) before building:

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
# → installer\bin\Release\RST.msi

dotnet build installer-r27\RST.Installer.R27.wixproj -c Release
# → installer-r27\bin\Release\RST-R27.msi
```

## What the MSIs install

Both MSIs use the RST-033 layout: a tiny bootstrap thunk in Revit's add-in directory, the engine + transitives in a per-major subdir under `%AppData%\RST\`. The bootstrap derives `R<major>` from `application.ControlledApplication.VersionNumber` at `OnStartup` (RST-037) and loads the matching engine.

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

`RST.msi` writes Addins\2025 + Addins\2026 + RST\R25\app + RST\R26\app. `RST-R27.msi` writes Addins\2027 + RST\R27\app.

**User data** (`%AppData%\RST\profiles\`, `\logs\`, `\branding.png`, `\active_profile.json`, `\user_profile_prefs.json`, etc.) lives outside both install trees and is preserved across reinstalls and uninstalls.

## Install / uninstall

Per-user MSI — no admin elevation needed.

```powershell
# Install (silent)
msiexec /i RST.msi /qn
msiexec /i RST-R27.msi /qn

# Install (interactive, with progress bar)
msiexec /i RST.msi

# Uninstall (silent)
msiexec /x RST.msi /qn

# Or uninstall via Settings → Apps → "RST" / "RST (Revit 2027 preview)"
```

Both MSIs appear as separate entries in Add/Remove Programs because their `UpgradeCode`s are distinct. Installing or uninstalling one does not affect the other.

## Versioning

**Source of truth:** the `<Package Version="…">` attribute in `installer/Product.wxs` and `installer-r27/Product.wxs`. This drives the version Add/Remove Programs displays and is what the MSI engine uses to decide whether an installer is an upgrade, downgrade, or sidegrade vs. the currently installed product.

**Release flow:**

1. Bump `<Package Version="x.y.z">` in **both** `installer/Product.wxs` and `installer-r27/Product.wxs` (keep them in lockstep — the user-visible product is one thing, the two MSIs are an internal split).
2. Commit the bump, merge to `main`.
3. Tag from `main` HEAD: `git tag v1.0.0 && git push origin v1.0.0`.
4. `.github/workflows/release.yml` fires on the `v*` tag, runs the matrix in `_build.yml`, creates a GitHub Release at `releases/tag/v1.0.0`, and attaches `RST.msi` (R25+R26) and `RST-R27.msi` (when its build succeeds — R27 is currently soft-fail in `release.yml`).
5. The tag is the release name; the `Version=` attribute is the installed product version. They should match — the workflow does not thread the tag value into the build, so a missed bump means the Release is named `v1.0.0` while installed copies report `0.x.y`.

**Pre-release tags:** any tag with a hyphen (`v1.0.0-rc.1`, `v0.2.0-alpha`) is flagged as pre-release on the GitHub Release.

**`UpgradeCode` is immutable.** Each MSI carries a fixed GUID:

| MSI | UpgradeCode |
|---|---|
| `RST.msi` | `AF0331FC-5280-492A-94BD-95EDF108BE74` |
| `RST-R27.msi` | `02BE4399-A563-4B15-BD12-ECF5D8486FB3` |

**Never change a shipped `UpgradeCode`.** It's how Windows Installer recognises a new MSI as an upgrade of a prior install rather than a parallel product. Changing it leaves orphaned installs in Add/Remove Programs.

`<MajorUpgrade>` handles the upgrade in place: installing v1.0.1 on a machine with v1.0.0 silently uninstalls the old and installs the new without losing user data.

## Code signing

Currently unsigned. `build/sign.targets` is a placeholder with `signtool` hooks scaffolded but the cert source (`SignAssemblies=true`, `SignCertThumbprint=…`) is not yet wired into CI. Tracked under RST-042; once that lands, the unsigned line in the project's top-level README intro can also be reverted.
