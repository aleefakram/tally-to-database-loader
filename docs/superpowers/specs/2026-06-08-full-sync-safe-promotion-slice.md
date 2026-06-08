# Design: Full Sync Safe Promotion Slice

## 1. Scope
Replace the unsafe direct live table truncation/loading in `FullSyncRunner` with a safe stage-validate-promote flow.
* A staging table is populated and validated.
* Validated data is promoted to the live table atomically (per-table transaction).
* Non-implemented database engines fail closed using a placeholder promoter that throws.
* Proof of concept and test coverage targeting in-memory SQLite.

---

## 2. Core Components

### `IFullSyncTablePromoter`
Interface defining the boundary for staging, validating, and promoting table data:
```csharp
using System.Data;
using System.Data.Common;
using System.Threading.Tasks;
using TallyDbLoader.Core.Tally;

namespace TallyDbLoader.Core.Sync
{
    public interface IFullSyncTablePromoter
    {
        /// <summary>
        /// Stages the data, runs validation, and promotes it to the live table inside a transaction.
        /// Returns the number of promoted rows.
        /// </summary>
        Task<int> StageValidateAndPromoteAsync(DataTable data, TableConfig table, DbConnection targetConn);
    }
}
```

### `SqliteFullSyncTablePromoter`
The SQLite promoter implementation:
1. **Create Staging Table:** Replicates schema columns of the live table using `CREATE TABLE temp_staging_{tableName} AS SELECT * FROM {tableName} WHERE 1=0;`. No primary keys or unique constraints are created on the staging table, allowing duplicate/null values to load without early driver crashes.
2. **Clear Staging:** Ensures the staging table is empty before import.
3. **Load Staging:** Inserts all rows from the parsed `DataTable` into `temp_staging_{tableName}` using insert commands executed directly on the provided `targetConn` (avoiding `SqliteLoader` to stay within the transaction and connection lifetime).
4. **Validation:** Runs table-aware checks (null checks, uniqueness checks depending on `TableConfig.Nature`).
5. **Atomic Promotion (Within one transaction):**
   * Clear the live table (`DELETE FROM {tableName};`).
   * Copy from staging to live using an explicit list of columns derived from the parsed `DataTable`:
     ```sql
     INSERT INTO {tableName} (col1, col2, ...) SELECT col1, col2, ... FROM temp_staging_{tableName};
     ```
   * Drop the staging table (`DROP TABLE temp_staging_{tableName};`).
6. **Graceful Rollback & Cleanup:** If validation fails or promotion fails, the transaction is rolled back, preserving existing live data. Staging tables are dropped in a `finally` block.

### `UnsupportedFullSyncTablePromoter`
Throws `NotSupportedException` immediately upon call. Used to fail closed for MSSQL, MySQL, and PostgreSQL until promoters are implemented.

---

## 3. Validation Rules

Validation rules are determined dynamically based on the table nature:

### For Primary Tables (`tableConfig.Nature == "Primary"`)
1. **GUID Configuration:** The table must have a `guid` field configured.
2. **GUID Values Present:** Staged rows must have non-null and non-empty `guid` values.
3. **GUID Uniqueness:** Staged `guid` values must be unique (no duplicate keys).

### For Derived Tables (`tableConfig.Nature != "Primary"`)
1. **GUID Not Required:** The table does not need a `guid` field.
2. **Duplicate GUIDs Allowed:** Duplicate `guid` values are permitted (no uniqueness check).
3. **No Blanket Uniqueness Check:** Replacement works in full snapshot format.

### For Any Table
1. **Schema Match:** Columns in the parsed `DataTable` must match columns in the target database table.
2. **Failure Blocks Promotion:** Any staging, loading, or validation failure aborts the promotion and rolls back the transaction. Live target tables are not mutated before the promotion transaction commits.

---

## 4. Runner Flow

For each table in the sync config, `FullSyncRunner` executes:
1. Generate TDL XML.
2. Fetch TDL XML response from Tally.
3. Parse XML response into a `DataTable`.
4. Call `StageValidateAndPromoteAsync(dt, table, targetConn)`.
5. Increment total rows count by the count returned from the promoter.

`FullSyncRunner` no longer executes direct deletes/truncations on the live database.

---

## 5. Verification & Required Tests

Tests will be added/updated in `FullSyncRunnerTests.cs`:
1. **Primary Table Duplicate GUID Fails Closed:** Attempts to promotion duplicate GUIDs in a `Primary` table fail and keep original live data intact.
2. **Primary Table Missing/Empty GUID Fails Closed:** Attempts to promote missing/empty GUIDs in a `Primary` table fail and preserve live data.
3. **Derived Table with No GUID Succeeds:** Derived tables without `guid` fields are promoted successfully.
4. **Derived Table with Duplicate parent GUID Succeeds:** Derived tables containing duplicate parent GUID references (e.g. child entries) promote successfully.
5. **Successful Primary Run Replaces Old Live Data:** Checks that a valid set of master rows clears old records and loads the new ones.
6. **Idempotency Check:** Running the sync twice with the same data preserves exactly one set of rows without duplication.
7. **Missing Promoter Fails Closed:** Creating a runner with an unsupported promoter throws and does not mutate any tables.

---

## 6. Non-Goals
* Safe promotion implementations for MSSQL, MySQL, or PostgreSQL (Phase 1 fails closed).
* Data chunking or streaming.
* Multi-table transaction atomicity (promotion is atomic per-table).
* Identifier capitalization/normalization.
* Scheduler state, locking, or background orchestration changes.
