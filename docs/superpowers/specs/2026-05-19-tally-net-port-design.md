# Technical Design Specification: .NET Port of Tally-to-Database Loader

This design document outlines the architecture, database structure, threading model, and UI specifications for porting the Tally-to-Database Loader utility from Node.js to a native Windows .NET application.

## 1. Background & Goals

The current Node.js command-line and simple HTML-based GUI utility is designed to sync master and transaction data from Tally Prime (via its HTTP XML server) to various target databases.

### Porting Goals:
- **Stability and Performance:** Re-write in C#/.NET for robust multithreaded operation, memory efficiency, and native Windows integration.
- **Native Windows UI:** Built using **WPF (Windows Presentation Foundation)** with modern styling.
- **Background Automation:** Runs in the user's interactive Windows session, minimizing to the **System Tray** and starting automatically upon login.
- **Multi-target Sync (1-to-Many):** Support mapping a single Tally company to multiple databases (potentially of different technologies or schemas on the same server) via configurable **Sync Jobs**.
- **Flexible Scheduling:** Automate jobs based on interval timers (e.g. every 15 minutes) or specific daily times (e.g., 2:00 AM local time).
- **Tally Prime Automation:** Automatically launch `tally.exe` when a job runs if it isn't already running, and configure `tally.ini` to auto-open target company folders.

---

## 2. System Architecture

```
[User Desktop Session]
┌────────────────────────────────────────────────────────┐
│  WPF Desktop App (System Tray Icon / Dashboard UI)     │
│  ┌───────────────────────┐   ┌──────────────────────┐  │
│  │   Dashboard GUI       │ ◄─┼─►   Sync Job Engine  │  │
│  └───────────────────────┘   └──────────┬───────────┘  │
│                                         │              │
│                                         ▼              │
│                              ┌─────────────────────┐   │
│                              │ Local Configuration │   │
│                              │   (SQLite DB)       │   │
│                              └─────────────────────┘   │
│                                                        │
│  ┌────────────────────┐      ┌────────────────────┐    │
│  │ Tally Prime (GUI)  │ ◄────┤ Tally XML Client   │    │
│  │ (Port 9000 XML)    │      └────────────────────┘    │
└──┴─────────▲──────────┴────────────────────────────────┘
             │
     Updates │ tally.ini
             │
┌────────────┴──────────┐      ┌────────────────────┐
│ Background App Engine ├─────►│ Target Databases   │
└───────────────────────┘      │ (MSSQL, MySQL, GP) │
                               └────────────────────┘
```

The application is structured as a native WPF application that runs in two modes:
1. **System Tray Worker (Default):** Runs silently in the background of the user session, managing the scheduler and initiating syncs.
2. **Dashboard UI (Active):** A window triggered by double-clicking the tray icon or selecting "Open Dashboard" from the context menu.

---

## 3. Local Storage Schema (SQLite)

We will use SQLite (`Microsoft.Data.Sqlite`) stored in `%AppData%\TallyToDbLoader\sync_config.db` to maintain application settings, logs, and sync states.

### Database Schema:

