---
rendered_by: super-coder
source: db
edit: changes here are overwritten — author via the shell or localhost GUI
feature: 
roadmap_status: 
frozen: false
title: VM Manual Test Pass — QAQC-1 — 2026-07-02
tags: [qa, vm, installer, verification]
date: 2026-07-02
project: rst-c
purpose: Results of the Windows-VM test pass behind flag QAQC-1 (post #88/#89/#90 merge)
---

# VM Manual Test Pass — 2026-07-02

Run on **W10C_DOS-ARCH_Testing** (Win10 19045, Revit 2026 26.4.10.51 + real
third-party add-ins, WebView2 149) via the vm-broker loop (reset → push → exec →
capture → reset-off). Code under test: `main` @ `8456c0b` (CI artifacts, run
28622874229). Companion to doc #3 (E2E QAQC Report); flag QAQC-1.

## Verdict

Everything testable without an Autodesk sign-in **passed**. The in-Revit GUI
half of the checklist is **blocked**: Revit 2026 demands an Autodesk account
sign-in on first launch and the clean snapshot has no cached session
(**flag QAQC-2** — operator must sign in on the VM console and re-snapshot
`clean`, or every reset re-locks it).

## Installer sequences

| Check | Result |
|---|---|
| Clean upgrade 1.0.0 → 1.2.0 (user data present) | **PASS** — single ARP entry, new ProductCode, all R25/R26 payloads replaced (binaries stamped `0.1.0-alpha+8456c0b`), every user-data file hash byte-identical |
| ARP metadata | **PASS** — help/about → repo docs; icon correctly cached per-user (`%APPDATA%\Microsoft\Installer\{PC}\RstIcon.ico`); empty `DisplayIcon` registry value is normal MSI behavior, not a bug |
| R27 side-by-side install | **PASS** — own ARP entry "RST (Revit 2027 preview)", own trees (Addins\2027 + %AppData%\RST\R27) |
| R27 uninstall isolation | **PASS** — only R27 trees removed; RST 1.2.0 + user data untouched |
| Uninstall preserves user data | **PASS** — profiles/active_profile/prefs/logs all survive; addins + app trees removed |
| Same-version dev-build collision | **CONFIRMED BUG** (doc #3 High 8 / Installers M3) — two different CI builds, both 1.2.0, install side-by-side: two "RST | 1.2.0" ARP rows. Fix stays on the books (thread real versions / AllowSameVersionUpgrades) |
| RC → final upgrade | SKIPPED — needs an RC-tagged release build |
| Upgrade with Revit open | BLOCKED — QAQC-2 |

## Priority check #88 (self-lockout fix) — core PASS on real box

Console harness (RST.Core built on the VM) against the real 2026 environment —
85 manifests across 4 search paths:

- Worst case (`DisableNonRequired` with an **empty** required list): 13 writable
  third-party manifests renamed (AlignTag ×8, BatchPrint, eTransmit,
  TotalCarbonAnalysis, WorksharingMonitor, FormItConverter); **RST.addin
  untouched, no `.RSTdisabled` twin created**.
- Preview: RST.addin is listed under **staying** (and not under disabling) —
  preview matches commit.
- `RestoreAll`: 13/13 restored, zero failures, no `.RSTdisabled` remained.
- Shipped `RST.addin` carries `<ClientId>4f8ef7a0-0001-4000-8000-525354000001`
  and the new ClientId parser fallback reads it (scan shows the id) — both
  IsSelf tiers (filename + ClientId) hold against the real installed manifest.

Unverified (GUI): the confirm-modal pixels showing RST under "staying" — QAQC-2.

## Priority check #89 (validation + import surfacing) — core PASS on real box

- Blank panel name → `Validate` rejects: `profile.panels[0].name: required`.
- Real profiles dir lists cleanly; the pre-existing 1.0.0-era `example` profile
  **survives the new save-side validation** (no vanish regression on upgrade).
- Corrupt JSON in a profiles dir surfaces via `onSkip` (no silent drop).

Unverified (GUI): "Import Failed" dialog + builder save-error toast — QAQC-2.

## Test suites on the VM (substitute for the offline integration runner)

- `RST.Tests`: **140/140 pass** (includes new AddinDisablerSelfProtectionTests + ProfileStoreTests).
- `RST.IntegrationTests`: **7/7 pass** against the real Revit 2026 install.
- Sandbox `./sc test` (headless UI Node suite): green.

## New findings (not in doc #3)

1. **Integration runner missing from the snapshot** — `docs/integration-runner.md`
   says the self-hosted runner lives at `C:\Windows\system32\actions-runner`; no
   actions-runner exists anywhere on the box. Five `integration` workflow runs
   are queued/waiting on GitHub. Also: the workflow comment references
   `docs/integration-runner-setup.md`, which doesn't exist (doc is
   `integration-runner.md`). Operator: reinstall/re-register the runner in the
   snapshot (or accept running the suites manually as done here).
2. **Autodesk sign-in wall** (QAQC-2) — blocks every in-Revit checklist item:
   zero-doc greyed buttons, RSTify persistence, preview-vs-commit modal, fuzzy
   false-positive, health UI freeze, appearance/theme checks, upgrade-with-Revit-open.
3. **vm-broker robustness** (engine, filed upstream) — `/exec` returns a raw
   `UnicodeDecodeError` when guest stdout isn't valid UTF-8 (e.g. `Get-Content`
   of a UTF-16/BOM file). Workaround: base64-encode guest output.

## Leftovers for the next pass

