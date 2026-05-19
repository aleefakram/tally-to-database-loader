using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using TallyDbLoader.Core.Models;
using TallyDbLoader.Core.Data;
using TallyDbLoader.Core.Sync;

namespace TallyDbLoader.Wpf
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly ConfigRepository _repo;
        private string _statusText = "Sync engine is idle.";
        private string _logOutput = "Ready to start sync loop.";
        private BackgroundSyncWorker? _worker;

        public ObservableCollection<DatabaseProfile> DatabaseProfiles { get; set; }
        public ObservableCollection<SyncJob> SyncJobs { get; set; }

        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); }
        }

        public string LogOutput
        {
            get => _logOutput;
            set { _logOutput = value; OnPropertyChanged(); }
        }

        // Tally Settings Form
        private string _tallyServer = "localhost";
        private int _tallyPort = 9000;
        private string _tallyExePath = string.Empty;
        private string _tallyIniPath = string.Empty;
        private bool _autoStartTally = false;

        public string TallyServer
        {
            get => _tallyServer;
            set { _tallyServer = value; OnPropertyChanged(); }
        }

        public int TallyPort
        {
            get => _tallyPort;
            set { _tallyPort = value; OnPropertyChanged(); }
        }

        public string TallyExePath
        {
            get => _tallyExePath;
            set { _tallyExePath = value; OnPropertyChanged(); }
        }

        public string TallyIniPath
        {
            get => _tallyIniPath;
            set { _tallyIniPath = value; OnPropertyChanged(); }
        }

        public bool AutoStartTally
        {
            get => _autoStartTally;
            set { _autoStartTally = value; OnPropertyChanged(); }
        }

        // Database Profile CRUD Form
        private string _dbName = string.Empty;
        private string _dbTech = "postgres"; // default
        private string _dbServer = "localhost";
        private int _dbPort = 5432;
        private string _dbUsername = string.Empty;
        private string _dbPassword = string.Empty;

        public string DbName
        {
            get => _dbName;
            set { _dbName = value; OnPropertyChanged(); }
        }

        public string DbTech
        {
            get => _dbTech;
            set { _dbTech = value; OnPropertyChanged(); }
        }

        public string DbServer
        {
            get => _dbServer;
            set { _dbServer = value; OnPropertyChanged(); }
        }

        public int DbPort
        {
            get => _dbPort;
            set { _dbPort = value; OnPropertyChanged(); }
        }

        public string DbUsername
        {
            get => _dbUsername;
            set { _dbUsername = value; OnPropertyChanged(); }
        }

        public string DbPassword
        {
            get => _dbPassword;
            set { _dbPassword = value; OnPropertyChanged(); }
        }

        // Sync Job CRUD Form
        private string _jobCompany = string.Empty;
        private DatabaseProfile? _jobSelectedProfile;
        private string _jobTargetCatalog = string.Empty;
        private int _jobInterval = 15;

        public string JobCompany
        {
            get => _jobCompany;
            set { _jobCompany = value; OnPropertyChanged(); }
        }

        public DatabaseProfile? JobSelectedProfile
        {
            get => _jobSelectedProfile;
            set { _jobSelectedProfile = value; OnPropertyChanged(); }
        }

        public string JobTargetCatalog
        {
            get => _jobTargetCatalog;
            set { _jobTargetCatalog = value; OnPropertyChanged(); }
        }

        public int JobInterval
        {
            get => _jobInterval;
            set { _jobInterval = value; OnPropertyChanged(); }
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

            // Load Tally Settings
            var settings = _repo.GetTallySettings();
            TallyServer = settings.Server;
            TallyPort = settings.Port;
            TallyExePath = settings.TallyExePath ?? string.Empty;
            TallyIniPath = settings.TallyIniPath ?? string.Empty;
            AutoStartTally = settings.AutoStartTally == 1;
            
            // Seed defaults if empty
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
            }
            
            // Load DB Profiles
            var profiles = _repo.GetAllDatabaseProfiles();
            foreach (var profile in profiles)
            {
                DatabaseProfiles.Add(profile);
            }

            // Load Sync Jobs
            var jobs = _repo.GetAllSyncJobs();
            if (jobs.Count == 0 && DatabaseProfiles.Count > 0)
            {
                var defaultJob = new SyncJob
                {
                    CompanyName = "Demo Kitchen Central",
                    DbProfileId = DatabaseProfiles[0].Id,
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

        // We will call repo methods to save
        public void SaveTallySettings()
        {
            var settings = new TallySettings
            {
                Server = TallyServer,
                Port = TallyPort,
                TallyExePath = TallyExePath,
                TallyIniPath = TallyIniPath,
                AutoStartTally = AutoStartTally ? 1 : 0
            };
            _repo.SaveTallySettings(settings);
            LogOutput = $"[{DateTime.Now:HH:mm:ss}] Saved Tally configuration.\n" + LogOutput;
        }

        public void SaveDatabaseProfile()
        {
            if (string.IsNullOrWhiteSpace(DbName)) return;

            var profile = new DatabaseProfile
            {
                Name = DbName,
                Technology = DbTech,
                Server = DbServer,
                Port = DbPort,
                Username = DbUsername,
                Password = DbPassword
            };
            _repo.SaveDatabaseProfile(profile);
            LoadConfiguration();
            LogOutput = $"[{DateTime.Now:HH:mm:ss}] Saved Database Profile '{DbName}'.\n" + LogOutput;
        }

        public void DeleteDatabaseProfile(DatabaseProfile profile)
        {
            if (profile == null) return;
            _repo.DeleteDatabaseProfile(profile.Id);
            LoadConfiguration();
            LogOutput = $"[{DateTime.Now:HH:mm:ss}] Deleted Database Profile '{profile.Name}'.\n" + LogOutput;
        }

        public void AddSyncJob()
        {
            if (string.IsNullOrWhiteSpace(JobCompany) || JobSelectedProfile == null) return;

            var job = new SyncJob
            {
                CompanyName = JobCompany,
                DbProfileId = JobSelectedProfile.Id,
                TargetCatalog = JobTargetCatalog,
                SyncIntervalMinutes = JobInterval,
                Status = "Idle"
            };
            _repo.SaveSyncJob(job);
            LoadConfiguration();
            LogOutput = $"[{DateTime.Now:HH:mm:ss}] Created Sync Job for '{JobCompany}'.\n" + LogOutput;
        }

        public void DeleteSyncJob(SyncJob job)
        {
            if (job == null) return;
            _repo.DeleteSyncJob(job.Id);
            LoadConfiguration();
            LogOutput = $"[{DateTime.Now:HH:mm:ss}] Deleted Sync Job for '{job.CompanyName}'.\n" + LogOutput;
        }

        private SyncJob? _selectedSyncJob;
        public SyncJob? SelectedSyncJob
        {
            get => _selectedSyncJob;
            set { _selectedSyncJob = value; OnPropertyChanged(); }
        }

        private DatabaseProfile? _selectedDatabaseProfile;
        public DatabaseProfile? SelectedDatabaseProfile
        {
            get => _selectedDatabaseProfile;
            set { _selectedDatabaseProfile = value; OnPropertyChanged(); }
        }

        public void TestDatabaseConnection()
        {
            if (string.IsNullOrWhiteSpace(DbName))
            {
                LogOutput = $"[{DateTime.Now:HH:mm:ss}] Connection test failed: Profile form is incomplete.\n" + LogOutput;
                return;
            }

            LogOutput = $"[{DateTime.Now:HH:mm:ss}] Testing database connection to '{DbServer}'...\n" + LogOutput;

            try
            {
                if (DbTech.Equals("postgres", StringComparison.OrdinalIgnoreCase))
                {
                    using (var conn = new Npgsql.NpgsqlConnection($"Host={DbServer};Port={DbPort};Username={DbUsername};Password={DbPassword};Database=postgres;Timeout=5"))
                    {
                        conn.Open();
                    }
                }
                else
                {
                    using (var conn = new Microsoft.Data.SqlClient.SqlConnection($"Server={DbServer},{DbPort};User Id={DbUsername};Password={DbPassword};TrustServerCertificate=True;Connection Timeout=5"))
                    {
                        conn.Open();
                    }
                }
                LogOutput = $"[{DateTime.Now:HH:mm:ss}] Database connection SUCCESSFUL!\n" + LogOutput;
                System.Windows.MessageBox.Show("Connection Successful!", "Database Test", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                LogOutput = $"[{DateTime.Now:HH:mm:ss}] Database connection FAILED: {ex.Message}\n" + LogOutput;
                System.Windows.MessageBox.Show($"Connection Failed:\n{ex.Message}", "Database Test", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        public void StartSyncEngine()
        {
            if (_worker == null)
            {
                _worker = new BackgroundSyncWorker(_repo, TallyServer, TallyPort);
                _worker.OnLogMessage += (msg) => {
                    LogOutput = $"[{DateTime.Now:HH:mm:ss}] {msg}\n" + LogOutput;
                    StatusText = msg;
                };
                _worker.OnSyncCompleted += () => {
                    System.Windows.Application.Current.Dispatcher.Invoke(() => LoadConfiguration());
                };
            }
            _worker.Start();
        }

        public void StopSyncEngine()
        {
            _worker?.Stop();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
