namespace TallyDbLoader.Core.Models
{
    public class DatabaseProfile
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Technology { get; set; } = string.Empty;
        public string Server { get; set; } = string.Empty;
        public int Port { get; set; }
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }
    public class TallySettings
    {
        public int Id { get; set; } = 1;
        public string Server { get; set; } = "localhost";
        public int Port { get; set; } = 9000;
        public string? TallyExePath { get; set; }
        public string? TallyIniPath { get; set; }
        public int AutoStartTally { get; set; }
    }

    public class SyncJob
    {
        public int Id { get; set; }
        public string CompanyName { get; set; } = string.Empty;
        public int DbProfileId { get; set; }
        public string TargetCatalog { get; set; } = string.Empty;
        public int? SyncIntervalMinutes { get; set; }
        public string? DailyTimeLocal { get; set; }
        public string? LastRunTime { get; set; }
        public string Status { get; set; } = "Idle";
        public string SyncMode { get; set; } = "full";
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
}
