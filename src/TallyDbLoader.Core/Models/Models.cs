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
        public string Server { get; set; } = "localhost";
        public int Port { get; set; } = 9000;
        public string TallyExePath { get; set; } = string.Empty;
        public string TallyIniPath { get; set; } = string.Empty;
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
    }
}
