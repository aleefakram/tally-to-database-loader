# .NET Sync Parity with Node Loader — Design Spec

**Date:** 2026-05-25
**Status:** Approved
**Scope:** Bring `TallyDbLoader.Core` sync engine to behavioral parity with the original Node.js loader (`src/tally.ts`, `src/database.ts`, `src/index.ts`) for **full sync** and **incremental sync** correctness. Out of scope: BigQuery, ADLS, JSON export, HTTP API.

---

## Problem

`BackgroundSyncWorker.SyncCompany()` (`src/TallyDbLoader.Core/Sync/BackgroundSyncWorker.cs:243-413`) currently has three correctness gaps versus the Node implementation:

1. **No incremental logic.** `company.Mode` is read but never branched on. All syncs run a single path that fetches every row.
2. **Full sync does not truncate.** Re-running full sync duplicates rows (`LoadBulkDataAsync` only inserts).
3. **Auto-date derivation is broken.** `FetchActiveCompaniesDetailedAsync` never populates `BooksFrom` / `BooksTo`, so `GetCompanyDatesAsync` always falls back to `2000-01-01 → today`.

## Goals

- Full sync and incremental sync produce a target database whose rows match Tally's current state exactly (no missing, duplicate, or stale rows).
- Incremental sync detects deletes, updates, inserts; respects `cascade_delete` and `cascade_update` from YAML; refreshes auto-numbered voucher numbers.
- Watermark (`Last AlterID Master` / `Last AlterID Transaction`) advances only on successful sync.
- Date range derives from Tally's `$BooksFrom` and `$LastVoucherDate` when company is configured for auto.

## Non-Goals

- Adding BigQuery, ADLS, or CSV/JSON loaders to .NET.
- Changing the YAML schema or TDL XML generation (already at parity).
- Refactoring `BackgroundSyncWorker`'s timer / pause / manual-trigger logic.
- Adding an HTTP API.

---

## Architecture

Split sync responsibilities into focused classes invoked by `BackgroundSyncWorker.SyncCompany()`. The worker becomes a thin dispatcher.

### New components

| Class | Responsibility | Mirrors |
|---|---|---|
| `CompanyInfoFetcher` | Issues the `TallyDatabaseLoaderReport` TDL request; parses GUID, Name, BooksFrom, LastVoucherDate, AltMstId, AltVchId; writes them to target DB `config` table. | `tally.ts:568-627 saveCompanyInfo()` |
| `WatermarkRepository` | Reads / writes `Last AlterID Master` and `Last AlterID Transaction` rows in the target DB's `config` table. | `tally.ts:103-104` and the insert in `saveCompanyInfo` |
| `StagingTableManager` | Ensures `_diff(guid, alterid)`, `_delete(guid)`, `_vchnumber(guid, voucher_number)`, and `config(name, value)` exist in target DB; truncates them on demand. Per-DB SQL via `IDatabaseLoader`. | `database-structure-incremental.sql` (already in repo root) |
| `FullSyncRunner` | For each YAML table: generate XML → POST → parse → bulk load into a `DataTable`; truncate target table; `LoadBulkDataAsync`. | `tally.ts:313-399` (full sync branch) |
| `IncrementalSyncRunner` | Implements the diff/delete/cascade/refetch flow. | `tally.ts:88-308` (incremental sync branch) |
| `WatermarkXmlBuilder` | Builds the static TDL XML that returns `$AltMstId,$AltVchId` for the active company. | `tally.ts:416 updateLastAlterId()` |

### Modified components

| Class | Change |
|---|---|
| `BackgroundSyncWorker` | `SyncCompany()` branches on `company.Mode`: `"incremental"` → `IncrementalSyncRunner`, else → `FullSyncRunner`. Both runners receive `CompanyInfoFetcher` output for date range. |
| `TallyClient` | Add `FetchCompanyInfoAsync(string companyName)` returning the parsed `TallyDatabaseLoaderReport` row, populating `BooksFrom` / `BooksTo` on `TallyCompanyInfo`. Existing `FetchActiveCompaniesDetailedAsync` keeps the lightweight name+isGroup contract. |

### Data flow (incremental)

```
BackgroundSyncWorker.SyncCompany(company)
  └─ CompanyInfoFetcher.FetchAndPersist(company)
       ├─ TallyClient.FetchCompanyInfoAsync   → BooksFrom, LastVoucherDate, AltMstId, AltVchId
       └─ writes target DB config table
  └─ IncrementalSyncRunner.Run(company, companyInfo)
       ├─ StagingTableManager.EnsureStagingTables()
       ├─ WatermarkRepository.GetWatermarks()    → (lastIdMasterDb, lastIdTxnDb)
       ├─ if (companyInfo.AltMstId == lastIdMasterDb && companyInfo.AltVchId == lastIdTxnDb) → skip
       ├─ foreach Primary table whose nature changed:
       │     ├─ truncate _diff, _delete
       │     ├─ fetch GUID+AlterID into _diff (DataTable → bulk load into _diff)
       │     ├─ INSERT INTO _delete SELECT guid FROM <table> WHERE guid NOT IN (SELECT guid FROM _diff)
       │     ├─ INSERT INTO _delete SELECT t.guid FROM <table> t JOIN _diff d ON d.guid=t.guid WHERE d.alterid<>t.alterid
       │     ├─ DELETE FROM <table> WHERE guid IN (SELECT guid FROM _delete)
       │     └─ foreach cascade_delete: DELETE FROM <child> WHERE <field> IN (SELECT guid FROM _delete)
       ├─ foreach Master table (if master changed):
       │     ├─ append filter "$AlterID > {lastIdMasterDb}"
       │     ├─ generate XML → POST → parse → bulk load (INSERT only — _delete cleared the rows)
       ├─ foreach Transaction table (if txn changed): same with txn watermark
       ├─ foreach Primary table → cascade_update SQL (per-DB)
       ├─ if any vouchertype uses auto numbering → refresh trn_voucher.voucher_number via _vchnumber
       ├─ truncate _diff, _delete, _vchnumber
       └─ WatermarkRepository.WriteWatermarks(companyInfo.AltMstId, companyInfo.AltVchId)   ← only on success
```

