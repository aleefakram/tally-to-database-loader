# Dynamic YAML-Driven .NET Loader Alignment Implementation Plan

**Goal:** Refactor the C#/.NET database loader from a static, single-table import to a fully dynamic, configuration-driven sync engine that reads `tally-export-config.yaml` at runtime and synchronizes all master and transaction tables.

**Status:** **COMPLETED** (All tasks fully implemented, verified, and committed)

---

### Task 1: Package Integration & Config Models
- [x] **Step 1: Write a failing test for YAML parsing**
- [x] **Step 2: Run test to verify it fails**
- [x] **Step 3: Add YamlDotNet package dependency**
- [x] **Step 4: Create config models & parser implementation**
- [x] **Step 5: Run tests to verify they pass**
- [x] **Step 6: Commit changes to Git**

### Task 2: Dynamic TDL XML Query Generator
- [x] **Step 1: Write a failing test for XML TDL generation**
- [x] **Step 2: Run test to verify it fails**
- [x] **Step 3: Implement DynamicTdlXmlGenerator**
- [x] **Step 4: Run tests to verify they pass**
- [x] **Step 5: Commit changes to Git**

### Task 3: Dynamic XML Response Parser
- [x] **Step 1: Write a failing test for response parsing**
- [x] **Step 2: Run test to verify it fails**
- [x] **Step 3: Implement DynamicXmlParser**
- [x] **Step 4: Run tests to verify they pass**
- [x] **Step 5: Commit changes to Git**

### Task 4: Dynamic Table Schema Generator
- [x] **Step 1: Write a failing test for SQL schema builder**
- [x] **Step 2: Run test to verify it fails**
- [x] **Step 3: Implement DynamicTableSchemaGenerator**
- [x] **Step 4: Run tests to verify they pass**
- [x] **Step 5: Commit changes to Git**

### Task 5: Core Database Loader Enhancements
- [x] **Step 1: Write a failing unit test for database loader type mappings**
- [x] **Step 2: Run test to verify it fails**
- [x] **Step 3: Refactor MSSqlLoader to map columns explicitly**
- [x] **Step 4: Refactor PostgreSqlLoader to write null values correctly**
- [x] **Step 5: Refactor MySqlLoader to ensure AllowLoadLocalInfile is set**
- [x] **Step 6: Run tests to verify they pass**
- [x] **Step 7: Commit changes to Git**

### Task 6: Dynamic YAML-Driven Sync Engine Orchestration
- [x] **Step 1: Update DatabaseWriter with MySQL connection builder and dynamic initialization**
- [x] **Step 2: Refactor BackgroundSyncWorker to read config dynamically and run bulk imports**
- [x] **Step 3: Write integration unit tests with mock HttpClient to verify sync orchestration**
- [x] **Step 4: Run all solution tests to verify complete compilation and passing status**
- [x] **Step 5: Commit changes to Git**

---

## Final Verification Results
All unit and integration tests successfully compiled and executed using `dotnet test`:
- **Total Tests:** 23
- **Passed:** 23
- **Failed:** 0
- **Skipped:** 0
- **Duration:** 4s
