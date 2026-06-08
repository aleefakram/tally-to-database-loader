# Sync Run Lifecycle & Safety State Handling

## System Overview

This specification establishes an explicit, conservative, state-machine driven execution lifecycle for the .NET synchronization utility. It addresses data-integrity and operational risks by ensuring scheduler safety, preventing overlapping runs, capturing ambiguous states on unexpected shutdown/restart, and managing runtime failures conservatively.

---

## 1. Status Normalization & Schema Updates

We normalize state values to track scheduler eligibility and historical execution attempts clearly.

### 1.1 Status Definitions

* **Scheduler Eligibility (`CompanyProfile.Status`)**:
  - `idle`: The profile is enabled and waiting for the next scheduler interval or manual run.
  - `running`: The sync job is actively running.
  - `completed`: The sync completed successfully.
  - `failed`: The sync run encountered a runtime error (e.g. transient query timeout, SQL execution error, parser error).
  - `review_required`: The sync encountered configuration conflicts or unsupported structures requiring developer/administrator review before it can run again.
  - `attention_required`: The sync could not proceed due to environment issues (e.g. Tally not running, target company not open, credentials missing/invalid).
  - `unknown`: The job was interrupted mid-run (e.g. due to crash, power loss, or application restart) and its final outcome is unknown.

* **Run Attempt Outcome (`SyncRun.Status`)**:
  - `running`: The attempt is active.
  - `completed`: The attempt succeeded.
  - `failed`: The attempt failed with error.
  - `unknown`: The attempt was aborted or interrupted.

### 1.2 Schema Alterations (SQLite config.db)

* Modify `SyncRun` mapping to support updating existing runs.
* Add an index on `company_profiles(status)` to optimize reconciliation/scheduler lookups if needed.
* Ensure `ended_at` in the `sync_runs` SQLite schema is nullable or initially populated with a placeholder/default that is overwritten on completion.
* **Migration of NULL/blank Status**: During database/engine startup initialization, migrate any existing NULL or blank `status` values in `company_profiles` to `'idle'`.


---

## 2. Repository API Contract

We update `IConfigRepository` and `ConfigRepository` to support atomic updates, targeted runtime state writes, and startup reconciliation.

```csharp
namespace TallyDbLoader.Core.Data
{
    public interface IConfigRepository
    {
        // ... (existing methods)

        /// <summary>
        /// Atomically transitions the company profile status to 'running' if it is eligible.
        /// Eligible statuses: 'idle', 'completed', 'failed'.
        /// Also requires enabled = 1.
        /// </summary>
        bool TryStartCompanyProfile(int id);

        /// <summary>
        /// Mark a company profile as unknown due to metadata/system failures.
        /// </summary>
        void MarkCompanyProfileUnknown(int id, string reason, DateTime now);

        /// <summary>
        /// Updates only the runtime statistics and final status of a company profile.
        /// Prevents overwriting general config changes.
        /// </summary>
        void CompleteCompanyProfileRun(
            int id,
            string finalStatus,
            DateTime endedAt,
            int durationMs,
            long rowsWritten,
            bool incrementErrorCount);

        /// <summary>
        /// Inserts a SyncRun and returns the generated long auto-increment ID.
        /// </summary>
        long AddSyncRun(SyncRun run);

        /// <summary>
        /// Updates an existing SyncRun record by ID.
        /// </summary>
        void UpdateSyncRun(SyncRun run);

        /// <summary>
        /// Reconciles any stale 'running' profiles/runs left over from an unexpected shutdown/restart.
        /// </summary>
        void ReconcileStaleRuns(DateTime now);
    }
}
```

### 2.1 SQL Implementations

* **`TryStartCompanyProfile`**:
  ```sql
  UPDATE company_profiles
  SET status = 'running'
  WHERE id = @Id
    AND enabled = 1
    AND status IN ('idle', 'completed', 'failed');
  ```
  *Returns `true` if rows affected > 0; otherwise `false`.*

* **`MarkCompanyProfileUnknown`**:
  ```sql
  UPDATE company_profiles
  SET status = 'unknown',
      last_run_at = @Now
  WHERE id = @Id;
  ```

* **`CompleteCompanyProfileRun`**:
  ```sql
  UPDATE company_profiles
  SET status = @FinalStatus,
      last_run_at = @EndedAt,
      last_duration_ms = @DurationMs,
      last_rows_written = @RowsWritten,
      error_count_24h = CASE WHEN @IncrementErrorCount = 1 THEN error_count_24h + 1 ELSE 0 END
  WHERE id = @Id;
  ```

* **`ReconcileStaleRuns`**:
  ```sql
  -- Update running company profiles to unknown
  UPDATE company_profiles
  SET status = 'unknown'
  WHERE status = 'running';

  -- Update running sync runs to unknown
  UPDATE sync_runs
  SET status = 'unknown',
      ended_at = @Now,
      result_summary = 'Interrupted by application restart before completion',
      log_excerpt = 'Startup reconciliation found stale running state.'
  WHERE status = 'running';
  ```

---

## 3. Scheduler & Manual Request Flow

### 3.1 Scheduler Eligibility Check

The scheduler ticks every 60 seconds. For each company profile, the background worker evaluates:
- **Condition 1**: `profile.Enabled` must be `true`. (If `false`, skip with log/reason `Disabled`).
- **Condition 2**: `profile.Status` must not be `running`, `review_required`, `attention_required`, or `unknown`. (If so, skip with log/reason `SafetyBlocked` or `AlreadyRunning`).
- **Condition 3**: Current time - `profile.LastRunAt` >= `profile.IntervalMinutes`. (If not met, skip with log/reason `IntervalNotMet`).

