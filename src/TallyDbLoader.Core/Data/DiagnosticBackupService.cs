using System;
using System.IO;
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

            return new DiagnosticBackupResult();
        }

        internal void PerformSQLiteBackup(string sourceDbPath, string destinationDbPath)
        {
            var destDir = Path.GetDirectoryName(destinationDbPath);
            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            using (var source = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={sourceDbPath}"))
            using (var destination = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={destinationDbPath}"))
            {
                source.Open();
                destination.Open();
                source.BackupDatabase(destination);
            }
        }
    }
}
