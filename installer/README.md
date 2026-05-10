# RST Installer

Per-user MSI for RST. Built with [WiX Toolset v4+](https://wixtoolset.org/).

## What it installs

- **`%AppData%\Autodesk\Revit\Addins\2025\`** — `RST.addin` manifest +
  `RST.Bootstrap.dll` (the IExternalApplication thunk Revit loads)
- **`%AppData%\RST\R25\app\`** — engine, transitives, assets, native
  runtime libraries (loaded by the bootstrap via
  `AssemblyDependencyResolver`)

User data (`%AppData%\RST\profiles\`, `\logs\`, `\branding.png`,
`\active_profile.json`, etc.) lives outside both install trees and is
preserved across reinstalls and uninstalls.

v0 ships Revit 2025 only. R26 / R27 land once those majors enter the
test matrix.

## Build (Windows)

WiX requires Windows for production-grade MSI emission (Linux works
in-tool but ICE validation is unavailable and behavior is officially
undefined).

```powershell
# One-time: install the WiX SDK
dotnet tool install --global wix

# From the repo root: stage the R25 bundle (the WXS reads from build/R25/)
bash build/stage.sh R25     # or run dotnet build manually + cp tree

# Build the MSI
cd installer
dotnet build -c Release
# → installer/bin/Release/RST.msi
```

## Build (Linux)

WiX 7's CLI runs on Linux but emits a `WIX0000` warning ("behavior is
undefined"). Fine for syntax iteration; produce production MSIs on
Windows.

```bash
build/stage.sh R25
cd installer
wix build Product.wxs -o bin/Linux/RST.msi
```

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

Major upgrades (newer `Version=` in Product.wxs) automatically replace
the prior install in place, courtesy of `<MajorUpgrade>`. `UpgradeCode`
is the stable product family identity — **never change it** once
shipped.

## Releasing

CI builds the MSI on every push to `main` (workflow artifact
`RST.msi`). Cutting a release is a tag push:

```bash
# Bump <Package Version="…"> in installer/Product.wxs (and
# installer-r27/Product.wxs if R27 is shipping) first.
git tag v1.0.0
git push origin v1.0.0
```

The `release` workflow rebuilds against the tagged commit, attaches
`RST.msi` (and `RST-R27.msi` if its stage produced an artifact) to a
new GitHub Release, and auto-generates release notes from commits since
the previous tag. Hyphenated tags (`v1.0.0-rc.1`, `v0.2.0-alpha`) are
flagged as pre-release.

Code signing remains a separate flag — current MSIs ship unsigned.
