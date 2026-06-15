using System;
using System.Collections.Generic;

namespace TallyDbLoader.Core.Models
{
    [Flags]
    public enum EntityFlags
    {
        None = 0,
        Vouchers = 1 << 0,      // 1
        Ledgers = 1 << 1,       // 2
        StockItems = 1 << 2,    // 4
        Groups = 1 << 3,        // 8
        CostCentres = 1 << 4,   // 16
        Currencies = 1 << 5,    // 32
        All = Vouchers | Ledgers | StockItems | Groups | CostCentres | Currencies
    }

    public class DatabaseProfile
    {
        public int Id { get; set; }
        public int DbId { get => Id; set => Id = value; } // For Dapper split mapping alias
        public string Name { get; set; } = string.Empty;
        public string Technology { get; set; } = "postgres"; // "postgres" | "mssql"
        public string Server { get; set; } = "localhost";
        public int Port { get; set; } = 5432;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty; // DPAPI encrypted format "dpapi:..." in database
        public string LastTestResult { get; set; } = "Untested";
        public DateTime? LastTestedAt { get; set; }
        public int UsedByCount { get; set; }
    }

    public class TallySettings
    {
        public int Id { get; set; } = 1;
        public string Server { get; set; } = "localhost";
        public int Port { get; set; } = 9000;
        public string? TallyExePath { get; set; }
        public string? TallyIniPath { get; set; }
        public bool AutoStartTally { get; set; }
    }

    public class CompanyProfile
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? TallyGuid { get; set; }
        public bool Consolidated { get; set; } = false;
        public DateTime? BooksFrom { get; set; }
        public DateTime? BooksTo { get; set; }

        public int DbProfileId { get; set; }
        public DatabaseProfile? Db { get; set; } // Populated on load

        public string TargetCatalog { get; set; } = string.Empty;
        public string Schema { get; set; } = "public";
        public string TablePrefix { get; set; } = "tally_";

        public string Mode { get; set; } = "full"; // "full" | "incremental"
        public int IntervalMinutes { get; set; } = 15;
        public bool Enabled { get; set; } = true;
        public bool NotifyOnError { get; set; } = true;
        public bool PauseOnTallyClose { get; set; } = false;

        // Default: 15 (Vouchers=1 | Ledgers=2 | StockItems=4 | Groups=8)
        public int EntityFlags { get; set; } = 15; 

        public string Status { get; set; } = "idle"; // "ok" | "warn" | "err" | "idle"
        public DateTime? LastRunAt { get; set; }
        public int? LastDurationMs { get; set; }
        public long? LastRowsWritten { get; set; }
        public int ErrorCount24h { get; set; }
    }

    public class SyncRun
    {
        public long Id { get; set; }
        public int CompanyId { get; set; }
        public string CompanyName { get; set; } = string.Empty; // Populated via join query
        public DateTime StartedAt { get; set; }
        public DateTime EndedAt { get; set; }
        public TimeSpan Duration => EndedAt - StartedAt;
        public string Mode { get; set; } = "full"; // "full" | "incremental"
        public string Status { get; set; } = "ok"; // "ok" | "warn" | "err"
        public int Retries { get; set; } = 0;
        public long RowsIn { get; set; } = 0;
        public long RowsWritten { get; set; } = 0;
        public string ByEntityJson { get; set; } = "{}"; // JSON stats mapping: {"Vouchers": 12, "Ledgers": 4}
        public string? ResultSummary { get; set; }
        public string? LogExcerpt { get; set; }
    }

    public class Ledger
    {
        public string Guid { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Parent { get; set; } = string.Empty;
        public decimal OpeningBalance { get; set; }
        public decimal ClosingBalance { get; set; }
    }

    public class Voucher
    {
        public string Guid { get; set; } = string.Empty;
        public string Date { get; set; } = string.Empty;
        public string VoucherNumber { get; set; } = string.Empty;
        public string VoucherType { get; set; } = string.Empty;
        public decimal Amount { get; set; }
    }

    // NOTE: TallyCompanyInfo already exists at Core.Tally.TallyCompanyInfo.
    // Extend the existing class (in src/TallyDbLoader.Core/Tally/TallyCompanyInfo.cs) with these fields:
    //   public string? Guid { get; set; }
    //   public DateTime? BooksFrom { get; set; }
    //   public DateTime? BooksTo { get; set; }
    // Keep existing IsGroup as bool, and map to CompanyProfile.Consolidated (as int) only when saving.

    public enum ImportAction
    {
        Create,
        Overwrite
    }

    public class ResolvedDatabaseProfileImport
    {
        public int SourceId { get; set; }
        public int? ExistingLocalId { get; set; }
        public ImportAction Action { get; set; }
        public DatabaseProfile Profile { get; set; } = null!;
        public string? Password { get; set; }
        public bool PreserveExistingPassword { get; set; }
    }

    public class ResolvedCompanyProfileImport
    {
        public int SourceId { get; set; }
        public int? ExistingLocalId { get; set; }
        public int SourceDbProfileId { get; set; }
        public ImportAction Action { get; set; }
        public CompanyProfile Profile { get; set; } = null!;
    }
}

