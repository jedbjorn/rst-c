---
rendered_by: super-coder
source: db
edit: changes here are overwritten — author via the shell or localhost GUI
feature: Revit activity telemetry v1 — capture + local dashboard
roadmap_status: in_progress
frozen: false
---

# CONFORMANCE: Telemetry v1 — capture + Activity tab

- Spec: doc 5, `Revit Activity Telemetry v1`
- Sprint: doc 6
- Judged tree: `main` at `d09a7d0db5c04c6cbf09a65a3ce20df33a3e9d0e`
- Narrative input: only the ratified judgement calls supplied in task #145 / sprint doc 6
- Evidence: PR #104 merged at the judged SHA; all six GitHub build/stage/MSI checks green; CI xUnit 367 passed / 0 failed / 0 skipped; shipped headless Activity-chart test passed locally. The sandbox has no `dotnet`, so managed tests were not redundantly rerun locally. Per kickoff, VM work was not run.

## Verdicts

| Spec section / requirement cluster | Verdict | Evidence / note |
|---|---|---|
| Overview — local-only capture + current-user dashboard; no shipper, endpoint, auth, watcher, backfill, or aggregate views | as-specced | Capture writes only the machine-local JSONL outbox; Activity reads it in process. No transport surface was added. |
| Scope Decisions 1–6 — capture/dashboard only, no watcher, JSONL, on by default, 180-day retention, Activity tab in Health | as-specced | `TelemetryPrefs`, `OutboxWriter`, `RecoveryScanner`, `RetentionPruner`, and the Health tab/bridge implement the six decisions. |
| Architecture — Revit/UI-free `RST.Core/Telemetry`, Revit collector in Engine, Health bridge/UI separation | as-specced | `RST.Core.csproj` has no Revit/WPF/WebView refs; Engine captures; Core writes/recovers/aggregates; UI serializes precomputed data. |
| Architecture — Core logging seam uses injected callback instead of Serilog | deviated-intentionally | Ratified U1 call; preserves the stricter Core placement rule. |
| Model Identity — full block on opened/saved/saved-as; keys-only block on remaining joinable events; null-on-failure; cloud/central/creation fields raw | as-specced | `IdentityCapture`, `DocumentIdentity.WriteTo`, and `WriteKeysTo`; amended `central_path` and unknown-cloud suppression are present. |
| Model Identity / internal correlation — amended priority must remain usable when `creation_guid` is null, and Save-As must re-identify file-share centrals | deviated-silently | SC-035: `DocumentIdentity.JoinKey` omits `CentralPath`, despite the amended priority and keys-only capture. This breaks integrated recovery/aggregation seams; Medium finding below. |
| Event Schema — mandatory envelope (`event_id`, session, seq, UTC ts, type, schema v1, source) and tolerant parsing | as-specced | `TelemetrySession`, `TelemetryEvent`, and `TelemetryJson`; parser rejects incomplete/invalid mandatory envelopes. |
| Event Schema — full event set and documented payloads, linked-doc exclusion, closing correlation | as-specced | Collector subscriptions and payload construction cover session, heartbeat, open/close, save, sync, pulses, view activation, and toggle markers. |
| Event Schema — `closing_id` from `args.DocumentId`; `session_start` emitted at `ApplicationInitialized` while GUID is minted at startup | deviated-intentionally | Ratified U2 calls; required by the real Revit event/Application API surfaces. |
| Active-time model — throttled per-document changed pulses; navigation-only undercount | as-specced | `PulseThrottle` caps at one/minute; `ViewActivationGate` only emits on active-document changes. |
| Active-time throttle key — `CreationGUID` continuity across Save-As, with detached-copy collapse | deviated-intentionally | Ratified U2 call; conservative undercount, never excess emission. |
| Outbox & Durability — LocalAppData layout, install/session filename, JSONL append-only, one live writer, `FileShare.Read`, flush each, fsync heartbeat/end, partial-line tolerance | as-specced | `AppDataPaths`, `InstallIdStore`, `OutboxFiles`, `OutboxWriter`, `TelemetryJson`. |
| Outbox test override and liveness probe details | deviated-intentionally | Ratified U1 calls: separate telemetry-root test hook and write-exclusive probe. |
| Closed-file contract and retention — `session_end` defines closed; closed files prune by newest event after configured 180 days; live/locked/unclosed skipped | as-specced | Recovery runs before prune; writer/recovery fsync terminal records. |
| Crash Recovery — skip live/locked; tolerate partial line; continue seq; synthesize closes and end at last observed ts | as-specced | `RecoveryScanner` follows the replay/append contract for ordinary captured identities. |
| Crash Recovery — Cancelled/Failed remains open; zero-parseable orphan still gets filename/mtime-derived terminal event | deviated-intentionally | Ratified U1 calls. |
| Threading & Safety — cheap guarded handlers, no handler disk writes, one bounded queue/writer, heartbeat timer, bounded shutdown drain, drop/log failure behavior | as-specced | `TelemetryCollector`, `CollectorLifecycle`, and `OutboxWriter`; API capture precedes lifecycle lock. |
| Startup maintenance while collection disabled | deviated-intentionally | Ratified U2 call; maintains historical data that remains viewable while disabled. |
| Activity Tab — Health/Activity tabs, Health default, current-session/file-open clocks, shared 7D/1M/3M/6M range, three graphs, footer status/toggle | as-specced | `HealthCommand`, `HealthBridge.GetActivity`, and `health_viewer.html`. Live session block is wired by unit 5. |
| Activity graphs — inline SVG, monotone Fritsch–Carlson curve, dots/sparse and empty states | as-specced | Shipped headless test samples the production path and passed. |
| Activity empty-state treatment — family docs use no-model state; all-zero day range uses quiet empty state | deviated-intentionally | Ratified U4 calls. Capture still records family docs. |
| Activity aggregation — local-day bucketing, concurrent-session interval union, toggle-gap undercount, point timestamps, Save-As mirror behavior | deviated-intentionally | Ratified U3 calls. The general behavior is implemented; SC-035 is the file-share-central hole in its internal re-identification key. |
| Current-file matching — cloud pair → central GUID → case-insensitive central path → creation GUID; live file included | as-specced | `ActivityAggregator.BuildMatcher` implements the amended display priority and reads every outbox file including live readable files. |
| Health plumbing — active identity keys incl. central path, bridge arity, small JSON result, no active document state, per-user/machine scope | as-specced | `HealthContext`, `HealthCommand`, `HealthBridge`; doc-less still renders the current-session block. |
| Sync-start central path — identity capture authoritative; `args.Location` is non-cloud fallback only | deviated-intentionally | Ratified U4 call; preserves the cloud-null invariant. |
| Consent & Config — roaming prefs, enabled default, one-time disclosure, immediate persisted toggle, historical data visible while off | as-specced | `TelemetryPrefs`, `FirstRunNotice`, `ConsentNotice`, `ConsentToggle`, collector/bridge wiring. |
| Consent ordering/gating — notice only when collector is actually enabled, after profile-tab build | deviated-intentionally | Ratified U5 calls. |
| Mid-session enable/disable — no lone marker file, enable-time session start, markers strictly frame gaps, heartbeat stops/resumes | deviated-intentionally | Ratified U2/U3 calls; `CollectorLifecycle` serializes the transition. |
| Fork Boundaries / v1 non-goals — stable outbox seam; future shipper/auth/server mapping remain absent | as-specced | No v2 integration surface was implemented. |
| Edge Cases — concurrent instances, crash tails, linked/family/detached/cloud/clock/disk/toggle/live-reader/roaming/zero-doc behavior | as-specced | Implemented and covered by Core tests where host-independent; ratified deviations are itemized above. |
| Build Plan steps 1–5 — Core, collector, aggregator, Activity tab, consent/live-session wiring | as-specced | All five sprint units are integrated on the judged main SHA. |
| Build Plan step 6 — R25/R26/R27 VM QA, kill/recovery, simultaneous instances, real central/cloud, toggle/ranges, startup latency | unimplemented | SC-036, Low: explicitly deferred by FnB and off-limits for this pass. It remains the feature QA gate, not a sprint-code defect. |
| Open Questions — dos-arch integration, push-time consent, navigation-only heuristic remain future work | as-specced | Preserved as nonblocking v1 questions; no silent commitments were introduced. |

