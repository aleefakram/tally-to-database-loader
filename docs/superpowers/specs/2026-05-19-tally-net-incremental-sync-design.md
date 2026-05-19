# Dynamic YAML-Driven .NET Loader Incremental Sync Design

**Goal:** Implement a dynamic, YAML-driven incremental synchronization engine in the C#/.NET database loader matching the Node.js implementation (`src/tally.ts` and `database-structure-incremental.sql`).

---

## 1. Schema Design

### 1.1 Config Table
Used to store configuration parameters and sync high-water marks:
- PostgreSQL / MySQL: `CREATE TABLE IF NOT EXISTS config (name VARCHAR(64) PRIMARY KEY, value VARCHAR(1024));`
- SQL Server (MSSQL):
  ```sql
  IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='config' AND xtype='U')
  CREATE TABLE config (name VARCHAR(64) NOT NULL PRIMARY KEY, value VARCHAR(1024));
  ```

### 1.2 Staging Tables
Used to identify deleted/modified records and synchronize voucher number shifts:
- PostgreSQL / MySQL:
  - `CREATE TABLE IF NOT EXISTS _diff (guid VARCHAR(64), alterid VARCHAR(64));`
  - `CREATE TABLE IF NOT EXISTS _delete (guid VARCHAR(64));`
  - `CREATE TABLE IF NOT EXISTS _vchnumber (guid VARCHAR(64), voucher_number VARCHAR(256));`
- SQL Server (MSSQL):
  - `IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='_diff' AND xtype='U') CREATE TABLE _diff (guid VARCHAR(64) NOT NULL, alterid VARCHAR(64));`
  - `IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='_delete' AND xtype='U') CREATE TABLE _delete (guid VARCHAR(64) NOT NULL);`
  - `IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='_vchnumber' AND xtype='U') CREATE TABLE _vchnumber (guid VARCHAR(64) NOT NULL, voucher_number VARCHAR(256) NOT NULL);`

---

## 2. Sync Orchestration Pipeline

### 2.1 AlterID Validation Check
Check database AlterIDs against Tally Prime before launching sync tasks.
- Read database AlterIDs:
  `SELECT value FROM config WHERE name = 'Last AlterID Master';`
  `SELECT value FROM config WHERE name = 'Last AlterID Transaction';`
- If no values are in the database, default them to `0`.
- Fetch Tally's current high-water AlterIDs using the following payload:
  ```xml
  <?xml version="1.0" encoding="utf-8"?>
  <ENVELOPE>
    <HEADER>
      <VERSION>1</VERSION>
      <TALLYREQUEST>Export</TALLYREQUEST>
      <TYPE>Data</TYPE>
      <ID>MyReport</ID>
    </HEADER>
    <BODY>
      <DESC>
        <STATICVARIABLES>
          <SVEXPORTFORMAT>ASCII (Comma Delimited)</SVEXPORTFORMAT>
        </STATICVARIABLES>
        <TDL>
          <TDLMESSAGE>
            <REPORT NAME="MyReport">
              <FORMS>MyForm</FORMS>
            </REPORT>
            <FORM NAME="MyForm">
              <PARTS>MyPart</PARTS>
            </FORM>
            <PART NAME="MyPart">
              <LINES>MyLine</LINES>
              <REPEAT>MyLine : MyCollection</REPEAT>
            </PART>
            <LINE NAME="MyLine">
              <FIELDS>FldAlterMaster,FldAlterTransaction</FIELDS>
            </LINE>
            <FIELD NAME="FldAlterMaster">
              <SET>$AltMstId</SET>
            </FIELD>
            <FIELD NAME="FldAlterTransaction">
              <SET>$AltVchId</SET>
            </FIELD>
            <COLLECTION NAME="MyCollection">
              <TYPE>Company</TYPE>
              <FILTER>FilterActiveCompany</FILTER>
            </COLLECTION>
            <SYSTEM TYPE="Formulae" NAME="FilterActiveCompany">$$IsEqual:##SVCurrentCompany:$Name</SYSTEM>
          </TDLMESSAGE>
        </TDL>
      </DESC>
    </BODY>
  </ENVELOPE>
  ```
  *(Escape the company name before replacement)*
- Parse comma-separated values. If identical to database AlterIDs, exit sync job early.

### 2.2 Active Records Synchronization (Deletion & Modification detection)
For each Primary table config:
1. Truncate `_diff` and `_delete`.
2. Retrieve all active GUIDs and AlterIDs from Tally.
3. Bulk load GUIDs and AlterIDs into `_diff`.
4. Run DB-specific commands to insert into `_delete`:
   - Deleted GUIDs: `INSERT INTO _delete SELECT guid FROM {tableName} WHERE guid NOT IN (SELECT guid FROM _diff);`
   - Modified GUIDs: `INSERT INTO _delete SELECT t.guid FROM {tableName} as t JOIN _diff as s ON s.guid = t.guid WHERE s.alterid <> CAST(t.alterid AS VARCHAR(64));`
5. Run DB-specific commands to delete obsolete records:
   - Target table: `DELETE FROM {tableName} WHERE guid IN (SELECT guid FROM _delete);`
   - Cascade delete dependents (from YAML `cascade_delete` config):
     `DELETE FROM {childTable} WHERE {childField} IN (SELECT guid FROM _delete);`

### 2.3 Incremental Fetch & Load
For all tables:
1. Append filter `$AlterID > {dbLastAlterId}` to XML query generator.
2. Query XML from Tally.
3. Parse and bulk-load matching rows into target table.

### 2.4 Cascade Reference Updates
Perform reference name updates from target lookup tables (e.g. updating parent names from referenced parents):
- **MSSQL**: `UPDATE t SET t.{targetField} = s.name FROM {targetTable} as t JOIN {activeTable} as s ON s.guid = t._{targetField};`
- **PostgreSQL**: `UPDATE {targetTable} as t SET {targetField} = s.name FROM {activeTable} as s WHERE s.guid = t._{targetField};`
- **MySQL**: `UPDATE {targetTable} as t JOIN {activeTable} as s ON s.guid = t._{targetField} SET t.{targetField} = s.name;`

### 2.5 Voucher Number Shift Corrections
If vouchertype table exists and database contains vouchers:
1. Run query `SELECT COUNT(*) FROM mst_vouchertype WHERE numbering_method LIKE '%Auto%';`
2. If count > 0, fetch current `Guid` and `VoucherNumber` list from Tally.
3. Bulk load into `_vchnumber`.
4. Run JOIN updates to update `trn_voucher.voucher_number` from `_vchnumber.voucher_number`.

---

## 3. UI and Model Schema Migration

### 3.1 Model Changes
- Add `SyncMode` property to `SyncJob` model (defaults to `"full"`).

### 3.2 Database SQLite Migration
- Add `ALTER TABLE sync_jobs ADD COLUMN sync_mode TEXT NOT NULL DEFAULT 'full';` (wrap in try-catch to allow backward compatibility).

### 3.3 WPF Interface
- Add ComboBox for selecting sync mode (`full` or `incremental`) in Job form.
- Add Grid Column for `Sync Mode` in Active Sync Schedules table.
