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

## License

TBD — to mirror the parent RST repo at release.
