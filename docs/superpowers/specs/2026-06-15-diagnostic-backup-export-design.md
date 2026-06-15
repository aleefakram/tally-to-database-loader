# Diagnostic Backup Export Design

## Goal

Add a Core-owned diagnostic backup export that creates a support ZIP bundle without changing sync state or target databases.

This slice is export-only. It does not implement restore, import, UI screens, retention, upload, or support-ticket workflows.

## Scope

Included:

- Add a `DiagnosticBackupService` in `TallyDbLoader.Core`.
- Create a timestamped ZIP archive for support diagnostics.
- Include a live-safe copy of the local SQLite configuration database.
- Include application log files using read-sharing.
- Include a generated system information text file.
- Optionally include raw XML diagnostic payloads behind an explicit opt-in flag.
- Write one successful-backup audit row.
- Add fast local tests for ZIP contents, raw XML opt-in behavior, and audit payload safety.

Excluded:

- Backup restore.
- Sanitized configuration export/import.
- Diagnostic retention or purge.
- Uploading backups to support.
- WPF file pickers or UI workflow.
- Scheduler pause/resume behavior.
- Target database backups.
- Auditing failed diagnostic backup attempts.

## Core Service

Create a small Core service at:

```text
src/TallyDbLoader.Core/Data/DiagnosticBackupService.cs
namespace TallyDbLoader.Core.Data
```

The service belongs beside `ConfigExportService` and `ConfigImportService` because it projects local configuration and diagnostic files into an export artifact.

Proposed public shape:

```csharp
public sealed class DiagnosticBackupService
{
    public DiagnosticBackupService(IConfigRepository repository);

    public DiagnosticBackupResult CreateBackup(DiagnosticBackupRequest request);
}
```

The request object carries all environment-specific paths and user intent:

```csharp
public sealed class DiagnosticBackupRequest
{
    public string ConfigDatabasePath { get; init; } = "";
    public string LogDirectoryPath { get; init; } = "";
    public string? RawXmlDirectoryPath { get; init; }
    public string OutputDirectoryPath { get; init; } = "";
    public string ApplicationVersion { get; init; } = "";
    public string Actor { get; init; } = "";
    public string Reason { get; init; } = "";
    public bool IncludeRawXml { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
```

Result:

```csharp
public sealed class DiagnosticBackupResult
{
    public string FilePath { get; init; } = "";
    public string FileName { get; init; } = "";
    public long FileSizeBytes { get; init; }
    public int LogFileCount { get; init; }
    public int RawXmlFileCount { get; init; }
    public long AuditId { get; init; }
}
```

Constructor validation:

- Throw `ArgumentNullException` when `repository` is null.

Request validation:

- Throw `ArgumentNullException` when `request` is null.
- Throw `ArgumentException` when `ConfigDatabasePath`, `OutputDirectoryPath`, `ApplicationVersion`, `Actor`, or `Reason` is null, empty, or whitespace.
- Throw `FileNotFoundException` when `ConfigDatabasePath` does not exist.
- Throw `DirectoryNotFoundException` when `OutputDirectoryPath` does not exist.
- Treat a missing log directory as zero log files.
- If `IncludeRawXml` is true, require a non-empty existing `RawXmlDirectoryPath`; otherwise throw `DirectoryNotFoundException`.

## ZIP Layout

The ZIP file name is deterministic from the caller-supplied timestamp:

```text
tally_diagnostic_yyyyMMdd_HHmmss.zip
```

Use the timestamp in local offset form from `CreatedAt`, sanitized for file naming.

ZIP entries:

```text
config/config.db
logs/<relative log file paths>
system/system_info.txt
raw_xml/<relative raw XML file paths>   # only when IncludeRawXml = true
manifest.json
```

Rules:

- Use forward-slash ZIP entry names.
- Do not include absolute local paths in ZIP entry names.
- Preserve relative paths under `logs/` and `raw_xml/` only where needed to avoid name collisions.
- Skip directories and hidden traversal entries.
- Do not include the generated ZIP file itself if the output directory is inside a source directory.
- Keep file enumeration deterministic by sorting paths ordinally.

## SQLite Database Copy

The config database must be copied with a live-safe SQLite mechanism, not by blindly copying the active file.

Use `Microsoft.Data.Sqlite` online backup semantics:

```csharp
using var source = new SqliteConnection(sourceConnectionString);
using var destination = new SqliteConnection(destinationConnectionString);
source.Open();
destination.Open();
source.BackupDatabase(destination);
```

Write the copied database to a temporary working directory first, then add it to the ZIP as `config/config.db`.

Rules:

- The source database is read-only from the service's perspective.
- The scheduler does not need to pause.
- If the backup API fails, abort the backup and do not write an audit row.
- Temporary files must be cleaned up on success and failure.

## Log Copying

Include regular files from `LogDirectoryPath` when the directory exists.

Rules:

