using System;
using System.IO;
using System.Collections.Generic;
using System.IO.Compression;
using System.Text.Json;
using Dapper;
using TallyDbLoader.Core.Models;

namespace TallyDbLoader.Core.Data
{
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

    public sealed class DiagnosticBackupResult
    {
        public string FilePath { get; init; } = "";
        public string FileName { get; init; } = "";
        public long FileSizeBytes { get; init; }
        public int LogFileCount { get; init; }
        public int RawXmlFileCount { get; init; }
        public long AuditId { get; init; }
    }

    public sealed class DiagnosticBackupService
    {
        private readonly IConfigRepository _repository;

        public DiagnosticBackupService(IConfigRepository repository)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        }

        public DiagnosticBackupResult CreateBackup(DiagnosticBackupRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (string.IsNullOrWhiteSpace(request.ConfigDatabasePath))
                throw new ArgumentException("ConfigDatabasePath is required.", nameof(request));
            if (string.IsNullOrWhiteSpace(request.OutputDirectoryPath))
                throw new ArgumentException("OutputDirectoryPath is required.", nameof(request));
            if (string.IsNullOrWhiteSpace(request.ApplicationVersion))
                throw new ArgumentException("ApplicationVersion is required.", nameof(request));
            if (string.IsNullOrWhiteSpace(request.Actor))
                throw new ArgumentException("Actor is required.", nameof(request));
            if (string.IsNullOrWhiteSpace(request.Reason))
                throw new ArgumentException("Reason is required.", nameof(request));

            if (!File.Exists(request.ConfigDatabasePath))
                throw new FileNotFoundException("Config database file not found.", request.ConfigDatabasePath);
            if (!Directory.Exists(request.OutputDirectoryPath))
                throw new DirectoryNotFoundException($"Output directory not found: {request.OutputDirectoryPath}");

            if (request.IncludeRawXml)
            {
                if (string.IsNullOrWhiteSpace(request.RawXmlDirectoryPath))
                    throw new ArgumentException("RawXmlDirectoryPath is required when IncludeRawXml is true.");
                if (!Directory.Exists(request.RawXmlDirectoryPath))
                    throw new DirectoryNotFoundException($"Raw XML directory not found: {request.RawXmlDirectoryPath}");
            }

            string fileName = $"tally_diagnostic_{request.CreatedAt.ToString("yyyyMMdd_HHmmss")}.zip";
            string zipFilePath = Path.Combine(request.OutputDirectoryPath, fileName);
            string stagingDir = Path.Combine(Path.GetTempPath(), $"tally_diag_stage_{Guid.NewGuid()}");
            Directory.CreateDirectory(stagingDir);

            try
            {
                // config db backup & sanitization
                string configStageDir = Path.Combine(stagingDir, "config");
                string configDestDb = Path.Combine(configStageDir, "config.db");
                PerformSQLiteBackup(request.ConfigDatabasePath, configDestDb);
                SanitizeConfigDatabase(configDestDb);

                // system info
                string systemStageDir = Path.Combine(stagingDir, "system");
                Directory.CreateDirectory(systemStageDir);
                string systemInfo = GenerateSystemInfoText(request);
                File.WriteAllText(Path.Combine(systemStageDir, "system_info.txt"), systemInfo);

                int logFileCount = 0;
                int rawXmlFileCount = 0;
                var skippedFiles = new List<string>();

                // copy logs
                if (!string.IsNullOrWhiteSpace(request.LogDirectoryPath) && Directory.Exists(request.LogDirectoryPath))
                {
                    CopyDirectoryRecursively(request.LogDirectoryPath, Path.Combine(stagingDir, "logs"), "logs", ref logFileCount, skippedFiles);
                }

                // copy raw xml
                if (request.IncludeRawXml && !string.IsNullOrWhiteSpace(request.RawXmlDirectoryPath) && Directory.Exists(request.RawXmlDirectoryPath))
                {
                    CopyDirectoryRecursively(request.RawXmlDirectoryPath, Path.Combine(stagingDir, "raw_xml"), "raw_xml", ref rawXmlFileCount, skippedFiles);
                }

                // manifest.json
                var manifestData = new
                {
                    format = "tally-db-loader.diagnostic-backup",
                    schema_version = 1,
                    application_version = request.ApplicationVersion,
                    created_at = request.CreatedAt.ToString("o"),
                    include_raw_xml = request.IncludeRawXml,
                    entries = new
                    {
                        config_database = true,
                        system_info = true,
                        log_file_count = logFileCount,
                        raw_xml_file_count = rawXmlFileCount,
                        skipped_file_count = skippedFiles.Count
                    },
                    skipped_files = skippedFiles
                };
                string manifestJson = JsonSerializer.Serialize(manifestData, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(Path.Combine(stagingDir, "manifest.json"), manifestJson);

                // zip packaging
                if (File.Exists(zipFilePath))
                {
                    File.Delete(zipFilePath);
                }
                ZipFile.CreateFromDirectory(stagingDir, zipFilePath);

                // get file size
                long fileSizeBytes = new FileInfo(zipFilePath).Length;

                // record in database
                long auditId = _repository.RecordDiagnosticBackupExport(
                    actor: request.Actor,
                    reason: request.Reason,
                    fileName: fileName,
                    fileSizeBytes: fileSizeBytes,
                    includeRawXml: request.IncludeRawXml,
                    logFileCount: logFileCount,
                    rawXmlFileCount: rawXmlFileCount,
                    skippedFileCount: skippedFiles.Count,
                    createdAt: request.CreatedAt.UtcDateTime
                );

                return new DiagnosticBackupResult
                {
                    FilePath = zipFilePath,
                    FileName = fileName,
                    FileSizeBytes = fileSizeBytes,
                    LogFileCount = logFileCount,
                    RawXmlFileCount = rawXmlFileCount,
                    AuditId = auditId
                };
            }
            finally
            {
                try
                {
                    if (Directory.Exists(stagingDir))
                    {
                        Directory.Delete(stagingDir, true);
                    }
                }
                catch { }
            }
        }

        private void SanitizeConfigDatabase(string dbPath)
        {
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Pooling=False;"))
            {
                conn.Open();
                conn.Execute("UPDATE database_profiles SET password = '***masked***' WHERE password IS NOT NULL;");
            }
        }

        private void CopyDirectoryRecursively(
            string sourceDir,
            string targetDir,
            string logCategoryPrefix,
            ref int copiedCount,
            List<string> skippedFiles)
        {
            if (!Directory.Exists(sourceDir)) return;

            foreach (var filePath in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                string relativeName = Path.GetRelativePath(sourceDir, filePath).Replace('\\', '/');
                string destPath = Path.Combine(targetDir, relativeName);

                try
                {
                    CopyFileWithReadSharing(filePath, destPath);
                    copiedCount++;
                }
                catch (Exception ex)
                {
                    skippedFiles.Add($"{logCategoryPrefix}/{relativeName}: {ex.GetType().Name}");
                }
            }
        }

        internal void PerformSQLiteBackup(string sourceDbPath, string destinationDbPath)
        {
            var destDir = Path.GetDirectoryName(destinationDbPath);
            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            using (var source = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={sourceDbPath};Pooling=False;"))
            using (var destination = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={destinationDbPath};Pooling=False;"))
            {
                source.Open();
                destination.Open();
                source.BackupDatabase(destination);
            }
        }

        internal string GenerateSystemInfoText(DiagnosticBackupRequest request)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"created_at={request.CreatedAt:o}");
            sb.AppendLine($"application_version={request.ApplicationVersion}");
            sb.AppendLine($"os_version={GetSafeEnvironment(() => Environment.OSVersion.ToString())}");
            sb.AppendLine($"machine_name={GetSafeEnvironment(() => Environment.MachineName)}");
            sb.AppendLine($"user_name={GetSafeEnvironment(() => Environment.UserName)}");
            sb.AppendLine($"dotnet_version={GetSafeEnvironment(() => System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription)}");
            sb.AppendLine($"process_name={GetSafeEnvironment(() => System.Diagnostics.Process.GetCurrentProcess().ProcessName)}");
            sb.AppendLine($"processor_count={GetSafeEnvironment(() => Environment.ProcessorCount.ToString(), "0")}");
            sb.AppendLine($"working_set_bytes={GetSafeEnvironment(() => System.Diagnostics.Process.GetCurrentProcess().WorkingSet64.ToString(), "0")}");
            sb.AppendLine($"is_64_bit_process={GetSafeEnvironment(() => Environment.Is64BitProcess.ToString().ToLowerInvariant())}");
            return sb.ToString();
        }

        private string GetSafeEnvironment(Func<string> propertySelector, string fallback = "Unknown")
        {
            try
            {
                return propertySelector() ?? fallback;
            }
            catch
            {
                return fallback;
            }
        }

        internal void CopyFileWithReadSharing(string sourcePath, string destinationPath)
        {
            var destDir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            using (var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var destStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                sourceStream.CopyTo(destStream);
            }
        }
    }
}
