---
rendered_by: super-coder
source: db
edit: changes here are overwritten — author via the shell or localhost GUI
feature: 
roadmap_status: 
frozen: true
---

# SPRINT: Telemetry v1 — capture + Activity tab
status: CLOSED                      # closed 2026-07-19 — conformance clean (doc 7 final: 0 Major / 0 Medium / 1 known Low SC-036), main green
declared: 2026-07-18 · planner: PLN1
models: devs=claude/fable · reviewers=codex/gpt-5.6-sol

Spec: doc 5 (Revit Activity Telemetry v1) · roadmap feature 2.

| seq | unit | shell | reviewer | depends on | branch | pr | status |
|---|---|---|---|---|---|---|---|
| 1 | Core engine — `RST.Core/Telemetry`: envelope + event models, JSONL serializer, OutboxWriter + bounded queue, prefs store, RecoveryScanner, RetentionPruner, unit tests (spec Build Plan step 1) | DEV1 | REV1 | — | feat/telemetry-core | #100 | merged (acbfb40; report filed msg #30; Low: Windows ACL/FileShare runtime verification rides the deferred VM gate) |
| 2 | Collector — `RST.Engine/Telemetry`: event subscriptions, identity capture, throttles, heartbeat, RstApplication wiring, closing/closed correlation (step 2) | DEV1 | REV1 | 1 | feat/telemetry-collector | #102 | merged (1ea00c0, squash of 62ac78d; report filed msg #97) |
| 3 | Aggregator — `RST.Core/Telemetry`: per-day open-hours series, open/sync event series, current-file matching, synthetic-outbox tests (step 3) | DEV2 | REV1 | 1 | feat/telemetry-aggregator | #101 | merged (f77d6e5; report filed msg #72) |
| 4 | Activity tab — `RST.UI/Health` + `health_viewer.html`: tab bar, HealthContext identity keys, bridge methods, three inline-SVG monotone-cubic charts, range pills, empty states (step 4) | DEV2 | REV1 | 3 | feat/telemetry-activity-tab | #103 | merged (73b5256; report filed msg #121; Low: AllowsCentralPath call-site adoption unenforced — analyzer/test guard candidate) |
| 5 | Consent — first-run TaskDialog, toggle wiring, prefs (step 5) **+ wire the Activity tab's live-session block** (unit 4 call #3: block renders null until this wiring lands) | DEV1 | REV1 | 2, 4 | feat/telemetry-consent | #104 | merged (d09a7d0; report filed msg #143; Low: ConsentToggle.Apply seam guard → VM gate) |

| 6 | **Conformance fix (SC-035)** — normalized `CentralPath` into `DocumentIdentity.JoinKey` (ahead of CreationGuid) + recovery/aggregator tests: central-path-only close/sync correlation, file-share Save-As with unchanged creation GUID | DEV2 | REV1 | 1–5 | fix/telemetry-joinkey-centralpath | #105 | merged (0042496; report filed msg #186; 5 review rounds, decline-on-ambiguity rule landed) |
| 7 | **Conformance re-run fix (SC-040, FnB-ruled 2026-07-19)** — decline guard must count live OPEN DOCS, not distinct JoinKeys (duplicate lower-level keys erase multiplicity); red-first regression | DEV2 | REV1 | 6 | (unit-7 branch) | #106 | merged (aba1034; report filed msg #214; SC-040/041/042 all resolved) |
| 8 | **Conformance completion (SC-043)** — current-file interval occupancy tracked per matched live element (matched ref-count), not per shared bucket; regression asserts A=1.0h / B=1.5h; ambiguous-decline behavior retained | DEV2 | REV1 | 7 | (unit-8 branch) | #107 | merged (3c8da29; report filed msg #226; 1-round clean; Low: same-key matched→rejected Save-As branch untested) |

**All 5 build units merged 2026-07-18. Conformance (doc 7): not clean — SC-035 Medium silent deviation → fix unit 6 inserted under still-ACTIVE authority; SC-036 Low = the known FnB-deferred VM gate. Freeze follows unit 6's merge + scoped conformance re-run.** Superseded queued integration run 29661540144 cancelled (runner offline per VM deferral); head run 29663136199 left queued for when the runner returns.

Judgement calls (unit 5, DEV1 — both **ratified** by PLN1 2026-07-18, pre-merge):
1. First-run notice gates on the collector actually collecting, not prefs alone — a failed telemetry start defers the notice to the next collecting session (a disclosure that claims collection while none runs would misinform). Upheld.
2. Notice shown after profile-tab build at ApplicationInitialized — spec leaves ordering open. Upheld.

Judgement calls (unit 4, DEV2 — all three **ratified** by PLN1 2026-07-18, pre-merge):
1. Active family document gates the file graphs like the doc-less state (capture still includes family docs, flagged) — spec left tab treatment open ("may group separately"); v1 shows the empty state. Upheld.
2. All-zero per-day range renders the quiet empty state, not a flat zero line. Upheld.
3. Live-session block renders null until the session wiring lands — assigned to unit 5 explicitly (see unit 5 scope). Upheld with obligation.