## Findings

### SC-035 — Medium — `central_path` missing from the shared bookkeeping key

- Spec: Model Identity amended priority/capture; Crash Recovery open-document replay; Activity Tab current-file matching; ratified U3 Save-As mirror.
- Code: `src/RST.Core/Telemetry/DocumentIdentity.cs:50` selects cloud model → central GUID → creation GUID → local path/title, omitting `CentralPath`. Consumers are `RecoveryScanner.cs:121-129` and `ActivityAggregator.cs:322,337-359,367-368,405-408`.
- Failure 1: for a file-share central where `creation_guid` capture is null but `central_path` succeeds (an allowed identity gap), keys-only `doc_closing`/sync events match the current file but produce no join key. The close cannot correlate and open time runs to `session_end`; recovery also cannot reconcile the real close correctly.
- Failure 2: Save As from one file-share central path to another keeps the same creation GUID. Because the bookkeeping key ignores central path, the old identity is treated as unchanged and accrues until close/session end, contrary to the amended display priority and ratified old-identity-end-at-save behavior.
- Required fix evidence: insert normalized/case-insensitive `CentralPath` ahead of `CreationGuid` in the bookkeeping key (or an equivalent explicit key type), then add recovery + aggregator tests for (a) central-path-only close/sync correlation and (b) file-share-central Save As changing central path with the same creation GUID.