- Copy files using `FileShare.ReadWrite` so active logs can be read while the app is running.
- If a single log file cannot be read because it is deleted or locked mid-copy, skip that file and record the skip in `manifest.json`.
- Do not fail the whole backup for one log file read failure.
- Sort log files deterministically.

## Raw XML Diagnostics

Raw XML payloads are sensitive and excluded by default.

Rules:

- `IncludeRawXml = false`: do not inspect or include `RawXmlDirectoryPath`.
- `IncludeRawXml = true`: include regular files from `RawXmlDirectoryPath` under `raw_xml/`.
- Raw XML inclusion must be visible in both `manifest.json` and the audit row.
- Phase 1 does not implement size limits or retention cleanup. Those belong to the diagnostic retention slice.

## System Information

Generate `system/system_info.txt` during backup creation.

Minimum fields:

```text
created_at
application_version
os_version
machine_name
user_name
dotnet_version
process_name
processor_count
working_set_bytes
is_64_bit_process
```

Do not include environment variables, command-line arguments, database passwords, connection strings, or raw file paths.

## Manifest

Add `manifest.json` to the ZIP.

Shape:

```json
{
  "format": "tally-db-loader.diagnostic-backup",
  "schema_version": 1,
  "application_version": "2.0.0-beta",
  "created_at": "2026-06-15T10:15:30.0000000+05:30",
  "include_raw_xml": false,
  "entries": {
    "config_database": true,
    "system_info": true,
    "log_file_count": 2,
    "raw_xml_file_count": 0,
    "skipped_file_count": 0
  },
  "skipped_files": []
}
```

Rules:

- The manifest must not contain absolute source paths.
- `skipped_files` may contain relative entry paths and skip reasons, not full local paths.
- The manifest records what was packaged, not the contents of logs or XML.

## Auditing

Add a narrow repository method:

```csharp
long RecordDiagnosticBackupExport(
    string actor,
    string reason,
    string fileName,
    long fileSizeBytes,
    bool includeRawXml,
    int logFileCount,
    int rawXmlFileCount,
    int skippedFileCount,
    DateTime createdAt);
```

The repository writes one audit row after the ZIP has been fully created and measured.

Audit row:

```text
action = "export_diagnostic_backup"
entity_type = "diagnostic_backup"
entity_id = 0
entity_name = fileName
```

`before_json`:

```json
{}
```

`after_json`:

```json
{
  "file_name": "tally_diagnostic_20260615_101530.zip",
  "file_size_bytes": 123456,
  "include_raw_xml": false,
  "log_file_count": 2,
  "raw_xml_file_count": 0,
  "skipped_file_count": 0
}
```

Rules:

- Audit only successful backup creation in Phase 1.
- If ZIP creation fails, propagate the exception and write no audit row.
- If audit insertion fails after ZIP creation, throw and leave the ZIP on disk. The artifact exists, but the caller must treat the operation as failed because it was not audited.
- Audit JSON must not include file contents, absolute source paths, passwords, DPAPI ciphertext, or raw XML snippets.

The repository can reuse the existing private `InsertConfigAuditLog` helper. Do not add a public audit service abstraction in this slice.

## Failure Handling

Backup creation fails closed for core artifacts:

- Missing config database: fail.
- SQLite backup failure: fail.
- ZIP write failure: fail.
- Audit write failure: fail after ZIP creation, with the ZIP left in place.

Non-core optional artifacts:

- Missing log directory: continue with `log_file_count = 0`.
- Individual log/raw XML file read failure: skip the file and record it in `manifest.json`.
- Missing raw XML directory when `IncludeRawXml = false`: ignore.
- Missing raw XML directory when `IncludeRawXml = true`: fail.

## Testing

Add fast local tests for:

- Constructor rejects null repository.
- Request validation rejects missing config DB path, output directory, actor, reason, and application version.
- A successful backup creates a ZIP with `config/config.db`, `manifest.json`, and `system/system_info.txt`.
- SQLite backup can be opened and contains expected local schema tables.
- Existing log files are included under `logs/`.
- Missing log directory still succeeds with zero log files.
- Raw XML files are excluded by default.
- Raw XML files are included only when `IncludeRawXml = true`.
- Manifest contains counts and no absolute local paths.
- Audit row is written once on success with `action = "export_diagnostic_backup"`.
- Audit payload contains counts and file metadata only.
- Audit payload and manifest do not contain passwords, `dpapi:`, raw XML content, or absolute source paths.
- ZIP creation failure writes no audit row.

Use temporary directories and temporary SQLite databases. Default `dotnet test` must remain fast and local.

## Success Criteria

- Diagnostic backup service lives in Core.
- No WPF files are changed.
- No restore/import behavior is added.
- Raw XML is excluded unless explicitly requested.
- A successful backup writes exactly one audit row.
- Failed backup creation writes no audit row.
- No credentials, DPAPI ciphertext, absolute source paths, or raw XML contents appear in audit JSON or manifest.
- `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore` passes.