4. (round 3) sync_start `central_path`: identity capture is authoritative; `args.Location` only a non-cloud fallback — preserves the cloud-null invariant on central_path uniformly. Upheld (ratified by PLN1).

PLN1 ruling (SC-032, decision #3, spec 5 amended in place): `WorksharingCentralGUID` is Revit Server-only per Autodesk docs — spec's match priority gains normalized `central_path` (case-insensitive ordinal) between central GUID and creation_guid; HealthContext identity keys gain central path. Raw capture unchanged. Implemented in unit 4's fix round (matcher lives in ActivityAggregator + HealthContext — both DEV2's units).

Judgement calls (unit 1, DEV1 — all five **ratified** by PLN1 2026-07-18, pre-merge):
1. Core logging via injectable `Action<string>` callback, not a Serilog ref — unit 2 wires Serilog; keeps the fork-liftable core dependency-free. Upheld: strengthens the placement rule.
2. Separate `OverrideTelemetryRootForTests` hook rather than widening `OverrideRootForTests` — telemetry root is LocalAppData, not the roaming root; one override would conflate scopes. Upheld.
3. Recovery replay: `doc_closed` with Cancelled/Failed status ⇒ doc still open (gets a synthetic close); missing/other status ⇒ closed. Upheld: matches Revit close semantics.
4. Zero-parseable-line orphan ⇒ synthetic `session_end` anyway (session_guid from filename, ts from mtime) — upholds the closed-file contract for every non-live file. Upheld.
5. Liveness probe = `FileAccess.Write`/`FileShare.None`; a reader-held file probes locked on Windows — spec already accepts skip-until-next-startup for reader-locked files. Upheld; probe verified against the writer's advisory lock on Linux CI.

Judgement calls (unit 3, DEV2 — all four **ratified** by PLN1 2026-07-18, pre-merge):
1. Per-day open-hours bucketed by injectable `TimeZoneInfo` (UTC default; bridge passes Local) — "hours that day" means the user's local day; wire ts stays UTC per spec. Upheld.
2. Toggle gap: open time capped at `collection_disabled`, no auto-resume accrual at `collection_enabled` — the gap is unobserved; undercount, never overcount (matches the spec's crash-truncation philosophy). Upheld.
3. Concurrent sessions on one file: union intervals to wall clock before day bucketing — a day can never exceed 24h. Upheld.
4. Load points plotted at `doc_opened.ts`, sync points at `sync_start.ts` — matches the spec's "plotted at the open's/sync's timestamp". Upheld.
5. (SC-025 fix, round 2) Save-As mirror: the OLD identity's interval also ends at the save — its view no longer accrues to session_end past a Save As (post-save events carry the new identity; accruing both ways would double-count). Upheld.

Judgement calls (unit 2, DEV1 — all four **ratified** by PLN1 2026-07-18, pre-merge):
1. `closing_id` sourced from `args.DocumentId` — the only correlation property the real DocumentClosing/Closed API exposes (verified vs R25 metadata); wire field name unchanged. Upheld.
2. `session_start` record emitted at `ApplicationInitialized` with a lazy first-record guard (session_guid still minted at OnStartup) — `ControlledApplication` has no `Username`; `autodesk_user` only exists on `Application`. Upheld.
3. RecoveryScanner + RetentionPruner run even when collection is disabled — they maintain past sessions' files, which the spec keeps viewable while off. Upheld.
4. Mid-session enable emits `session_start` at enable time (session is collection-scoped); disable-before-anything-written produces no lone-marker file — keeps the closed-file contract satisfiable. Upheld.
5. (SC-027 fix, round 2) Per-doc throttle tracking key = `CreationGUID` — Save-As continuity; detached copies of one model share a throttle bucket, so pulse caps collapse for that edge. Upheld: conservative (fewer events, never more), matches server lineage semantics.

Notes:
- Dependency map is code-dependency-stingy: unit 3 (aggregator) depends only on unit 1's core models (no Revit refs), so DEV2 starts on unit 1's merge — tighter than the spec's "3 and 4 after 2" sequencing.
- Spec Build Plan step 6 (QA gate — VM pass on R25/26/27, crash recovery, concurrent instances, workshared sync, range settings, startup-latency check) is **not a sprint unit**: it needs Revit VMs and is FnB-owned, post-merge. It gates the feature, not the sprint close.
- Severity bar: Major/Medium block merge; Low goes to the unit report.
- **VM testing deferred (FnB, 2026-07-18):** the Windows Test VM is off-limits for this sprint — host RAM constraint; FnB will run the VM pass themselves later. No `windows_devkit` / `windows_vm_gui` runs by any shell. Verification = unit tests + CI + the headless UI suite only. The spec's QA gate (step 6) remains FnB-owned and now explicitly deferred past sprint close.