### SC-036 — Low — VM QA gate deferred

- Spec: Build Plan step 6 and the R25/R26/R27 edge-case verification.
- Code location: runtime/installer behavior, not a missing code symbol.
- Gap: no permitted VM execution in this sprint, so Windows sharing/ACL semantics, actual Revit event behavior across three versions, crash recovery, simultaneous instances, real central/cloud identity, toggle/range UX, and startup latency remain unverified.
- Disposition: known FnB-owned post-sprint gate; do not freeze feature 2 as fully QA-complete until it is run.

## Recommendation

Conformance is **not clean**: one Medium silent deviation (SC-035) requires a fix unit and scoped re-run before spec freeze. SC-036 is a known Low deferred feature gate and does not by itself block sprint close under the kickoff ruling.


## Scoped Conformance Re-run - Sprint 6 unit 6

- Judged tree: `main` at `004249668173f06eccc46aaa5f2f4584546b50b3` (PR #105 merge).
- Scope: SC-035 fix surface only - Model Identity bookkeeping key/correlation, Crash Recovery Save-As replay, ActivityAggregator bookkeeping, and the ratified decline-on-ambiguity rule cited as rulings #171/#177.
- Narrative input: task #188 and sprint doc 6 only.
- Evidence: direct code/test reads from the pinned git tree. The merge-SHA build workflow completed all six xUnit/stage/MSI jobs successfully; the VM-backed integration run remains waiting and is excluded by the explicit VM deferral. The sandbox has no `dotnet`, so managed tests were not redundantly rerun locally. No VM work was run.

### Re-run verdicts

| Scoped requirement | Verdict | Evidence / note |
|---|---|---|
| SC-035 required fix - normalized, case-insensitive `CentralPath` ahead of `CreationGuid` in the shared bookkeeping key | as-specced | `DocumentIdentity.JoinKey` now ranks cloud model, central GUID, upper-invariant central path, then creation GUID. `DocumentIdentityTests` covers priority and casing. |
| Crash Recovery - central-path-only close correlation and file-share Save-As replay, including absent-path lineage fallback | as-specced | `RecoveryScanner.TrackOpenDocs` moves the open identity across Save-As and declines a non-unique creation-lineage fallback. Tests cover null-creation central-path close, changed-central Save-As, absent path joins, and ambiguous lineage. |
| ActivityAggregator - central-path-only close/sync correlation and file-share Save-As old/new interval split | as-specced | Replay keys close/sync endpoints on normalized central path and retires the old identity at Save-As. The required central-path-only and changed-central tests are present. |
| ActivityAggregator - ambiguity must decline close/sync pairing, including an exact lower-level hit | deviated-silently | SC-040: the pairwise resolver implements the ruling only after live docs have been placed in a key-unique dictionary. Two indistinguishable live docs with the same gapped lower-level key are collapsed first, so their ambiguity is invisible. |

### SC-035 disposition

The original SC-035 Medium is **resolved as-specced**. Its two reported failures and required regression evidence are implemented at the judged SHA.

### SC-040 - Medium - duplicate lower-level `JoinKey` erases live-document ambiguity

- Spec/ruling: every identity field may be null; Save-As siblings may share `creation_guid`; rulings #171/#177 require an ambiguous close or sync end to decline even when its lower-level key hits exactly.
- Code: `src/RST.Core/Telemetry/ActivityAggregator.cs:270-283,323-330` stores live docs in `Dictionary<string, DocumentIdentity>` and ignores a second `doc_opened` when its `JoinKey` already exists. `ResolveOpenDoc` at lines 566-579 therefore sees one candidate, and the close/sync paths at lines 430-478 accept what now appears to be a non-ambiguous exact hit.
- Failure: open two live lineage siblings whose central/cloud identity reads both gap, leaving the same creation-only key. The second open is discarded from `allOpenDocs`. A creation-only close or sync end can then retire/pair the first exact-key entry even though either sibling could own the event, truncating open time or inventing a sync duration contrary to the ratified decline rule.
- Missing evidence: the suite covers a gapped exact-key doc plus a higher-key sibling, but not two simultaneously live docs that share the same lower-level `JoinKey`.
- Required fix evidence: preserve live-doc multiplicity independently of `JoinKey` (or explicit ref-counted equivalent), then add close and sync regressions with two creation-only live siblings showing that the first ambiguous endpoint declines and a later uniquely attributable endpoint still resolves.

### SC-036 status

SC-036 remains the known Low deferred VM QA gate and does not itself block sprint close.

## Latest Recommendation

Scoped conformance is **not clean**: SC-035 is resolved, but SC-040 is a new Medium silent deviation in the same ActivityAggregator ambiguity surface and blocks spec freeze pending planner disposition. SC-036 remains a non-sprint Low.


## Scoped Conformance Re-run — Sprint 6 unit 7

- Judged tree: `main` at `aba1034ab464de641bb307e738b793760169b800` (PR #106 merge).
- Scope: SC-040/041/042 fix surface only — ActivityAggregator decline-guard multiplicity, unique attribution, and resolver precedence.
- Narrative input: task #215 and sprint doc 6 only.
- Evidence: direct code/test reads from the pinned git tree. All six PR #106 xUnit/stage/MSI checks passed. The sandbox has no `dotnet`, so managed tests were not redundantly rerun locally. Per the sprint's explicit deferral, no VM work was run.

### Unit 7 re-run verdicts

| Scoped requirement | Verdict | Evidence / note |
|---|---|---|
| SC-040 — preserve multiplicity when several live docs share one lower-level `JoinKey`; ambiguous close/sync/Save-As fallback declines | as-specced | `allOpenDocs` now stores a list per key and `ResolveOpenDoc` counts each live element. Regressions cover duplicate-key close, sync end, and Save-As fallback. |
| SC-041 — a rejecting same-key sibling is not a candidate; a uniquely confirmed endpoint retires only the named live doc | deviated-silently | Pairwise resolution and element removal are correct, but the matched current-file interval remains keyed to the whole bucket and survives while the rejecting sibling remains. SC-043 below. |
| SC-042 — a resolver winner outranks the event's different exact-key bucket; exact-key handling is fallback-only | as-specced | The close path consults the exact bucket only when `ResolveOpenDoc` returned no candidate, and the cross-bucket close regression covers the winner-over-fallback case. |

### SC-040/041/042 disposition

SC-040's original duplicate-key ambiguity defect is resolved as-specced, and SC-042's close resolver precedence is implemented as-specced. The rerun is not clean because SC-041's unique-attribution fix leaves current-file interval ownership attached to the shared lower-level bucket rather than to the live elements that actually match the current file.

### SC-043 — Medium — rejecting same-key sibling extends the closed current file's interval

- Spec: Activity Tab “Session time — this file” and current-file matching priority (cloud pair → central GUID → central path → creation GUID); every event must be filtered to the active document at its highest present identity level.
- Code: `src/RST.Core/Telemetry/ActivityAggregator.cs:270-282,326-339,438-485`. `openDocs` stores one timestamp per `JoinKey`; after the resolver uniquely removes a matching element, line 479 preserves that timestamp whenever any sibling remains in the bucket, including a sibling that `BuildMatcher` rejects.
- Existing regression evidence: `src/RST.Tests/Telemetry/ActivityAggregatorTests.cs:1252-1300` opens A (project P1 + creation G, model GUID gapped) at 09:00 and B (project P2 + the same creation G) at 09:30. B is explicitly rejected for A's view by the cloud-project mismatch. A's 10:00 close is uniquely attributed because B rejects it, yet the test expects A's interval to continue until B closes at 11:00.
- Failure: the Activity tab for A reports 2.0 hours instead of A's actual 1.0 hour, attributing an explicitly different cloud project/model's open time to A. This violates the current-file-first identity priority and misattributes workplace telemetry.
- Required fix evidence: track matched interval occupancy per live element (or an equivalent matched ref-count) independently of the all-doc resolver bucket. A uniquely attributed close must end the current-file interval when the last matching element closes even if a rejecting sibling remains; retain the SC-040 ambiguous-decline behavior. The regression above must assert A = 1.0h and B = 1.5h, alongside the duplicate-key ambiguous cases.

### SC-036 status

SC-036 remains the known Low deferred VM QA gate and does not itself block sprint close.

## Latest Recommendation

Scoped conformance is **not clean**: SC-040 and SC-042 are resolved, but SC-043 is a new Medium silent deviation in SC-041's unique-attribution surface and blocks spec freeze pending planner disposition. SC-036 remains a non-sprint Low.



## Final Scoped Conformance Re-run — Sprint 6 unit 8

- Judged tree: `main` at `3c8da2998a9b7f3f3b131bdd9b2116e5c0ca014f` (PR #107 merge).
- Scope: SC-043 fix surface only — per-live-element matched occupancy in `ActivityAggregator`, including close and Save-As retirement while rejecting same-key siblings remain live.
- Narrative input: task #228 and sprint doc 6 only.
- Evidence: direct code and regression-test reads from the pinned git tree. All six PR #107 xUnit/stage/MSI checks passed; xUnit reported 391 passed / 0 failed / 0 skipped. The sandbox has no `dotnet`, so managed tests were not redundantly rerun locally. Per the sprint's explicit deferral, no VM work was run.

### Unit 8 re-run verdicts

| Scoped requirement | Verdict | Evidence / note |
|---|---|---|
| SC-043 — current-file interval occupancy follows matching live elements rather than the shared lower-level key bucket | as-specced | Each `OpenDoc` now carries a `Matched` flag. `RetireMatchedIfLast` ends `openDocs[key]` when the last matching element retires, while rejecting siblings remain in `allOpenDocs` for SC-040/041 ambiguity resolution. |
| SC-043 close path — a uniquely attributed close ends the current file at its close without retiring or accruing a rejecting sibling | as-specced | The resolved element is removed first, then matched occupancy is retired independently of bucket liveness. The SC-041 regression now asserts A = 1.0h and the rejecting sibling B = 1.5h. |
| SC-043 Save-As path — re-identification out of the current-file view ends matched occupancy even when a rejecting same-key sibling survives | as-specced | Both same-key identity replacement and changed-key movement update per-element matched state and retire the old matched interval when its final matching element leaves. A dedicated regression asserts the interval ends at the 10:00 Save-As. |
| Prior ambiguity and resolver guarantees — duplicate-key ambiguity declines; uniquely attributable endpoints remove one element; resolver winner outranks exact-key fallback | as-specced | The SC-040/041/042 regression surface remains present and passed in the 391-test xUnit job; the occupancy change does not collapse live multiplicity or alter resolver precedence. |

### SC-043 disposition

SC-043 is **resolved as-specced**. The implementation separates current-file time attribution from resolver-bucket liveness, and the required A = 1.0h / B = 1.5h regression is green. No new scoped finding was found.

### SC-036 status

SC-036 remains the known Low deferred VM QA gate and does not block sprint close under the kickoff ruling.

## Final Recommendation

Conformance is **clean for sprint freeze**: 0 Major, 0 Medium, 1 known Low. SC-043, the final Medium, is resolved as-specced at the judged main SHA. SC-036 remains the FnB-owned post-sprint VM verification gate.
