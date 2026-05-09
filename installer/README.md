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

Code signing + GitHub Actions matrix build land in **RST-013**. Until
then, MSIs are built locally and shared by hand.