```sql
-- 1. Database Connection Profiles
CREATE TABLE database_profiles (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL UNIQUE,
    technology TEXT NOT NULL,         -- 'mssql', 'mysql', 'postgres', 'bigquery', 'adls', 'csv', 'json'
    server TEXT NOT NULL DEFAULT 'localhost',
    port INTEGER NOT NULL,
    username TEXT,
    password TEXT,                    -- Encrypted locally using DPAPI (Data Protection API)
    ssl INTEGER NOT NULL DEFAULT 0,    -- 0 = False, 1 = True
    load_method TEXT NOT NULL DEFAULT 'insert' -- 'insert' or 'file'
);

-- 2. Tally Connection Settings
CREATE TABLE tally_settings (
    id INTEGER PRIMARY KEY CHECK (id = 1), -- Single-row settings table
    server TEXT NOT NULL DEFAULT 'localhost',
    port INTEGER NOT NULL DEFAULT 9000,
    tally_exe_path TEXT,                   -- Path to tally.exe for auto-launching
    tally_ini_path TEXT                    -- Path to tally.ini for auto-loading companies
);

-- 3. Sync Jobs
CREATE TABLE sync_jobs (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL UNIQUE,
    company_name TEXT NOT NULL,            -- Name of company in Tally
    company_folder_id TEXT,                -- 5-digit Tally folder (e.g. '10001') for tally.ini automation
    db_profile_id INTEGER NOT NULL,
    db_schema TEXT NOT NULL,               -- Catalog name / database name to write to
    sync_method TEXT NOT NULL DEFAULT 'incremental', -- 'incremental' or 'full'
    trigger_type TEXT NOT NULL DEFAULT 'interval',   -- 'interval', 'daily', or 'manual'
    interval_minutes INTEGER,              -- Used if trigger_type = 'interval'
    daily_time TEXT,                       -- Used if trigger_type = 'daily' (Format 'HH:mm')
    auto_load_company INTEGER NOT NULL DEFAULT 1, -- 0 = False, 1 = True
    is_active INTEGER NOT NULL DEFAULT 1,          -- 0 = Suspended, 1 = Active
    FOREIGN KEY (db_profile_id) REFERENCES database_profiles(id) ON DELETE RESTRICT
);

-- 4. Sync Logs
CREATE TABLE sync_logs (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    job_id INTEGER NOT NULL,
    timestamp TEXT NOT NULL,              -- UTC ISO8601 string
    status TEXT NOT NULL,                 -- 'success', 'warning', 'error'
    message TEXT NOT NULL,                -- High-level description or exception message
    details TEXT,                         -- Detailed table-by-table sync counts or stack traces
    FOREIGN KEY (job_id) REFERENCES sync_jobs(id) ON DELETE CASCADE
);

-- 5. Cache for Incremental Sync (AlterIDs)
CREATE TABLE sync_alterid_cache (
    job_id INTEGER NOT NULL,
    entity_type TEXT NOT NULL,            -- 'Master' or 'Transaction'
    last_alter_id INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (job_id, entity_type),
    FOREIGN KEY (job_id) REFERENCES sync_jobs(id) ON DELETE CASCADE
);
```

---

## 4. Sync Execution & Multithreading Model

### Multithreaded Orchestrator:
- The app utilizes a `System.Threading.Timer` or `System.Timers.Timer` checking scheduled jobs every minute.
- When a job's scheduled interval or specific daily time is hit, a task is spawned via `Task.Run()`.
- **Tally Concurrency Limit:** Since Tally Prime listens on a single port (e.g., 9000) and executes XML queries sequentially, the sync engine maintains a **Tally Port Lock** (via a `SemaphoreSlim(1, 1)`) per Tally instance to ensure only one job queries Tally at any given time.
- **Database Concurrency:** Database loading for different jobs can run concurrently since they target separate connections/servers.

### Tally Verification & Auto-Launch Logic:
When a Sync Job starts:
1. Check if Tally Prime is running in the current Windows user session (looking for process `tally`).
2. If closed:
   - Check if `tally_exe_path` is configured.
   - If `auto_load_company` is enabled for the job, modify the `tally.ini` file at `tally_ini_path` to ensure the company's folder ID is added to the `Load = ...` list.
   - Launch Tally Prime using `Process.Start`.
   - Wait up to 30 seconds for the Tally HTTP server to start responding on the configured port.
3. Check if the target company is currently open/loaded in Tally using a fast XML ping (`postTallyXML` with a basic query). If closed:
   - Alert/log error "Target company not loaded in Tally". (Because loading a company dynamically via external XML request is not natively supported, Tally must be started with the company specified in `tally.ini` or opened by the user).

---

## 5. UI Layout & View Models (WPF)

The UI will be designed using **WPF** with Modern styling (rounded corners, sleek dark/light mode integration, glassmorphism highlights).