### Data flow (full)

```
BackgroundSyncWorker.SyncCompany(company)
  └─ CompanyInfoFetcher.FetchAndPersist(company)        ← derives dates if 'auto'
  └─ FullSyncRunner.Run(company, companyInfo)
       └─ foreach YAML table (filtered by EntityFlags):
            ├─ generate XML → POST → parse to DataTable
            ├─ TRUNCATE TABLE <name>
            └─ LoadBulkDataAsync(dt, name)
```

---

## Schema

Reuse the existing `database-structure-incremental.sql` in repo root. `StagingTableManager` issues `CREATE TABLE IF NOT EXISTS` per database flavor for:

- `config(name varchar(64) PK, value varchar(1024))`
- `_diff(guid varchar(64) PK, alterid bigint)`
- `_delete(guid varchar(64) PK)`
- `_vchnumber(guid varchar(64) PK, voucher_number varchar(64))`

These are created on first run if missing — no migration step required from operators.

---

## Watermark Atomicity

The new `Last AlterID *` rows are written only after **all** bulk loads + cascade updates succeed. If any step throws:
- `SyncRun` is recorded as `err` (existing behavior).
- `WatermarkRepository.WriteWatermarks()` is NOT called.
- Staging tables are NOT truncated (left for inspection; truncated on next successful run start).
- Target tables may contain partial deletes from the diff phase — acceptable, because the next incremental run will re-detect and re-fetch from the unchanged DB watermark.

Note: this matches Node's behavior, which also does not transactionally wrap the whole sync. The watermark is the consistency anchor.

---

## Per-Database SQL Variations

`IDatabaseLoader` gains methods that return DB-specific SQL strings (no execution):

```csharp
string TruncateSql(string tableName);
string CascadeUpdateSql(string primaryTable, string childTable, string field);
string VoucherNumberUpdateSql();    // trn_voucher refresh from _vchnumber
string CountAutoNumberVoucherTypesSql();
```

Implementations follow Node's per-DB branches (`tally.ts:239-247`, `tally.ts:292-300`). SQLite gets `DELETE FROM <table>` instead of `TRUNCATE`.

---

## Testing Strategy

xUnit project already exists at `tests/TallyDbLoader.Tests/`.

**Unit tests** (no DB):
- `WatermarkXmlBuilderTests` — XML matches Node's `tally.ts:416` byte-for-byte (after whitespace normalization).
- `CompanyInfoFetcherTests` — given a canned Tally XML response, parses the six fields correctly. Cover: company closed (empty response) → throws; auto-date parsing of YYYYMMDD integers.
- `WatermarkRepositoryTests` — round-trip read/write against in-memory SQLite.

**Integration tests** (SQLite in target role):
- `StagingTableManagerTests` — staging tables exist after `EnsureStagingTables`; idempotent.
- `FullSyncRunnerTests` —
  - With a `FakeTallyClient` returning canned data, run full sync, assert target rows == fixture rows.
  - Re-run full sync, assert row count unchanged (truncate works, no duplicates).
- `IncrementalSyncRunnerTests` — end-to-end scenarios:
  - **Initial sync** — empty DB, watermark 0 → all rows loaded, watermark advanced.
  - **No changes** — second run with same Tally state, watermarks equal → no DB writes, no XML POSTs for table data.
  - **Insert** — new master row in Tally → appears in DB, watermark advances.
  - **Update** — existing row's `AlterID` bumped → row replaced in DB (old deleted, new inserted), other rows untouched.
  - **Delete** — row removed from Tally → row deleted from DB, watermark advances.
  - **Cascade delete** — deleting a `mst_vouchertype` removes its `trn_voucher` rows.
  - **Cascade update** — renaming a master flows into derived-text columns on transactions.
  - **Auto voucher number refresh** — inserting a back-dated voucher renumbers subsequent vouchers.
  - **Failure mid-sync** — second table throws; watermark not advanced; next run re-attempts.

**Per-DB SQL string tests** — `MSSqlLoaderTests`, `MySqlLoaderTests`, `PostgreSqlLoaderTests` assert the cascade-update / voucher-refresh SQL strings match the Node templates exactly.

`FakeTallyClient` is a new test helper: a `TallyClient` subclass (or interface extraction) that returns pre-registered XML strings keyed by request signature. Avoids real Tally dependency.

---

## Rollout

Single feature branch. No flag — the new code path is the only path once merged. Existing users with `Mode="full"` are unaffected (full path is also fixed, but behavior is more correct: no row duplication on re-run). Users with `Mode="incremental"` go from broken-as-full to actually-incremental.

---

## Open Questions

None at design time. Decisions deferred to implementation:
- Whether to extract `ITallyClient` interface or use a virtual `TallyClient`. Lean toward interface for testability; revisit if it bloats production code.
- Whether `IncrementalSyncRunner` should be one class or split (e.g., `DiffPhase`, `RefetchPhase`, `CascadePhase`). Start as one class; split if it exceeds ~400 lines.
