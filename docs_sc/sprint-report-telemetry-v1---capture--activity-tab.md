---
rendered_by: super-coder
source: db
edit: changes here are overwritten — author via the shell or localhost GUI
feature: 
roadmap_status: 
frozen: false
---

# SPRINT REPORT: Telemetry v1 — capture + Activity tab

Sprint doc 6 (frozen) · Spec doc 5 · Conformance doc 7 · Feature 2 · Declared 2026-07-18, closed 2026-07-19 · Planner PLN1
Models: devs=claude/fable · reviewers=codex/gpt-5.6-sol

## Verdict

**8 units / 7 PRs (#100–#107, one PR per unit except unit 2's stacked fix pushes), all merged; conformance: conforms-with-ratified-deviations; main green.** The five planned build units shipped the full spec — capture engine, collector, aggregator, Activity tab, consent — and the pre-freeze conformance pass then caught one silent deviation (SC-035: `central_path` missing from the shared bookkeeping key) that no per-unit review saw, spawning three conformance-fix units (6–8) before the re-verify came back clean (0 Major / 0 Medium). Deferred with eyes open: the **VM QA gate (SC-036, Low)** — Revit 2025/2026/2027 runtime verification is FnB-owned and explicitly post-sprint (host RAM constraint); feature 2 stays `in_progress` until it runs.

## Units Shipped

| seq | unit | dev | PR | merge | review rounds |
|---|---|---|---|---|---|
| 1 | Core engine (RST.Core/Telemetry) | DEV1 | #100 | acbfb40 | 3 |
| 2 | Collector (RST.Engine/Telemetry) | DEV1 | #102 | 1ea00c0 | 3 |
| 3 | Aggregator (RST.Core/Telemetry) | DEV2 | #101 | f77d6e5 | 3 |
| 4 | Activity tab (RST.UI/Health + viewer) | DEV2 | #103 | 73b5256 | 4 |
| 5 | Consent + live-session wiring | DEV1 | #104 | d09a7d0 | 3 |
| 6 | Conformance fix: JoinKey central_path (SC-035) | DEV2 | #105 | 0042496 | 5 |
| 7 | Conformance fix: decline-guard multiplicity (SC-040–042) | DEV2 | #106 | aba1034 | 3 |
| 8 | Conformance fix: per-element interval occupancy (SC-043) | DEV2 | #107 | 3c8da29 | 1 |

Planned order held: 1 → {2, 3} parallel → 4 → 5; units 6–8 were inserted at close-out by the conformance loop. REV1 gated all eight and ran three conformance passes (full + two scoped re-runs).

## Judgements Made

21 ambiguity calls across the five build units — **all ratified pre-merge, none overruled**; logged in full on sprint doc 6. Highlights: injectable log callback keeps RST.Core dependency-free (fork-liftable); `session_start` emitted at `ApplicationInitialized` because `ControlledApplication` lacks `Username`; throttle key = CreationGUID (detached copies share a bucket — undercount direction); tz-injectable local-day bucketing; toggle-gap and Save-As accrual both undercount-never-overcount; first-run notice gates on the collector *actually* collecting. Zero severity disputes reached the planner. Two planner rulings mid-fix-chain: the **decline-on-ambiguity close rule** (rulings #171/#177 — an ambiguous close/sync retires nothing; session_end/recovery closes it) and its unique-attribution balance (accept only a resolver-unique winner).

## Spec Accuracy

Conformance doc 7 (main @ 3c8da29, final): every spec section **as-specced or deviated-intentionally per ratified calls**, after four rounds of Medium findings were fixed:

- **SC-032 (spec defect, found in review):** the spec keyed file-based centrals on `WorksharingCentralGUID`, which is Revit Server-only. Ruled + spec amended in place (decision #3): normalized `central_path` joins the match priority and the keys-only capture set.
- **SC-035 (silent deviation, found by conformance):** the amendment reached display matching but not the shared bookkeeping key — the exact class the conformance pass exists to catch. Fixed in units 6–8 along with four follow-on precision defects in the fix itself (SC-037/038/041/042/043).
- Cross-check: every unit report declared `deviations: none`, and the conformance verdicts agree *given the ratified calls* — SC-035 was the one true silent gap, and it was integration-level, invisible to any single unit's review.
- **SC-036 / unimplemented:** Build Plan step 6 (VM QA) — FnB-deferred, the known Low.

## Issues Encountered

- **Review depth was the sprint's story:** 25 blocking findings (SC-016–SC-043) across 25 review rounds, all fixed and verified; zero real CI reds all sprint; every PR green 6/6 on each push. The reviewer lineage (gpt-5.6-sol) consistently produced red-reproducible findings; no finding was rejected as invalid.
- **Unit 6's five-round tail:** one conformance Medium unfolded into an enumeration of identity-gap ambiguity interleavings until the general decline-on-ambiguity rule replaced point fixes.
- **Provider rate limit** (2026-07-18/19 boundary) killed one DEV2 boot mid-round-5; resumed cleanly ~4h later, no state lost (board + message trail carried everything).
- **Headless session pattern:** dev sessions frequently ended while CI ran; the planner-routed PR watches + explicit-prompt reboots covered the gap every time. Two superseded queued `integration` runs cancelled (offline VM runner); head runs left queued.
- **Infra notes:** repo map missing sprint-6 telemetry sources — reported to CART1 (msg #160), **cartographer boot needed**; upstream engine issues #256/#398 (`map-sql` unavailable from codex seats) reproduced and reported by REV1; REV1's worktree carries pre-existing mid-work (`fix/gpu-vram-64bit`, 25+ behind) — never surfaced for adoption, still outstanding.

## Deferred & Follow-ups

1. **FnB VM QA pass (SC-036, the feature gate)** — consolidated checklist: spec step 6 on R25/26/27 (crash recovery, concurrent instances, real file-central + cloud opens, toggle/ranges, startup latency) plus the unit Lows: Windows ACL/FileShare runtime behavior (unit 1), `ConsentToggle.Apply` Engine/UI seam (unit 5), in-Revit GUI verification generally. Also: re-bake the `clean` snapshot with the Autodesk sign-in first, and re-register the GitHub Actions runner while at it (doc #4 finding).
2. Same-key matched→rejected Save-As recovery regression (unit 8 Low — code correct, untested).
3. `AllowsCentralPath` call-site adoption guard — analyzer or test (unit 4 Low).
4. RecoveryScanner's guard-less duplicate-key collapse (unit 7 note) — recovery-side analog of SC-040; synthesize-at-session-boundary makes it lower stakes, unexamined.
5. Cartographer boot to heal the repo map (msg #160).
6. User-facing docs for the Activity tab (feature 1 docs stream / docs-pending).

## Spec Debt

Written back already: decision #3's two spec amendments (central_path in match priority + keys-only capture; unknown cloud-ness suppresses central_path). Still owed to the spec: (a) the **decline-on-ambiguity correlation rule** and per-element interval occupancy are now load-bearing aggregator semantics that live only in rulings #171/#177 and code — the spec's "Current file matching" section should state them; (b) `closing_id` should be documented as `args.DocumentId`; (c) `session_start` trigger should read `ApplicationInitialized`, not `OnStartup`; (d) the aggregator's correlation logic proved to be an interleaving space that example-based review enumerated finding-by-finding — a **property-based test suite** over open/close/sync/Save-As sequences with capture gaps is the structural answer and belongs in the spec's test guidance.

## Metrics

- 8 units, 7 PRs, 25 review rounds, 25 blocking findings (2 Major-severity classes among them), 0 findings rejected, 0 severity disputes, 0 real CI reds, 2 superseded CI runs cancelled.
- 3 conformance passes (1 full, 2 scoped). 21 dev ambiguity calls ratified + 2 planner rulings + 1 FnB ruling (SC-040 fix-now) + 1 FnB scope ruling (VM deferral).
- Test count on main: 140 (pre-sprint) → ~395 (xUnit) + headless chart suite.
- Wall clock: declared 18:14 day 1, closed 05:00 day 2, including a ~4h provider outage.
