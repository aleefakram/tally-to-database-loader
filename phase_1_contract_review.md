# Architectural and Safety Review: .NET Port Phase 1 Contract

This document provides a rigorous design and safety review of the **.NET Port Phase 1 Contract** (`2026-06-08-dotnet-port-phase-1-contract.md`). The review cross-references the proposed Phase 1 specification with the established domain concepts and technical decisions documented in `CONTEXT.md`.

---

## 1. Glossary and Terminology Alignment

The contract is largely aligned with the domain language, but a few refinements will prevent downstream implementation ambiguity:

*   **Sync Job Profile vs. Sync Job**:
    *   The contract uses **Sync Job Profile** (Criteria 5, 6, 8, 16, 18) and references `CompanyProfile` as the short-term class name.
    *   *Recommendation:* To avoid confusion between the short-term class name (`CompanyProfile`) and the target model (`SyncJob`), explicitly note that in code, the configuration records will load into `CompanyProfile` entities representing **Sync Jobs** (and their associated scheduling/target settings), while the target database schema structure will map to this model.
*   **Sync Mode & AlterID**:
    *   The contract uses the term "Incremental" but does not explicitly name the **AlterID** mechanism in Phase 1 Success Criteria.
    *   *Recommendation:* Add a quick clause to the incremental sync criteria confirming that incremental sync tracks changes using Tally's **AlterID** (as defined in the glossary).

---

## 2. Key Security and Resilience Gaps

To ensure the .NET port can safely replace the Node.js loader without introducing regressions, the following security and reliability behaviors from `CONTEXT.md` should be explicitly added to the Phase 1 Contract:

### A. Database Credential Protection
*   **Gap:** Database Profiles store server and credential settings, but there is no mention of credential encryption or the `ICredentialProtector` abstraction in Phase 1.
*   **Risk:** Plaintext storage of database credentials in SQLite during Phase 1 is a security regression.
*   **Action:** Add a Success Criterion:
    > "Database credentials must be protected using a Core-facing security abstraction (`ICredentialProtector`) and stored encrypted in the SQLite configuration database. The sync utility must not prompt the user for credentials mid-run."

### B. Startup Reconciliation and Crash Recovery
*   **Gap:** The contract specifies that failed runs do not advance watermarks (Criterion 11), but it does not define what happens to jobs that were interrupted due to a process crash or sudden shutdown.
*   **Risk:** Interrupted syncs could remain in a perpetual `running` state in SQLite, blocking subsequent schedule runs.
*   **Action:** Add a Success Criterion:
    > "On startup, the core utility must perform reconciliation: stale `running` jobs from a previous process session must be marked as interrupted/unknown, and jobs with ambiguous commit states must be marked as `attention_required`."

### C. Bounded Graceful Shutdown
*   **Gap:** Tray application exit behaviors are omitted.
*   **Risk:** Hard closing the WPF tray app during a target transaction could leave the destination database in a partially updated or locked state.
*   **Action:** Add a Success Criterion:
    > "App shutdown must perform a bounded graceful exit: stop scheduling new runs, signal cancellation to active sync operations, rollback active transactions where possible, and mark ambiguous commit-phase interruptions as `unknown` / `attention_required`."

### D. Tally Availability & Identity Verification
*   **Gap:** Verification of Tally Prime's availability and matching company identity before running is missing.
*   **Risk:** Syncing against the wrong company (e.g. if the user opened a different Tally company on the same port) will overwrite target data with incorrect records.
*   **Action:** Add a Success Criterion:
    > "Before starting sync, the utility must verify Tally availability and validate the target company identity (matching by GUID, with normalized name matching as a fallback). Mismatched or changed company identities must fail closed and enter `attention_required`."

### E. SQLite Schema Initialization
*   **Gap:** Numbered migrations are deferred to the backlog.
*   **Risk:** There is no defined strategy for how the initial SQLite configuration database schema is created on fresh installations.
*   **Action:** Add a Success Criterion:
    > "Phase 1 will use a simple programmatic database schema initialization (e.g., `EnsureCreated` or a baseline DDL script) executed on app startup before the scheduler starts."

---

## 3. Validation and Test Coverage

The proposed test coverage is strong but requires minor refinements to address recovery and failure modes:

*   **Rollback & Clean Failures:** The tests should verify not just that failed runs do not advance watermarks, but that the staging schema is cleanly cleaned up (or isolated) and the destination live tables are completely untouched on Full Sync failure.
*   **Import Conflict Behavior:** Since legacy YAML configuration import is a Phase 1 goal (Criterion 3), tests must cover conflict detection (e.g. importing a configuration with target identities or profiles that already exist in SQLite).
*   *Suggested Test Additions:*
    *   Verify Full Sync staging rollback/cleanup upon validation failure.
    *   Verify conflict resolution during configuration import (skip, copy/rename, overwrite).
    *   Verify startup reconciliation of interrupted sync runs.