Staged on the VM share (`Z:\` / `/home/j3d1/VM_Shared`): `RST.msi` (main tip),
`RST-prev.msi` (collision repro), `RST-R27.msi`, `harness.zip` (real-env core
harness), `vmtests.zip` (unit+integration suites), `rst_uia.ps1` (session-1 UI
driver — pair with `schtasks /tn RSTUia`). After the operator signs in and
re-snapshots, the in-Revit half of doc #3's checklist can run with these as-is.


---

# Addendum — In-Revit GUI half — 2026-07-03

QAQC-2 cleared: the operator signed in on the VM console (Autodesk account
`jedBPKK4`); Revit 2026.4 boots straight to Home. Tests ran on the **live
signed-in box** (not from a reset — the sign-in is NOT yet baked into the
`clean` snapshot; a reset still re-locks QAQC-2 until the operator re-bakes).

**Method.** Windows-MCP inside the guest on port **8001** (the baked
`windows-mcp-server` task binds 8000 and dies: the **dos-arch API test
instance already owns 8000** — fix the port or the squatter at next bake).
Reached over the vm-broker exec loop via a guest-side MCP bridge
(`C:\Users\Public\mcp_bridge.py`, base64-wrapped output per super-coder#261)
plus a UIA driver (`C:\Users\Public\uia.ps1`) run through the MCP PowerShell
tool so it executes in session 1. Revit's WPF ribbon + RST's WebView2 UIs are
fully UIA-drivable this way (ribbon *panel contents* are UIA-blind — the one
place clicks fall back to tree-derived coordinates with screenshot verify).

## Results

| Check | Result |
|---|---|
| Zero-doc buttons (start screen, no model) | **PASS** — all four RST buttons (Builder/Loader/RSTify/Health) greyed; enabled once a doc opens |
| #89 vanishing profile (blank panel name → Save) | **PASS (fixed)** — red toast `Save failed: Profile validation failed: profile.panels[0].name: required`; no file written; nothing vanishes |
| #89 silent import failure (corrupt zip) | **PASS (fixed)** — "Import Failed — Not a valid RST profile package: End of Central Directory record could not be found." |
| #88 preview-vs-commit modal | **PASS** — "Confirm Add-in Changes" lists **RST first under STAYING ACTIVE**; required AlignTag also staying; writable manifests under WILL BE DISABLED; non-writable under TRY DISABLE; restart notice shown |
| #88 self-lockout on real apply | **PASS** — after Confirm & Load, `RST.addin` untouched, no `.RSTdisabled` twin |
| Profile tab build + appearance | **PASS** — QAQC Tab built live (no restart), branding panel at index 0, panel tinted `#4f8ef7`, opacity honored |
| Unload profile | **PASS** — profile tab removed live |
| Preset adoption ("Adopt RSTify Presets?") | **PASS** — admin defaults surfaced and applied (2 tabs, disable-unused on) |
| RSTify live toggle | **PASS** — hides/restores Structure+Steel immediately, both directions |
| Health scan | **PASS (basic)** — scan refreshes snapshot; Revit section correct (build 26.4.10.51, user, model, warnings 0); hardware-accel flagged Disabled. Freeze/50k and read-only-file items not run |
| Branding logo click | **PASS (inert as shipped)** — no action, no browser (branding URL retired in #90) |

## New bugs

1. **HIGH — Disable-unused silently fails and the UI claims success.** All 5
   manifests the modal promised under "WILL BE DISABLED"
   (BatchPrint, eTransmit, TotalCarbonAnalysis, WorksharingMonitor,
   FormItConverter — all in `C:\ProgramData\...\Addins\2026`) failed to rename:
   `UnauthorizedAccessException` (non-elevated Revit has no write there). Log:
   `disabled 0 addins, skippedReadOnly=71, failed=5`, then
   `load_profile OK {...failed_disables:5}` — **no toast, no dialog, QA said
   "all clear", loader auto-closed.** Two defects: (a) the modal's
   writability probe disagrees with the rename outcome — the earlier
   core-harness PASS renamed these same files only because **OpenSSH sessions
   carry the elevated admin token** while interactive Revit runs filtered;
   (b) `failed_disables` reaches the UI layer and is discarded. Same
   error-surfacing theme as doc #3's "Failed restore reporting".
2. **MEDIUM — RSTify hidden-tabs don't survive restart.** On boot the log
   claims `RstifyToggle: visible=false, affected 2/2 tabs`, but when the
   first document opens Revit rebuilds the tab set and Structure/Steel come
   back. One manual RSTify toggle re-applies the hide. (Doc #3 predicted
   "tabs hidden again" — actual is the opposite once a doc opens.)

## Nits

- Loading toast says "Tab 'QAQC Tab' will build on next **pyRevit** reload" — leftover copy; RST is not pyRevit.
- Branding spacer element id is `REST_Branding_Spacer_RibbonItemControl` ("REST" vs "RST").
- windows-mcp `Type` tool errors on empty `text` ("string index out of range") — guest tooling, not RST.

## Not run (scope)

Cleanup reset, duplicate-name delete, UNC slot (feature retired), fuzzy
false-positive deep-check (observed: Loader shows honest "Not Found" without
download links for native-word names "Room & Area"/"Color Fill"), live theme
switch, health freeze/read-only/CPU-disk cross-checks, unsaved-model size (no
ACC cache on box), upgrade-with-Revit-open.

## Box state left behind

VM left **running** (sign-in must survive for the operator to re-bake —
do NOT `/reset` before re-snapshotting). Revit closed. Leftovers, operator's
call before bake: profile `qaqc1_985536c5-….json` (+ `user_profile_prefs.json`
now points at qaqc1; `example` unloaded), scheduled task `RSTMcp8001` +
`C:\Users\Public\{mcp8001.cmd,mcp_bridge.py,uia.ps1,probe8000.py,mcp8001.log/err}`.
Fix the baked windows-mcp task port (8000 collision) at the same time.
