# RST-C — Design Decisions

A short log of architectural choices made during the port. Each entry is
dated and answers: *what was decided, what alternatives were considered,
why this choice.* New decisions append; existing entries are not edited.

---

## 2026-05-02 — Multi-target strategy: Configuration-per-Revit-version

**Decision.** Use the Nice3point convention of one MSBuild Configuration
per supported Revit version (`Debug R24`, `Debug R25`, `Debug R26`,
`Debug R27`, plus `Release` variants). Each Configuration selects a
TargetFramework and `RevitVersion` MSBuild property, which in turn
selects the correct `Nice3point.Revit.Api.*` package version.

**Alternatives considered.**

- *Single TargetFramework per project, multiple `.csproj` files.* Rejected:
  duplicates source, drifts over time.
- *`<TargetFrameworks>net48;net8.0-windows</TargetFrameworks>` with float
  versions.* Rejected: 2025/2026/2027 all share `net8.0-windows`, so TFM
  alone can't pick a Revit API version.
- *Build only against the latest API and pray for binary compatibility.*
  Rejected: Revit API often introduces breaking changes between major
  versions; pyRevit-era RST burns a lot of cycles working around exactly
  this on the IronPython side, and we want to leave that behind.

**Consequence.** Each `dotnet build` produces one DLL targeted at one
Revit major. The installer (RST-012) ships per-major DLLs and the .addin
manifest selects by Revit version.

---

## 2026-05-02 — Code signing: defer cert decision to RST-013, design for it now

**Decision.** Treat all DLLs and the MSI as code-sign targets. The
project is built so that signing can be added in CI (RST-013) without
restructuring. Specifically:

- `Directory.Build.props` exposes `SignAssemblies` and `SignTimestampUrl`
  properties, both no-ops in dev.
- A `build/sign.targets` is reserved (empty for now) for `signtool`
  invocations.
- No assemblies are *strong-named* (no `.snk`). Strong names buy nothing
  in a Revit add-in shipped as a single DLL set, and they bind us into a
  signing key that has to live somewhere awkward.
- Authenticode signing is the goal — DLL + MSI signed with the same EV
  cert at release, timestamped against a public TSA.

**Cert sourcing.** Open question for RST-013. Options:

1. *DigiCert / Sectigo EV cert on a hardware HSM.* Real production
   answer; cleanest for SmartScreen reputation; requires the operator
   to own the HSM. ~$300-500/yr.
2. *Azure Trusted Signing.* Newer, cheaper, key lives in Azure. Adequate
   for non-driver code; operator needs an Azure tenant. ~$120/yr.
3. *Self-signed during dev.* Acceptable for in-house deployment, fails
   SmartScreen on public download. Use for early testing only.

**Consequence.** No structural change in RST-001…RST-012. RST-013
plumbs `signtool` into GitHub Actions and adds a `Release` tag-driven
workflow.

---

## 2026-05-02 — Profile schema: keep JSON, version it, round-trip fixture-tested

**Decision.** Keep the existing pyRevit profile JSON as the on-disk
format. The C# port adds:

- A required top-level `"schemaVersion": 1` field. Older pyRevit profiles
  without this field are read as `schemaVersion: 0` and migrated on load.
- A `RST.Core` profile model that round-trips through `System.Text.Json`
  with a fixture set under `src/RST.Tests/Fixtures/Profiles/`.
- Validation at load: unknown panel kinds, missing required fields, and
  malformed colors all surface as a single `ProfileLoadException` with
  a path-style locator (`profile.tabs[0].panels[2].buttons[0].color`).

**Alternatives considered.**

- *YAML or TOML.* Rejected: pywebview UIs already emit JSON; staying on
  JSON means existing user profiles continue to load without conversion.
- *XML to mirror Revit's own ribbon serialization.* Rejected: heavy and
  the existing RST community emits JSON.
- *Keep JSON but normalize through a generated schema.* Possible later;
  not required for parity port.

**Consequence.** `RST-002` lands the model and a fixture round-trip.
Migration of `schemaVersion: 0` profiles is implemented as a single
upgrade pass and a fixture proves the upgraded shape.

---

## 2026-05-02 — UI hosting: WPF in-process, no WebView2

**Decision.** Drop pywebview / WebView2 entirely. Loader, Profiler, and
Health windows are native WPF in the Revit add-in process.

**Why.** The original RST uses WebView2 for the Profiler UI because
IronPython can't host WPF cleanly. C# has no such constraint, and a
single-process WPF design removes:

- The pywebview install step (and the ARM64 caveat in `README`).
- The temp-JSON IPC bridge.
- A whole class of pywebview lifecycle bugs.

**Consequence.** UI XAML lives under `src/RST.UI/`. Styling stays close
to the existing CSS (`rst_components.css`) but is reimplemented in
WPF resource dictionaries — pixel parity is not a goal, design parity is.

---

## 2026-05-02 — Storage paths: `%AppData%\RST` (preserved across updates)

**Decision.** All user data (profiles, scan caches, intent logs, branding
overrides) lives under `%AppData%\RST\`, mirroring the parent RST. The
installer's "preserve user data" rule keys off this path.

**Subdirs.**

```
%AppData%\RST\
  profiles\        Per-profile JSON
  users\           Per-user scan + state JSON (mirrors original app/users/)
  branding\        Logo overrides
  logs\            Rolling logs
  config.json      Last-loaded profile, RSTify state, etc.
```

**Why not `%LocalAppData%`.** Some firms roam `%AppData%` between
machines but not `%LocalAppData%`. Profiles are users' work — they should
roam.

---

## 2026-05-02 — Logging: `Serilog`, file sink under `%AppData%\RST\logs`

**Decision.** `Serilog` with a rolling-file sink. One log file per Revit
session, named `rst_{yyyy-MM-dd_HHmmss}.log`. Rolling deletes oldest
beyond the most recent N (configurable, default 20).

**Why Serilog specifically.** Structured logging, reliable rolling sinks,
no transitive Microsoft.Extensions.Logging dependency that conflicts with
Revit's own runtime libraries.

**Consequence.** `RST.Core` exposes a thin `ILogger` abstraction so
non-Revit code stays Serilog-agnostic; `RST.Engine` wires Serilog at
`OnStartup`.

---