### Pages:
1. **Sync Jobs Dashboard:**
   - Grid list of all configured jobs showing Status (Idle, Syncing, Failed, Paused).
   - "Sync Now" manual trigger buttons.
   - Form to add/edit jobs with inputs for Company details, target Database Connection profile, target Database/Catalog name, Sync Method, and Schedule.
2. **Database Profiles Page:**
   - Grid list of defined database profiles.
   - "New Profile" wizard supporting: Microsoft SQL Server, PostgreSQL, MySQL, BigQuery, ADLS, CSV, JSON.
   - "Test Connection" button that runs a quick ADO.NET query to verify connection details.
3. **Tally Connection Settings:**
   - Server Address (default: localhost) and Port (default: 9000).
   - Executable path to `tally.exe` (with file picker dialog).
   - Config path to `tally.ini` (with file picker dialog).
4. **Logs Viewer:**
   - DataGrid displaying recent sync logs.
   - Filter logs by Sync Job, status (Success/Error), or date range.

---

## 6. Reliability & Robustness Enhancements

### 6.1. Date-Based Chunking for Large Exports
- **Problem:** Exporting a large range of transactions (e.g., a full financial year Day Book) in a single XML request can exceed Tally's memory threshold or cause HTTP request timeouts.
- **Solution:** For Full Syncs or large initial historical loads, the sync engine will chunk the date range into smaller blocks (e.g., daily, weekly, or monthly date ranges using `SVFROMDATE` and `SVTODATE`). The engine will serialize, fetch, and load these chunks sequentially, purging temporary caches in between.

### 6.2. Advanced Deletion Reconciliation
- **Reconciliation Sweep:** In incremental mode, Tally does not record deleted vouchers as updated `AlterID` events. The C# engine will run a complete GUID reconciliation sweep (getting the list of active GUIDs from Tally and comparing with the DB target) periodically.
- **Edit Log Integration (TallyPrime Edit Log):** If the Edit Log is enabled, the C# app can send a custom TDL collection query of type `Edit Logs: Deleted Master` and `Edit Logs: Voucher` with filter `ObjectUpdateAction = "Delete"`. This allows retrieving only the deleted GUIDs since the last sync time, avoiding a full GUID reconciliation sweep.
- **Voucher Cancellation Guidance:** Users will be advised in the UI documentation to prefer **Cancelling (Alt+X)** over **Deleting (Alt+D)** in Tally Prime, as cancelled records retain their GUID and increment their AlterID, causing the sync to be propagated instantly.

### 6.3. Transient Database & Network Recovery
- **Connection Retry Policy:** Implement a simple transient fault handler (exponential backoff, 3 retries) for database connections and HTTP posts to Tally.
- **Explicit Timeouts:** Set default `CommandTimeout` to 60 seconds on all DB operations and XML requests to prevent tasks from hanging indefinitely.
- **Tally Port Lock:** A `SemaphoreSlim(1,1)` ensures that no two threads query the Tally XML API simultaneously, avoiding race conditions or HTTP port exhaustion.

---

## 7. Implementation Stages

1. **Stage 1: Core Library & Database Porting:**
   - Implement the SQLite config manager and repository classes in C#.
   - Port the XML generator/parser logic from TypeScript (`src/tally.ts`) to C#.
   - Port database bulk loaders (MSSQL using `SqlBulkCopy`, MySQL using `MySqlBulkLoader`, Postgres using `NpgsqlBinaryImporter`, etc.).
2. **Stage 2: Background Engine & Scheduling:**
   - Implement the scheduler and task executor with the semaphore queue.
   - Implement the Tally process checker and `tally.ini` reader/writer.
   - Integrate date-range chunking and transient fault retries.
3. **Stage 3: WPF User Interface & System Tray:**
   - Design the main window layout using XAML.
   - Implement WPF System Tray integration (`NotifyIcon` framework).
   - Hook up ViewModels to SQLite configurations.
4. **Stage 4: Verification and Testing:**
   - Verify connection testers, scheduler triggers, time-of-day fires, and Tally auto-loading.