If eligible, the worker invokes `TryStartCompanyProfile(profile.Id)` to atomically lock the profile.

### 3.2 Manual Trigger Flow

To support reliable UI triggers without hidden queues:
- The background worker exposes:
  ```csharp
  private int? _manualCompanyProfileId;
  ```
- **UI preflight call**: `TryRequestManualSync(int companyProfileId)`:
  - Check database state of profile.
  - If `Status == "running"`, return `SyncStartResult(Accepted = false, ReasonCode = "AlreadyRunning", Message = "A sync run is already active.")`.
  - If `Status` is safety-blocked (`review_required`, `attention_required`, `unknown`), return `SyncStartResult(Accepted = false, ReasonCode = "SafetyBlocked", Message = "Sync blocked. Requires manual resolution.")`.
  - If `Enabled == false`, return `SyncStartResult(Accepted = false, ReasonCode = "Disabled", Message = "Profile is disabled.")`.
  - If `_manualCompanyProfileId != null`, return `SyncStartResult(Accepted = false, ReasonCode = "WorkerBusy", Message = "Another manual run request is already pending.")`.
  - Otherwise, set `_manualCompanyProfileId = companyProfileId` and signal the worker token to wake up. Return `SyncStartResult(Accepted = true, ReasonCode = "PendingDispatch", Message = "Sync run requested.")`.

### 3.3 Authoritative Worker Recheck

When the worker wakes up and handles a manual request for `_manualCompanyProfileId`:
- It queries the profile from the database.
- It performs the eligibility checks again.
- It attempts the atomic `TryStartCompanyProfile(id)`.
- If successful, it clears `_manualCompanyProfileId` and begins execution. If blocked/failed, it clears the request and logs `ManualTriggerRejected`.

---

## 4. Sync Execution Lifecycle

For every run that begins:

 1. Transition `CompanyProfile.Status` to `running` via `TryStartCompanyProfile(id)`.
 2. Create `SyncRun` with status `running` and insert via `AddSyncRun(run)`.
    - **Crucial Safety Gate**: Wrap the insert in a try-catch. If `AddSyncRun` throws an exception, call `MarkCompanyProfileUnknown(id, reason, now)` to fail closed, log `MetadataWriteError`, and terminate immediately.
3. Perform synchronization work (fetch, parse, stage, validate, promote).
4. For Incremental Sync: Watermarks are written **only** inside/after a successful database promotion transaction. If the run results in any status other than `completed`, watermarks are not advanced.
5. In the `finally` block of the execution path:
   - Calculate duration.
   - Map execution outcome to final status based on the conservative rules below.
   - Update `SyncRun` using `UpdateSyncRun(run)`.
   - Update `CompanyProfile` runtime metrics using `CompleteCompanyProfileRun(...)`.

### 4.1 Conservative Status Mapping Rules

- **`OperationCanceledException` / Token Cancellation**:
  - `SyncRun.Status` = `unknown`
  - `CompanyProfile.Status` = `unknown`
  - Reason: Run interrupted before a safe terminal state could be reached.
- **Tally Connection Offline / Target Company Mismatch**:
  - `SyncRun.Status` = `failed`
  - `CompanyProfile.Status` = `attention_required`
  - Reason: Environment issue requiring user attention.
- **Unsupported Config / Import Specification Errors**:
  - `SyncRun.Status` = `failed`
  - `CompanyProfile.Status` = `review_required`
  - Reason: Structural config issue requiring schema/definition changes.
- **Parser, Staging, DB Schema, or Promotion Validation Failure**:
  - `SyncRun.Status` = `failed`
  - `CompanyProfile.Status` = `failed`
  - Reason: Sync operation failed during processing.
- **Post-commit Metadata Failure**:
  - `SyncRun.Status` = `unknown`
  - `CompanyProfile.Status` = `unknown`
  - Reason: Data promoted, but metadata state is ambiguous.
- **Generic Exception**:
  - `SyncRun.Status` = `failed`
  - `CompanyProfile.Status` = `failed`

---

## 5. Verification Plan

### 5.1 Automated Unit & Integration Tests

* **Test 1: Startup Reconciliation**
  - Seed a company profile with `Status = "running"`.
  - Seed a corresponding active `SyncRun` with `Status = "running"`.
  - Invoke `ReconcileStaleRuns(now)`.
  - Assert company profile status changes to `unknown`.
  - Assert the sync run status changes to `unknown`, its `ended_at` is set, and its `result_summary` matches the interrupted string.
* **Test 2: Blocked States Prevent Scheduling**
  - Set up profiles in `review_required`, `attention_required`, and `unknown` status.
  - Run the scheduler eligibility check.
  - Assert none of these profiles are scheduled.
* **Test 3: Watermark Safety on Failure**
  - Run Incremental Sync with a failure injected during the data loop (e.g. database schema mismatch).
  - Verify that the final status is `failed` and the watermark table remains unchanged.
* **Test 4: Avoid Overlapping Runs (Atomic Transition)**
  - Seed a profile in `running` status.
  - Call `TryStartCompanyProfile`.
  - Assert it returns `false`.
* **Test 5: UI Preflight Validation**
  - Verify that manual sync requests are rejected with proper reason codes (`AlreadyRunning`, `SafetyBlocked`, `WorkerBusy`) depending on current status and active requests.
