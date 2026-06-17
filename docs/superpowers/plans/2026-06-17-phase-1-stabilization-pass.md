# Phase 1 Stabilization Pass Execution Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Execute the Phase 1 stabilization checklist to ensure the loader's .NET port is hardened, fully audited, safe, and ready for beta release.

**Architecture:** We will systematically run automated verification commands, verify that config mutations, safety state resolutions, and diagnostic backups adhere to safety/audit specifications in the codebase, and update release notes to reflect the status.

**Tech Stack:** .NET 8, WPF, SQLite, xUnit

---

### Task 1: Automated Build and Test Suite Validation

**Files:**
- Test: `tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj`

- [x] **Step 1: Clean and build the .NET solution**
  Run: `dotnet build src/TallyDbLoader.sln`
  Expected: Build succeeds with zero errors.

- [x] **Step 2: Run the automated unit/integration test suite**
  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore`
  Expected: All 214 tests pass, and the 3 external database provider tests are skipped.

- [x] **Step 3: Check active working tree changes and git format compliance**
  Run: `git diff --check`
  Expected: No output (no whitespace errors in active diff).

---

### Task 2: Verify Configuration Mutation Guards

**Files:**
- Verify: `src/TallyDbLoader.Wpf/MainViewModel.cs:926-935`

- [x] **Step 1: Inspect GuardEngineRunning usages in MainViewModel**
  Verify that all database and company profile mutations call `GuardEngineRunning` before changing state:
  - `SaveTallySettings` (line 939)
  - `SaveCompanyProfile` (line 963)
  - `DeleteCompanyProfile` (line 1009)
  - `SaveDatabaseProfile` (line 1062)
  - `DeleteDatabaseProfile` (line 1095)
  - `TestDatabaseConnection` (line 1117)
  - `DetectActiveCompanies` (line 1208)
  - `ImportSanitizedConfig` (line 1371)
  Expected: All mutations are guarded.

- [x] **Step 2: Confirm tests exist for GuardEngineRunning**
  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --filter "FullyQualifiedName~MainViewModelTests"`
  Expected: All tests pass.

---

### Task 3: Verify Safety State Resolution and Audit Trail

**Files:**
- Verify: `src/TallyDbLoader.Core/Data/ConfigRepository.cs:942-1018`

- [x] **Step 1: Verify safety-state resolution status enforcement**
  Verify that `ResolveCompanyProfileSafetyState` explicitly checks that the profile's current status is one of the safety-blocked states (`review_required`, `attention_required`, or `unknown`) and throws an exception otherwise.
  Expected: Status check is present and throws on invalid status.

- [x] **Step 2: Verify audit log insertion during safety resolution**
  Verify that `ResolveCompanyProfileSafetyState` calls `InsertConfigAuditLog` inside a transaction and records a `resolve_safety_state` action with correct snapshots and actor name.
  Expected: Single transaction, correct audit log written, rollback on error.

- [x] **Step 3: Run the relevant unit tests**
  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --filter "FullyQualifiedName~ConfigRepositoryTests.SaveCompanyProfile_Create_WritesAuditRow"`
  Expected: Test passes.

---

### Task 4: Verify Sanitized Config Transfer

**Files:**
- Verify: `src/TallyDbLoader.Core/Data/ConfigExportService.cs`
- Verify: `src/TallyDbLoader.Core/Data/ConfigImportService.cs`

- [x] **Step 1: Verify passwords and DPAPI ciphertexts are excluded on export**
  Verify that `ConfigExportService` omits password values or replaces them with a placeholder/boolean `has_password = true` indicator rather than raw text or DPAPI ciphertext.
  Expected: Passwords are omitted.

- [x] **Step 2: Verify conflict rejection during import**
  Verify that `ConfigImportService` blocks silent overwriting or conflicts during sanitized config import (as create-new-only is the rule).
  Expected: Conflicts are detected and import is blocked.

- [x] **Step 3: Verify import writes a single transaction and audit log summary**
  Verify that `ConfigImportService` executes in a transaction and inserts a single `import_sanitized_config` audit row.
  Expected: Single transaction and audit entry.

---

### Task 5: Verify Diagnostic Backup Robustness

**Files:**
- Verify: `src/TallyDbLoader.Core/Data/DiagnosticBackupService.cs:43-171`

- [x] **Step 1: Verify backup behaves correctly when directories are missing**
  Verify that `DiagnosticBackupService` does not fail when log or raw XML directories are missing and `IncludeRawXml` is false.
  Expected: Successful ZIP generation with missing folders handled gracefully.

- [x] **Step 2: Verify backup writes audit trail record**
  Verify that `DiagnosticBackupService` inserts an `export_diagnostic_backup` audit log record.
  Expected: Audit record written successfully.

---

### Task 6: Update Release History and Document Provider Status

**Files:**
- Modify: `docs/release-history.md`

- [x] **Step 1: Check and update release-history.md**
  Ensure the release history reflects today's date (`17-Jun-2026`) for `2.1.0-beta` or updates the provider certification status notes clearly.
  Expected: File updated.

- [x] **Step 2: Commit all updates**
  Run: `git commit -am "docs: update release history and verify stabilization criteria"`
  Expected: Clean working tree.
