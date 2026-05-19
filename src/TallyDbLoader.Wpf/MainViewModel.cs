using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using TallyDbLoader.Core.Models;
using TallyDbLoader.Core.Data;

namespace TallyDbLoader.Wpf
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly ConfigRepository _repo;
        private string _statusText = "Sync engine is idle.";

        public ObservableCollection<DatabaseProfile> DatabaseProfiles { get; set; }
        public ObservableCollection<SyncJob> SyncJobs { get; set; }

        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); }
        }

        public MainViewModel(string dbPath)
        {
            DatabaseHelper.InitializeDatabase(dbPath);
            _repo = new ConfigRepository(dbPath);
            
            DatabaseProfiles = new ObservableCollection<DatabaseProfile>();
            SyncJobs = new ObservableCollection<SyncJob>();
            
            LoadConfiguration();
        }

        public void LoadConfiguration()
        {
            DatabaseProfiles.Clear();
            SyncJobs.Clear();
            
            // Seed default values for demonstration if database is empty
            var postgresProfile = _repo.GetDatabaseProfileByName("PostgreSqlLocal");
            if (postgresProfile == null)
            {
                postgresProfile = new DatabaseProfile
                {
                    Name = "PostgreSqlLocal",
                    Technology = "postgres",
                    Server = "localhost",
                    Port = 5432,
                    Username = "postgres",
                    Password = "password"
                };
                _repo.SaveDatabaseProfile(postgresProfile);
                
                // Fetch again to retrieve the auto-generated primary key (Id)
                postgresProfile = _repo.GetDatabaseProfileByName("PostgreSqlLocal");
            }
            
            if (postgresProfile != null)
            {
                DatabaseProfiles.Add(postgresProfile);
                
                var jobs = _repo.GetAllSyncJobs();
                if (jobs.Count == 0)
                {
                    var defaultJob = new SyncJob
                    {
                        CompanyName = "Demo Kitchen Central",
                        DbProfileId = postgresProfile.Id,
                        TargetCatalog = "kitchen_central",
                        SyncIntervalMinutes = 15,
                        Status = "Idle"
                    };
                    _repo.SaveSyncJob(defaultJob);
                    jobs = _repo.GetAllSyncJobs();
                }
                
                foreach (var job in jobs)
                {
                    SyncJobs.Add(job);
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
