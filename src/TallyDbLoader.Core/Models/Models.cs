namespace TallyDbLoader.Core.Models
{
    public class DatabaseProfile
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Technology { get; set; }
        public string Server { get; set; }
        public int Port { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
    }
    
    public class TallySettings
    {
        public string Server { get; set; } = "localhost";
        public int Port { get; set; } = 9000;
        public string TallyExePath { get; set; }
        public string TallyIniPath { get; set; }
    }
}
