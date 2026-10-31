---
title: Activity rollups
tags: [rst-c, telemetry, activity, rollups, health, tray, fork]
date: 2026-10-31
project: rst-c
purpose: The file contract for rst-c's per-model activity rollups (schema version 1), the rules that fill them, and the retention hook a fork extends
---

# Activity rollups

## Overview

From v3.2 the RST tray turns the add-in's raw session logs (the outbox) into one small **rollup file per model**: daily totals kept indefinitely, plus a bounded window of individual open and sync times. Revit's Health window and the tray's Health page read the rollups; nothing is sent anywhere. This page is the contract for those files, so a fork can read them (for example to ship them to its own app) without reading rst-c's code.

- **One writer.** Only the tray writes rollups. The Revit add-in only appends to the outbox and reads rollups.
- **Machine-local.** Everything lives under `%LOCALAPPDATA%\RST\telemetry\rollups\`, beside the outbox (`%LOCALAPPDATA%\RST\telemetry\outbox\`). Neither roams.
- **Derived data.** Every number in a rollup comes from outbox files. While the raw files exist, deleting the rollups folder rebuilds it; once cleanup has deleted a raw file, its history lives only in the rollups and their `.bak.json` copies.
- **Versioned.** Every file carries `schema_version` (currently **1**). A breaking change bumps it and rst-c migrates older files forward in code. A reader should ignore fields and columns it does not know.

The code is in `src/RST.Core/Telemetry/` (`ActiveTime`, `SessionReplay`) and `src/RST.Core/Telemetry/Rollups/`; the score is `src/RST.Core/Health/ModelHealth.cs`.

## Folder layout

| File | Holds |
|---|---|
| `models\<model_id>.json` | One model's rollup |
| `models\<model_id>.bak.json` | The previous good write of the same rollup |
| `models\<model_id>.corrupt.json` | A rollup that could not be read, set aside (kept for inspection) |
| `index.json` (+ `.bak.json`) | One row per model: everything a list of models needs in one read |
| `ledger.json` (+ `.bak.json`) | One row per outbox file the fold has seen |
| `fold.lock` | Held open exclusively while a fold pass runs |

All files are UTF-8 JSON, written compactly. Timestamps are UTC, ISO-8601 with milliseconds (`2026-10-30T14:03:07.123Z`). Dates are local calendar days (`2026-10-30`) in the user's Windows time zone. Durations are **seconds** with millisecond precision.

**Writes are atomic.** The tray writes a temp file, flushes it to disk, then replaces the file, keeping the previous one as `.bak.json`. Open a file for reading with read/write/delete sharing; if it is briefly locked, retry, then read `.bak.json`. Never write to these files: the tray would treat your change as corruption and restore from `.bak.json`.

## Model identity

A model is keyed at the highest level its `doc_opened` identity carries, on the same ladder the Health window has always matched by:

1. cloud project GUID + cloud model GUID (both),
2. central GUID (Revit Server centrals),
3. central path (file-share centrals; case-insensitive),
4. creation GUID.

`model_id` is the first 16 bytes of SHA-256 over `"<kind>:<VALUE>"` as 32 lowercase hex characters, where `kind` is `cloud`, `central`, `central_path` or `creation` and `VALUE` is the key upper-cased (for cloud, `PROJECT/MODEL`). The same model always gets the same id, and every local copy of one central rolls up to one model.

A document opened once through an identity-capture gap (for example the central path could not be read, so only the creation GUID was recorded) folds into the same model when nothing else in that session could claim it. Across sessions it can still produce a second, lower-keyed rollup; Revit's Health matches every rollup the current model's keys accept, as the aggregator always has. Detached and Save As copies that share only a creation GUID are one model (spec #24, out of scope).

## Rollup file

`models\<model_id>.json`:

| Field | Meaning |
|---|---|
| `schema_version` | `1` |
| `model_id`, `key_kind` | The id and the ladder level it is keyed at |
| `keys` | `cloud_project_guid`, `cloud_model_guid`, `central_guid`, `central_path`, `creation_guid` of the `doc_opened` that defined the model; matching uses these |
| `keys_seen` | Every value seen of each key (`cloud_project_guids`, `cloud_model_guids`, `central_guids`, `central_paths`, `creation_guids`), plus the last 20 `local_paths` and `titles`, most recent last |
| `identity` | Latest `title`, `local_path`, `central_path`, `is_workshared`, `is_cloud`, and `as_of` (the event they came from) |
| `days` | The daily rows (table below): **the whole truth**, sealed and pending together |
| `opens` | `[{ "ts", "seconds" }]` timed opens (`doc_opening` → `doc_opened`), oldest first |
| `syncs` | `[{ "ts", "seconds" }]` syncs, plotted at their start, oldest first |
| `opens_trimmed_through`, `syncs_trimmed_through` | The newest event the window has dropped; absent when none |
| `pending`, `pending_base`, `sealed_sessions` | Fold bookkeeping (below); a reader can ignore them |

**Daily rows** are a table: `{"columns": [...], "rows": [[...], ...]}`. Map cells by column name. Columns in schema 1:

| Column | Meaning |
|---|---|
| `date` | Local calendar day; one row per day with any data |
| `active_seconds` | Active time (see [Active time](#active-time)) |
| `open_seconds` | Time the model was open, unioned within a session |
| `opens`, `open_seconds_sum`, `open_seconds_max` | Timed opens that day: count, sum, longest |
| `syncs`, `sync_seconds_sum`, `sync_seconds_max` | Syncs that day: count, sum, longest |
| `revit_versions`, `revit_subversions` | The Revit versions (`2026`) and subversions (`2026.4`) the model was open in that day; subversions are empty before v3.2 |
| `stats_ts`, `warnings_count`, `file_size_bytes` | The day's latest `model_stats`; all null when none was recorded that day. A null size is data (network, cloud or unsaved model) |
| `health_file_size_bytes`, `health_warnings_count`, `health_open_median_seconds`, `health_sync_median_seconds` | The four health inputs as they stood at the end of the day |

**Event window.** `opens` and `syncs` keep the last **90 days** and always at least the **last 10** of each.

**Concurrent sessions.** Two Revit instances with the same model open at once each count their own time, so a day can over-count the overlap. Readers clamp every duration to 0–24 h per day and never trust a negative value.

**Bookkeeping.** While an outbox file still exists, its share of each model is held under `pending[<session_guid>]` (its file name, size and last-write time, its own day rows and events), and `pending_base` holds, for each day a pending share touches, that day's sealed totals. A file that changes after folding is re-folded by replacing its share. **Sealing** moves a share into the permanent totals and records the session in `sealed_sessions`; markers are dropped once the raw file is gone. `days`, `opens` and `syncs` already include every pending share.

## Index

`index.json` is `{ "schema_version", "updated_utc", "models": [row, ...] }`. A row:

| Field | Meaning |
|---|---|
| `model_id`, `key_kind`, `keys`, `keys_seen` | As in the rollup |
| `title`, `local_path`, `central_path`, `is_workshared`, `is_cloud` | Latest identity |
| `first_active_day`, `last_active_day` | First and last day with any open time |
| `revit_versions`, `revit_subversions` | `{ "2026": "2026-10-30" }`: the last day the model was open in each version and subversion |
| `last_stats` | `{ "ts", "warnings_count", "file_size_bytes" }`, the latest `model_stats` ("last known") |
| `health` | The current health inputs (`file_size_bytes`, `warnings_count`, `open_median_seconds`, `sync_median_seconds`) |
| `totals` | All time: `active_seconds`, `open_seconds`, `opens`, `open_seconds_sum`, `syncs`, `sync_seconds_sum` |
| `recent_days_totals` | Table (`date` + the six totals): the last 14 days ending the day the row was written, days with data only |
| `recent_weeks_totals` | Table (`date` = the week's Monday + the six totals): the last 53 ISO weeks, the current one included |
| `rollup_bytes`, `rollup_last_write_utc` | The rollup's size and last-write time when the row was written |

Recent windows are dated, so a row written days ago is still correct: filter by date against your own today.

## Ledger

`ledger.json` is `{ "schema_version", "pass", "files": [row, ...] }`. A row: `name`, `session_guid`, `size` and `last_write_utc` when folded, `folded_pass`, and `models: [{ "model_id", "sealed" }]` where `sealed` is `no`, `current` (in the rollup) or `both` (in the rollup and its `.bak.json`). A row with `unclosed: true` is a live or not-yet-recovered file the fold looked at and left alone. The ledger is written last in every fold step.

## Active time

Decision #46. For each model, within each interval it is open in a session (from `doc_opened` to its close, `session_end` or `collection_disabled`, whichever comes first):

1. every `doc_changed_pulse` at *t* marks [*t*, *t* + 1 min) active;
2. two consecutive pulses of that interval at most **10 minutes** apart also mark the time between them active;
3. spans are clipped to the interval, so a gap never bridges a close or a collection gap;
4. active time is the length of the union, split at local midnight.

Edits Revit makes for you (a sync's reload, another add-in's change) count; a model open but untouched counts nothing.

## Model health

Decision #55. Four inputs, 25 points each, each penalty linear between its two points:

| Input | No penalty at | Full penalty at |
|---|---|---|
| Model size (`file_size_bytes`) | 500 MB | 4 GB |
| Warning density (warnings ÷ size in MB) | 0.5 per MB | 2 per MB |
| Median of the last 10 opens | 30 s | 5 min |
| Median of the last 10 syncs | 30 s | 5 min |

Score = 100 − the penalties (1 MB = 1,048,576 bytes). An input with no data is left out and the others are scaled to 100 (no size also means no density). Colour: green 75–100, yellow 50–74, amber 25–49, red below 25, grey with no inputs at all. The score is never shown; the stored inputs let a later formula recompute all history. Each day's inputs are the latest `model_stats` recorded by the end of that day and the medians of the last 10 opens and syncs up to then; a late-folded session recomputes them from its day forward while the event window still reaches.

## Weekly values

Long ranges (6M, 1Y) plot ISO weeks, Monday start, derived from the daily rows when read: active and open time sum; open and sync time are the weekly average (sum ÷ count); the Longest cards use the maximum; a week's health is its last day used.

## Folding and cleanup

The tray folds one closed outbox file (it has `session_end` and no Revit holds it) per step, oldest first, writing each touched rollup, then the index, then the ledger. A pass that finds the outbox unchanged ends there. A file folded in one pass is sealed on a later pass if unchanged; a seal reaches `.bak.json` when the model is next written, or on the following pass.

A raw outbox file is deleted only when **all** hold: it is closed and not live; its share is sealed in every model it touched, and both the rollup and its `.bak.json` (read back from disk) contain the seal; it is not one of the **3 newest** closed files; and every registered retention gate allows it. The age prune (`retentionDays`, default 180) stays as a backstop for files the tray never folds.

## Retention hook for forks

Register a gate once at tray start-up; upstream registers none:

```csharp
using RST.Core.Telemetry.Rollups;

RetentionGates.Register(new ShippedGate());

sealed class ShippedGate : IRetentionGate
{
    // candidate: Path, SessionGuid, ModelIds, SizeBytes, LastWriteUtc
    public bool AllowsDelete(RetentionCandidate candidate) => MyShipper.HasShipped(candidate.SessionGuid);
}
```

Return false to keep the file for now (it is asked again next pass). A gate that throws counts as a refusal.

## Reading from code

In-process readers use the same code Revit does: `ModelActivityReader.ReadCurrentModel(identity, options)` (Revit; adds the live session) or `ModelActivityReader.ReadModel(modelId, options)` (the tray), which fold any not-yet-folded outbox files in memory and write nothing, and `ModelActivityReader.ReadIndex()` for the list of models.
