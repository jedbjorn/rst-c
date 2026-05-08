# RST-C

C# / WPF port of the [RST](https://github.com/jedbjorn/RST) pyRevit extension.

> **Status:** v0.1.0-alpha — under active port. Not buildable end-to-end yet.
> Track progress against the `RST-001` … `RST-013` flag series.

## Why a port?

The original RST runs as a pyRevit extension across two processes:

```
Revit (IronPython 2.7)            CPython 3.12 + pywebview
┌──────────────────────┐         ┌─────────────────────────┐
│ Pushbutton scripts   │── JSON ─▶│ Tab Creator UI (admin)  │
│ startup.py           │  files   │ Profile Loader UI       │
│ (builds ribbon)      │         └─────────────────────────┘
└──────────────────────┘
```

That works, but carries five operational costs:

1. **Two runtimes** to install (pyRevit + Python 3.12 + pywebview, with the pywebview ARM64 caveat).
2. **JSON-file IPC** instead of in-process calls.
3. **No code signing path** — pyRevit extensions ship as source.
4. **No installer** — users add the GitHub URL through the pyRevit Extensions dialog.
5. **No AdWindows / Fluent ribbon access** from IronPython without reflection gymnastics.

RST-C collapses this to a single signed managed add-in:

```
Revit (.NET 4.8 or .NET 8)
┌────────────────────────────────────────────────┐
│ IExternalApplication                           │
│   ↳ Profile engine (Core)                      │
│   ↳ WPF windows (Loader, Profiler, Health)     │
│   ↳ AdWindows panel adapter (color/opacity)    │
└────────────────────────────────────────────────┘
```

## Targets

| Revit version | TFM             | API ref package                          |
|---------------|-----------------|------------------------------------------|
| 2024          | `net48`         | `Nice3point.Revit.Api.RevitAPI@2024.*`   |
| 2025          | `net8.0-windows`| `Nice3point.Revit.Api.RevitAPI@2025.*`   |
| 2026          | `net8.0-windows`| `Nice3point.Revit.Api.RevitAPI@2026.*`   |
| 2027          | `net8.0-windows`| `Nice3point.Revit.Api.RevitAPI@2027.*`   |

(2027 is provisional pending Autodesk's release.)

## Layout

```
RST-C/
├── src/
│   ├── RST.Core/         JSON profile model, scanner core, no Revit deps
│   ├── RST.Engine/       Revit-bound logic, IExternalApplication
│   ├── RST.UI/           WPF windows (Loader, Profiler, Health)
│   ├── RST.AdWindows/    AdWindows.dll adapter (colored panels)
│   └── RST.Tests/        xUnit tests against RST.Core
├── build/                Directory.Build.props, signing pipeline, MSBuild fragments
├── spike/                Scanner spike + proof-of-concept code
└── .github/workflows/    CI (matrix build per Revit version)
```

## Build

Local builds need only the .NET 8 SDK — Revit API references resolve from NuGet.

```bash
dotnet restore
dotnet build -c "Debug R25"     # Revit 2025
dotnet build -c "Debug R24"     # Revit 2024 (requires .NET Framework 4.8 reference assemblies on Linux)
```

(Configurations encode the Revit version per the Nice3point convention.)

## Testing on Revit (Windows)

Pre-built bundles ship on `main` under `build/R<NN>/` — one folder per Revit major (`build/R24/`, `build/R25/`, `build/R26/`, `build/R27/`). Currently only **R25** is built; the rest are pending until first build for that target.

### Pull on the test machine

```bash
# fresh clone:
git clone git@github.com:jedbjorn/rst-c-.git
# or on an existing clone:
git pull
```

### Install into Revit's per-user add-in directory

Close Revit first (it locks the DLLs), then copy the matching bundle into `%AppData%\Autodesk\Revit\Addins\<version>\`. For Revit 2025:

```powershell
# from the rst-c- working tree
Copy-Item -Recurse -Force "build\R25\*" "$env:APPDATA\Autodesk\Revit\Addins\2025\"
```

After install, the directory should look like:

```
%AppData%\Autodesk\Revit\Addins\2025\
├── RST.addin              # manifest — points at RST\RST.Engine.dll
└── RST\                   # all DLLs/PDBs/runtimes/Assets
    ├── RST.Engine.dll
    ├── RST.Core.dll
    ├── RST.UI.dll
    ├── Microsoft.Web.WebView2.*.dll
    ├── Serilog*.dll
    ├── Assets/
    └── runtimes/
```

Start Revit. The **RST** tab should appear with the Loader button.

### Smoke tests

1. **Loader opens** — click the Loader button on the RST tab; the WebView2 window loads and lists profiles.
2. **Live profile switch (RST-020)** — pick a profile, hit Apply. The profile tab should rebuild **without** prompting for a Revit restart.
3. **URL slot resolution (#21)** — a slot URL of `gmail.com` (no scheme) or `support@example.com` (bare email) should open in the default browser/mail client. Pre-fix, both threw `Win32Exception(2)`.
4. **No-leak check** — Apply 10–20 different profiles back-to-back in one session; check the Serilog file (see below) for `Freeze` failures or other warnings.

### Logs

Serilog rolling files at `%AppData%\RST\`. Always check there first when something looks wrong — both C# and WebView2-side errors land in the same file via the `log_event` bridge.

## License

TBD — to mirror the parent RST repo at release.
