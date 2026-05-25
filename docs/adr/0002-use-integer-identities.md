# Use Integer Database Identities for Company Profiles and Sync Runs

We decided to use integer database identities (SQL `INTEGER PRIMARY KEY AUTOINCREMENT` mapped to C# `int`) for the `CompanyProfile` and `SyncRun` models, rather than string GUIDs as originally drafted in the V2 specifications.

## Context

The V1 application schema defines the primary identity column `id` on the `sync_jobs` table as `INTEGER PRIMARY KEY AUTOINCREMENT`. The V2 specification suggests using string GUIDs for the corresponding `CompanyProfile.Id` and `SyncRun.CompanyId` properties.

However:
1. SQLite does not support changing a primary key column's data type or removing its autoincrement property using simple `ALTER TABLE` statements.
2. Converting the existing primary key columns to TEXT GUIDs would require recreating the tables, copying data, and dropping the original tables, which increases migration risk and potential data loss for existing users.
3. The database migration must remain idempotent and run seamlessly on first launch of the V2 application.
4. Preserving the integer identity is the safest and most robust path.

## Decision

1. **Keep Integer Primary Keys**: `CompanyProfile.Id` will continue to map to the `INTEGER PRIMARY KEY AUTOINCREMENT` column (which is renamed from `sync_jobs` to `company_profiles`).
2. **C# Model Types**: The C# model properties `CompanyProfile.Id` and `SyncRun.CompanyId` will be defined as `int` rather than `string`.
3. **Database Schema Types**: The foreign key `CompanyId` in the `sync_runs` table will be defined as `INTEGER NOT NULL REFERENCES company_profiles(id)`.
4. **Tally Identifier**: The external GUID from Tally Prime will be tracked separately in the nullable `TallyGuid` property (defined as `TEXT` in the database / `string?` in C#).

## Consequences

- The database migration script is extremely lightweight, fast, and does not require dropping or recreating existing user tables.
- Compatibility with existing database records is preserved.
- The UI page navigation and parameters will route using `int` IDs.
- External systems wishing to link via Tally GUID can query the `TallyGuid` field.