---

## 4. Analysis of Deferred Backlog Items

Moving the following complex requirements to the **Future Hardening Backlog** is a reasonable trade-off to keep Phase 1 focused. However, we must note their immediate operational impact:

| Deferred Item | Phase 1 Operational Impact / Workaround |
| :--- | :--- |
| **Numbered SQLite Migrations** | Fresh installs will work fine, but any schema updates during the Phase 1 trial will require manual database deletion (`config.db` reset). |
| **Append-Only Audit Log** | Configuration changes won't be historically auditable. The `SyncRun` table will serve as the sole execution ledger. |
| **Config Export / Diagnostic Backups** | Users cannot export their profiles/jobs to JSON, nor package troubleshooting bundles. Debugging must rely on direct inspection of local SQLite and text log files. |
| **Target-Side Watermark Readback** | If a job commits to the target database but the local SQLite write fails, the state will be ambiguous (`unknown`). The user will need to manually reconcile or reset the job's watermark. |

---

## 5. Recommended Contract Revisions

To incorporate these safety and security rules, the following diff shows the recommended additions to the contract document:

```diff
 ## Phase 1 Success Criteria
 
 1. Core sync parity is proven for the supported relational targets: MSSQL, MySQL, and PostgreSQL.
 2. `TallyDbLoader.Core` remains UI-free and can run/test sync behavior without WPF.
 3. Existing YAML table configuration is importable without silently dropping unknown/custom definitions.
 4. Unsupported or partially understood imported configuration is saved as `review_required` and cannot run automatically.
 5. `CompanyProfile` remains the short-term implementation name for a Sync Job Profile.
 6. Database Profile stores reusable server/credential settings; target catalog, schema, and table prefix belong to the Sync Job Profile.
+7. Database credentials must be protected using a Core-facing security abstraction (`ICredentialProtector`) and stored encrypted in the SQLite config database.
-7. Tally XML communication uses UTF-16 and is serialized per Tally endpoint.
+8. Tally XML communication uses UTF-16 and is serialized per Tally endpoint.
-8. The same Sync Job Profile cannot run concurrently.
+9. The same Sync Job Profile cannot run concurrently.
-9. Writes to the same target identity are locked.
+10. Writes to the same target identity are locked.
-10. Incremental watermarks advance only after extraction, parsing, and target writes commit successfully.
+11. Incremental watermarks advance only after extraction, parsing, and target writes commit successfully.
-11. Failed runs are recorded and do not advance watermarks.
+12. Failed runs are recorded in `SyncRun` and do not advance watermarks.
-12. Full Sync loads into staging, validates structurally, and promotes only after the staged data passes validation.
+13. Full Sync loads into staging, validates structurally, and promotes only after the staged data passes validation.
-13. Full Sync must not expose empty or partial live target tables as the default production behavior.
+14. Full Sync must not expose empty or partial live target tables as the default production behavior.
-14. Normal operational target writes are idempotent by stable Tally identity where available.
+15. Normal operational target writes are idempotent by stable Tally identity where available using AlterID tracking.
-15. If no stable identity exists for an entity stream, incremental upsert fails closed for that stream.
+16. If no stable identity exists for an entity stream, incremental upsert fails closed for that stream.
-16. WPF can configure Database Profiles and Sync Job Profiles, run sync manually, and display run status/history.
+17. WPF can configure Database Profiles and Sync Job Profiles, run sync manually, and display run status/history.
-17. Safety-blocked states such as `review_required`, `attention_required`, and `unknown` block automatic scheduling.
+18. Safety-blocked states such as `review_required`, `attention_required`, and `unknown` block automatic scheduling.
+19. App startup performs reconciliation: stale `running` jobs from previous sessions are marked interrupted/unknown, and ambiguous commit states trigger `attention_required`.
+20. App shutdown performs a bounded graceful exit: active runs are cancelled, target transactions rolled back, and ambiguous exits flagged.
+21. Tally availability and company identity (GUID/normalized name) are verified pre-run; mismatch blocks execution.
+22. SQLite database schema is programmatically initialized (baseline DDL/EnsureCreated) on startup.

 ## Required Phase 1 Tests
 
 ...
 - Tally XML generation and UTF-16 request content
 - parser behavior using sanitized XML fixtures
 - YAML import preservation and `review_required` blocking
 - watermark advancement only after commit success
 - failed runs not advancing watermarks
 - no overlapping runs for the same Sync Job Profile
 - Tally request serialization
 - target identity write locking
 - Full Sync staging validation and failure rollback
 - idempotent upsert behavior using fakes or local test doubles
 - identifier normalization and collision detection
 - local SQLite config/repository behavior
+ - ICredentialProtector encryption and decryption
+ - startup reconciliation and graceful shutdown behaviors
+ - import conflict resolution (skip, copy, overwrite)
```
